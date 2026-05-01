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

        // ----- Track-walking state (server-only) -----
        // The tornado walks the level's adjacency graph one edge at a time so
        // it always rides the racing line (including branches). At each
        // anchor it picks an outgoing edge at random; when it reaches the
        // finish point it despawns gracefully — no off-track drift, no
        // ghosting past the end of the level.
        int   _edgeFrom = -1;
        int   _edgeTo   = -1;
        float _edgeT;        // [0..1] along the current bezier edge
        bool  _walkInit;
        // Lateral oscillation: a sin wave perpendicular to the tangent so the
        // tornado side-steps across the track width as it advances.
        [Header("Lateral oscillation")]
        public float lateralAmplitude = 2.5f;   // metres off the centerline at the peaks
        public float lateralPeriod    = 2.4f;   // seconds per full sine cycle
        float _walkClock;

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

            var lvl = Dio.Net.DioNetworkManager.Instance != null
                ? Dio.Net.DioNetworkManager.Instance.ActiveLevel : null;
            Vector3 planetCenter = planet != null ? planet.position : Vector3.zero;

            if (lvl != null && lvl.HasMinimum)
            {
                lvl.EnsureLinearChainNext();
                if (!_walkInit) InitWalkFromCurrentPosition(lvl, planetCenter);

                _walkClock += Time.deltaTime;

                // Step forward by forwardSpeed arc-units, but in t-space we
                // need to divide by THIS edge's arc length so a long edge
                // takes proportionally longer. Approximate with the chord
                // (anchor-to-anchor great-circle arc) — close enough for the
                // tornado's purposes.
                float edgeArcLen = ApproxEdgeArcLength(lvl, _edgeFrom, _edgeTo);
                if (edgeArcLen < 0.01f) edgeArcLen = 1f;
                _edgeT += forwardSpeed * Time.deltaTime / edgeArcLen;

                // Advance through anchors when we cross the end of an edge.
                while (_edgeT >= 1f && isServer)
                {
                    int nextAnchor = _edgeTo;
                    int finishIdx = lvl.points.Count - 1;
                    if (nextAnchor == finishIdx)
                    {
                        Debug.Log("[Tornado] reached finish anchor — despawning gracefully.");
                        ServerDespawn();
                        return;
                    }
                    var outNexts = lvl.points[nextAnchor].next;
                    if (outNexts == null || outNexts.Count == 0)
                    {
                        Debug.Log("[Tornado] dead-end anchor — despawning.");
                        ServerDespawn();
                        return;
                    }
                    int picked = outNexts.Count == 1 ? outNexts[0]
                        : outNexts[Random.Range(0, outNexts.Count)]; // branch: random
                    _edgeFrom = nextAnchor;
                    _edgeTo   = picked;
                    _edgeT    = _edgeT - 1f;
                    edgeArcLen = ApproxEdgeArcLength(lvl, _edgeFrom, _edgeTo);
                    if (edgeArcLen < 0.01f) edgeArcLen = 1f;
                }

                // Sample the current edge at t. SphericalBezier returns a unit
                // direction; resolve the actual surface radius in that
                // direction (so the funnel hugs hills + valleys instead of
                // floating at constant `planetRadius`), then add the lateral
                // oscillation in the tangent plane.
                lvl.GetEdge(_edgeFrom, _edgeTo, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                const float DerivEps = 0.001f;
                Vector3 dir       = Dio.Level.GeodesicUtil.SphericalBezier(a, ah, bh, b, _edgeT);
                Vector3 dirAhead  = Dio.Level.GeodesicUtil.SphericalBezier(a, ah, bh, b, Mathf.Min(1f, _edgeT + DerivEps));
                var raycaster     = Dio.Level.RaceBootstrap.CurrentRaycaster;
                float surfaceR    = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : planetRadius;
                Vector3 newPos    = planetCenter + dir * surfaceR;
                Vector3 surfaceUp = (newPos - planetCenter).normalized;
                Vector3 newTan    = Vector3.ProjectOnPlane((dirAhead - dir), surfaceUp).normalized;
                if (newTan.sqrMagnitude < 1e-6f) newTan = transform.forward;

                // Lateral wobble: oscillate side-to-side along the tangent-plane
                // RIGHT vector. Amplitude is fixed in metres so it scales with
                // track width naturally.
                Vector3 right = Vector3.Cross(surfaceUp, newTan).normalized;
                float wobble = Mathf.Sin(_walkClock * (Mathf.PI * 2f / Mathf.Max(0.01f, lateralPeriod))) * lateralAmplitude;
                newPos += right * wobble;

                transform.position = newPos;
                transform.rotation = Quaternion.LookRotation(newTan, surfaceUp);
                return;
            }

            // Fallback: no level resolvable. Glide along the local tangent
            // plane so the tornado is at least kinetic instead of pinned.
            // Hug the actual surface via the raycaster — falling back to
            // `planetRadius` only when no raycaster is available.
            Vector3 dirf = (transform.position - planetCenter).normalized;
            if (dirf.sqrMagnitude < 1e-4f) dirf = Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, dirf).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.Cross(dirf, Vector3.right).normalized;
            transform.position += fwd * forwardSpeed * Time.deltaTime;
            dirf = (transform.position - planetCenter).normalized;
            var rc = Dio.Level.RaceBootstrap.CurrentRaycaster;
            float rfall = rc != null ? rc.ResolveSurfaceRadius(dirf) : planetRadius;
            transform.position = planetCenter + dirf * rfall;
            Vector3 fwdT2 = Vector3.ProjectOnPlane(transform.forward, dirf).normalized;
            if (fwdT2.sqrMagnitude < 1e-4f) fwdT2 = fwd;
            transform.rotation = Quaternion.LookRotation(fwdT2, dirf);
        }

        // Pick the anchor closest to the tornado's current world position as
        // `from`, then `to = first outgoing edge`. Stays consistent across
        // host vs guest because we route everything through the LevelData
        // struct (server-authoritative) rather than per-peer state.
        void InitWalkFromCurrentPosition(Dio.Level.LevelData lvl, Vector3 planetCenter)
        {
            Vector3 ourDir = (transform.position - planetCenter).normalized;
            int bestIdx = 0;
            float bestDot = float.NegativeInfinity;
            for (int i = 0; i < lvl.points.Count; i++)
            {
                float d = Vector3.Dot(ourDir, lvl.DirOf(i));
                if (d > bestDot) { bestDot = d; bestIdx = i; }
            }
            int finishIdx = lvl.points.Count - 1;
            // If we land on the finish anchor at spawn, walk backwards one
            // anchor so we have somewhere to GO before despawning.
            if (bestIdx == finishIdx) bestIdx = Mathf.Max(0, bestIdx - 1);

            var outNexts = lvl.points[bestIdx].next;
            int toIdx = (outNexts != null && outNexts.Count > 0)
                ? outNexts[Random.Range(0, outNexts.Count)]
                : Mathf.Min(finishIdx, bestIdx + 1);

            _edgeFrom  = bestIdx;
            _edgeTo    = toIdx;
            _edgeT     = 0f;
            _walkInit  = true;
        }

        static float ApproxEdgeArcLength(Dio.Level.LevelData lvl, int from, int to)
        {
            Vector3 a = lvl.DirOf(from);
            Vector3 b = lvl.DirOf(to);
            float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f));
            return angle * lvl.NominalRadius;
        }

        void SnapToSurface()
        {
            Vector3 planetCenter = planet != null ? planet.position : Vector3.zero;
            Vector3 toSpawn = transform.position - planetCenter;
            float spawnRadius = toSpawn.magnitude;
            Vector3 dir = spawnRadius > 1e-4f ? toSpawn / spawnRadius : Vector3.up;
            // Use the SPAWN POSITION's actual radius — same approach Banana
            // uses for its settle. The previous version forced `planetRadius`
            // which is the level's nominal radius (circumference / 2π), but
            // the world.fbx surface varies by direction (mountains / valleys),
            // so the constant-radius snap was placing the funnel "in the
            // sky" over low terrain. Preserving the spawn radius matches
            // wherever the caster was standing, which is on the actual
            // surface by definition.
            if (spawnRadius < 1f) spawnRadius = planetRadius;
            transform.position = planetCenter + dir * spawnRadius;
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
            // This is the exact path that worked in commit 5f9d565 — the
            // earlier "robustness" pass that set _Surface/_Blend/_Cull
            // forced URP/Unlit-specific properties that fought with the
            // legacy CG SubShader, leaving the funnel invisible. Letting
            // the shader own its own surface settings is what actually works.
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
            RpcSpawnImpactBurst(car.transform.position, new Color(0.78f, 0.88f, 1f, 1f), 1.0f);

            // Tornado-specific impact: a SWIRL around the surface-up axis (so
            // the car genuinely spins as if grabbed by the funnel) plus a big
            // upward suck. ApplyClientImpact's angular axis is horizontal
            // (cross(up, away)) which produces a flip — we want yaw, not flip,
            // so we apply yaw directly via TargetApplyImpact.
            Vector3 planetCenter = Dio.Level.RaceBootstrap.CurrentPlanet != null
                ? Dio.Level.RaceBootstrap.CurrentPlanet.transform.position : Vector3.zero;
            Vector3 up = (car.transform.position - planetCenter).normalized;
            if (up.sqrMagnitude < 0.0001f) up = car.transform.up;
            Vector3 vel = up * 6f;                 // upward suck
            Vector3 ang = up * 22f;                // strong yaw spin
            if (car.connectionToClient != null)
            {
                car.TargetApplyImpact(car.connectionToClient, vel, ang);
                car.TargetFlashScreen(car.connectionToClient, Dio.Powerups.PowerupKind.Tornado, tumbleDuration);
            }
            ApplyState(car, new TornadoTumbleState(), tumbleDuration);
        }
    }
}
