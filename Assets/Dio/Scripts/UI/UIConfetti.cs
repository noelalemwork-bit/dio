using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Dio.UI
{
    /// Cheap, ScreenSpaceOverlay-friendly confetti: spawns N small UI Image
    /// children, animates them falling with random horizontal drift + rotation
    /// in coroutines, and destroys them when their lifetime ends. Lives inside
    /// the canvas so it renders on top of the gameplay scene without needing
    /// a particle camera or a stacked canvas.
    public class UIConfetti : MonoBehaviour
    {
        public Sprite pieceSprite;        // small square / SVG; null → white default
        public int countPerBurst = 100;
        public float pieceLifetime = 4.5f;
        public Vector2 pieceSizeRange = new Vector2(8f, 18f);
        public Vector2 fallSpeedRange = new Vector2(220f, 480f);
        public float horizontalDrift = 200f;
        public float horizontalSpread = 900f;
        public float spinRange = 540f;
        public Color[] palette = new[]
        {
            new Color(0.95f, 0.61f, 0.16f, 1f), // orange
            new Color(0.36f, 0.72f, 0.37f, 1f), // green
            new Color(0.24f, 0.65f, 0.88f, 1f), // cyan
            new Color(0.88f, 0.32f, 0.30f, 1f), // red
            new Color(0.66f, 0.49f, 0.78f, 1f), // purple
            new Color(0.95f, 0.82f, 0.31f, 1f), // yellow
        };

        public void Burst()
        {
            StopAllCoroutines();
            // Wipe any existing pieces from a previous burst before starting fresh.
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child != null) Destroy(child.gameObject);
            }
            for (int i = 0; i < countPerBurst; i++) StartCoroutine(SpawnAndAnimate());
        }

        IEnumerator SpawnAndAnimate()
        {
            var go = new GameObject("ConfettiPiece", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            float size = Random.Range(pieceSizeRange.x, pieceSizeRange.y);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = new Vector2(Random.Range(-horizontalSpread * 0.5f, horizontalSpread * 0.5f),
                                              Random.Range(-40f, 80f));
            rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            var img = go.GetComponent<Image>();
            if (pieceSprite != null) img.sprite = pieceSprite;
            img.color = palette.Length > 0 ? palette[Random.Range(0, palette.Length)] : Color.white;
            img.raycastTarget = false;
            img.preserveAspect = true;

            float fall = Random.Range(fallSpeedRange.x, fallSpeedRange.y);
            float drift = Random.Range(-horizontalDrift, horizontalDrift);
            float spin = Random.Range(-spinRange, spinRange);
            float t = 0f;
            while (t < pieceLifetime)
            {
                if (rt == null) yield break;
                t += Time.unscaledDeltaTime;
                rt.anchoredPosition += new Vector2(drift, -fall) * Time.unscaledDeltaTime;
                rt.localRotation = Quaternion.Euler(0f, 0f, rt.localEulerAngles.z + spin * Time.unscaledDeltaTime);
                if (t > pieceLifetime - 0.6f)
                {
                    var c = img.color;
                    c.a = Mathf.Clamp01((pieceLifetime - t) / 0.6f);
                    img.color = c;
                }
                yield return null;
            }
            if (go != null) Destroy(go);
        }
    }
}
