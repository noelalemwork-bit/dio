using Mirror;
using UnityEngine;
using Dio.Player;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Forward-moving projectile that bounces a few times then despawns
    /// (Green Shell archetype). Server runs the rigidbody; clients see it
    /// via NetworkTransform.
    ///
    /// File deliberately holds ONE MonoBehaviour. Bobomb + BlueShell have
    /// been split out into BobombObstacle.cs / BlueShellObstacle.cs so each
    /// gets its own stable fileID 11500000 in serialized prefabs. (When
    /// they all shared this file, the prefabs of the second / third class
    /// wrote `m_Script: {fileID: 0}` and threw "Missing script" at runtime
    /// inside `Instantiate`.)
    public class GreenShellObstacle : Obstacle
    {
        public float launchSpeed = 35f;
        public int maxBounces = 2;
        public float blindDuration = 5f;

        Rigidbody _rb;
        int _bounces;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
            _rb.useGravity = false; // lives on a sphere; SphericalGravity handles it.
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (lifetime <= 0f) { lifetime = 6f; Invoke(nameof(ServerDespawn), lifetime); }
            _rb.linearVelocity = transform.forward * launchSpeed;
        }

        void OnCollisionEnter(Collision col)
        {
            if (!isServer) return;
            var car = CarOf(col.collider);
            if (car != null && !IsOwnerImmune(car))
            {
                RpcSpawnImpactBurst(transform.position, new Color(0.20f, 0.70f, 1f, 1f), 1.0f);
                // Heavier hit + a real vertical pop. Green shell is now
                // distinct from Blue: it's the BIG SHOVE (lots of horizontal
                // displacement, a satisfying upward bounce, short blind so
                // you can recover quickly from a clean line shot).
                ApplyClientImpact(car, transform.position, 16f, 8f, 7f);
                ApplyState(car, new GreenShellBlindState(), blindDuration);
                if (car.connectionToClient != null)
                    car.TargetFlashScreen(car.connectionToClient, PowerupKind.GreenShell, blindDuration);
                ServerDespawn();
                return;
            }

            _bounces++;
            if (_bounces > maxBounces) ServerDespawn();
            else
            {
                // Reflect velocity around contact normal, preserve speed.
                var v = _rb.linearVelocity;
                var n = col.GetContact(0).normal;
                _rb.linearVelocity = Vector3.Reflect(v, n).normalized * launchSpeed;
            }
        }
    }
}
