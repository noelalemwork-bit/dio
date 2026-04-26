using Mirror;
using UnityEngine;
using Dio.Player;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Bo-bomb: dropped behind the car, fuse-armed → AOE explosion. Every car
    /// within radius gets tumbled + impulse. Stays where it lands (server
    /// makes it kinematic so the bomb doesn't float off without gravity).
    ///
    /// LIVES IN ITS OWN FILE so the prefab importer can resolve a stable
    /// fileID 11500000 for `m_Script`. When this class shared a .cs with
    /// GreenShell + BlueShell, Unity wrote `m_Script: {fileID: 0}` on the
    /// prefab — manifesting at runtime as "Missing script on Bobomb" errors
    /// during Instantiate.
    public class BobombObstacle : Obstacle
    {
        public float fuseSeconds = 2.5f;
        public float explosionRadius = 9f;
        public float explosionImpulse = 20f;
        public float tumbleDuration = 2f;

        Rigidbody _rb;
        bool _exploded;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
            _rb.useGravity = false;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            // Drop in place — the BobombEffect spawns this 3 m behind the car
            // already. Make it kinematic so it stays put regardless of gravity
            // setup (we don't add SphericalGravity to obstacles).
            _rb.isKinematic = true;
            _rb.linearVelocity = Vector3.zero;
            Invoke(nameof(Explode), fuseSeconds);
        }

        // Trigger overlap also explodes early — fires on any car that runs
        // INTO the dropped bomb before the fuse. The collider on the prefab
        // is set to a trigger, so kinematic-vs-kinematic still fires here
        // (server-side cars are kinematic in the client-authority refactor).
        void OnTriggerEnter(Collider other)
        {
            if (!isServer || _exploded) return;
            var car = CarOf(other);
            if (car != null && IsOwnerImmune(car)) return;
            // Any non-immune trigger entry detonates: car hit it, or another
            // shell, etc. The OverlapSphere in Explode catches everyone.
            if (car != null) Explode();
        }

        [Server]
        void Explode()
        {
            if (_exploded) return;
            _exploded = true;
            RpcSpawnImpactBurst(transform.position, new Color(1f, 0.48f, 0.20f, 1f), 1.8f);
            var hits = Physics.OverlapSphere(transform.position, explosionRadius);
            foreach (var h in hits)
            {
                var car = CarOf(h);
                if (car == null) continue;
                // Owner-immunity: don't blow up the player who dropped it.
                if (IsOwnerImmune(car)) continue;
                ApplyState(car, new BobombBlastState(), tumbleDuration);
                ApplyClientImpact(car, transform.position, explosionImpulse, 1.4f, 11f);
            }
            ServerDespawn();
        }
    }
}
