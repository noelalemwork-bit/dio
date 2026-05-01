using UnityEngine;
using Dio.Common;
using Dio.Player;

namespace Dio.CameraRig
{
    /// Three panoramic modes inspired by the references the user called out:
    /// Mario Kart's wide chase, Trackmania's pulled-back cinematic, and a tight
    /// hood-cam for purists. Cycle with Tab (or via
    /// `ArcadeCarController.OnCameraCycleRequested`).
    public enum RaceCameraMode { Chase = 0, Panoramic = 1, Hood = 2 }

    /// Spherical-aware camera with three modes. Cycle with Tab (or wire it via
    /// ArcadeCarController.OnCameraCycleRequested).
    public class RaceCamera : MonoBehaviour
    {
        public Transform target;
        public SphericalGravity gravity; // pulled from target if null
        // Default to the pulled-back Panoramic ("far") mode — best read of
        // the track + planet curvature. Tab cycles through Chase + Hood.
        public RaceCameraMode mode = RaceCameraMode.Panoramic;

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

        [Header("Chase (Mario Kart-style: wide + airy)")]
        public float chaseDistance = 7.5f;
        public float chaseHeight = 3.2f;
        public float chasePosLerp = 8f;
        public float chaseRotLerp = 6f;
        public float fovBase = 72f;
        public float fovAtMaxSpeed = 92f;
        [Tooltip("Look-ahead lead: shift the look target this far along the car's tangent at full speed. Adds the 'racing forward into the world' feel.")]
        public float lookAheadAtMaxSpeed = 6f;
        [Tooltip("Lateral camera roll into corners (degrees at full lock). Subtle MK-style lean.")]
        public float chaseTiltDeg = 7f;
        [Tooltip("How quickly the chase pose lateral-orbits around the car when the car turns (helps the panoramic read).")]
        public float chaseSwingFactor = 1.4f;

        [Header("Panoramic (Trackmania-style: pulled back + elevated)")]
        public float panoramicDistance = 14f;
        public float panoramicHeight = 7f;
        public float panoramicFovBase = 78f;
        public float panoramicFovAtMaxSpeed = 100f;

        [Header("Hood offset (target local space)")]
        // Sits above the windshield + ahead of the front bumper so the car
        // body (chassis ≈ 0.7 m tall, 3.2 m long, pivot at the floor between
        // the wheels) never clips the near plane. Previous values
        // (0, 1.05, 0.4) sat right inside the cabin mesh on Kenney profiles.
        public Vector3 hoodOffset = new Vector3(0f, 1.45f, 1.3f);

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

            float speedT = (car != null)
                ? Mathf.Clamp01(car.SpeedMps / Mathf.Max(0.01f, car.maxSpeed))
                : 0f;

            switch (mode)
            {
                case RaceCameraMode.Chase:
                    ApplyChase(fwd, up, speedT, chaseDistance, chaseHeight, applyTilt: true);
                    LerpFov(Mathf.Lerp(fovBase, fovAtMaxSpeed, speedT));
                    break;
                case RaceCameraMode.Panoramic:
                    ApplyChase(fwd, up, speedT, panoramicDistance, panoramicHeight, applyTilt: true);
                    LerpFov(Mathf.Lerp(panoramicFovBase, panoramicFovAtMaxSpeed, speedT));
                    break;
                case RaceCameraMode.Hood:
                    SnapTo(target.TransformPoint(hoodOffset), Quaternion.LookRotation(fwd, up));
                    LerpFov(Mathf.Lerp(fovBase, fovAtMaxSpeed, speedT));
                    break;
            }
        }

        void LerpFov(float targetFov)
        {
            if (_cam == null) return;
            _cam.fieldOfView = Mathf.Lerp(_cam.fieldOfView, targetFov, 4f * Time.deltaTime);
        }

        // Shared chase logic for the Chase + Panoramic modes. Both pull back
        // along the car's planar forward and lift along the planet's surface
        // normal; Panoramic just uses bigger numbers. The look target is biased
        // forward by `lookAheadAtMaxSpeed * speedT` for that "racing into the
        // distance" feel; the camera rolls slightly into corners (`chaseTiltDeg`)
        // to read as "the car's leaning in" without nausea.
        void ApplyChase(Vector3 fwd, Vector3 up, float speedT,
                        float distance, float height, bool applyTilt)
        {
            Vector3 chaseDesired = target.position - fwd * distance + up * height;

            // Look-ahead bias: aim the camera further down the track at speed
            // so the player sees more of the road and less of the kart.
            Vector3 lookAt = target.position
                           + up * (height * 0.18f)
                           + fwd * (lookAheadAtMaxSpeed * speedT);
            Quaternion chaseDesiredRot = Quaternion.LookRotation(lookAt - chaseDesired, up);

            // Lateral lean. Steering angle is clamped to ±maxSteerAngle, so
            // dividing normalises into [-1..1]. Speed-faded so a stationary
            // wheel-flick doesn't snap-roll the camera.
            if (applyTilt && car != null && car.maxSteerAngle > 0.01f)
            {
                float steerNorm = Mathf.Clamp(car.CurrentSteerAngle / car.maxSteerAngle, -1f, 1f);
                float roll = -steerNorm * chaseTiltDeg * Mathf.Lerp(0.4f, 1f, speedT);
                chaseDesiredRot = chaseDesiredRot * Quaternion.AngleAxis(roll, Vector3.forward);

                // Subtle azimuthal swing: turning right shifts the camera
                // LEFT of the car (so more of the right-side road is visible
                // in the frame). `Cross(fwd, up)` gives the left vector in
                // Unity's left-handed convention; positive steer * left =
                // camera-left = inside-of-turn reveal.
                Vector3 swing = Vector3.Cross(fwd, up) * (steerNorm * chaseSwingFactor);
                chaseDesired += swing;
            }

            if (_introBlend > 0f)
            {
                // Tick the blend down (1 → 0) over _introDuration; cubic-out
                // means we accelerate INTO the chase pose, settling smoothly
                // right at the GO! moment.
                _introBlend = Mathf.Max(0f, _introBlend - Time.deltaTime / _introDuration);
                float t = 1f - _introBlend;
                float blend = DioStyle.EaseOutCubic(t);

                // Cinematic pose: way back along tangent, way up along normal.
                // That's "from above the player, looking down and forward".
                Vector3 cinDesired = target.position
                    - fwd * (distance * introDistanceMul)
                    + up  * (height   * introHeightMul);
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
