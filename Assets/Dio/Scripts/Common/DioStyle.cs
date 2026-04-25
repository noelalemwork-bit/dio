using UnityEngine;

namespace Dio.Common
{
    /// Single source of truth for UI colors, sizes, and animation timings.
    /// Every UI surface should pull from here so a tweak in one place
    /// propagates everywhere.
    public static class DioStyle
    {
        // ----- Surfaces -----
        public static readonly Color PaperLight = new Color32(0xFE, 0xF6, 0xE3, 0xFF);
        public static readonly Color PaperWarm  = new Color32(0xFD, 0xEA, 0xC4, 0xFF);
        public static readonly Color InkDark    = new Color32(0x1F, 0x18, 0x12, 0xFF);
        public static readonly Color InkSoft    = new Color32(0x4B, 0x3F, 0x36, 0xFF);
        public static readonly Color CardSoft   = new Color32(0xFF, 0xFF, 0xFF, 0xE6);
        public static readonly Color ScrimSoft  = new Color32(0x10, 0x0A, 0x1E, 0xB3);

        // ----- Brand accents (also serve as button base colors) -----
        public static readonly Color Sun       = new Color32(0xF5, 0xA8, 0x2C, 0xFF);
        public static readonly Color Sky       = new Color32(0x4D, 0xC2, 0xF1, 0xFF);
        public static readonly Color Mint      = new Color32(0x7B, 0xD8, 0x9C, 0xFF);
        public static readonly Color Coral     = new Color32(0xF0, 0x65, 0x55, 0xFF);
        public static readonly Color Lavender  = new Color32(0xC1, 0xA1, 0xE8, 0xFF);
        public static readonly Color Lemon     = new Color32(0xFF, 0xE2, 0x6B, 0xFF);

        // ----- Sizes -----
        public const float SwatchBase   = 36f;
        public const float SwatchPicked = 48f;     // grows on selection
        public const float SwatchOutline = 4f;
        public const float ButtonRadiusBig = 28f;

        // ----- Spring tween defaults — gentle, bouncy.
        // omega = 2 * pi / period; zeta < 1 = underdamped (springy).
        public const float SpringPeriod = 0.45f;
        public const float SpringDamping = 0.55f;
        public const float SpringSnap = 0.5f;       // px / unit threshold for "settled"

        // Cubic ease-out for non-springy tweens (panel fade, swap).
        public static float EaseOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return 1f - u * u * u;
        }

        // Pick a color that's a lighter, brighter version of `c` for outlines on
        // selected items — instead of a black scrim. ~25% blend toward white in
        // OkLab-ish space (well, RGB; close enough for UI pop).
        public static Color Lighten(Color c, float amount = 0.45f)
        {
            return Color.Lerp(c, Color.white, Mathf.Clamp01(amount));
        }
    }
}
