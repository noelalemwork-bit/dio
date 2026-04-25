using UnityEngine;

namespace Dio.Player
{
    /// Pulls a Rigidbody toward a planet origin and gently aligns its up axis
    /// with the surface normal. Drop on any dynamic body that should be
    /// "stuck to" the planet (cars, dropped pickups, etc.).
    ///
    /// Planet lookup order:
    ///   1. Inspector-assigned `planet`.
    ///   2. `RaceBootstrap.CurrentPlanet` (set when the race scene is built).
    ///   3. A GameObject named "Planet" anywhere in the scene.
    /// We retry the lookup on FixedUpdate until found, so cars that spawn
    /// before the planet exists pick it up the moment it appears.
    [RequireComponent(typeof(Rigidbody))]
    public class SphericalGravity : MonoBehaviour
    {
        [Tooltip("Planet transform. If null, looked up at runtime via RaceBootstrap.CurrentPlanet or a GameObject named 'Planet'.")]
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
            TryFindPlanet();
        }

        void TryFindPlanet()
        {
            if (planet != null) return;
            var bootstrapPlanet = Dio.Level.RaceBootstrap.CurrentPlanet;
            if (bootstrapPlanet != null) { planet = bootstrapPlanet.transform; return; }
            var byName = GameObject.Find("Planet");
            if (byName != null) planet = byName.transform;
        }

        void FixedUpdate()
        {
            if (planet == null)
            {
                TryFindPlanet();
                if (planet == null) return;
            }

            Vector3 up = PlanetUp;

            _rb.AddForce(-up * gravity, ForceMode.Acceleration);

            Quaternion fromTo = Quaternion.FromToRotation(_rb.rotation * Vector3.up, up);
            Quaternion target = fromTo * _rb.rotation;
            Quaternion next = Quaternion.Slerp(_rb.rotation, target, alignSpeed * Time.fixedDeltaTime);
            _rb.MoveRotation(next);
        }
    }
}
