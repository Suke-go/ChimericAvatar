using System;
using Newtonsoft.Json;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// PlayerPrefs-backed credential store, namespaced by sessionCode so a device can
    /// cache credentials for multiple concurrent sessions (e.g., a presenter switching
    /// between two posters at a workshop).
    ///
    /// P0: PlayerPrefs (XML on Quest 3 / Android SharedPreferences). Adequate for
    /// short-lived runtimeToken + ~12h refreshToken in a research-lab context.
    /// P1: route through a native Keystore-backed encryptor (Android Keystore on
    /// Quest 3, NSKeychain on visionOS). The interface stays the same.
    /// </summary>
    public static class CredentialStore
    {
        private const string PrefPrefix    = "chimera.creds.";
        private const string PrefDeviceId  = "chimera.deviceId";

        public static void Save(SessionCredentials credentials)
        {
            if (credentials == null) throw new ArgumentNullException(nameof(credentials));
            if (string.IsNullOrEmpty(credentials.SessionCode))
                throw new ArgumentException("SessionCredentials.SessionCode required for storage", nameof(credentials));
            string json = JsonConvert.SerializeObject(credentials);
            PlayerPrefs.SetString(KeyFor(credentials.SessionCode), json);
            PlayerPrefs.Save();
        }

        public static SessionCredentials Load(string sessionCode)
        {
            if (string.IsNullOrEmpty(sessionCode)) return null;
            string key = KeyFor(sessionCode);
            if (!PlayerPrefs.HasKey(key)) return null;
            string json = PlayerPrefs.GetString(key, null);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonConvert.DeserializeObject<SessionCredentials>(json); }
            catch { Clear(sessionCode); return null; }
        }

        public static void Clear(string sessionCode)
        {
            if (string.IsNullOrEmpty(sessionCode)) return;
            PlayerPrefs.DeleteKey(KeyFor(sessionCode));
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Stable per-install device identifier. Generated on first use and persisted
        /// in PlayerPrefs. Deliberately not <c>SystemInfo.deviceUniqueIdentifier</c>:
        /// that value is privacy-sensitive on Android, requires permissions, and is not
        /// guaranteed stable across factory resets we'd want to invalidate anyway.
        /// </summary>
        public static string GetOrCreateDeviceId()
        {
            string id = PlayerPrefs.GetString(PrefDeviceId, null);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(PrefDeviceId, id);
                PlayerPrefs.Save();
            }
            return id;
        }

        private static string KeyFor(string sessionCode) => PrefPrefix + sessionCode;
    }
}
