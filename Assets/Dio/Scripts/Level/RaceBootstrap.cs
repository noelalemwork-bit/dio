using Mirror;
using UnityEngine;
using Dio.Net;
using Dio.Player;
using Dio.CameraRig;

namespace Dio.Level
{
    /// Client/host-side: builds the visible planet + procedural track strip and
    /// attaches the camera to the local player's car when it spawns. The car
    /// itself is server-spawned by DioNetworkManager.
    public class RaceBootstrap : MonoBehaviour
    {
        /// Set whenever any RaceBootstrap finishes EnsurePlanet. SphericalGravity
        /// uses this for lookup so we don't depend on an unregistered "Planet" tag.
        public static GameObject CurrentPlanet { get; private set; }

        /// The active LevelData for the running race. Used by the HUD's position
        /// tracker and by `TrackBuilder.ArcLengthOf` to project car positions
        /// onto the geodesic chain.
        public static LevelData CurrentLevel { get; private set; }

        /// The procedural track-strip GameObject built by EnsureTrack. Kept
        /// public so the minimap renderer can throw it (and the planet) onto a
        /// dedicated layer.
        public static GameObject CurrentTrack { get; private set; }

        /// Layer index reserved for "things visible to the minimap camera" —
        /// the planet and the track strip. Other things (cars, obstacles, UI)
        /// stay off this layer so the minimap stays clean.
        public const int MinimapLayer = 8;

        [Tooltip("Default level used if no race-start message has been received yet.")]
        public LevelData defaultLevel;

        [Tooltip("If true, build the visible scene immediately on Start (for solo testing without networking).")]
        public bool autoStartInEditor = false;

        GameObject _planet;
        GameObject _track;
        GameObject _guards;
        GameObject _spawnPad;
        Camera _cam;

        void Awake()
        {
            DioNetworkManager.OnRaceStarted += OnRaceStarted;
            DioNetworkManager.OnRaceWon     += OnRaceWon;
        }

        void OnDestroy()
        {
            DioNetworkManager.OnRaceStarted -= OnRaceStarted;
            DioNetworkManager.OnRaceWon     -= OnRaceWon;
        }

        void Start() { if (autoStartInEditor) BuildVisualScene(defaultLevel); }

        void OnRaceStarted(bool _, RaceStartMessage __) => BuildVisualScene(defaultLevel);

        void OnRaceWon(RaceWonMessage msg)
        {
            // Schedule local-scene teardown to align with the server's cleanup.
            Invoke(nameof(CleanupLocalScene), Mathf.Max(0.1f, msg.cleanupDelay));
        }

        // Destroys the planet + track + camera attachment. Per-car NetworkIdentities
        // are torn down by NetworkServer.Destroy on the server side, so we don't
        // touch them here.
        public void CleanupLocalScene()
        {
            if (_spawnPad != null) { Destroy(_spawnPad); _spawnPad = null; }
            if (_guards != null) { Destroy(_guards); _guards = null; }
            if (_track != null) { Destroy(_track); _track = null; }
            if (_planet != null) { Destroy(_planet); _planet = null; }
            CurrentPlanet = null;
            CurrentLevel = null;
            CurrentTrack = null;

            // Detach the race camera so it stops following a destroyed car.
            if (_cam != null)
            {
                var rc = _cam.GetComponent<RaceCamera>();
                if (rc != null)
                {
                    rc.target = null;
                    rc.car = null;
                    rc.gravity = null;
                }
            }
        }

        void Update()
        {
            if (_planet == null) return;
            if (_cam != null && _cam.GetComponent<RaceCamera>() != null && _cam.GetComponent<RaceCamera>().target != null) return;

            DioCar local = null;
            if (NetworkClient.localPlayer != null)
            {
                foreach (var ni in NetworkClient.spawned.Values)
                {
                    var car = ni != null ? ni.GetComponent<DioCar>() : null;
                    if (car == null) continue;
                    if (car.isLocalPlayer) { local = car; break; }
                    if (car.isOwned) { local = car; break; }
                }
            }
            if (local != null) AttachCameraTo(local.transform);
        }

