using UnityEngine;
using Dio.Player;

namespace Dio.CameraRig
{
    public enum RaceCameraMode { Chase = 0, Bumper = 1, Hood = 2 }

    /// Spherical-aware camera with three modes. Cycle with Tab (or wire it via
    /// ArcadeCarController.OnCameraCycleRequested).
    public class RaceCamera : MonoBehaviour
    {
        public Transform target;
        public ArcadeCarController car; // optional, for the cycle hook + speed FOV
        public SphericalGravity gravity; // pulled from target if null
        public RaceCameraMode mode = RaceCameraMode.Chase;

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

        Camera _cam;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            if (_cam == null) _cam = Camera.main;
            if (target != null && gravity == null) gravity = target.GetComponent<SphericalGravity>();
            if (target != null && car == null) car = target.GetComponent<ArcadeCarController>();
            if (car != null) car.OnCameraCycleRequested += Cycle;
        }

        void OnDestroy()
        {
            if (car != null) car.OnCameraCycleRequested -= Cycle;
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
            Vector3 desired = target.position - fwd * chaseDistance + up * chaseHeight;
            Quaternion desiredRot = Quaternion.LookRotation((target.position + up * 0.5f) - desired, up);
            transform.position = Vector3.Lerp(transform.position, desired, chasePosLerp * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, chaseRotLerp * Time.deltaTime);
        }

        void SnapTo(Vector3 pos, Quaternion rot)
        {
            transform.position = pos;
            transform.rotation = rot;
        }
    }
}
