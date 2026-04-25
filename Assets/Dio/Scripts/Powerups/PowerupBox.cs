using Mirror;
using UnityEngine;

namespace Dio.Powerups
{
    /// A pickup box on the track. On overlap with a car (server-side), grants
    /// a random powerup to the holder and despawns. Respawns after a delay.
    public class PowerupBox : NetworkBehaviour
    {
        [SyncVar(hook = nameof(OnAvailableChanged))] public bool available = true;
        [Tooltip("If non-None, this box always grants this kind. None = pick at random from PowerupRegistry.")]
        [SyncVar] public PowerupKind forceKind = PowerupKind.None;
        [Tooltip("Boxes sharing the same non-zero groupId can only be consumed once per player. 0 = no constraint.")]
        [SyncVar] public int groupId = 0;
        public float respawnDelay = 5f;

        Renderer[] _renderers;

        void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>();
            // Ensure the trigger collider is, well, a trigger. Works whether the
            // builder used a Sphere, Box, or other collider.
            var c = GetComponent<Collider>();
            if (c != null) c.isTrigger = true;
        }

        public override void OnStartClient() => OnAvailableChanged(available, available);

        void OnTriggerEnter(Collider other)
        {
            if (!isServer || !available) return;
            var car = other.GetComponentInParent<Dio.Player.DioCar>();
            if (car == null) return;
            var holder = car.GetComponent<PowerupHolder>();
            if (holder == null) return;

            // Group constraint: a single mid-track row hands out one powerup per car.
            if (groupId != 0 && holder.HasConsumedGroup(groupId)) return;

            var kind = forceKind != PowerupKind.None ? forceKind : PowerupRegistry.RandomFromBox();
            if (kind == PowerupKind.None) return;

            if (kind == PowerupKind.TripleBoost) holder.Grant(PowerupKind.Boost, 3);
            else holder.Grant(kind);

            if (groupId != 0) holder.MarkGroupConsumed(groupId);

            available = false;
            Invoke(nameof(Respawn), respawnDelay);
        }

        [Server]
        void Respawn() { available = true; }

        void OnAvailableChanged(bool _, bool now)
        {
            foreach (var r in _renderers) if (r != null) r.enabled = now;
            var c = GetComponent<Collider>();
            if (c != null) c.enabled = now;
        }
    }
}
