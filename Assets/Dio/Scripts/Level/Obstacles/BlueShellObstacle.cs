using Mirror;
using UnityEngine;
using Dio.Player;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Blue Shell: tracks the leader. We approximate "leader" as the car whose
    /// arc-length progress along the geodesic chain is highest. For M1 we just
    /// pick the car with the smallest forward-axis-angle to the next checkpoint;
    /// see plan.md §11 for the proper progress tracker.
    ///
    /// LIVES IN ITS OWN FILE so the prefab importer can resolve a stable
    /// fileID 11500000 for `m_Script` (see BobombObstacle.cs for the same
    /// reasoning).
    public class BlueShellObstacle : Obstacle
    {
        public float speed = 50f;
        public float explosionRadius = 9f;
        public float explosionImpulse = 15f;
        public float blindDuration = 5f;
        public Transform planet;
        public float planetRadius = 200f;

        Transform _target;

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (lifetime <= 0f) { lifetime = 12f; Invoke(nameof(ServerDespawn), lifetime); }
            _target = FindLeaderCar();
        }

        Transform FindLeaderCar()
        {
            DioCar best = null;
            float bestArc = float.NegativeInfinity;
            foreach (var ni in NetworkServer.spawned.Values)
            {
                var car = ni.GetComponent<DioCar>();
                if (car == null || car == this) continue;
                // Leader = highest arc-length progress along the track.
                float arc = car.progressArc;
                if (arc > bestArc) { bestArc = arc; best = car; }
            }
            return best != null ? best.transform : null;
        }

        void Update()
        {
            if (!isServer || _target == null) return;
            // Glide along the surface toward the target.
            var planetPos = planet != null ? planet.position : Vector3.zero;
            var n = (transform.position - planetPos).normalized;
            var tangentDir = Vector3.ProjectOnPlane((_target.position - transform.position), n).normalized;
            transform.position += tangentDir * speed * Time.deltaTime;
            // Re-snap to surface.
            transform.position = planetPos + (transform.position - planetPos).normalized * planetRadius;
            transform.rotation = Quaternion.LookRotation(tangentDir, n);

            if ((transform.position - _target.position).sqrMagnitude < 4f) Explode();
        }

        [Server]
        void Explode()
        {
            RpcSpawnImpactBurst(transform.position, new Color(0.28f, 0.60f, 1f, 1f), 1.5f);
            var hits = Physics.OverlapSphere(transform.position, explosionRadius);
            // Dedup: a single car can have multiple colliders (chassis +
            // wheel + cabin), so we'd otherwise apply state and flash the
            // screen once per child collider — multiplying the effect.
            var seen = new System.Collections.Generic.HashSet<DioCar>();
            foreach (var h in hits)
            {
                var car = CarOf(h);
                if (car == null || !seen.Add(car)) continue;
                ApplyState(car, new BlueShellBlindState(), blindDuration);
                ApplyClientImpact(car, transform.position, explosionImpulse, 1.0f, 8f);
                if (car.connectionToClient != null)
                    car.TargetFlashScreen(car.connectionToClient, Dio.Powerups.PowerupKind.BlueShell, blindDuration);
            }
            ServerDespawn();
        }
    }
}
