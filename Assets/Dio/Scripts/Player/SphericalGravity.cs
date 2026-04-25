using UnityEngine;

namespace Dio.Player
{
    /// Pulls a Rigidbody toward a planet origin and gently aligns its up axis
    /// with the surface normal. Drop on any dynamic body that should be
    /// "stuck to" the planet (cars, dropped pickups, etc.).
    [RequireComponent(typeof(Rigidbody))]
    public class SphericalGravity : MonoBehaviour
    {
        [Tooltip("Transform the gravity points toward. Defaults to a global 'Planet' tag if null.")]
        public Transform planet;

        [Tooltip("Acceleration toward the planet, in m/s^2.")]
        public float gravity = 30f;

        [Tooltip("How quickly the rigidbody's up axis snaps to the surface normal (per second).")]
        public float alignSpeed = 8f;

        Rigidbody _rb;

        public Vector3 PlanetUp => planet == null
            ? Vector3.up
            : (transform.position - planet.position).normalized;

        public Vector3 PlanetCenter => planet == null ? Vector3.zero : planet.position;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.useGravity = false;

            if (planet == null)
            {
                var go = GameObject.FindWithTag("Planet");
                if (go != null) planet = go.transform;
            }
        }

        void FixedUpdate()
        {
            if (planet == null) return;

            Vector3 up = PlanetUp;

            // Pull toward planet.
            _rb.AddForce(-up * gravity, ForceMode.Acceleration);

            // Smoothly rotate so transform.up == up while preserving forward as much as possible.
            Quaternion fromTo = Quaternion.FromToRotation(_rb.rotation * Vector3.up, up);
            Quaternion target = fromTo * _rb.rotation;
            Quaternion next = Quaternion.Slerp(_rb.rotation, target, alignSpeed * Time.fixedDeltaTime);
            _rb.MoveRotation(next);
        }
    }
}
