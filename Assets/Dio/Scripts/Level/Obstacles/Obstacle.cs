using Mirror;
using UnityEngine;
using Dio.Player;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Three obstacle archetypes, all server-authoritative:
    ///
    ///   StaticObstacle     — placed on the track, doesn't move (banana, mine).
    ///                        Trigger overlap → effect → optional self-despawn.
    ///   KinematicObstacle  — deterministic time-driven motion (tornado, fans).
    ///                        Position computed from a seed + a phase, so every
    ///                        peer can reproduce it locally if we ever want to
    ///                        skip syncing — for now we still NetworkServer.Spawn
    ///                        them so newcomers see them in the right place.
    ///   ProjectileObstacle — server-only physics (rigidbody) projectile (shells,
    ///                        bombs). NetworkTransform syncs to clients.
    public abstract class Obstacle : NetworkBehaviour
    {
        [Tooltip("Seconds before this obstacle auto-despawns. 0 = no timeout.")]
        public float lifetime = 0f;

        [Tooltip("Connection id of the car that spawned this. Used to ignore self-collision briefly.")]
        [SyncVar] public int ownerConnectionId = -1;

        [Tooltip("Seconds during which the owner is immune to this obstacle.")]
        public float ownerImmunityWindow = 0.6f;

        float _spawnTime;

        public override void OnStartServer()
        {
            _spawnTime = Time.time;
            if (lifetime > 0f) Invoke(nameof(ServerDespawn), lifetime);
        }

        public override void OnStartClient()
        {
            // On non-server peers, freeze the rigidbody so NetworkTransform is
            // the only thing moving us. Otherwise client-side physics fights
            // the snapshot interpolation.
            if (!isServer)
            {
                var rb = GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
            }
        }

        protected bool IsOwnerImmune(DioCar car)
        {
            if (car == null || car.connectionToClient == null) return false;
            if (car.connectionToClient.connectionId != ownerConnectionId) return false;
            return (Time.time - _spawnTime) < ownerImmunityWindow;
        }

        protected static DioCar CarOf(Collider c) => c == null ? null : c.GetComponentInParent<DioCar>();

        [Server]
        protected void ServerDespawn()
        {
            if (this == null || gameObject == null) return;
            NetworkServer.Destroy(gameObject);
        }

        [ClientRpc]
        protected void RpcSpawnImpactBurst(Vector3 position, Color color, float scale)
        {
            Dio.Powerups.Effects.ImpactBurst.Spawn(position, color, scale);
        }

        /// Convenience: apply a state to a car for a duration.
        [Server]
        protected static void ApplyState(DioCar car, CarState state, float duration)
        {
            if (car == null) return;
            var sm = car.GetComponent<CarStateMachine>();
            if (sm == null) return;
            sm.Clear(state.Kind);
            state.Duration = duration;
            sm.Apply(state);
        }

        [Server]
        protected static void ApplyClientImpact(DioCar car, Vector3 origin, float tangentialForce, float upwardForce, float angularForce)
        {
            if (car == null || car.connectionToClient == null) return;

            Vector3 planetCenter = Vector3.zero;
            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            if (planet != null) planetCenter = planet.transform.position;

            Vector3 up = (car.transform.position - planetCenter).normalized;
            if (up.sqrMagnitude < 0.0001f) up = car.transform.up;

            Vector3 away = Vector3.ProjectOnPlane(car.transform.position - origin, up);
            if (away.sqrMagnitude < 0.0001f)
                away = Vector3.ProjectOnPlane(-car.transform.forward, up);
            if (away.sqrMagnitude < 0.0001f)
                away = car.transform.forward;
            away.Normalize();

            Vector3 velocityDelta = away * tangentialForce + up * upwardForce;
            Vector3 angularAxis = Vector3.Cross(up, away).normalized;
            if (angularAxis.sqrMagnitude < 0.0001f) angularAxis = car.transform.right;
            Vector3 angularDelta = angularAxis * angularForce;

            car.TargetApplyImpact(car.connectionToClient, velocityDelta, angularDelta);
        }
    }
}
