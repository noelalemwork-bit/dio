using Mirror;
using UnityEngine;
using Dio.Player;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Tornado: wanders deterministically along a circle on the planet surface.
    /// On overlap, applies a Tumble state and pops the car upward.
    /// Position is computed from time + seed so every peer reproduces the same
    /// path — networking is still on (NetworkTransform) so late-joiners catch up.
    public class TornadoObstacle : Obstacle
    {
        public Transform planet;
        public float planetRadius = 200f;
        public float orbitRadius = 30f;       // distance from center of its wandering circle
        public float angularSpeed = 0.5f;     // rad/s along the circle
        public float pushUpForce = 80f;
        public float tumbleDuration = 1.0f;

        [SyncVar] public Vector3 circleCenterDir;
        [SyncVar] public Vector3 tangentBasis;
        [SyncVar] public Vector3 binormalBasis;
        [SyncVar] public double startServerTime;

        public override void OnStartServer()
        {
            base.OnStartServer();
            startServerTime = NetworkTime.time;
            if (circleCenterDir == Vector3.zero) circleCenterDir = Vector3.up;
            BuildBasisIfNeeded();
        }

        public override void OnStartClient() => BuildBasisIfNeeded();

        void BuildBasisIfNeeded()
        {
            if (tangentBasis != Vector3.zero) return;
            var n = circleCenterDir.normalized;
            tangentBasis = Vector3.Cross(n, Vector3.up);
            if (tangentBasis.sqrMagnitude < 1e-3f) tangentBasis = Vector3.Cross(n, Vector3.right);
            tangentBasis.Normalize();
            binormalBasis = Vector3.Cross(n, tangentBasis).normalized;
        }

        void Update()
        {
            float t = (float)(NetworkTime.time - startServerTime);
            float ang = t * angularSpeed;
            Vector3 offset = (tangentBasis * Mathf.Cos(ang) + binormalBasis * Mathf.Sin(ang)) * orbitRadius;
            Vector3 dir = (circleCenterDir.normalized * planetRadius + offset).normalized;
            transform.position = (planet != null ? planet.position : Vector3.zero) + dir * planetRadius;
            // Tornado stands "up" along the surface normal.
            transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
        }

        void OnTriggerEnter(Collider other)
        {
            if (!isServer) return;
            var car = CarOf(other);
            if (car == null) return;
            ApplyState(car, new TumbleState(), tumbleDuration);
            var rb = car.GetComponent<Rigidbody>();
            if (rb != null) rb.AddForce(transform.up * pushUpForce, ForceMode.VelocityChange);
        }
    }
}
