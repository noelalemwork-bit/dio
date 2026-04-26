using UnityEngine;
using Dio.Player;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Banana-style hazard: trigger sphere on the ground, on overlap apply a
    /// state to the car and despawn. Has a brief THROW phase right after
    /// spawn (server animates the position backward + slightly up, then snaps
    /// to the surface) so the peel visibly arcs out of the car. NetworkTransform
    /// streams the throw to all peers — no special projectile physics needed.
    ///
    /// File deliberately holds ONE MonoBehaviour. OilSlick was split into
    /// OilSlickObstacle.cs so each gets its own stable fileID 11500000 in
    /// serialized prefabs (see BobombObstacle.cs for the same reasoning).
    public class BananaObstacle : Obstacle
    {
        public float slipDuration = 5f;

        [Header("Throw")]
        [Tooltip("How long the throw animation lasts (seconds).")]
        public float throwDuration = 0.55f;
        [Tooltip("Total backward distance covered during the throw.")]
        public float throwDistance = 5.5f;
        [Tooltip("Peak height above the surface mid-arc.")]
        public float throwArcHeight = 1.5f;

        Vector3 _throwStart;
        Vector3 _throwBack;     // per-frame backward direction from spawn
        Vector3 _throwUp;       // surface-up at spawn
        float _throwElapsed;
        bool _throwing;
        float _settleRadius;    // distance from planet origin to keep the peel sitting on the surface

        public override void OnStartServer()
        {
            base.OnStartServer();
            _throwStart = transform.position;
            // Backward = -forward of the spawner (rotation was set to owner's
            // rotation by the effect). Up = the local surface normal at spawn.
            _throwBack = -transform.forward;
            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            Vector3 planetCenter = planet != null ? planet.transform.position : Vector3.zero;
            _throwUp = (transform.position - planetCenter).normalized;
            _settleRadius = (transform.position - planetCenter).magnitude;
            _throwElapsed = 0f;
            _throwing = true;
        }

        void Update()
        {
            if (!isServer || !_throwing) return;
            _throwElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_throwElapsed / Mathf.Max(0.01f, throwDuration));
            Vector3 along = _throwBack * (throwDistance * t);
            // Sin arc for a clean parabolic feel: peaks at t=0.5.
            Vector3 vert = _throwUp * (throwArcHeight * Mathf.Sin(t * Mathf.PI));
            transform.position = _throwStart + along + vert;

            if (t >= 1f)
            {
                // Snap to the surface so the peel sits flat on the road.
                var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
                Vector3 planetCenter = planet != null ? planet.transform.position : Vector3.zero;
                Vector3 dir = (transform.position - planetCenter).normalized;
                transform.position = planetCenter + dir * _settleRadius;
                _throwing = false;
            }
        }

        void OnTriggerEnter(Collider other)
        {
            if (!isServer) return;
            // Don't fire while still mid-arc — the peel is in the air, not on the road.
            if (_throwing) return;
            var car = CarOf(other);
            if (car == null || IsOwnerImmune(car)) return;

            RpcSpawnImpactBurst(transform.position, new Color(1f, 0.88f, 0.20f, 1f), 0.9f);
            ApplyClientImpact(car, transform.position, 5.5f, 1.2f, 8f);
            ApplyState(car, new BananaSlipState(), slipDuration);
            ServerDespawn();
        }
    }
}
