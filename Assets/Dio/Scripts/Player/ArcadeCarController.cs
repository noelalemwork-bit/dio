using UnityEngine;
using UnityEngine.InputSystem;

namespace Dio.Player
{
    /// Arcade-flavoured WheelCollider car.
    ///
    /// Input model:
    ///   * `readLocalInput == true`: reads the Input System (or keyboard
    ///      fallback) every FixedUpdate and updates `currentInputs`. Use this
    ///      for solo testing or non-networked builds.
    ///   * `readLocalInput == false`: an external system (e.g. `DioCar`)
    ///      writes to `currentInputs` and we just apply it. Use this in the
    ///      networked path where the server runs the simulation.
    ///
    /// Tuned for stability + punchy launch:
    ///   - High lateral stiffness so we don't spin out.
    ///   - Steer angle scales down with speed.
    ///   - Counter-spin damping around local up.
    ///   - Air control torques when wheels are off the ground.
    [RequireComponent(typeof(Rigidbody))]
    public class ArcadeCarController : MonoBehaviour
    {
        [System.Serializable]
        public struct Inputs
        {
            public Vector2 steerThrottle; // x = steer (-1..1), y = throttle (-1..1)
            public bool brake;
            public bool handbrake;
        }

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
        [Tooltip("Peak motor torque at zero speed (Nm). Falls off with speed via a sqrt curve for arcade punch.")]
        public float peakMotorTorque = 2800f;
        public float maxSpeed = 45f;             // m/s ~ 162 kph
        public float brakeTorque = 3500f;
        public float handbrakeTorque = 5500f;
        [Tooltip("Extra torque multiplier while below launchSpeedFraction × maxSpeed (off-the-line punch).")]
        public float launchBoost = 1.4f;
        [Range(0f, 1f)] public float launchSpeedFraction = 0.35f;

        [Header("Steering")]
        public float maxSteerAngle = 32f;
        [Tooltip("At this speed (m/s) the steer angle is halved.")]
        public float steerHalfSpeed = 18f;
        public float steerSmooth = 8f;

        [Header("Arcade tuning")]
        public float counterSpinDamping = 4f;
        public float airPitchTorque = 8f;
        public float airYawTorque = 12f;

        [Header("Input")]
        [Tooltip("If true, this component reads inputs itself each FixedUpdate. " +
                 "Set false when an external system (e.g. DioCar) drives currentInputs over the network.")]
        public bool readLocalInput = true;

        public InputActionReference moveAction;
        public InputActionReference brakeAction;
        public InputActionReference handbrakeAction;
        public InputActionReference cameraCycleAction;

        public Inputs currentInputs;

        // Read-out for camera + UI.
        public float SpeedMps { get; private set; }
        public bool AnyWheelGrounded { get; private set; }
        public System.Action OnCameraCycleRequested;

        Rigidbody _rb;
        SphericalGravity _gravity;
        float _currentSteer;
        System.Action<InputAction.CallbackContext> _cycleHandler;

        // Wheel-visual state (runs on every peer — client cars are kinematic so
        // we can't use WheelCollider.GetWorldPose; instead derive spin from
        // the rigidbody / transform velocity and steer from a synced source).
        float _spinAngleDeg;
        Vector3 _visualPrevPos;
        bool _visualPrevPosSeeded;
        Dio.Player.DioCar _dioCar;
        float _wheelRadiusCached = 0.4f;

        public float CurrentSteerAngle => _currentSteer;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _gravity = GetComponent<SphericalGravity>();
            _dioCar = GetComponent<Dio.Player.DioCar>();
            _rb.centerOfMass = new Vector3(0f, -0.4f, 0f);
            // Cache wheel radius from one of the wheels for spin computation.
            if (frontAxle != null && frontAxle.left != null) _wheelRadiusCached = frontAxle.left.radius;
            else if (rearAxle != null && rearAxle.left != null) _wheelRadiusCached = rearAxle.left.radius;

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

            UpdateWheelVisuals(Time.deltaTime);
        }

