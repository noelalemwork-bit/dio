using Mirror;
using UnityEngine;
using Dio.Net;
using Dio.Player;
using Dio.CameraRig;
using Dio.Common;

namespace Dio.Level
{
    /// Live-builds a placeholder race scene around a sphere planet, spawns the
    /// local car at the level's start point oriented along the geodesic toward
    /// the next point, and attaches the camera. Hooks DioNetworkManager.OnRaceStarted.
    ///
    /// This is the "demo" path — no procedural track mesh yet, just a visualised
    /// geodesic + a car you can drive on the sphere.
    public class RaceBootstrap : MonoBehaviour
    {
        [Tooltip("Default level used if none is loaded by the network message.")]
        public LevelData defaultLevel;

        [Tooltip("If true, build the race scene immediately on Start (for editor solo testing without going through the menu).")]
        public bool autoStartInEditor = false;

        GameObject _planet;
        GameObject _car;
        Camera _cam;
        LineRenderer _trackLine;

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
            if (autoStartInEditor) BuildAndSpawn(defaultLevel);
        }

        void OnRaceStarted(bool _) => BuildAndSpawn(defaultLevel);

        public void BuildAndSpawn(LevelData level)
        {
            if (level == null || !level.HasMinimum)
            {
                Debug.LogWarning("[Dio] No valid level to bootstrap. Add at least a start + finish.");
                return;
            }

            EnsurePlanet(level.planetRadius);
            EnsureTrackLine(level);

            // Spawn the car at the start oriented along the geodesic toward next point.
            Vector3 startDir = level.points[0].directionFromCenter.normalized;
            Vector3 nextDir  = level.points[1].directionFromCenter.normalized;
            Vector3 startPos = startDir * level.planetRadius + startDir * 1.5f; // slightly above surface
            Vector3 forward  = GeodesicUtil.TangentAt(startDir, nextDir);
            Vector3 up = startDir;

            int colorIdx = LocalPrefs.ColorIndex;
            _car = SpawnBlockoutCar(startPos, Quaternion.LookRotation(forward, up), ColorPalette.Get(colorIdx));

            // Camera.
            EnsureCamera(_car.transform);
        }

        void EnsurePlanet(float radius)
        {
            if (_planet != null) return;
            _planet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _planet.name = "Planet";
            _planet.tag = "Planet";
            _planet.transform.position = Vector3.zero;
            _planet.transform.localScale = Vector3.one * radius * 2f;
            // Use a SphereCollider (cheap, exact) — uniform scale keeps it correct.
            var rend = _planet.GetComponent<Renderer>();
            if (rend != null) rend.material.color = new Color(0.42f, 0.65f, 0.42f, 1f); // grass-ish
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
                if (i > 0 && pts.Count > 0) pts.RemoveAt(pts.Count - 1); // avoid duplicate join
                pts.AddRange(arc);
            }
            _trackLine.positionCount = pts.Count;
            _trackLine.SetPositions(pts.ToArray());
        }

        GameObject SpawnBlockoutCar(Vector3 pos, Quaternion rot, Color bodyColor)
        {
            var root = new GameObject("PlayerCar");
            root.transform.SetPositionAndRotation(pos, rot);
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 800f;
            rb.linearDamping = 0.1f;
            rb.angularDamping = 0.1f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Body visuals.
            CarBlockoutBuilder.Build(root.transform, bodyColor);

            // Wheels.
            var fl = CarBlockoutBuilder.BuildWheel(root.transform, new Vector3(-0.8f, 0.4f,  1.3f), "FL");
            var fr = CarBlockoutBuilder.BuildWheel(root.transform, new Vector3( 0.8f, 0.4f,  1.3f), "FR");
            var rl = CarBlockoutBuilder.BuildWheel(root.transform, new Vector3(-0.8f, 0.4f, -1.3f), "RL");
            var rr = CarBlockoutBuilder.BuildWheel(root.transform, new Vector3( 0.8f, 0.4f, -1.3f), "RR");

            // Gravity + controller.
            var grav = root.AddComponent<SphericalGravity>();
            grav.planet = _planet != null ? _planet.transform : null;

            var ctrl = root.AddComponent<ArcadeCarController>();
            ctrl.frontAxle = new ArcadeCarController.Axle
            {
                left = fl.collider, right = fr.collider,
                leftVisual = fl.visual, rightVisual = fr.visual,
                steers = true, drives = false, brakes = true
            };
            ctrl.rearAxle = new ArcadeCarController.Axle
            {
                left = rl.collider, right = rr.collider,
                leftVisual = rl.visual, rightVisual = rr.visual,
                steers = false, drives = true, brakes = true
            };
            return root;
        }

        void EnsureCamera(Transform target)
        {
            // Reuse main camera if present, otherwise build one.
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
