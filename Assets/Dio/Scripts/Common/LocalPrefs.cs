using UnityEngine;

namespace Dio.Common
{
    /// Tiny convenience wrapper over PlayerPrefs for local-only player profile data.
    public static class LocalPrefs
    {
        const string NameKey = "dio.player.name";
        const string ColorKey = "dio.player.color";

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
    }
}