        // Runs on every peer. Drives wheel local rotation from a logical
        // model: spin around chassis-X by accumulated angle (proportional to
        // forward speed / wheel radius), steer around chassis-Y by either the
        // controller's local steering (server) or a synced steer angle from
        // DioCar (clients). Position is left to the parent transform — the
        // visual root is parented to the car root so it follows automatically.
        void UpdateWheelVisuals(float dt)
        {
            if (dt < 1e-5f) return;

            if (!_visualPrevPosSeeded) { _visualPrevPos = transform.position; _visualPrevPosSeeded = true; return; }

            Vector3 delta = transform.position - _visualPrevPos;
            _visualPrevPos = transform.position;
            float fwdSpeed = Vector3.Dot(delta, transform.forward) / dt;
            float spinDeltaDeg = (fwdSpeed / Mathf.Max(0.01f, _wheelRadiusCached)) * Mathf.Rad2Deg * dt;
            _spinAngleDeg = (_spinAngleDeg + spinDeltaDeg) % 360f;

            // Steer source: server's controller writes _currentSteer + DioCar.steerAngle SyncVar;
            // remote clients only have the SyncVar. Prefer the SyncVar when available so all
            // peers see the same value — otherwise fall back to the local controller's value.
            float steerDeg = _dioCar != null ? _dioCar.steerAngle : _currentSteer;

            ApplyWheelLocalRotation(frontAxle, _spinAngleDeg, steerDeg);
            ApplyWheelLocalRotation(rearAxle, _spinAngleDeg, 0f);
        }

        static void ApplyWheelLocalRotation(Axle axle, float spinDeg, float steerDeg)
        {
            if (axle == null) return;
            var rot = Quaternion.Euler(spinDeg, steerDeg, 0f);
            if (axle.leftVisual  != null) axle.leftVisual.localRotation  = rot;
            if (axle.rightVisual != null) axle.rightVisual.localRotation = rot;
        }

        public Inputs ReadLocalInputsNow()
        {
            return new Inputs
            {
                steerThrottle = ReadMove(),
                brake = ReadBrake(),
                handbrake = ReadHandbrake(),
            };
        }

        Vector2 ReadMove()
        {
            if (moveAction != null && moveAction.action.enabled)
                return moveAction.action.ReadValue<Vector2>();

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
            if (readLocalInput) currentInputs = ReadLocalInputsNow();

            Vector2 move = currentInputs.steerThrottle;
            bool brake = currentInputs.brake;
            bool handbrake = currentInputs.handbrake;

            SpeedMps = _rb.linearVelocity.magnitude;
            AnyWheelGrounded = WheelGrounded(frontAxle) || WheelGrounded(rearAxle);

            float steerScale = steerHalfSpeed / Mathf.Max(steerHalfSpeed, SpeedMps + steerHalfSpeed * 0.5f);
            float targetSteer = move.x * maxSteerAngle * steerScale;
            _currentSteer = Mathf.Lerp(_currentSteer, targetSteer, steerSmooth * Time.fixedDeltaTime);

            ApplySteer(frontAxle, _currentSteer);
            ApplySteer(rearAxle, 0f);

            float forwardThrottle = Mathf.Clamp(move.y, -1f, 1f);
            // Sqrt falloff keeps power high through most of the range and tapers near the top.
            float speedRatio = Mathf.Clamp01(SpeedMps / Mathf.Max(0.001f, maxSpeed));
            float speedFalloff = 1f - Mathf.Sqrt(speedRatio);
            // Off-the-line launch boost.
            float launch = speedRatio < launchSpeedFraction ? launchBoost : 1f;
            float drive = forwardThrottle * peakMotorTorque * speedFalloff * launch;
            float brakeAmt = brake ? brakeTorque : 0f;
            float hbAmt = handbrake ? handbrakeTorque : 0f;

            ApplyMotor(frontAxle, frontAxle.drives ? drive : 0f, brakeAmt);
            ApplyMotor(rearAxle, rearAxle.drives ? drive : 0f, brakeAmt + hbAmt);

            // Wheel VISUAL spin/steer is driven by Update() on every peer
            // (the server-only WheelCollider pose path was unreliable for
            // remote clients whose rigidbody is kinematic).

            Vector3 up = _gravity != null ? _gravity.PlanetUp : transform.up;
            Vector3 angVel = _rb.angularVelocity;
            float yaw = Vector3.Dot(angVel, up);
            float yawCap = Mathf.Max(1.5f, SpeedMps * 0.15f);
            if (Mathf.Abs(yaw) > yawCap)
            {
                float excess = yaw - Mathf.Sign(yaw) * yawCap;
                _rb.AddTorque(-up * excess * counterSpinDamping, ForceMode.Acceleration);
            }

            if (!AnyWheelGrounded)
            {
                Vector3 right = transform.right;
                _rb.AddTorque(right * (move.y * airPitchTorque), ForceMode.Acceleration);
                _rb.AddTorque(up * (move.x * airYawTorque), ForceMode.Acceleration);
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

        static bool WheelGrounded(Axle axle)
        {
            return (axle.left != null && axle.left.isGrounded) || (axle.right != null && axle.right.isGrounded);
        }
    }
}
