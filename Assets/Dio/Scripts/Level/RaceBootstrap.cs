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
        }

        // Build invisible-but-collidable guard walls along both edges of the
        // track ribbon. Cars that drift off the track edge bounce off the
        // wall instead of plunging into space. The wall slightly overhangs
        // the surface (skirtDepth = 3m below) so micro-jitter at the
        // ribbon-planet seam can't punch a car through.
        void EnsureGuards(LevelData level)
        {
            if (_guards != null) Destroy(_guards);

            var (left, right) = TrackBuilder.BuildGuards(level, height: 2.5f, outwardOffset: 0.1f, skirtDepth: 3f);
            if (left == null || right == null) return;

            _guards = new GameObject("Guards");
            _guards.transform.SetParent(_planet != null ? _planet.transform.parent : null, true);
            _guards.layer = MinimapLayer; // hide from main minimap-isolate camera

            // Subtle dark stripe — readable but not loud.
            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh) { name = "GuardRail (runtime)" };
            mat.color = new Color(0.20f, 0.18f, 0.22f, 1f);

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
            Debug.Log($"[RaceBootstrap] camera attached to '{target.name}' (local owned car)");
        }
    }
}
