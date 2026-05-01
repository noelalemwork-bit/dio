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

        // One sharedMaterial per process — all cars + every boost trail share
        // it so we never spawn N runtime materials. The shader is procedural
        // (`_Time.y` driven) so they all swirl in lock-step, which actually
        // reads better than per-car random phases.
        static Material s_rainbowMat;

        CarStateMachine _sm;

        void Awake()
        {
            _sm = GetComponentInParent<CarStateMachine>();
            if (trail == null) trail = GetComponent<TrailRenderer>();
            if (trail != null)
            {
                trail.emitting = false;
                trail.sharedMaterial = GetOrCreateRainbowMat();
            }
        }

        static Material GetOrCreateRainbowMat()
        {
            if (s_rainbowMat != null) return s_rainbowMat;
            var sh = Shader.Find("Dio/BoostRainbow");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh != null) s_rainbowMat = new Material(sh) { name = "BoostRainbow (runtime)" };
            return s_rainbowMat;
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
