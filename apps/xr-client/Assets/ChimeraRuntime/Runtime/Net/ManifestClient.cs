using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace Chimera.Runtime
{
    /// <summary>
    /// Authenticated GET /api/v1/runtime/{sessionCode}/manifest. Bearer token comes
    /// from the orchestrator (ChimeraSession), which owns refresh policy: this client
    /// never reads or writes <see cref="CredentialStore"/>. On 401 we throw
    /// <see cref="ManifestUnauthorizedException"/> so the orchestrator can refresh
    /// and retry once.
    ///
    /// For local dev / preview manifests where the API has no auth gate yet,
    /// pass a null bearer.
    /// </summary>
    public class ManifestClient
    {
        private readonly string _apiBase;

        public ManifestClient(string apiBase)
        {
            if (string.IsNullOrWhiteSpace(apiBase)) throw new ArgumentException("apiBase is required", nameof(apiBase));
            _apiBase = apiBase.TrimEnd('/');
        }

        public async Task<RuntimeManifest> FetchAsync(string sessionCode, string bearerToken = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionCode)) throw new ArgumentException("sessionCode is required", nameof(sessionCode));
            string url = $"{_apiBase}/api/v1/runtime/{sessionCode}/manifest";
            using var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrEmpty(bearerToken))
            {
                request.SetRequestHeader("Authorization", "Bearer " + bearerToken);
            }
            await UnityWebRequestExtensions.SendAsync(request, ct);

            int code = (int)request.responseCode;
            if (code == 401)
            {
                throw new ManifestUnauthorizedException(
                    $"Manifest fetch unauthorized (401) for {sessionCode}: {request.error} body={request.downloadHandler?.text}");
            }
            if (request.result != UnityWebRequest.Result.Success || code < 200 || code >= 300)
            {
                throw new ManifestFetchException(
                    $"Manifest fetch failed: {code} {request.error} body={request.downloadHandler?.text}");
            }
            return JsonConvert.DeserializeObject<RuntimeManifest>(request.downloadHandler.text);
        }
    }

    public class ManifestFetchException : Exception
    {
        public ManifestFetchException(string message) : base(message) { }
    }

    public class ManifestUnauthorizedException : ManifestFetchException
    {
        public ManifestUnauthorizedException(string message) : base(message) { }
    }

    internal static class UnityWebRequestExtensions
    {
        public static Task SendAsync(UnityWebRequest request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<object>();
            var op = request.SendWebRequest();
            CancellationTokenRegistration registration = ct.Register(() =>
            {
                try { request.Abort(); } catch { }
                tcs.TrySetCanceled();
            });
            op.completed += _ =>
            {
                registration.Dispose();
                tcs.TrySetResult(null);
            };
            return tcs.Task;
        }
    }
}
