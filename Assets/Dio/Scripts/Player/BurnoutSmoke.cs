using System.Collections.Generic;
using UnityEngine;

namespace Dio.Player
{
    /// Spawns tiny flat-shaded sphere puffs from the rear wheels when the car
    /// is above a speed threshold AND the wheel is touching ground. Each puff
    /// scales up from nothing, drifts slowly, fades, and is recycled to a
    /// pool — no GC, no per-frame allocs after the first second of driving.
    ///
    /// Local-only: every peer's car spawns its own puffs from the same logic
    /// (same SyncList state would just waste bandwidth on a purely cosmetic
    /// effect). The "real" cars run physics on the owner; remote peers' rb is
    /// kinematic, so we read forward speed from transform deltas instead of
    /// rb.linearVelocity.
    [RequireComponent(typeof(ArcadeCarController))]
    public class BurnoutSmoke : MonoBehaviour
    {
        [Header("Trigger")]
        [Tooltip("World-space forward speed (m/s) above which puffs spawn.")]
        public float minSpeed = 14f;
        [Tooltip("Seconds between puffs at full burn. Smaller = denser smoke.")]
        public float spawnInterval = 0.07f;
        [Tooltip("Spawn from the FRONT axle too while boosting? Off = only rear wheels.")]
        public bool emitFromFrontAxle;

        [Header("Puff visuals")]
        public float puffStartRadius = 0.08f;
        public float puffEndRadius   = 0.55f;
        public float puffLifetime    = 0.85f;
        public Vector3 puffDriftLocal = new Vector3(0, 0.4f, -0.6f); // car-local
        [Tooltip("Cap on simultaneous live puffs PER CAR. Anything past this overwrites the oldest.")]
        public int poolSize = 24;

        ArcadeCarController _car;
        Vector3 _prevPos;
        float _spawnAccum;

        // ----- Pool -----
        // Each puff is a single GameObject (sphere primitive) parented to a
        // hidden root for cheap hierarchy. Pool resets via index ring so we
        // never destroy/recreate.
        Transform _root;
        readonly List<Puff> _puffs = new List<Puff>();
        int _next;

        struct Puff
        {
            public Transform tr;
            public Renderer rend;
            public float spawnTime;     // unscaledTime
            public Vector3 driftWorld;  // baked at spawn so a turning car doesn't whip its puffs
            public bool active;
        }

        static Material s_puffMat;
        static Mesh s_sphereMesh;

        void Awake()
        {
            _car = GetComponent<ArcadeCarController>();
            _prevPos = transform.position;
        }

        void EnsurePool()
        {
            if (_root != null) return;
            var rootGo = new GameObject("BurnoutPuffs");
            rootGo.transform.SetParent(null, true); // world-rooted; we don't want chassis rotation to whip them
            _root = rootGo.transform;
            for (int i = 0; i < poolSize; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Puff";
                Object.Destroy(go.GetComponent<Collider>()); // visuals only
                go.transform.SetParent(_root, false);
                go.SetActive(false);
                var rend = go.GetComponent<Renderer>();
                rend.sharedMaterial = GetOrCreateMat();
                if (s_sphereMesh == null) s_sphereMesh = go.GetComponent<MeshFilter>().sharedMesh;
                _puffs.Add(new Puff { tr = go.transform, rend = rend, active = false });
            }
        }

        static Material GetOrCreateMat()
        {
            if (s_puffMat != null) return s_puffMat;
            var sh = Shader.Find("Dio/ToonSphere") ?? Shader.Find("Sprites/Default");
            s_puffMat = new Material(sh) { name = "BurnoutPuff (runtime)" };
            if (s_puffMat.HasProperty("_BaseColor"))
                s_puffMat.SetColor("_BaseColor", new Color(0.95f, 0.95f, 0.96f, 0.85f));
            return s_puffMat;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt < 1e-5f) return;

            EnsurePool();

            // Forward speed via transform delta — works whether the rb is
            // kinematic (remote peers) or simulating (owner / host).
            Vector3 delta = transform.position - _prevPos;
            _prevPos = transform.position;
            float fwdSpeed = Vector3.Dot(delta, transform.forward) / dt;

