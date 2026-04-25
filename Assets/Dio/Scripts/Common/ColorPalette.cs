using UnityEngine;

namespace Dio.Common
{
    public static class ColorPalette
    {
        public static readonly Color[] Colors =
        {
            new Color32(0xF3, 0x9B, 0x2A, 0xFF), // orange
            new Color32(0x5B, 0xB8, 0x5E, 0xFF), // green
            new Color32(0x3D, 0xA6, 0xE0, 0xFF), // cyan
            new Color32(0xE0, 0x53, 0x3D, 0xFF), // red
            new Color32(0xA8, 0x7C, 0xC9, 0xFF), // purple
            new Color32(0xF1, 0xD2, 0x4D, 0xFF), // yellow
        };

        public static readonly string[] Names =
        {
            "Orange", "Green", "Cyan", "Red", "Purple", "Yellow"
        };

        public static Color Get(int index) =>
            Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
    }
}
