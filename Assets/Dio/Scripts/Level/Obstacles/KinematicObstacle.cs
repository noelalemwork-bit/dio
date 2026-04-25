using Mirror;
using UnityEngine;
using Dio.Player;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Tornado: spat forward by the caster. Travels along the planet surface,
    /// spins visibly, and despawns after `lifetime`. The visible mesh is
    /// procedurally generated — a cone-with-noise that's narrow at the bottom
    /// and widest at the top, with a sin-wave radius offset that gives the
    /// tornado its uneven funnel shape. Vertex colors lerp white→grey based
    /// on the local wave magnitude so peaks read as bright and troughs darken.
    ///
    /// Open at top + bottom (no caps) — looks more like a vortex that fades
    /// into the sky / ground than a sealed cone.
    public class TornadoObstacle : Obstacle
    {
        [Header("Travel")]
        public Transform planet;
        public float planetRadius = 200f;
        [Tooltip("Forward travel speed along the surface (m/s).")]
        public float forwardSpeed = 16f;

        [Header("Spin")]
        [Tooltip("Visible spin around the surface-up axis (deg/sec). Local-only animation.")]
        public float spinDegPerSec = 540f;

        [Header("Funnel mesh")]
        public float height = 2f;
        public float maxRadius = 0.4f;
        [Tooltip("Vertical subdivisions along the funnel.")]
        public int rings = 24;
        [Tooltip("Angular subdivisions around the funnel.")]
        public int segments = 18;
        public float waveAmp = 0.12f;
        public float waveFreqY = 20f;
        public float waveFreqPhi = 3f;

        [Header("Effect")]
        public float tumbleDuration = 1.2f;

        // Kept for save-file compatibility but unused after the orbit removal.
        [SyncVar] public Vector3 circleCenterDir;
        [SyncVar] public Vector3 tangentBasis;
        [SyncVar] public Vector3 binormalBasis;
        [SyncVar] public double startServerTime;

        Transform _meshRoot;

        public override void OnStartServer()
        {
            base.OnStartServer();
            startServerTime = NetworkTime.time;
            // Snap to surface in case the spawning effect placed us slightly
            // above or below. NetworkTransform syncs the corrected position.
            SnapToSurface();
            BuildMeshIfNeeded();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            BuildMeshIfNeeded();
        }

        void Update()
        {
            // Local visible spin (every peer). Spin the MESH child, not the
            // root — the root's rotation must stay aligned with the surface
            // normal for forward travel to work.
            if (_meshRoot != null)
            {
                _meshRoot.localRotation *= Quaternion.Euler(0f, spinDegPerSec * Time.deltaTime, 0f);
            }

            if (!isServer) return;

            // Server: glide forward along the tangent plane, re-snap to the
            // surface every tick. NetworkTransform broadcasts the new pose.
            Vector3 planetCenter = planet != null ? planet.position : Vector3.zero;
            Vector3 dir = (transform.position - planetCenter).normalized;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, dir).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.Cross(dir, Vector3.right).normalized;

            transform.position += fwd * forwardSpeed * Time.deltaTime;
            // Re-snap radial.
            dir = (transform.position - planetCenter).normalized;
            transform.position = planetCenter + dir * planetRadius;
            // Re-orient: forward in tangent plane, up = surface normal.
            Vector3 fwdT = Vector3.ProjectOnPlane(transform.forward, dir).normalized;
            if (fwdT.sqrMagnitude < 1e-4f) fwdT = fwd;
            transform.rotation = Quaternion.LookRotation(fwdT, dir);
        }

        void SnapToSurface()
        {
            Vector3 planetCenter = planet != null ? planet.position : Vector3.zero;
            Vector3 dir = (transform.position - planetCenter).normalized;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.up;
            transform.position = planetCenter + dir * planetRadius;
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, dir).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.Cross(dir, Vector3.right).normalized;
            transform.rotation = Quaternion.LookRotation(fwd, dir);
        }

        // ---- Procedural funnel mesh ----

        void BuildMeshIfNeeded()
        {
            if (_meshRoot != null) return;

            var mesh = BuildFunnelMesh();
            var go = new GameObject("TornadoMesh");
            go.transform.SetParent(transform, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // Build runtime material instance — vertex colors carry the
            // brightness gradient; the shader just multiplies by a tint.
            Shader sh = Shader.Find("Dio/Tornado") ?? Shader.Find("Sprites/Default");
            if (sh != null)
            {
                var mat = new Material(sh) { name = "TornadoRuntime" };
                if (mat.HasProperty("_Tint")) mat.SetColor("_Tint", new Color(0.85f, 0.92f, 1f, 0.55f));
                mr.material = mat;
            }
            _meshRoot = go.transform;
        }

        Mesh BuildFunnelMesh()
        {
            int ringCount = Mathf.Max(2, rings);
            int segCount  = Mathf.Max(3, segments);

            int vCount = (ringCount + 1) * (segCount + 1);
            var verts  = new Vector3[vCount];
            var colors = new Color[vCount];
            var uvs    = new Vector2[vCount];
            var tris   = new int[ringCount * segCount * 6];

            for (int ry = 0; ry <= ringCount; ry++)
            {
                float t = (float)ry / ringCount;        // 0 at bottom, 1 at top
                float yPos = t * height;
                float baseR = t * maxRadius;            // narrow at bottom, wide at top
                for (int s = 0; s <= segCount; s++)
                {
                    float phi = (float)s / segCount * Mathf.PI * 2f;
                    // Wavy radius offset — each ring shifts based on a sin
                    // sampled in y AND phi. Result is a turbulent funnel.
                    float wave = Mathf.Sin(t * waveFreqY + Mathf.Sin(phi) * waveFreqPhi) * waveAmp;
                    float r = baseR + wave;
                    int idx = ry * (segCount + 1) + s;
                    verts[idx] = new Vector3(Mathf.Cos(phi) * r, yPos, Mathf.Sin(phi) * r);
                    uvs[idx]   = new Vector2((float)s / segCount, t);

                    // Color: dark grey ↔ white based on wave magnitude.
                    // wave ranges in roughly [-waveAmp, +waveAmp]; remap to [0,1].
                    float waveT = (wave / Mathf.Max(0.001f, waveAmp) + 1f) * 0.5f;
                    Color baseCol = Color.Lerp(new Color(0.42f, 0.46f, 0.52f),
                                               new Color(0.95f, 0.97f, 1.00f),
                                               waveT);
                    // Fade alpha at the very top / bottom so the funnel
                    // dissolves into nothing rather than ending in a hard ring.
                    float alpha = 1f;
                    if (t < 0.10f) alpha = Mathf.SmoothStep(0f, 1f, t / 0.10f);
                    if (t > 0.90f) alpha = Mathf.SmoothStep(1f, 0f, (t - 0.90f) / 0.10f);
                    baseCol.a = alpha;
                    colors[idx] = baseCol;
                }
            }

            int ti = 0;
            for (int ry = 0; ry < ringCount; ry++)
            {
                for (int s = 0; s < segCount; s++)
                {
                    int a = ry * (segCount + 1) + s;
                    int b = a + 1;
                    int c = a + (segCount + 1);
                    int d = c + 1;
                    // CCW from outside; tornado is two-sided (Cull Off in shader)
                    // so winding doesn't matter visually.
                    tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                    tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
                }
            }

            var mesh = new Mesh
            {
                name = "TornadoFunnel",
                indexFormat = vCount > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        void OnTriggerEnter(Collider other)
        {
            if (!isServer) return;
            var car = CarOf(other);
            if (car == null) return;
            if (IsOwnerImmune(car)) return;
            ApplyState(car, new TumbleState(), tumbleDuration);
        }
    }
}
