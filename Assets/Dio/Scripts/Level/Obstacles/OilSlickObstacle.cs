using UnityEngine;
using Dio.Player;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Oil slick: an area hazard. Continuously refreshes the slippery state on
    /// any car inside it; never despawns by overlap (only by lifetime).
    ///
    /// LIVES IN ITS OWN FILE so the prefab importer can resolve a stable
    /// fileID 11500000 for `m_Script` (see BobombObstacle.cs for the same
    /// reasoning).
    public class OilSlickObstacle : Obstacle
    {
        public float refreshDuration = 5f;

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

            if (sm.IsActive(PowerupKind.OilSlick)) return;

            RpcSpawnImpactBurst(car.transform.position, new Color(0.18f, 0.22f, 0.28f, 1f), 0.8f);
            ApplyClientImpact(car, transform.position, 3.5f, 0.3f, 13f);
            ApplyState(car, new OilSpinState(), refreshDuration);
        }
    }
}
