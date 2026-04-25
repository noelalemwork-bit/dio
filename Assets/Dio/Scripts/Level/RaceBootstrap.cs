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
        /// tracker (closest-to-finish proxy until M2 lands the real arc-length tracker).
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
        Camera _cam;

        void Awake() { DioNetworkManager.OnRaceStarted += OnRaceStarted; }
        void OnDestroy() { DioNetworkManager.OnRaceStarted -= OnRaceStarted; }
        void Start() { if (autoStartInEditor) BuildVisualScene(defaultLevel); }

        void OnRaceStarted(bool _, RaceStartMessage __) => BuildVisualScene(defaultLevel);

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
        }

        void EnsurePlanet(float radius)
        {
            if (_planet != null) { CurrentPlanet = _planet; return; }
            _planet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _planet.name = "Planet";
            _planet.transform.position = Vector3.zero;
            _planet.transform.localScale = Vector3.one * radius * 2f;
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
