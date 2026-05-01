using UnityEngine;
using UnityEngine.UI;

namespace Dio.UI
{
    /// Replaces a flat-color UI Image with a procedural multi-stop gradient
    /// sprite — much more "Mario Kart × Trackmania" than a solid backdrop.
    /// Generates a 4×N pixel texture once at Awake, packages it as a Sprite,
    /// and assigns it to the attached Image. Vertical orientation by default;
    /// flip `horizontal` for a side-to-side gradient.
    ///
    /// Use `BuildSprite` directly from editor / scene-builder code if you
    /// don't want to attach this MonoBehaviour at runtime.
    [RequireComponent(typeof(Image))]
    [DisallowMultipleComponent]
    public class GradientBackground : MonoBehaviour
    {
        [Tooltip("Stops along the gradient axis. Sorted by `t` 0..1 at Apply.")]
        public Stop[] stops = new[]
        {
            new Stop { t = 0f,    color = new Color(0.97f, 0.66f, 0.31f, 1f) }, // sun
            new Stop { t = 0.55f, color = new Color(0.91f, 0.45f, 0.55f, 1f) }, // coral
            new Stop { t = 1f,    color = new Color(0.40f, 0.30f, 0.65f, 1f) }, // dusk-purple
        };
        [Tooltip("If true, gradient runs left → right; otherwise top → bottom.")]
        public bool horizontal = false;
        [Tooltip("Texture resolution along the gradient axis. 256 is usually plenty.")]
        public int resolution = 256;

        [System.Serializable]
        public struct Stop
        {
            [Range(0f, 1f)] public float t;
            public Color color;
        }

        void Awake() { Apply(); }

        public void Apply()
        {
            var img = GetComponent<Image>();
            if (img == null) return;
            var sprite = BuildSprite(stops, resolution, horizontal);
            img.sprite = sprite;
            img.type = Image.Type.Simple;
            img.color = Color.white;
            // Keep raycast behavior whatever the caller set; don't toggle.
        }

        /// One-shot helper used by editor scene-builders. Builds a sprite
        /// from the given stops. Stops are sorted internally so the caller
        /// can pass them in any order.
        public static Sprite BuildSprite(Stop[] stopsIn, int resolution, bool horizontal)
        {
            if (stopsIn == null || stopsIn.Length == 0)
            {
                stopsIn = new[] {
                    new Stop { t = 0f, color = Color.white },
                    new Stop { t = 1f, color = Color.gray },
                };
            }
            var sorted = (Stop[])stopsIn.Clone();
            System.Array.Sort(sorted, (a, b) => a.t.CompareTo(b.t));

            int n = Mathf.Max(8, resolution);
            // 4-pixel-thick texture along the perpendicular axis so Unity's
            // mip + filtering paths don't squash a 1-pixel strip into noise.
            int w = horizontal ? n : 4;
            int h = horizontal ? 4 : n;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "DioGradient",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[w * h];
            for (int i = 0; i < n; i++)
            {
                float u = (float)i / (n - 1);
                Color c = SampleStops(sorted, u);
                Color32 c32 = c;
                if (horizontal)
                {
                    for (int y = 0; y < h; y++) px[y * w + i] = c32;
                }
                else
                {
                    for (int x = 0; x < w; x++) px[i * w + x] = c32;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);

            var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            sp.name = "DioGradient";
            return sp;
        }

        static Color SampleStops(Stop[] stops, float t)
        {
            if (t <= stops[0].t) return stops[0].color;
            if (t >= stops[stops.Length - 1].t) return stops[stops.Length - 1].color;
            for (int i = 0; i < stops.Length - 1; i++)
            {
                if (t >= stops[i].t && t <= stops[i + 1].t)
                {
                    float span = Mathf.Max(1e-4f, stops[i + 1].t - stops[i].t);
                    float local = (t - stops[i].t) / span;
                    return Color.Lerp(stops[i].color, stops[i + 1].color, local);
                }
            }
            return stops[stops.Length - 1].color;
        }
    }
}