        public void BuildVisualScene(LevelData level)
        {
            if (level == null || !level.HasMinimum)
            {
                Debug.LogWarning("[Dio] No valid level to bootstrap. Add at least a start + finish.");
                return;
            }

            Debug.Log($"[RaceBootstrap] BuildVisualScene level='{level.name}' radius={level.planetRadius}");
            CurrentLevel = level;
            EnsurePlanet(level.planetRadius);
            EnsureTrack(level);
            EnsureGuards(level);
            EnsureSpawnPad(level);
        }

        // Short straight ribbon behind the start line + a back wall, so cars
        // initially placed at slot N (for N > 0, rows behind the front row)
        // have actual track under them and can't reverse off the edge of the
        // world. Length scales with the number of starting columns; we pick
        // a comfortable buffer (10m + per-row spacing) regardless of player
        // count, so even solo races get a back wall.
        void EnsureSpawnPad(Dio.Level.LevelData level)
        {
            if (_spawnPad != null) Destroy(_spawnPad);
            const float backBuffer = 12f;
            const float perRowSpacing = 4.5f;
            const int worstCaseRows = 4;       // 8 cars / 3 per row → 3 rows; +1 buffer
            float length = backBuffer + worstCaseRows * perRowSpacing;
            var (ribbon, wall) = TrackBuilder.BuildSpawnPad(level, length);
            if (ribbon == null) return;

            _spawnPad = new GameObject("SpawnPad");
            _spawnPad.transform.SetParent(_planet != null ? _planet.transform.parent : null, true);

            Shader sh = Shader.Find("Standard");
            var asphalt = sh != null ? new Material(sh) { name = "SpawnPad (runtime)" } : null;
            if (asphalt != null) asphalt.color = new Color(0.18f, 0.18f, 0.20f, 1f);

            var ribbonGo = new GameObject("Ribbon", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            ribbonGo.transform.SetParent(_spawnPad.transform, false);
            ribbonGo.layer = MinimapLayer;
            ribbonGo.GetComponent<MeshFilter>().sharedMesh = ribbon;
            if (asphalt != null) ribbonGo.GetComponent<MeshRenderer>().sharedMaterial = asphalt;
            var rmc = ribbonGo.GetComponent<MeshCollider>();
            rmc.sharedMesh = ribbon; rmc.convex = false;

            if (wall != null)
            {
                Shader wsh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                var wmat = new Material(wsh) { name = "SpawnPadWall (runtime)" };
                wmat.color = new Color(0.20f, 0.18f, 0.22f, 1f);

                var wallGo = new GameObject("BackWall", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
                wallGo.transform.SetParent(_spawnPad.transform, false);
                wallGo.layer = MinimapLayer;
                wallGo.GetComponent<MeshFilter>().sharedMesh = wall;
                wallGo.GetComponent<MeshRenderer>().sharedMaterial = wmat;
                var wmc = wallGo.GetComponent<MeshCollider>();
                wmc.sharedMesh = wall; wmc.convex = false;
            }
        }

        // Build invisible-but-collidable guard walls along both edges of the
        // track ribbon. Cars that drift off the track edge bounce off the
        // wall instead of plunging into space. The wall slightly overhangs
        // the surface (skirtDepth = 3m below) so micro-jitter at the
        // ribbon-planet seam can't punch a car through.
        void EnsureGuards(LevelData level)
        {
            if (_guards != null) Destroy(_guards);

            // Z-shape rails. Wall is tall enough to box cars in (≈ 2.2m,
            // visibly above the chassis but not so high that it blocks
            // distant scenery / horizon visible past the rail) with a 0.4m
            // outward lip that catches a car bouncing off the wall before
            // it can pop over the top. 1m skirt covers the seam to the
            // low-poly planet collider.
            var (left, right) = TrackBuilder.BuildGuards(level,
                wallHeight: 2.2f, lipOutward: 0.4f, skirtDepth: 1f, outwardOffset: 0.05f);
            if (left == null || right == null) return;

            _guards = new GameObject("Guards");
            _guards.transform.SetParent(_planet != null ? _planet.transform.parent : null, true);
            _guards.layer = MinimapLayer; // hide from main minimap-isolate camera

            // Subtle dark stripe — readable but not loud. Backface culling
            // turned off so the rails render from both sides (helpful when
            // the camera dips behind a rail during the cinematic intro)
            // and so the rails read as solid walls rather than one-sided
            // strips when seen from outside the track.
            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh) { name = "GuardRail (runtime)" };
            mat.color = new Color(0.20f, 0.18f, 0.22f, 1f);
            // Cull mode 0 = Off in URP/Standard's _Cull property. Set both
            // names so it works whichever shader Unity resolved to.
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", 0f);

            BuildGuardChild(_guards.transform, "GuardLeft", left, mat);
            BuildGuardChild(_guards.transform, "GuardRight", right, mat);
        }

        static void BuildGuardChild(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            go.transform.SetParent(parent, false);
            go.layer = MinimapLayer;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            // Convex would inflate the mesh into a hull (wrong); keep concave
            // and uncheck convex. Concave mesh colliders are static-only,
            // which is fine here — guards never move.
            var mc = go.GetComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = false;
        }

        void EnsurePlanet(float radius)
        {
            if (_planet != null) { CurrentPlanet = _planet; return; }
            // Unity's primitive sphere is a low-poly icosphere — at our 200u
            // radius the silhouette is visibly faceted and the track strip
            // (which follows great circles) drifts above/below it. Build a
            // higher-resolution icosphere so the visible surface lines up
            // with where the track sits and where SphericalGravity expects.
            var mesh = HighResIcoSphere.Build(subdivisions: 4);
            _planet = new GameObject("Planet", typeof(MeshFilter), typeof(MeshRenderer), typeof(SphereCollider));
            _planet.GetComponent<MeshFilter>().sharedMesh = mesh;
            _planet.transform.position = Vector3.zero;
            _planet.transform.localScale = Vector3.one * radius;
            // SphereCollider takes radius from its Radius field times localScale.x,
            // so we scale by `radius` (not radius*2 like the primitive sphere) and
            // leave SphereCollider.radius at 1 — the default.
            _planet.GetComponent<SphereCollider>().radius = 1f;
            _planet.layer = MinimapLayer;

            var rend = _planet.GetComponent<Renderer>();
            if (rend != null)
            {
                var earthShader = Shader.Find("Dio/EarthPlanet");
                if (earthShader != null)
                {
                    var mat = new Material(earthShader) { name = "EarthPlanet (runtime)" };
                    rend.material = mat;
                }
                else
                {
                    rend.material.color = new Color(0.42f, 0.65f, 0.42f, 1f);
                }
            }

            CurrentPlanet = _planet;
        }

        void EnsureTrack(LevelData level)
        {
            if (_track != null) Destroy(_track);

            var mesh = TrackBuilder.Build(level);
            if (mesh == null) return;

            _track = new GameObject("Track", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            _track.layer = MinimapLayer;
            _track.transform.SetParent(_planet != null ? _planet.transform.parent : null, true);
            _track.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mc = _track.GetComponent<MeshCollider>();
            mc.sharedMesh = mesh;

            var rend = _track.GetComponent<MeshRenderer>();
            // Simple asphalt-grey material for now. Could become a Dio/Track shader later.
            var trackShader = Shader.Find("Standard");
            var mat = trackShader != null ? new Material(trackShader) : null;
            if (mat != null)
            {
                mat.name = "TrackAsphalt (runtime)";
                mat.color = new Color(0.18f, 0.18f, 0.20f, 1f);
                rend.sharedMaterial = mat;
            }

            CurrentTrack = _track;
        }

        void AttachCameraTo(Transform target)
        {
            _cam = Camera.main;
            if (_cam == null)
            {
                var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camGo.tag = "MainCamera";
                _cam = camGo.GetComponent<Camera>();
            }
            var rc = _cam.gameObject.GetComponent<RaceCamera>();
            if (rc == null) rc = _cam.gameObject.AddComponent<RaceCamera>();
            rc.target = target;
            rc.car = target.GetComponent<ArcadeCarController>();
            rc.gravity = target.GetComponent<SphericalGravity>();
            rc.mode = RaceCameraMode.Chase;
            // Match the countdown window in DioNetworkManager.ServerStartRace
            // (startServerTime = NetworkTime.time + 4.0). Ease the cinematic
            // pose back to chase exactly as the GO! lands.
            rc.StartCinematicIntro(duration: 4.0f);
            Debug.Log($"[RaceBootstrap] camera attached to '{target.name}' (local owned car)");
        }
    }
}
