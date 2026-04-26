using UnityEngine;
using Dio.Player;

namespace Dio.Powerups.States
{
    /// Builds a client-side copy of the state that matches the synced
    /// PowerupKind on the car.
    public static class CarStateFactory
    {
        public static CarState Create(PowerupKind kind)
        {
            switch (kind)
            {
                case PowerupKind.Boost:       return new SpeedBoostState();
                case PowerupKind.TripleBoost: return new SpeedBoostState();
                case PowerupKind.Star:        return new StarState();
                case PowerupKind.Lightning:   return new LightningFreezeState();
                case PowerupKind.Banana:      return new BananaSlipState();
                case PowerupKind.OilSlick:    return new OilSpinState();
                case PowerupKind.GreenShell:  return new GreenShellBlindState();
                case PowerupKind.BlueShell:   return new BlueShellBlindState();
                case PowerupKind.Bobomb:      return new BobombBlastState();
                case PowerupKind.Tornado:     return new TornadoTumbleState();
                case PowerupKind.FreezeRay:   return new FrozenState();
                default: return null;
            }
        }
    }

    static class StatePhysicsKick
    {
        public static void Apply(DioCar car, Vector3 velocityDelta, Vector3 angularDelta)
        {
            if (car == null) return;
            var rb = car.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic || !car.isOwned) return;

            rb.linearVelocity += velocityDelta;
            rb.angularVelocity += angularDelta;
        }
    }

    public class SpeedBoostState : CarState
    {
        public override PowerupKind Kind => PowerupKind.Boost;
        public float SpeedMultiplier = 1.45f;
        public float TorqueMultiplier = 1.6f;

        public override void ModifyController(ArcadeCarController c)
        {
            c.maxSpeed *= SpeedMultiplier;
            c.peakMotorTorque *= TorqueMultiplier;
        }
    }

    public class StarState : CarState
    {
        public override PowerupKind Kind => PowerupKind.Star;

        public override void ModifyController(ArcadeCarController c)
        {
            c.maxSpeed *= 1.2f;
            c.peakMotorTorque *= 1.25f;
        }
    }

    public class LightningFreezeState : CarState
    {
        public override PowerupKind Kind => PowerupKind.Lightning;

        public override void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            inputs.steerThrottle = Vector2.zero;
            inputs.brake = true;
            inputs.handbrake = false;
        }

        public override void ModifyController(ArcadeCarController c)
        {
            c.maxSpeed *= 0.08f;
            c.peakMotorTorque = 0f;
        }
    }

    public class BananaSlipState : CarState
    {
        public override PowerupKind Kind => PowerupKind.Banana;

        float _seed;

        public override void OnEnter()
        {
            _seed = Random.value * 100f;
            if (Car == null) return;

            float side = Random.value < 0.5f ? -1f : 1f;
            Vector3 tangent = Vector3.ProjectOnPlane(Car.transform.right * side, Car.transform.up).normalized;
            Vector3 velocity = tangent * 5.5f;
            Vector3 angular = Car.transform.up * (8f * side);
            StatePhysicsKick.Apply(Car, velocity, angular);
        }

        public override void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            inputs.steerThrottle.x = Mathf.Sin((Time.time + _seed) * 9f);
            inputs.steerThrottle.y *= 0.35f;
            inputs.brake = false;
            inputs.handbrake = true;
        }
    }

    public class OilSpinState : CarState
    {
        public override PowerupKind Kind => PowerupKind.OilSlick;

        float _seed;

        public override void OnEnter()
        {
            _seed = Random.value * 100f;
            if (Car == null) return;

            float side = Random.value < 0.5f ? -1f : 1f;
            Vector3 tangent = Vector3.ProjectOnPlane(Car.transform.right * side, Car.transform.up).normalized;
            Vector3 velocity = tangent * 3.5f;
            Vector3 angular = Car.transform.up * (13f * side);
            StatePhysicsKick.Apply(Car, velocity, angular);
        }

        public override void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            inputs.steerThrottle.x = Mathf.Sin((Time.time + _seed) * 12f);
            inputs.steerThrottle.y *= 0.15f;
            inputs.brake = false;
            inputs.handbrake = true;
        }
    }

    public class GreenShellBlindState : CarState
    {
        public override PowerupKind Kind => PowerupKind.GreenShell;
    }

    public class BlueShellBlindState : CarState
    {
        public override PowerupKind Kind => PowerupKind.BlueShell;
    }

    public class BobombBlastState : CarState
    {
        public override PowerupKind Kind => PowerupKind.Bobomb;

        public override void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            inputs.steerThrottle = Vector2.zero;
            inputs.brake = true;
            inputs.handbrake = true;
        }
    }

    public class TornadoTumbleState : CarState
    {
        public override PowerupKind Kind => PowerupKind.Tornado;

        public override void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            inputs.steerThrottle = Vector2.zero;
            inputs.brake = false;
            inputs.handbrake = true;
        }
    }

    public class FrozenState : CarState
    {
        public override PowerupKind Kind => PowerupKind.FreezeRay;

        public override void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            inputs.steerThrottle = Vector2.zero;
            inputs.brake = true;
            inputs.handbrake = false;
        }
    }
}
