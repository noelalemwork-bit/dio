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
    public class BananaObstacle : Obstacle
    {
        public float slipDuration = 1.4f;

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

            ApplyState(car, new SlipperyState { Mode = SlipperyState.SlipMode.RandomSteer }, slipDuration);
            ServerDespawn();
        }
    }

    /// Oil slick: an area hazard. Continuously refreshes the slippery state on
    /// any car inside it; never despawns by overlap (only by lifetime).
    public class OilSlickObstacle : Obstacle
    {
        public float refreshDuration = 0.4f;

        public override void OnStartServer()
        {
            base.OnStartServer();
            // Snap to the planet surface + orient so the disc is tangent
            // to the surface (its Y axis = surface up).
            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            Vector3 planetCenter = planet != null ? planet.transform.position : Vector3.zero;
            Vector3 dir = (transform.position - planetCenter).normalized;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.up;
            var lvl = Dio.Level.RaceBootstrap.CurrentLevel;
            // Use the actual surface raycaster (preserves uneven terrain
            // height); fall back to NominalRadius and finally to the
            // hazard's own radial position. The hazard's pre-spawn position
            // matters less than landing on the actual mesh so it doesn't
            // hover or sink on bumpy planets.
            var raycaster = Dio.Level.RaceBootstrap.CurrentRaycaster;
            float r = raycaster != null
                ? raycaster.ResolveSurfaceRadius(dir)
                : (lvl != null ? lvl.NominalRadius : (transform.position - planetCenter).magnitude);
            transform.position = planetCenter + dir * (r + 0.05f);
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, dir).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.Cross(dir, Vector3.right).normalized;
            transform.rotation = Quaternion.LookRotation(fwd, dir);
        }

        void OnTriggerStay(Collider other)
        {
            if (!isServer) return;
            var car = CarOf(other);
            if (car == null || IsOwnerImmune(car)) return;

            var sm = car.GetComponent<CarStateMachine>();
            if (sm == null) return;

            // Refresh: clear any existing oil-slick slip, reapply.
            sm.Clear(PowerupKind.OilSlick);
            ApplyState(car, new SlipperyState { Mode = SlipperyState.SlipMode.ZeroSteer }, refreshDuration);
        }
    }
}
