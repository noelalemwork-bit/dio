using UnityEngine;
using Dio.Common;
using Dio.Player;

namespace Dio.CameraRig
{
    public enum RaceCameraMode { Chase = 0, Bumper = 1, Hood = 2 }

    /// Spherical-aware camera with three modes. Cycle with Tab (or wire it via
    /// ArcadeCarController.OnCameraCycleRequested).
    public class RaceCamera : MonoBehaviour
    {
        public Transform target;
        public SphericalGravity gravity; // pulled from target if null
        public RaceCameraMode mode = RaceCameraMode.Chase;

        // Backed property so we re-subscribe on assignment. Awake runs the moment
        // a component is added, which is BEFORE the assigner has a chance to set
        // these refs — so the subscription has to live in the setter, not Awake.
        [SerializeField] ArcadeCarController _car;
        public ArcadeCarController car
        {
            get => _car;
            set
            {
                if (_car != null) _car.OnCameraCycleRequested -= Cycle;
                _car = value;
                if (_car != null) _car.OnCameraCycleRequested += Cycle;
            }
        }

        [Header("Chase")]
        public float chaseDistance = 5.5f;
        public float chaseHeight = 2.0f;
        public float chasePosLerp = 8f;
        public float chaseRotLerp = 6f;
        public float fovBase = 60f;
        public float fovAtMaxSpeed = 75f;

        [Header("Bumper / Hood offsets (target local space)")]
        public Vector3 bumperOffset = new Vector3(0f, 0.6f, 1.3f);
        public Vector3 hoodOffset = new Vector3(0f, 1.05f, 0.4f);

        [Header("Cinematic intro")]
        // Multipliers applied to chaseDistance / chaseHeight at the START of
        // the intro. Higher = further out + higher up = more dramatic swoop.
        public float introDistanceMul = 4f;
        public float introHeightMul = 5f;
        // 0 = at chase pose. 1 = at cinematic pose. Drives a cubic-ease blend
        // back down to the chase pose over `_introDuration` seconds.
        float _introBlend = 0f;
        float _introDuration = 0f;

        /// Place the camera at a high, swoopy offset above + behind the
        /// player and ease back to the regular chase pose over `duration`
        /// seconds. Called by RaceBootstrap right after camera attach so
        /// the countdown UI plays over a moving 3-quarter establishing shot.
        public void StartCinematicIntro(float duration)
        {
            _introDuration = Mathf.Max(0.01f, duration);
            _introBlend = 1f;
        }

        Camera _cam;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            if (_cam == null) _cam = Camera.main;
            if (target != null && gravity == null) gravity = target.GetComponent<SphericalGravity>();
            if (target != null && _car == null) car = target.GetComponent<ArcadeCarController>();
            // Subscription happens via the property setter above.
        }

        void OnDestroy()
        {
            if (_car != null) _car.OnCameraCycleRequested -= Cycle;
        }

        public void Cycle()
        {
            mode = (RaceCameraMode)(((int)mode + 1) % 3);
        }

        void LateUpdate()
        {
            if (target == null) return;
            Vector3 up = gravity != null ? gravity.PlanetUp : target.up;
            Vector3 fwd = Vector3.ProjectOnPlane(target.forward, up).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = target.forward;

            switch (mode)
            {
                case RaceCameraMode.Chase: ApplyChase(fwd, up); break;
                case RaceCameraMode.Bumper: SnapTo(target.TransformPoint(bumperOffset), Quaternion.LookRotation(fwd, up)); break;
                case RaceCameraMode.Hood: SnapTo(target.TransformPoint(hoodOffset), Quaternion.LookRotation(fwd, up)); break;
            }

            if (_cam != null && car != null)
            {
                float t = Mathf.Clamp01(car.SpeedMps / Mathf.Max(0.01f, car.maxSpeed));
                _cam.fieldOfView = Mathf.Lerp(_cam.fieldOfView, Mathf.Lerp(fovBase, fovAtMaxSpeed, t), 4f * Time.deltaTime);
            }
        }

        void ApplyChase(Vector3 fwd, Vector3 up)
        {
            Vector3 chaseDesired = target.position - fwd * chaseDistance + up * chaseHeight;
            Quaternion chaseDesiredRot = Quaternion.LookRotation((target.position + up * 0.5f) - chaseDesired, up);

            if (_introBlend > 0f)
            {
                // Tick the blend down (1 → 0) over _introDuration; cubic-out
                // means we accelerate INTO the chase pose, settling smoothly
                // right at the GO! moment.
                _introBlend = Mathf.Max(0f, _introBlend - Time.deltaTime / _introDuration);
                float t = 1f - _introBlend;                    // 0 at start, 1 at end
                float blend = DioStyle.EaseOutCubic(t);

                // Cinematic pose: way back along tangent, way up along normal.
                // That's "from above the player, looking down and forward" —
                // exactly what the countdown wants behind it.
                Vector3 cinDesired = target.position
                    - fwd * (chaseDistance * introDistanceMul)
                    + up  * (chaseHeight   * introHeightMul);
                Quaternion cinDesiredRot = Quaternion.LookRotation(
                    (target.position + up * 0.5f) - cinDesired, up);

                Vector3 pos = Vector3.Lerp(cinDesired, chaseDesired, blend);
                Quaternion rot = Quaternion.Slerp(cinDesiredRot, chaseDesiredRot, blend);
                transform.position = pos;
                transform.rotation = rot;
                return;
            }

            transform.position = Vector3.Lerp(transform.position, chaseDesired, chasePosLerp * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, chaseDesiredRot, chaseRotLerp * Time.deltaTime);
        }

        void SnapTo(Vector3 pos, Quaternion rot)
        {
            transform.position = pos;
            transform.rotation = rot;
        }
    }
}
