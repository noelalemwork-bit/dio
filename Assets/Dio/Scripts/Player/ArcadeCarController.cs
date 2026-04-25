using UnityEngine;
using UnityEngine.InputSystem;

namespace Dio.Player
{
    /// Arcade-flavoured WheelCollider car. Tuned for stability + punchy launch:
    /// - High lateral stiffness so we don't spin out.
    /// - Steer angle scales down with speed.
    /// - Counter-spin damping around local up.
    /// - Air control torques when wheels are off the ground.
    [RequireComponent(typeof(Rigidbody))]
    public class ArcadeCarController : MonoBehaviour
    {
        [System.Serializable]
        public class Axle
        {
            public WheelCollider left;
            public WheelCollider right;
            public Transform leftVisual;
            public Transform rightVisual;
            public bool steers;
            public bool drives;
            public bool brakes;
        }

        [Header("Wheels")]
        public Axle frontAxle = new Axle { steers = true, drives = false, brakes = true };
        public Axle rearAxle  = new Axle { steers = false, drives = true,  brakes = true };

        [Header("Drive")]
        [Tooltip("Peak motor torque at zero speed (Nm). Falls off linearly to maxSpeed.")]
        public float peakMotorTorque = 1500f;
        public float maxSpeed = 35f;             // m/s ~ 126 kph
        public float brakeTorque = 3000f;
        public float handbrakeTorque = 5000f;

        [Header("Steering")]
        public float maxSteerAngle = 32f;
        [Tooltip("At this speed (m/s) the steer angle is halved.")]
        public float steerHalfSpeed = 18f;
        public float steerSmooth = 8f;

        [Header("Arcade tuning")]
        [Tooltip("Damp angular velocity around local up when it grows large compared to forward speed.")]
        public float counterSpinDamping = 4f;
        public float airPitchTorque = 8f;
        public float airYawTorque = 12f;

        [Header("Input (assignable; otherwise falls back to keyboard)")]
        public InputActionReference moveAction;
        public InputActionReference brakeAction;
        public InputActionReference handbrakeAction;
        public InputActionReference cameraCycleAction;

        // public read-out for camera + UI
        public float SpeedMps { get; private set; }
        public bool AnyWheelGrounded { get; private set; }
        public System.Action OnCameraCycleRequested;

        Rigidbody _rb;
        SphericalGravity _gravity;
        float _currentSteer;
        System.Action<InputAction.CallbackContext> _cycleHandler;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _gravity = GetComponent<SphericalGravity>();

            // Lower the centre of mass so we don't tip over on hard turns.
            _rb.centerOfMass = new Vector3(0f, -0.4f, 0f);

            if (moveAction != null) moveAction.action.Enable();
            if (brakeAction != null) brakeAction.action.Enable();
            if (handbrakeAction != null) handbrakeAction.action.Enable();
            if (cameraCycleAction != null)
            {
                cameraCycleAction.action.Enable();
                _cycleHandler = _ => OnCameraCycleRequested?.Invoke();
                cameraCycleAction.action.performed += _cycleHandler;
            }
        }

        void OnDisable()
        {
            if (cameraCycleAction != null && _cycleHandler != null)
                cameraCycleAction.action.performed -= _cycleHandler;
        }

