using UnityEngine;

namespace Dio.Common
{
    /// Tiny convenience wrapper over PlayerPrefs for local-only player profile data.
    public static class LocalPrefs
    {
        const string NameKey   = "dio.player.name";
        const string ColorKey  = "dio.player.color";
        const string DirectIpKey = "dio.lastDirectIp";
        const string PreferredLevelKey = "dio.preferredLevel";

        public static string PlayerName
        {
            get => PlayerPrefs.GetString(NameKey, string.Empty);
            set { PlayerPrefs.SetString(NameKey, value ?? string.Empty); PlayerPrefs.Save(); }
        }

        public static int ColorIndex
        {
            get => PlayerPrefs.GetInt(ColorKey, 0);
            set { PlayerPrefs.SetInt(ColorKey, value); PlayerPrefs.Save(); }
        }

        public static string LastDirectIp
        {
            get => PlayerPrefs.GetString(DirectIpKey, string.Empty);
            set { PlayerPrefs.SetString(DirectIpKey, value ?? string.Empty); PlayerPrefs.Save(); }
        }

        // -1 = "no preference yet", server falls back to its default level.
        public static int PreferredLevelIndex
        {
            get => PlayerPrefs.GetInt(PreferredLevelKey, -1);
            set { PlayerPrefs.SetInt(PreferredLevelKey, value); PlayerPrefs.Save(); }
        }
    }
}
