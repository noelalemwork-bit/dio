using Mirror;
using UnityEngine;
using Dio.Net;
using Dio.Player;
using Dio.CameraRig;

namespace Dio.Level
{
    /// Client/host-side: builds the visible planet + geodesic preview line and
    /// attaches the camera to the local player's car when it spawns. The car
    /// itself is server-spawned by DioNetworkManager.
    public class RaceBootstrap : MonoBehaviour
    {
        /// Set whenever any RaceBootstrap finishes EnsurePlanet. SphericalGravity
        /// uses this for lookup so we don't depend on an unregistered "Planet" tag.
        public static GameObject CurrentPlanet { get; private set; }

        [Tooltip("Default level used if no race-start message has been received yet.")]
        public LevelData defaultLevel;

        [Tooltip("If true, build the visible scene immediately on Start (for solo testing without networking).")]
        public bool autoStartInEditor = false;

        GameObject _planet;
        LineRenderer _trackLine;
        Camera _cam;

        void Awake()
        {
            DioNetworkManager.OnRaceStarted += OnRaceStarted;
        }

        void OnDestroy()
        {
            DioNetworkManager.OnRaceStarted -= OnRaceStarted;
        }

        void Start()
        {
            if (autoStartInEditor) BuildVisualScene(defaultLevel);
        }

        void OnRaceStarted(bool _, RaceStartMessage __) => BuildVisualScene(defaultLevel);

        void Update()
        {
            // Once the local player's car appears, attach the camera.
            if (_planet == null) return;
            if (_cam != null && _cam.GetComponent<RaceCamera>() != null && _cam.GetComponent<RaceCamera>().target != null) return;

            DioCar local = null;
            if (NetworkClient.localPlayer != null)
            {
                // The local "player" identity is the lobby DioPlayer; the car has its own NetworkIdentity owned by us.
                foreach (var ni in NetworkClient.spawned.Values)
                {
                    var car = ni != null ? ni.GetComponent<DioCar>() : null;
                    if (car != null && car.isLocalPlayer) { local = car; break; }
                    // Fallback: server-side host where isLocalPlayer is on the DioPlayer, not the car.
                    if (car != null && car.isOwned) { local = car; break; }
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

            EnsurePlanet(level.planetRadius);
            EnsureTrackLine(level);
        }

        void EnsurePlanet(float radius)
        {
            if (_planet != null) { CurrentPlanet = _planet; return; }
            _planet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _planet.name = "Planet";
            // No tag — we don't depend on Unity's TagManager. Lookup goes via CurrentPlanet.
            _planet.transform.position = Vector3.zero;
            _planet.transform.localScale = Vector3.one * radius * 2f;
            var rend = _planet.GetComponent<Renderer>();
            if (rend != null) rend.material.color = new Color(0.42f, 0.65f, 0.42f, 1f);
            CurrentPlanet = _planet;
        }

        void EnsureTrackLine(LevelData level)
        {
            if (_trackLine != null) Destroy(_trackLine.gameObject);

            var go = new GameObject("TrackPreview");
            go.transform.SetParent(_planet != null ? _planet.transform : null, true);
            _trackLine = go.AddComponent<LineRenderer>();
            _trackLine.useWorldSpace = true;
            _trackLine.widthMultiplier = 0.6f;
            _trackLine.material = new Material(Shader.Find("Sprites/Default"));
            _trackLine.startColor = new Color(0.30f, 0.65f, 0.95f, 1f);
            _trackLine.endColor = _trackLine.startColor;

            const int segPerArc = 64;
            var pts = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < level.points.Count - 1; i++)
            {
                Vector3 a = level.points[i].directionFromCenter.normalized;
                Vector3 b = level.points[i + 1].directionFromCenter.normalized;
                Vector3[] arc = GeodesicUtil.Geodesic(a, b, level.planetRadius + 0.05f, segPerArc);
                if (i > 0 && pts.Count > 0) pts.RemoveAt(pts.Count - 1);
                pts.AddRange(arc);
            }
            _trackLine.positionCount = pts.Count;
            _trackLine.SetPositions(pts.ToArray());
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
        }
    }
}
