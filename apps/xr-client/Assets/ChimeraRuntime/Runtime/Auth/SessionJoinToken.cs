using System;
using System.Collections.Generic;
using System.Text;

namespace Chimera.Runtime
{
    /// <summary>
    /// Parsed payload of a Chimera session-entry QR / deep link.
    /// URI shape: <c>chimera://session?code=&lt;sessionCode&gt;&amp;join=&lt;joinToken&gt;[&amp;api=&lt;url&gt;]</c>
    ///
    /// The join token is single-use, server-side. The optional <c>api</c> override exists
    /// so the same Quest build can be pointed at staging vs production by swapping QR posters.
    /// </summary>
    public class SessionJoinToken
    {
        public string SessionCode;
        public string JoinToken;
        public string ApiBaseUrlOverride;

        public bool IsValid => !string.IsNullOrEmpty(SessionCode) && !string.IsNullOrEmpty(JoinToken);

        public static SessionJoinToken Parse(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) throw new FormatException("session URI is empty");

            const string scheme = "chimera://";
            if (!uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"session URI must start with '{scheme}' (got '{Truncate(uri, 32)}')");
            }
            int questionMark = uri.IndexOf('?');
            if (questionMark < 0)
            {
                throw new FormatException("session URI has no query component");
            }
            string host = uri.Substring(scheme.Length, questionMark - scheme.Length);
            if (!string.Equals(host, "session", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"session URI host must be 'session' (got '{host}')");
            }

            Dictionary<string, string> q = ParseQuery(uri.Substring(questionMark + 1));
            var payload = new SessionJoinToken
            {
                SessionCode = q.TryGetValue("code", out var c) ? c : null,
                JoinToken   = q.TryGetValue("join", out var j) ? j : null,
                ApiBaseUrlOverride = q.TryGetValue("api", out var a) ? a : null,
            };
            if (!payload.IsValid)
            {
                throw new FormatException("session URI missing required code= or join= query parameter");
            }
            return payload;
        }

        public static bool TryParse(string uri, out SessionJoinToken payload, out string error)
        {
            try { payload = Parse(uri); error = null; return true; }
            catch (Exception ex) { payload = null; error = ex.Message; return false; }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            foreach (string raw in query.Split('&'))
            {
                if (raw.Length == 0) continue;
                int eq = raw.IndexOf('=');
                string key = eq < 0 ? raw : raw.Substring(0, eq);
                string val = eq < 0 ? string.Empty : raw.Substring(eq + 1);
                dict[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(val);
            }
            return dict;
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

        public override string ToString()
        {
            var sb = new StringBuilder("chimera://session?");
            sb.Append("code=").Append(Uri.EscapeDataString(SessionCode ?? string.Empty));
            sb.Append("&join=").Append(Uri.EscapeDataString(JoinToken ?? string.Empty));
            if (!string.IsNullOrEmpty(ApiBaseUrlOverride))
            {
                sb.Append("&api=").Append(Uri.EscapeDataString(ApiBaseUrlOverride));
            }
            return sb.ToString();
        }
    }
}
