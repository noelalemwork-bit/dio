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
            // Snap to the planet surface at the SAME altitude where we were
            // dropped — exactly the approach Banana uses for its settle-down,
            // and known to land where the player was. The previous version
            // queried the runtime raycaster, but on uneven terrain the
            // raycaster occasionally returns a wildly off radius (e.g. when
            // the cast direction grazes a vertical face) which would teleport
            // the slick to a random spot on the planet. Preserving the
            // spawn-time radius is robust across every level + every
            // surface profile.
            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            Vector3 planetCenter = planet != null ? planet.transform.position : Vector3.zero;
            Vector3 toSpawn = transform.position - planetCenter;
            float spawnRadius = toSpawn.magnitude;
            Vector3 dir = spawnRadius > 1e-4f ? toSpawn / spawnRadius : Vector3.up;
            if (spawnRadius < 1f)
            {
                // Defensive — should never happen for a player-dropped slick,
                // but if it does, fall back to NominalRadius so we land
                // approximately on the surface instead of at the planet
                // center.
                var lvl = Dio.Level.RaceBootstrap.CurrentLevel;
                spawnRadius = lvl != null ? lvl.NominalRadius : 200f;
            }
            transform.position = planetCenter + dir * (spawnRadius + 0.05f);
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
