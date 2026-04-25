using UnityEngine;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Player
{
    /// Toggles a TrailRenderer's `emitting` based on whether the car has any
    /// active speed-boost / star state. Lives on every peer's copy of the car
    /// — the SyncList that backs `CarStateMachine.IsActive` is replicated, so
    /// remote peers see the trail too. Looks up `CarStateMachine` via
    /// GetComponentInParent so this can sit on a child trail GameObject and
    /// still find the car-root state machine.
    public class CarBoostTrail : MonoBehaviour
    {
        [Tooltip("Trail to toggle while a boost-class state is active.")]
        public TrailRenderer trail;

        CarStateMachine _sm;

        void Awake()
        {
            _sm = GetComponentInParent<CarStateMachine>();
            if (trail == null) trail = GetComponent<TrailRenderer>();
            if (trail != null) trail.emitting = false;
        }

        void Update()
        {
            if (trail == null || _sm == null) return;
            bool boosting = _sm.IsActive(PowerupKind.Boost)
                         || _sm.IsActive(PowerupKind.TripleBoost)
                         || _sm.IsActive(PowerupKind.Star);
            if (trail.emitting != boosting) trail.emitting = boosting;
        }
    }
}
