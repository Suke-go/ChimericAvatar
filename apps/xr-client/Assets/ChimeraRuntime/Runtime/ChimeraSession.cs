using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// High-level orchestrator: fetches the runtime manifest, places anchors,
    /// loads avatar bytes (if any), and opens the live-QA WebSocket.
    /// Drop on a GameObject in your bootstrap scene.
    /// </summary>
    public class ChimeraSession : MonoBehaviour
    {
        [Header("Endpoint")]
        public string ApiBaseUrl = "http://localhost:8000";
        public string SessionCode;
        public string DefaultProfile = "master";
        public string DefaultLanguage = "ja";

        [Header("Auth")]
        [Tooltip("MonoBehaviour implementing ISessionEntryProvider (ManualSessionEntry / MetaPassthroughQrScanner / XrealQrScanner). " +
                 "If null, ChimeraSession searches its children. If still none, boot proceeds without auth (dev/preview).")]
        public MonoBehaviour SessionEntrySource;
        [Tooltip("Editor escape hatch. If non-empty, sent as the manifest bearer and the QR / exchange flow is skipped.")]
        public string DebugRuntimeToken;
        [Tooltip("Human-readable label sent during /runtime/exchange so the dashboard can identify the device.")]
        public string DeviceLabel = "Quest 3";

        [Header("Wiring")]
        [Tooltip("Optional. Drives anchor backend resolution and ApiBaseUrl override at boot.")]
        public RuntimeBuildProfile BuildProfile;
        [Tooltip("Optional. If null and BuildProfile is set, AnchorBackendFactory creates one at boot.")]
        public PosterAnchorRoot Anchor;
        public ScriptPlayback ScriptPlayback;
        public bool AutoBootOnStart = false;
        public bool AutoConnectQa = true;

        [Header("Status (read-only)")]
        [SerializeField] private string _status = "idle";

        public RuntimeManifest Manifest { get; private set; }
        public AssetDownloader Downloader { get; private set; }
        public LiveQaSocket LiveQa { get; private set; }
        public Transform AvatarRoot { get; private set; }
        public GvrmBundle LoadedGvrm { get; private set; }

        public event Action<RuntimeManifest> OnManifestLoaded;
        public event Action<GvrmBundle> OnGvrmLoaded;
        public event Action<QaAnswer> OnQaAnswer;
        public event Action<string> OnError;
        public event Action OnAuthRequired;

        private CancellationTokenSource _cts;

        private async void Start()
        {
            if (AutoBootOnStart && !string.IsNullOrEmpty(SessionCode))
            {
                try { await BootAsync(); }
                catch (Exception ex) { LogError(ex.Message); }
            }
        }

        public async Task BootAsync(CancellationToken ct = default)
        {
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CancellationToken token = _cts.Token;

            Downloader ??= new AssetDownloader();
            ScriptPlayback?.Configure(Downloader);

            SetStatus("authorizing");
            string apiBase = ResolveApiBaseUrl();
            string bearer = await EnsureBearerTokenAsync(apiBase, token);

            SetStatus("fetching-manifest");
            var client = new ManifestClient(apiBase);
            Manifest = await FetchManifestWithRetryAsync(client, apiBase, bearer, token);
            OnManifestLoaded?.Invoke(Manifest);

            ResolveAnchor();
            ApplyAnchorPlacement();
            CreateAvatarRoot();
            await LoadGvrmIfRequestedAsync(token);

            if (AutoConnectQa) await ConnectQaAsync(token);

            SetStatus("ready");
        }

        public void PlayDefaultScript()
        {
            if (Manifest?.Scripts == null || Manifest.Scripts.Count == 0)
            {
                LogError("No scripts in manifest");
                return;
            }
            ScriptTrack track = SelectTrack(DefaultProfile, DefaultLanguage);
            ScriptPlayback?.Play(track, Manifest.PresentationCues);
        }

        public ScriptTrack SelectTrack(string profile, string language)
        {
            if (Manifest?.Scripts == null) return null;
            ScriptTrack exact = null;
            ScriptTrack langOnly = null;
            foreach (var t in Manifest.Scripts)
            {
                if (t.Profile == profile && t.Language == language) exact = t;
                if (t.Language == language && langOnly == null) langOnly = t;
            }
            return exact ?? langOnly ?? Manifest.Scripts[0];
        }

        public async Task ConnectQaAsync(CancellationToken ct = default)
        {
            if (Manifest?.Qa == null || string.IsNullOrEmpty(Manifest.Qa.WebsocketUrl)) return;
            if (Manifest.Features != null && !Manifest.Features.LiveQa)
            {
                Debug.Log("[Chimera] features.liveQa=false; skipping WebSocket");
                return;
            }
            LiveQa?.Dispose();
            LiveQa = new LiveQaSocket(
                Manifest.Qa.WebsocketUrl,
                Manifest.Qa.RuntimeToken,
                DefaultProfile,
                DefaultLanguage);
            LiveQa.OnAnswer += a => OnQaAnswer?.Invoke(a);
            LiveQa.OnError += msg => LogError($"[Chimera] live-qa: {msg}");
            await LiveQa.ConnectAsync(ct);
        }

        public Task AskAsync(string question, string activePanelId = null, int topK = 6, CancellationToken ct = default)
        {
            if (LiveQa == null || !LiveQa.IsOpen) return Task.CompletedTask;
            return LiveQa.SendQuestionAsync(question, activePanelId, topK, DefaultProfile, DefaultLanguage, ct);
        }

        private void Update()
        {
            LiveQa?.PumpMainThread();
        }

        private string ResolveApiBaseUrl()
        {
            if (BuildProfile != null
                && !string.IsNullOrWhiteSpace(BuildProfile.DefaultApiBaseUrl)
                && (string.IsNullOrWhiteSpace(ApiBaseUrl) || ApiBaseUrl == "http://localhost:8000"))
            {
                return BuildProfile.DefaultApiBaseUrl;
            }
            return ApiBaseUrl;
        }

        private async Task<string> EnsureBearerTokenAsync(string apiBase, CancellationToken ct)
        {
            // (1) Inspector escape hatch: bypass auth entirely.
            if (!string.IsNullOrEmpty(DebugRuntimeToken)) return DebugRuntimeToken;

            // (2) Already cached credentials for this session?
            if (!string.IsNullOrEmpty(SessionCode))
            {
                var stored = CredentialStore.Load(SessionCode);
                if (stored != null && !stored.IsExpired()) return stored.RuntimeToken;

                if (stored != null && !string.IsNullOrEmpty(stored.RefreshToken))
                {
                    try
                    {
                        var refreshed = await new RuntimeAuthClient(apiBase).RefreshAsync(stored.RefreshToken, ct);
                        CredentialStore.Save(refreshed);
                        return refreshed.RuntimeToken;
                    }
                    catch (RuntimeAuthException ex) when (ex.IsUnauthorized || ex.IsForbidden)
                    {
                        CredentialStore.Clear(SessionCode);
                        // fall through to provider entry
                    }
                }
            }

            // (3) Need a fresh QR / manual entry → exchange.
            ISessionEntryProvider provider = ResolveEntryProvider();
            if (provider == null)
            {
                // No entry source — proceed unauthenticated. Useful for local preview manifests.
                return null;
            }

            SetStatus("waiting-for-session-entry");
            OnAuthRequired?.Invoke();
            SessionJoinToken entry = await provider.RequestEntryAsync(ct);
            if (entry == null || !entry.IsValid)
            {
                throw new InvalidOperationException("session entry provider returned no valid entry");
            }
            if (!string.IsNullOrEmpty(entry.ApiBaseUrlOverride))
            {
                apiBase = entry.ApiBaseUrlOverride.TrimEnd('/');
                ApiBaseUrl = apiBase;
            }

            SetStatus("exchanging-credentials");
            var auth = new RuntimeAuthClient(apiBase);
            string deviceId = CredentialStore.GetOrCreateDeviceId();
            SessionCredentials creds = await auth.ExchangeAsync(entry, deviceId, DeviceLabel, ct);
            CredentialStore.Save(creds);
            SessionCode = creds.SessionCode;
            return creds.RuntimeToken;
        }

        private async Task<RuntimeManifest> FetchManifestWithRetryAsync(ManifestClient client, string apiBase, string bearer, CancellationToken ct)
        {
            try
            {
                return await client.FetchAsync(SessionCode, bearer, ct);
            }
            catch (ManifestUnauthorizedException)
            {
                if (string.IsNullOrEmpty(SessionCode)) throw;
                var stored = CredentialStore.Load(SessionCode);
                if (stored == null || string.IsNullOrEmpty(stored.RefreshToken))
                {
                    CredentialStore.Clear(SessionCode);
                    OnAuthRequired?.Invoke();
                    throw;
                }
                try
                {
                    var refreshed = await new RuntimeAuthClient(apiBase).RefreshAsync(stored.RefreshToken, ct);
                    CredentialStore.Save(refreshed);
                    return await client.FetchAsync(SessionCode, refreshed.RuntimeToken, ct);
                }
                catch
                {
                    CredentialStore.Clear(SessionCode);
                    OnAuthRequired?.Invoke();
                    throw;
                }
            }
        }

        private ISessionEntryProvider ResolveEntryProvider()
        {
            if (SessionEntrySource is ISessionEntryProvider direct) return direct;
            foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is ISessionEntryProvider candidate) return candidate;
            }
            return null;
        }

        private void ResolveAnchor()
        {
            if (Anchor != null) return;
            if (BuildProfile == null) return;
            Anchor = AnchorBackendFactory.ResolveOrInstantiate(BuildProfile, transform, Manifest);
        }

        private void ApplyAnchorPlacement()
        {
            if (Anchor == null || Manifest?.Tracking == null) return;
            Anchor.ReferenceImageName = Manifest.Tracking.ReferenceImageName;
            if (Manifest.Tracking.PhysicalSizeM != null && Manifest.Tracking.PhysicalSizeM.Length == 2)
            {
                Anchor.PhysicalSizeM = new Vector2(
                    Manifest.Tracking.PhysicalSizeM[0],
                    Manifest.Tracking.PhysicalSizeM[1]);
            }
        }

        private void CreateAvatarRoot()
        {
            if (Manifest?.Avatar == null || Anchor == null) return;
            var holder = new GameObject($"Avatar({Manifest.Avatar.RuntimeType})");
            holder.transform.SetParent(Anchor.transform, worldPositionStays: false);
            var p = Manifest.Avatar.Placement;
            if (p != null)
            {
                if (p.Position != null && p.Position.Length == 3)
                    holder.transform.localPosition = new Vector3(p.Position[0], p.Position[1], p.Position[2]);
                if (p.Rotation != null && p.Rotation.Length == 3)
                    holder.transform.localRotation = Quaternion.Euler(p.Rotation[0], p.Rotation[1], p.Rotation[2]);
                holder.transform.localScale = Vector3.one * Mathf.Max(0.001f, p.Scale);
            }
            AvatarRoot = holder.transform;
        }

        private async Task LoadGvrmIfRequestedAsync(CancellationToken ct)
        {
            var av = Manifest?.Avatar;
            if (av == null || av.RuntimeType != "gvrm" || av.AssetBundle == null) return;
            SetStatus("downloading-gvrm");
            byte[] bytes = await Downloader.GetBytesAsync(av.AssetBundle, ct);
            LoadedGvrm = GvrmZipReader.Load(bytes);
            OnGvrmLoaded?.Invoke(LoadedGvrm);
            // Hand-off: a higher-level GVRM renderer subscribes to OnGvrmLoaded
            // and instantiates VRM + Skinned Gaussian Splat under AvatarRoot.
        }

        private void LogError(string message)
        {
            Debug.LogError($"[Chimera] {message}");
            SetStatus($"error: {message}");
            OnError?.Invoke(message);
        }

        private void SetStatus(string status)
        {
            _status = status;
        }

        private void OnDestroy()
        {
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            LiveQa?.Dispose();
            ScriptPlayback?.Stop();
        }
    }
}
