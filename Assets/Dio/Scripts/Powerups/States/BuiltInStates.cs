using UnityEngine;
using Dio.Player;

namespace Dio.Powerups.States
{
    /// Builds a "client copy" CarState from a synced PowerupKind so the owner
    /// can apply ModifyInputs / ModifyController locally. The server is still
    /// authoritative on durations / expiry; the owner just mirrors the active
    /// kinds and applies the SAME modification each FixedUpdate.
    public static class CarStateFactory
    {
        public static CarState Create(PowerupKind kind)
        {
            switch (kind)
            {
                case PowerupKind.Boost:       return new SpeedBoostState();
                case PowerupKind.TripleBoost: return new SpeedBoostState();
                case PowerupKind.Star:        return new StarState();
                case PowerupKind.Lightning:   return new ShrunkState();
                // Multiple kinds map to the same CarState type with different
                // parameters. The SyncList stores the original PowerupKind so
                // visuals are distinct; the factory creates the right variant.
                case PowerupKind.Banana:      return new SlipperyState { Mode = SlipperyState.SlipMode.RandomSteer };
                case PowerupKind.OilSlick:    return new SlipperyState { Mode = SlipperyState.SlipMode.ZeroSteer };
                case PowerupKind.GreenShell:
                case PowerupKind.BlueShell:
                case PowerupKind.Bobomb:
                case PowerupKind.Tornado:     return new TumbleState();
                case PowerupKind.FreezeRay:   return new FrozenState();
                default: return null;
            }
        }
    }

    /// Linear forward speed boost. Bumps max speed and peak torque for a short duration.
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

    /// Star: invincibility (flagged for collision filtering) + a milder speed bump.
    public class StarState : CarState
    {
        public override PowerupKind Kind => PowerupKind.Star;

        public override void ModifyController(ArcadeCarController c)
        {
            c.maxSpeed *= 1.2f;
            c.peakMotorTorque *= 1.25f;
        }
    }

    /// Lightning'd: shrunk + slower acceleration. Visual scale handled by CarStateMachine.
    public class ShrunkState : CarState
    {
        public override PowerupKind Kind => PowerupKind.Lightning;

        public override void ModifyController(ArcadeCarController c)
        {
            c.maxSpeed *= 0.6f;
            c.peakMotorTorque *= 0.5f;
        }
    }

    /// Slippery: zero out the steering input so the car drifts straight (or
    /// inverts steering for chaos — set Mode). Used by banana / oil slick.
    public class SlipperyState : CarState
    {
        public override PowerupKind Kind => PowerupKind.OilSlick; // reused for banana too
        public enum SlipMode { ZeroSteer, InvertSteer, RandomSteer }
        public SlipMode Mode = SlipMode.ZeroSteer;

        float _seed;
        public override void OnEnter() { _seed = Random.value * 100f; }

        public override void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            switch (Mode)
            {
                case SlipMode.ZeroSteer: inputs.steerThrottle.x = 0f; break;
                case SlipMode.InvertSteer: inputs.steerThrottle.x = -inputs.steerThrottle.x; break;
                case SlipMode.RandomSteer:
                    inputs.steerThrottle.x = Mathf.Sin((Time.time + _seed) * 8f);
                    break;
            }
        }
    }

    /// Tumble: zero throttle + zero steer for a moment after a hard hit.
    /// (Used by shells, bombs, tornado, blue-shell-AOE.)
    public class TumbleState : CarState
    {
        public override PowerupKind Kind => PowerupKind.GreenShell; // reused
        public override void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            inputs.steerThrottle = Vector2.zero;
            inputs.brake = false;
            inputs.handbrake = false;
        }
    }

    /// Frozen: total input lockdown. Stub for the FreezeRay powerup.
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