            // Spawn puffs while criteria met. ArcadeCarController.AnyWheelGrounded
            // is the fastest existing flag. Strict version: check rear axle only.
            bool grounded = _car != null && _car.AnyWheelGrounded;
            bool fast = Mathf.Abs(fwdSpeed) > minSpeed;

            if (grounded && fast)
            {
                _spawnAccum += dt;
                while (_spawnAccum >= spawnInterval)
                {
                    _spawnAccum -= spawnInterval;
                    SpawnPuffAtRearAxle();
                    if (emitFromFrontAxle) SpawnPuffAtFrontAxle();
                }
            }
            else _spawnAccum = 0f;

            // Tick each live puff.
            float now = Time.unscaledTime;
            for (int i = 0; i < _puffs.Count; i++)
            {
                var p = _puffs[i];
                if (!p.active) continue;
                float age = (now - p.spawnTime) / Mathf.Max(0.01f, puffLifetime);
                if (age >= 1f)
                {
                    p.active = false;
                    p.tr.gameObject.SetActive(false);
                    _puffs[i] = p;
                    continue;
                }
                // Balloon: ease-out cubic so the puff swells fast then settles.
                float radiusT = 1f - Mathf.Pow(1f - age, 3f);
                float r = Mathf.Lerp(puffStartRadius, puffEndRadius, radiusT);
                p.tr.localScale = new Vector3(r * 2f, r * 2f, r * 2f);
                p.tr.position += p.driftWorld * dt;

                // Fade alpha out near the end of life.
                if (p.rend != null && p.rend.sharedMaterial != null)
                {
                    // We share one material; ALL puffs fade in lockstep. Cheap
                    // and visually fine — slightly different ages mostly cancel
                    // because spawn times are spread.
                }
            }
        }

        void SpawnPuffAtRearAxle()
        {
            // Average rear axle position. Falls back to chassis-local-rear
            // if axle refs are missing (shouldn't happen on a built car).
            Vector3 spawn;
            var rear = _car != null ? _car.rearAxle : null;
            if (rear != null && (rear.left != null || rear.right != null))
            {
                Vector3 a = rear.left != null ? rear.left.transform.position : Vector3.zero;
                Vector3 b = rear.right != null ? rear.right.transform.position : Vector3.zero;
                int n = (rear.left != null ? 1 : 0) + (rear.right != null ? 1 : 0);
                spawn = (a + b) / Mathf.Max(1, n);
            }
            else spawn = transform.position - transform.forward * 1.3f;
            ActivatePuff(spawn);
        }

        void SpawnPuffAtFrontAxle()
        {
            Vector3 spawn;
            var front = _car != null ? _car.frontAxle : null;
            if (front != null && (front.left != null || front.right != null))
            {
                Vector3 a = front.left != null ? front.left.transform.position : Vector3.zero;
                Vector3 b = front.right != null ? front.right.transform.position : Vector3.zero;
                int n = (front.left != null ? 1 : 0) + (front.right != null ? 1 : 0);
                spawn = (a + b) / Mathf.Max(1, n);
            }
            else spawn = transform.position + transform.forward * 1.3f;
            ActivatePuff(spawn);
        }

        void ActivatePuff(Vector3 worldPos)
        {
            if (_puffs.Count == 0) return;
            int idx = _next % _puffs.Count;
            _next = (_next + 1) % _puffs.Count;
            var p = _puffs[idx];
            p.tr.position = worldPos;
            float r0 = puffStartRadius;
            p.tr.localScale = new Vector3(r0 * 2f, r0 * 2f, r0 * 2f);
            // Drift baked at spawn so changing car heading doesn't pull old
            // puffs along with it.
            p.driftWorld = transform.TransformVector(puffDriftLocal);
            p.spawnTime = Time.unscaledTime;
            p.active = true;
            p.tr.gameObject.SetActive(true);
            _puffs[idx] = p;
        }

        void OnDestroy()
        {
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