        void Update()
        {
            // Tab to cycle camera if no input action is bound.
            if (cameraCycleAction == null && Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
                OnCameraCycleRequested?.Invoke();
        }

        Vector2 ReadMove()
        {
            if (moveAction != null && moveAction.action.enabled)
                return moveAction.action.ReadValue<Vector2>();

            // Keyboard fallback.
            var kb = Keyboard.current;
            if (kb == null) return Vector2.zero;
            float x = (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? -1f : 0f)
                    + (kb.dKey.isPressed || kb.rightArrowKey.isPressed ?  1f : 0f);
            float y = (kb.sKey.isPressed || kb.downArrowKey.isPressed ? -1f : 0f)
                    + (kb.wKey.isPressed || kb.upArrowKey.isPressed   ?  1f : 0f);
            return new Vector2(x, y);
        }

        bool ReadBrake()
        {
            if (brakeAction != null && brakeAction.action.enabled)
                return brakeAction.action.IsPressed();
            return Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
        }

        bool ReadHandbrake()
        {
            if (handbrakeAction != null && handbrakeAction.action.enabled)
                return handbrakeAction.action.IsPressed();
            return Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
        }

        void FixedUpdate()
        {
            Vector2 move = ReadMove();
            bool brake = ReadBrake();
            bool handbrake = ReadHandbrake();

            SpeedMps = _rb.linearVelocity.magnitude;
            AnyWheelGrounded = WheelGrounded(frontAxle) || WheelGrounded(rearAxle);

            // Steering with speed-based scaling.
            float steerScale = steerHalfSpeed / Mathf.Max(steerHalfSpeed, SpeedMps + steerHalfSpeed * 0.5f);
            float targetSteer = move.x * maxSteerAngle * steerScale;
            _currentSteer = Mathf.Lerp(_currentSteer, targetSteer, steerSmooth * Time.fixedDeltaTime);

            ApplySteer(frontAxle, _currentSteer);
            ApplySteer(rearAxle, 0f);

            // Drive / brake.
            float forwardThrottle = Mathf.Clamp(move.y, -1f, 1f);
            float speedFalloff = Mathf.Clamp01(1f - SpeedMps / Mathf.Max(0.001f, maxSpeed));
            float drive = forwardThrottle * peakMotorTorque * speedFalloff;
            float brakeAmt = brake ? brakeTorque : 0f;
            float hbAmt = handbrake ? handbrakeTorque : 0f;

            ApplyMotor(frontAxle, frontAxle.drives ? drive : 0f, brakeAmt);
            ApplyMotor(rearAxle, rearAxle.drives ? drive : 0f, brakeAmt + hbAmt);

            UpdateVisuals(frontAxle);
            UpdateVisuals(rearAxle);

            // Counter-spin: damp angular velocity around local up if it's getting silly.
            Vector3 up = _gravity != null ? _gravity.PlanetUp : transform.up;
            Vector3 angVel = _rb.angularVelocity;
            float yaw = Vector3.Dot(angVel, up);
            float yawCap = Mathf.Max(1.5f, SpeedMps * 0.15f); // rad/s
            if (Mathf.Abs(yaw) > yawCap)
            {
                float excess = yaw - Mathf.Sign(yaw) * yawCap;
                _rb.AddTorque(-up * excess * counterSpinDamping, ForceMode.Acceleration);
            }

            // Air control.
            if (!AnyWheelGrounded)
            {
                Vector3 right = transform.right;
                Vector3 fwdUp = up;
                _rb.AddTorque(right * (move.y * airPitchTorque), ForceMode.Acceleration);
                _rb.AddTorque(fwdUp * (move.x * airYawTorque), ForceMode.Acceleration);
            }
        }

        static void ApplySteer(Axle axle, float angle)
        {
            if (!axle.steers) return;
            if (axle.left != null) axle.left.steerAngle = angle;
            if (axle.right != null) axle.right.steerAngle = angle;
        }

        static void ApplyMotor(Axle axle, float motor, float brake)
        {
            if (axle.left != null) { axle.left.motorTorque = motor; axle.left.brakeTorque = brake; }
            if (axle.right != null) { axle.right.motorTorque = motor; axle.right.brakeTorque = brake; }
        }

        static void UpdateVisuals(Axle axle)
        {
            UpdateOne(axle.left, axle.leftVisual);
            UpdateOne(axle.right, axle.rightVisual);
        }

        static void UpdateOne(WheelCollider wc, Transform vis)
        {
            if (wc == null || vis == null) return;
            wc.GetWorldPose(out Vector3 p, out Quaternion r);
            vis.SetPositionAndRotation(p, r);
        }

        static bool WheelGrounded(Axle axle)
        {
            return (axle.left != null && axle.left.isGrounded) || (axle.right != null && axle.right.isGrounded);
        }
    }
}
