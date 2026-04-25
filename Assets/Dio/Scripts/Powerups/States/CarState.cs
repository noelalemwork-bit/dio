using UnityEngine;
using Dio.Player;

namespace Dio.Powerups.States
{
    /// A timed state applied to a DioCar. States can stack — the state machine
    /// composes their `ModifyInputs` and `ModifyController` calls every step.
    /// Server-authoritative: only the server creates / removes states.
    public abstract class CarState
    {
        public abstract PowerupKind Kind { get; }
        public float Duration;
        public float Elapsed;
        public DioCar Car;

        public virtual bool IsExpired => Elapsed >= Duration;

        public virtual void OnEnter() { }
        public virtual void OnExit() { }
        public virtual void OnTick(float dt) { Elapsed += dt; }

        /// Mutate the input that the controller will apply this tick.
        public virtual void ModifyInputs(ref ArcadeCarController.Inputs inputs) { }

        /// One-shot mutation of the controller (e.g. tweaking max speed). Called
        /// every tick — the state machine restores the original on exit.
        public virtual void ModifyController(ArcadeCarController c) { }
    }
}
