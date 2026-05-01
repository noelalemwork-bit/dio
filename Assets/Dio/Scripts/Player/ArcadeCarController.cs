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

        [Tooltip("When true, all inputs are forced to zero — used to freeze the car after the race ends.")]
        public bool inputsLocked;

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

        // Captured at Awake so surface drag multipliers can be applied per-
        // frame without compounding across ticks.
        float baseLinearDamping;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _gravity = GetComponent<SphericalGravity>();
            _dioCar = GetComponent<Dio.Player.DioCar>();
            _rb.centerOfMass = new Vector3(0f, -0.4f, 0f);
            baseLinearDamping = _rb.linearDamping;
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
            // Camera cycle without an InputActionReference: Tab on keyboard
            // or North face button (Y / Triangle) on a gamepad. Pressed-
            // this-frame so a held button doesn't cycle every tick.
            if (cameraCycleAction == null)
            {
                bool tab = Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
                bool padY = Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame;
                if (tab || padY) OnCameraCycleRequested?.Invoke();
            }

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

            // Steer is local-only: the owner's controller writes `_currentSteer`
            // from their inputs (full physics), the host's controller writes it
            // for their own car, and remote peers leave it at 0 (their kinematic
            // car has no live controller). Visual fidelity loss on remote cars'
            // front-wheel steering is acceptable; chassis rotation tells the story.
            float steerDeg = _currentSteer;

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

            // Gamepad path: left stick steers + supplies analog throttle, with
            // right trigger acting as a "forward-only throttle override" so
            // players who prefer trigger-throttle (Forza-style) get full
            // forward without pushing the stick forward. The trigger NEVER
            // overrides reverse — pulling the stick back (or pressing
            // D-Pad ↓ / S / down-arrow) gives reverse regardless of the
            // trigger's value. Past versions clobbered reverse by writing
            // `if (rt > stick.y) stick.y = rt;` which fired whenever the
            // trigger was at rest (0) and the stick was pulled back (-1),
            // since 0 > -1, replacing reverse with idle.
            Vector2 stick = Vector2.zero;
            var pad = Gamepad.current;
            if (pad != null)
            {
                Vector2 ls = pad.leftStick.ReadValue();
                if (ls.sqrMagnitude < 0.04f) ls = Vector2.zero; // small dead zone (sticks past Input System defaults can drift)
                Vector2 dp = pad.dpad.ReadValue();
                stick = ls + dp;
                stick.x = Mathf.Clamp(stick.x, -1f, 1f);
                stick.y = Mathf.Clamp(stick.y, -1f, 1f);
            }

            var kb = Keyboard.current;
            if (kb != null)
            {
                float x = (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? -1f : 0f)
                        + (kb.dKey.isPressed || kb.rightArrowKey.isPressed ?  1f : 0f);
                float y = (kb.sKey.isPressed || kb.downArrowKey.isPressed ? -1f : 0f)
                        + (kb.wKey.isPressed || kb.upArrowKey.isPressed   ?  1f : 0f);
                // Magnitude-wins merge so a held key doesn't get cancelled by
                // a stick at rest (and vice versa), AND so a stick pulled
                // back to reverse beats a forward keypress only if the stick
                // is pushed harder than the key.
                if (Mathf.Abs(x) > Mathf.Abs(stick.x)) stick.x = x;
                if (Mathf.Abs(y) > Mathf.Abs(stick.y)) stick.y = y;
            }

            // Apply the right trigger AFTER the stick + keyboard merge, and
            // only as a FORWARD-additive override: it can raise stick.y
            // toward +1 but never replaces a negative (reverse) value.
            if (pad != null)
            {
                float rt = pad.rightTrigger.ReadValue();
                if (rt > 0.05f && stick.y >= 0f && rt > stick.y) stick.y = rt;
            }
            return stick;
        }

        bool ReadBrake()
        {
            if (brakeAction != null && brakeAction.action.enabled)
                return brakeAction.action.IsPressed();
            // Brake = Space on keyboard, Left Trigger on gamepad. Threshold
            // 0.3 because a half-pulled trigger should already start braking
            // — racers expect immediate response.
            if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed) return true;
            var pad = Gamepad.current;
            return pad != null && pad.leftTrigger.ReadValue() > 0.3f;
        }

        bool ReadHandbrake()
        {
            if (handbrakeAction != null && handbrakeAction.action.enabled)
                return handbrakeAction.action.IsPressed();
            // Handbrake = LShift on keyboard, B / Circle (East) or right
            // shoulder bumper on gamepad. East button is the natural drift
            // button (closest to thumb on the pad's right cluster).
            if (Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed) return true;
            var pad = Gamepad.current;
            return pad != null && (pad.buttonEast.isPressed || pad.rightShoulder.isPressed);
        }

        void FixedUpdate()
        {
            // Skip the entire physics-application path on a kinematic body
            // (server's view + remote clients). WheelCollider torque writes on
            // a kinematic rigidbody throw "WheelCollider on kinematic body"
            // warnings in some Unity versions and are pure waste.
            if (_rb.isKinematic) return;

            if (readLocalInput) currentInputs = ReadLocalInputsNow();
            if (inputsLocked) currentInputs = default;

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

            // Surface-aware physics: query the wheels' current contact for a
            // SurfaceComponent. If found, multiply drive/brake by the surface
            // profile so ice slips, dirt drags, magnetic grips, etc. Default
            // (asphalt or no component) is 1.0 across the board.
            var surface = SampleSurfaceFromAxles();
            drive    *= surface.forwardFrictionMultiplier;
            brakeAmt *= surface.brakeMultiplier;
            hbAmt    *= surface.brakeMultiplier;
            // Drag multiplier folds into _rb.linearDamping per-frame so the
            // surface effect persists even when the wheels lift briefly off
            // the ground (e.g. tiny bumps mid-corner). We restore the base
            // value before applying so successive frames don't compound.
            if (_rb != null) _rb.linearDamping = baseLinearDamping * surface.dragMultiplier;
            // Boost surfaces fire a one-shot impulse along the surface up.
            if (surface.launchImpulse > 0.01f && _rb != null && AnyWheelGrounded)
            {
                Vector3 surfUp = _gravity != null ? _gravity.PlanetUp : transform.up;
                _rb.AddForce(surfUp * surface.launchImpulse, ForceMode.VelocityChange);
            }

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

        // Inspect each wheel's current ground contact for a SurfaceComponent.
        // Multiple wheels can disagree on ground type (e.g. one off the
        // track), so we average their property profiles weighted by the
        // number of grounded wheels. If no wheel is grounded, return Asphalt
        // as a stable neutral default.
        Dio.Level.SurfaceProperties SampleSurfaceFromAxles()
        {
            int count = 0;
            float fF = 0, fS = 0, fD = 0, fB = 0, fLI = 0;
            void Sample(WheelCollider w)
            {
                if (w == null || !w.isGrounded) return;
                if (!w.GetGroundHit(out WheelHit hit)) return;
                var col = hit.collider;
                Dio.Level.SurfaceType type = Dio.Level.SurfaceType.Asphalt;
                if (col != null)
                {
                    var sc = col.GetComponentInParent<Dio.Level.SurfaceComponent>();
                    if (sc != null) type = sc.type;
                }
                var props = Dio.Level.SurfacePropertyTable.Get(type);
                fF += props.forwardFrictionMultiplier;
                fS += props.sidewaysFrictionMultiplier;
                fD += props.dragMultiplier;
                fB += props.brakeMultiplier;
                fLI += props.launchImpulse;
                count++;
            }
            if (frontAxle != null) { Sample(frontAxle.left); Sample(frontAxle.right); }
            if (rearAxle  != null) { Sample(rearAxle.left);  Sample(rearAxle.right); }
            if (count == 0) return Dio.Level.SurfacePropertyTable.Get(Dio.Level.SurfaceType.Asphalt);
            float inv = 1f / count;
            return new Dio.Level.SurfaceProperties
            {
                forwardFrictionMultiplier  = fF * inv,
                sidewaysFrictionMultiplier = fS * inv,
                dragMultiplier             = fD * inv,
                brakeMultiplier            = fB * inv,
                launchImpulse              = fLI * inv,
            };
        }
    }
}
