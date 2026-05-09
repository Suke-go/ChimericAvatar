using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace Chimera.Runtime
{
    /// <summary>
    /// Thin HTTP client over POST /api/v1/runtime/exchange and /api/v1/runtime/refresh.
    /// Always JSON in / JSON out; never persists anything (CredentialStore owns persistence).
    ///
    /// Failure model: every non-2xx response throws <see cref="RuntimeAuthException"/>
    /// with the HTTP status code and response body so the orchestrator can branch on
    /// 401/403/409 without parsing the message string.
    /// </summary>
    public class RuntimeAuthClient
    {
        private readonly string _apiBase;

        public RuntimeAuthClient(string apiBase)
        {
            if (string.IsNullOrWhiteSpace(apiBase)) throw new ArgumentException("apiBase is required", nameof(apiBase));
            _apiBase = apiBase.TrimEnd('/');
        }

        public Task<SessionCredentials> ExchangeAsync(SessionJoinToken entry, string deviceId, string deviceLabel = null, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!entry.IsValid) throw new ArgumentException("session entry is incomplete", nameof(entry));
            var body = new
            {
                sessionCode = entry.SessionCode,
                joinToken   = entry.JoinToken,
                deviceId    = deviceId,
                deviceLabel = deviceLabel ?? string.Empty,
            };
            return PostJsonAsync<SessionCredentials>("/api/v1/runtime/exchange", body, ct);
        }

        public Task<SessionCredentials> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(refreshToken)) throw new ArgumentException("refreshToken required", nameof(refreshToken));
            return PostJsonAsync<SessionCredentials>("/api/v1/runtime/refresh", new { refreshToken }, ct);
        }

        private async Task<T> PostJsonAsync<T>(string path, object body, CancellationToken ct) where T : class
        {
            string url = _apiBase + path;
            string json = JsonConvert.SerializeObject(body);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            using var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            await UnityWebRequestExtensions.SendAsync(request, ct);

            int code = (int)request.responseCode;
            if (request.result != UnityWebRequest.Result.Success || code < 200 || code >= 300)
            {
                throw new RuntimeAuthException(code, request.error, request.downloadHandler?.text, $"POST {path} failed");
            }
            return JsonConvert.DeserializeObject<T>(request.downloadHandler.text);
        }
    }

    public class RuntimeAuthException : Exception
    {
        public int StatusCode { get; }
        public string TransportError { get; }
        public string ResponseBody { get; }

        public RuntimeAuthException(int statusCode, string transportError, string body, string message)
            : base($"{message} (status={statusCode} transport='{transportError}' body='{Truncate(body, 200)}')")
        {
            StatusCode = statusCode;
            TransportError = transportError;
            ResponseBody = body;
        }

        public bool IsUnauthorized => StatusCode == 401;
        public bool IsForbidden    => StatusCode == 403;
        public bool IsConflict     => StatusCode == 409;

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(0, n) + "...");
    }
}
