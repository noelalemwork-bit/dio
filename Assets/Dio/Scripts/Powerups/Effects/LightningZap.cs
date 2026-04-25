using UnityEngine;

namespace Dio.Powerups.Effects
{
    /// One-shot procedural lightning bolt: a jagged LineRenderer between two
    /// points with brief alpha-flicker. Destroys itself after `lifetime`.
    /// Spawned client-side from PowerupHolder.RpcShowLightning so every peer
    /// sees the same zap from the caster to each target.
    [RequireComponent(typeof(LineRenderer))]
    public class LightningZap : MonoBehaviour
    {
        public float lifetime = 0.55f;
        [Tooltip("How many segments along the bolt; more = jaggier.")]
        public int segments = 14;
        [Tooltip("Max perpendicular offset of each interior point.")]
        public float jitter = 1.2f;

        LineRenderer _lr;
        Vector3 _from, _to;
        float _elapsed;

        public static LightningZap Spawn(Vector3 from, Vector3 to)
        {
            var go = new GameObject("LightningZap");
            var lr = go.AddComponent<LineRenderer>();
            // Use the additive sprites shader so the bolt glows on dark + light.
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
            if (sh != null) lr.material = new Material(sh);
            lr.startWidth = 0.35f; lr.endWidth = 0.18f;
            lr.numCapVertices = 2; lr.numCornerVertices = 2;
            lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            var grad = new Gradient();
            grad.SetKeys(
                new[] {
                    new GradientColorKey(new Color(0.85f, 0.95f, 1.0f), 0f),
                    new GradientColorKey(new Color(0.40f, 0.70f, 1.0f), 0.5f),
                    new GradientColorKey(new Color(0.85f, 0.95f, 1.0f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
            );
            lr.colorGradient = grad;
            var z = go.AddComponent<LightningZap>();
            z._lr = lr;
            z._from = from;
            z._to = to;
            z.RebuildBolt();
            return z;
        }

        void Awake()
        {
            if (_lr == null) _lr = GetComponent<LineRenderer>();
        }

        void Update()
        {
            _elapsed += Time.deltaTime;
            float u = _elapsed / Mathf.Max(0.01f, lifetime);
            if (u >= 1f) { Destroy(gameObject); return; }

            // Re-jitter the path a few times for a "crackle" effect.
            if (Mathf.Repeat(_elapsed * 30f, 1f) < Time.deltaTime * 30f) RebuildBolt();

            // Fade alpha out near the end.
            var c = _lr.colorGradient;
            var alphas = c.alphaKeys;
            float a = Mathf.Clamp01(1f - u);
            alphas[0].alpha = a;
            alphas[1].alpha = a;
            c.alphaKeys = alphas;
            _lr.colorGradient = c;
        }

        void RebuildBolt()
        {
            _lr.positionCount = segments + 1;
            Vector3 axis = (_to - _from);
            Vector3 perp = Vector3.Cross(axis.normalized, Vector3.up);
            if (perp.sqrMagnitude < 1e-3f) perp = Vector3.Cross(axis.normalized, Vector3.right);
            perp.Normalize();
            Vector3 perp2 = Vector3.Cross(axis.normalized, perp);
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                Vector3 basePt = _from + axis * t;
                if (i > 0 && i < segments)
                {
                    float r = Random.Range(-jitter, jitter);
                    float r2 = Random.Range(-jitter * 0.5f, jitter * 0.5f);
                    basePt += perp * r + perp2 * r2;
                }
                _lr.SetPosition(i, basePt);
            }
        }
    }
}
