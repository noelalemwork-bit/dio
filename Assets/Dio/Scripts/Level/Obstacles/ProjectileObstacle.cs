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
    public class GreenShellObstacle : Obstacle
    {
        public float launchSpeed = 35f;
        public int maxBounces = 2;
        public float tumbleDuration = 1.0f;

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
                ApplyState(car, new TumbleState(), tumbleDuration);
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

    /// Bob-omb: forward physics projectile with a fuse → AOE explosion. On
    /// explosion, every car within radius gets tumbled (and the bomb despawns).
    public class BobombObstacle : Obstacle
    {
        public float launchSpeed = 22f;
        public float fuseSeconds = 2.0f;
        public float explosionRadius = 7f;
        public float explosionImpulse = 25f;
        public float tumbleDuration = 1.2f;

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
            _rb.linearVelocity = transform.forward * launchSpeed;
            Invoke(nameof(Explode), fuseSeconds);
        }

        void OnCollisionEnter(Collision col)
        {
            if (!isServer || _exploded) return;
            var car = CarOf(col.collider);
            if (car != null && IsOwnerImmune(car)) return;
            Explode();
        }

        [Server]
        void Explode()
        {
            if (_exploded) return;
            _exploded = true;
            var hits = Physics.OverlapSphere(transform.position, explosionRadius);
            foreach (var h in hits)
            {
                var car = CarOf(h);
                if (car == null) continue;
                ApplyState(car, new TumbleState(), tumbleDuration);
                var rb = car.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    var dir = (car.transform.position - transform.position);
                    if (dir.sqrMagnitude < 0.01f) dir = Random.onUnitSphere;
                    rb.AddForce(dir.normalized * explosionImpulse, ForceMode.VelocityChange);
                }
            }
            ServerDespawn();
        }
    }

    /// Blue Shell: tracks the leader. We approximate "leader" as the car whose
    /// arc-length progress along the geodesic chain is highest. For M1 we just
    /// pick the car with the smallest forward-axis-angle to the next checkpoint;
    /// see plan.md §11 for the proper progress tracker.
    public class BlueShellObstacle : Obstacle
    {
        public float speed = 50f;
        public float explosionRadius = 9f;
        public float explosionImpulse = 30f;
        public float tumbleDuration = 1.4f;
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
            float bestAlt = float.NegativeInfinity;
            foreach (var ni in NetworkServer.spawned.Values)
            {
                var car = ni.GetComponent<DioCar>();
                if (car == null) continue;
                // Placeholder: pick the car furthest from the spawn direction (transform.forward).
                float metric = Vector3.Dot(car.transform.position.normalized, transform.forward);
                if (metric > bestAlt) { bestAlt = metric; best = car; }
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
            var hits = Physics.OverlapSphere(transform.position, explosionRadius);
            foreach (var h in hits)
            {
                var car = CarOf(h);
                if (car == null) continue;
                ApplyState(car, new TumbleState(), tumbleDuration);
                var rb = car.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    var dir = (car.transform.position - transform.position);
                    if (dir.sqrMagnitude < 0.01f) dir = Random.onUnitSphere;
                    rb.AddForce(dir.normalized * explosionImpulse, ForceMode.VelocityChange);
                }
            }
            ServerDespawn();
        }
    }
}
