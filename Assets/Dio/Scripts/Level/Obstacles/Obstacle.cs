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

        /// Convenience: apply a state to a car for a duration.
        [Server]
        protected static void ApplyState(DioCar car, CarState state, float duration)
        {
            if (car == null) return;
            var sm = car.GetComponent<CarStateMachine>();
            if (sm == null) return;
            state.Duration = duration;
            sm.Apply(state);
        }
    }
}
