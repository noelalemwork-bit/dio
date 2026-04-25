using Mirror;
using UnityEngine;

namespace Dio.Powerups
{
    /// A pickup box on the track. On overlap with a car (server-side), grants
    /// a random powerup to the holder and despawns. Respawns after a delay.
    [RequireComponent(typeof(SphereCollider))]
    public class PowerupBox : NetworkBehaviour
    {
        [SyncVar(hook = nameof(OnAvailableChanged))] public bool available = true;
        public float respawnDelay = 5f;

        Renderer[] _renderers;

        void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>();
            var c = GetComponent<SphereCollider>();
            c.isTrigger = true;
        }

        public override void OnStartClient() => OnAvailableChanged(available, available);

        void OnTriggerEnter(Collider other)
        {
            if (!isServer || !available) return;
            var car = other.GetComponentInParent<Dio.Player.DioCar>();
            if (car == null) return;
            var holder = car.GetComponent<PowerupHolder>();
            if (holder == null) return;

            var kind = PowerupRegistry.RandomFromBox();
            if (kind == PowerupKind.None) return;

            // TripleBoost grants 3 charges of Boost.
            if (kind == PowerupKind.TripleBoost) holder.Grant(PowerupKind.Boost, 3);
            else holder.Grant(kind);

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
