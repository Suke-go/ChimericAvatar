using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Inspector-driven session entry. Used by:
    ///   - Editor smoke (typed once, kept across Play sessions)
    ///   - Quest 3 / XREAL fallback when QR scanning is unavailable
    ///   - CI / dev with a paste of "chimera://session?code=...&amp;join=..."
    ///
    /// At runtime the bound text fields are read once when
    /// <see cref="RequestEntryAsync"/> is awaited; if SessionUri is non-empty
    /// it wins, otherwise SessionCode + JoinToken are combined.
    /// </summary>
    public class ManualSessionEntry : MonoBehaviour, ISessionEntryProvider
    {
        [Header("Either paste a full session URI...")]
        [Tooltip("chimera://session?code=...&join=...")]
        public string SessionUri;

        [Header("...or fill these explicitly")]
        public string SessionCode;
        public string JoinToken;
        public string ApiBaseUrlOverride;

        public Task<SessionJoinToken> RequestEntryAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(SessionUri))
            {
                return Task.FromResult(SessionJoinToken.Parse(SessionUri.Trim()));
            }
            var token = new SessionJoinToken
            {
                SessionCode = (SessionCode ?? string.Empty).Trim(),
                JoinToken = (JoinToken ?? string.Empty).Trim(),
                ApiBaseUrlOverride = string.IsNullOrWhiteSpace(ApiBaseUrlOverride) ? null : ApiBaseUrlOverride.Trim(),
            };
            if (!token.IsValid)
            {
                throw new InvalidOperationException(
                    "ManualSessionEntry has neither SessionUri nor (SessionCode + JoinToken). " +
                    "Fill the Inspector before pressing Play, or wire a different ISessionEntryProvider.");
            }
            return Task.FromResult(token);
        }
    }
}
