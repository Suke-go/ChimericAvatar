using System;
using Newtonsoft.Json;

namespace Chimera.Runtime
{
    /// <summary>
    /// Result of POST /api/v1/runtime/exchange (and /refresh). Treated as opaque by the
    /// client — the Chimera API mints these from a Supabase-backed identity inside the
    /// backend; the device never sees Supabase tokens directly. See ADR 0004.
    ///
    /// Lifetime guidance from the API contract:
    ///   runtimeToken : ~15 minutes; sent as Authorization: Bearer ...
    ///   refreshToken : ~12 hours; sent only to /api/v1/runtime/refresh
    ///   joinToken    : single-use, ~10 minutes (lives only in the QR payload)
    /// </summary>
    public class SessionCredentials
    {
        [JsonProperty("schemaVersion")] public string SchemaVersion = "1.0";
        [JsonProperty("sessionCode")]   public string SessionCode;
        [JsonProperty("runtimeToken")]  public string RuntimeToken;
        [JsonProperty("refreshToken")]  public string RefreshToken;
        [JsonProperty("expiresAt")]     public string ExpiresAt;
        [JsonProperty("scope")]         public string Scope;

        [JsonIgnore]
        public DateTime ExpiresAtUtc =>
            DateTime.TryParse(ExpiresAt, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt) ? dt.ToUniversalTime() : DateTime.MinValue;

        /// <summary>
        /// True when the runtimeToken should be considered expired. Defaults to a 60s skew so we
        /// proactively refresh before the wire actually sees a 401.
        /// </summary>
        public bool IsExpired(TimeSpan? skew = null)
        {
            DateTime exp = ExpiresAtUtc;
            if (exp == DateTime.MinValue) return false; // unknown — let the wire decide
            return DateTime.UtcNow + (skew ?? TimeSpan.FromSeconds(60)) >= exp;
        }
    }
}
