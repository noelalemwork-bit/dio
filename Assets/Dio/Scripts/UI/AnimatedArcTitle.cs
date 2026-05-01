using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Dio.UI
{
    /// Title-card text laid out along an arc, with each glyph bouncing
    /// independently (per-character offset + scale wave). Used for the
    /// "KING RACER" home-screen header — the cartoon "Mario Kart-meets-
    /// Trackmania" feel relies on letters that wobble individually rather
    /// than a single static line.
    ///
    /// One TMP_Text child per glyph is spawned at Awake and re-arranged
    /// each frame: x = arc position, y = arc-curve height, rotation = arc
    /// tangent. A sin-wave scale + bob is layered on top, with each glyph
    /// getting a different phase so the wave reads as "letters dancing"
    /// rather than the whole word lerping in unison.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public class AnimatedArcTitle : MonoBehaviour
    {
        [Header("Text")]
        public string text = "KING RACER";
        [Tooltip("Pixel size of each glyph at idle scale.")]
        public float fontSize = 96f;
        [Tooltip("TMP font asset. Falls back to the project default if null.")]
        public TMP_FontAsset font;
        public Color glyphColor = new Color(0.99f, 0.95f, 0.30f, 1f);
        public Color outlineColor = new Color(0.18f, 0.10f, 0.05f, 1f);
        [Range(0f, 1f)] public float outlineWidth = 0.30f;
        public FontStyles style = FontStyles.Bold;

        [Header("Arc layout")]
        [Tooltip("Total arc width in pixels (left-edge of first glyph to right-edge of last).")]
        public float arcWidth = 720f;
        [Tooltip("Peak height of the arc above the baseline (px). Bigger = more curved.")]
        public float arcHeight = 60f;
        [Tooltip("Rotate glyphs to follow the arc tangent.")]
        public bool rotateAlongArc = true;
        [Tooltip("Reduce the rotation amount; 1 = full tangent, 0.5 = half (less tilt).")]
        [Range(0f, 1f)] public float rotationStrength = 0.65f;

        [Header("Bounce animation")]
        [Tooltip("Hz of the per-glyph bounce wave.")]
        public float bounceHz = 1.4f;
        [Tooltip("Vertical bob amplitude in pixels at peak.")]
        public float bounceAmp = 14f;
        [Tooltip("Scale amplitude on top of base scale; 0.12 = ±12% size pulse.")]
        public float scaleAmp = 0.12f;
        [Tooltip("Phase shift between adjacent glyphs (radians). 0.6 = ~34° staggered.")]
        public float phasePerGlyph = 0.6f;

        readonly List<TextMeshProUGUI> _glyphs = new List<TextMeshProUGUI>();

        void Awake() { Rebuild(); }

#if UNITY_EDITOR
        void OnValidate()
        {
            // Rebuilding from OnValidate is unsafe (called before Awake);
            // editor previews skip until Awake fires.
            if (_glyphs.Count > 0 && Application.isPlaying) Rebuild();
        }
#endif

        public void Rebuild()
        {
            // Clear existing glyph children.
            for (int i = _glyphs.Count - 1; i >= 0; i--)
                if (_glyphs[i] != null) Destroy(_glyphs[i].gameObject);
            _glyphs.Clear();

            if (string.IsNullOrEmpty(text)) return;
            var fontAsset = font != null ? font : TMP_Settings.defaultFontAsset;

            for (int i = 0; i < text.Length; i++)
            {
                var go = new GameObject($"G_{i}_{text[i]}", typeof(RectTransform));
                go.transform.SetParent(transform, false);
                var t = go.AddComponent<TextMeshProUGUI>();
                t.text = text[i].ToString();
                t.fontSize = fontSize;
                t.fontStyle = style;
                t.alignment = TextAlignmentOptions.Center;
                t.color = glyphColor;
                t.font = fontAsset;
                t.outlineWidth = outlineWidth;
                t.outlineColor = outlineColor;
                t.raycastTarget = false;
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(fontSize * 0.95f, fontSize * 1.4f);
                _glyphs.Add(t);
            }
        }

        void LateUpdate()
        {
            if (_glyphs.Count == 0) return;
            int n = _glyphs.Count;
            // For an arc with `arcWidth` span and `arcHeight` peak, use a
            // parabola y = h * (1 - (2u - 1)^2) where u in [0,1] maps to
            // x in [-W/2, +W/2]. Tangent slope = -4h*(2u-1)/W; convert to
            // degrees for rotation.
            float W = arcWidth;
            float H = arcHeight;
            float halfW = W * 0.5f;
            float wavePhase = Time.unscaledTime * bounceHz * Mathf.PI * 2f;

            for (int i = 0; i < n; i++)
            {
                var g = _glyphs[i];
                if (g == null) continue;
                // Center each glyph in its slot. Slots are equally spaced
                // along the arc, with the FIRST and LAST slot pinned at the
                // arc's left and right edges (so a longer string fills the
                // same total width).
                float u = n == 1 ? 0.5f : (float)i / (n - 1);
                float x = Mathf.Lerp(-halfW, halfW, u);
                float baseY = H * (1f - Mathf.Pow(2f * u - 1f, 2f));

                // Per-glyph bounce: sin(wavePhase + i*phasePerGlyph).
                float w = Mathf.Sin(wavePhase + i * phasePerGlyph);
                float yBob = w * bounceAmp;
                float scale = 1f + w * scaleAmp;

                var rt = g.rectTransform;
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot     = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(x, baseY + yBob);
                rt.localScale = Vector3.one * scale;

                if (rotateAlongArc)
                {
                    // Slope of parabola: dy/dx = -4H/W^2 * 2x = -8H*u(1-u)... actually
                    // simpler — derive analytically. y = H*(1 - (2u-1)^2). dy/du = H * -2*(2u-1)*2 = -4H*(2u-1).
                    // dx/du = W. So dy/dx = -4H*(2u-1)/W.
                    float slope = -4f * H * (2f * u - 1f) / Mathf.Max(0.001f, W);
                    float deg = Mathf.Atan(slope) * Mathf.Rad2Deg * rotationStrength;
                    rt.localEulerAngles = new Vector3(0, 0, deg);
                }
                else
                {
                    rt.localEulerAngles = Vector3.zero;
                }
            }
        }
    }
}
