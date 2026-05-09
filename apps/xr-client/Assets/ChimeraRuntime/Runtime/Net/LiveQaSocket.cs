using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Chimera.Runtime
{
    public class LiveQaSocket : IDisposable
    {
        public event Action OnReady;
        public event Action<QaAnswer> OnAnswer;
        public event Action<string> OnError;
        public event Action OnClosed;

        private readonly Uri _uri;
        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        public LiveQaSocket(string websocketUrl, string runtimeToken, string profile = "master", string language = "ja")
        {
            if (string.IsNullOrEmpty(websocketUrl)) throw new ArgumentException(nameof(websocketUrl));
            if (string.IsNullOrEmpty(runtimeToken)) throw new ArgumentException(nameof(runtimeToken));
            string sep = websocketUrl.Contains("?") ? "&" : "?";
            _uri = new Uri(
                $"{websocketUrl}{sep}token={Uri.EscapeDataString(runtimeToken)}" +
                $"&profile={Uri.EscapeDataString(profile)}" +
                $"&language={Uri.EscapeDataString(language)}");
        }

        public bool IsOpen => _socket != null && _socket.State == WebSocketState.Open;

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(_uri, _cts.Token);
            _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        }

        public Task SendQuestionAsync(string question, string activePanelId = null, int topK = 6, string profile = null, string language = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("question is required", nameof(question));
            var payload = new Dictionary<string, object>
            {
                {"type", "question"},
                {"question", question},
                {"topK", Math.Max(1, Math.Min(topK, 12))},
            };
            if (!string.IsNullOrEmpty(activePanelId)) payload["activePanelId"] = activePanelId;
            if (!string.IsNullOrEmpty(profile)) payload["profile"] = profile;
            if (!string.IsNullOrEmpty(language)) payload["language"] = language;
            return SendAsync(payload, ct);
        }

        public Task SendPingAsync(CancellationToken ct = default) =>
            SendAsync(new Dictionary<string, object> { { "type", "ping" } }, ct);

        private async Task SendAsync(object payload, CancellationToken ct)
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not open");
            string json = JsonConvert.SerializeObject(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        public void PumpMainThread()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); } catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
            }
        }

        private void Post(Action action) => _mainThreadQueue.Enqueue(action);

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[16 * 1024];
            using var ms = new MemoryStream();
            try
            {
                while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                            Post(() => OnClosed?.Invoke());
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    HandleMessage(text);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Post(() => OnError?.Invoke(ex.Message));
            }
            finally
            {
                Post(() => OnClosed?.Invoke());
            }
        }

        private void HandleMessage(string text)
        {
            try
            {
                var obj = JObject.Parse(text);
                string type = obj.Value<string>("type");
                switch (type)
                {
                    case "ready":
                        Post(() => OnReady?.Invoke());
                        break;
                    case "answer":
                        var answer = obj.ToObject<QaAnswer>();
                        Post(() => OnAnswer?.Invoke(answer));
                        break;
                    case "error":
                        string msg = obj.Value<string>("message") ?? "unknown error";
                        Post(() => OnError?.Invoke(msg));
                        break;
                    case "pong":
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Post(() => OnError?.Invoke($"parse failed: {ex.Message} text={text}"));
            }
        }

        public async Task CloseAsync()
        {
            if (_socket != null && _socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            _socket?.Dispose();
            _cts?.Dispose();
        }
    }

    public class QaAnswer
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("answerability")] public string Answerability;
        [JsonProperty("reason")] public string Reason;
        [JsonProperty("draftAnswer")] public string DraftAnswer;
        [JsonProperty("qaMatch")] public QaMatch QaMatch;
        [JsonProperty("chunks")] public List<RetrievedChunk> Chunks;
    }

    public class QaMatch
    {
        [JsonProperty("qa_id")] public string QaId;
        [JsonProperty("question")] public string Question;
        [JsonProperty("answer")] public string Answer;
        [JsonProperty("score")] public float Score;
        [JsonProperty("panel_id")] public string PanelId;
        [JsonProperty("evidence_chunk_ids")] public List<string> EvidenceChunkIds;
    }

    public class RetrievedChunk
    {
        [JsonProperty("chunk_id")] public string ChunkId;
        [JsonProperty("document_id")] public string DocumentId;
        [JsonProperty("panel_id")] public string PanelId;
        [JsonProperty("score")] public float Score;
        [JsonProperty("text")] public string Text;
        [JsonProperty("page_number")] public int? PageNumber;
        [JsonProperty("metadata")] public Dictionary<string, object> Metadata;
    }
}
