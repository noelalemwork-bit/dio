using UnityEngine;
using Mirror;

namespace Mirror.Examples.Tanks
{
    [RequireComponent(typeof(Rigidbody))]
    public class Car : NetworkBehaviour
    {
        [Header("Components")]
        public Rigidbody rb;
        public Animator animator;
        public TextMesh healthBar;
        public Transform turret;

        [Header("Car Movement (Physics)")]
        public float motorForce = 2000f;
        public float maxSpeed = 15f;
        public float steerForce = 150f;
        [Range(0, 1)] 
        public float driftFriction = 0.95f; // 1 = no drift, 0 = ice-like sliding
        public Vector3 centerOfMassOffset = new Vector3(0, -0.5f, 0); // Lowers center of mass to prevent tipping on sharp turns

        [Header("Firing")]
        public KeyCode shootKey = KeyCode.Space;
        public GameObject projectilePrefab;
        public Transform projectileMount;

        [Header("Stats")]
        [SyncVar] public int health = 5;

        void Start()
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            
            // Lower the center of mass to make it handle like a racing car without flipping
            if (rb != null)
            {
                rb.centerOfMass += centerOfMassOffset;
            }
        }

        public override void OnStartLocalPlayer()
        {
            // The local player controls the physics, so it must not be kinematic
            if (rb != null)
            {
                rb.isKinematic = false;
            }
        }

        public override void OnStartClient()
        {
            name = $"Car[{netId}|{(isLocalPlayer ? "local" : "remote")}]";
            
            // Remote players are updated by NetworkTransform, so they MUST be kinematic
            // to avoid physics fighting the network updates.
            if (rb != null && !isLocalPlayer)
            {
                rb.isKinematic = true;
            }
        }

        public override void OnStartServer()
        {
            name = $"Car[{netId}|server]";
        }

        void Update()
        {
            // always update health bar.
            if (healthBar != null)
                healthBar.text = new string('-', health);

            if (!Application.isFocused) return;

            if (isLocalPlayer)
            {
                if (Input.GetKeyDown(shootKey))
                {
                    CmdFire();
                }

                if (turret != null) RotateTurret();
            }
        }

        void FixedUpdate()
        {
            if (!Application.isFocused) return;

            if (isLocalPlayer && rb != null)
            {
                float vertical = Input.GetAxis("Vertical");
                float horizontal = Input.GetAxis("Horizontal");

                // 1. Forward / Backward Acceleration
                if (Mathf.Abs(vertical) > 0.05f)
                {
                    rb.AddForce(transform.forward * vertical * motorForce * Time.fixedDeltaTime, ForceMode.Acceleration);
                }

                // 2. Speed Limit
                if (rb.linearVelocity.magnitude > maxSpeed)
                {
                    rb.linearVelocity = rb.linearVelocity.normalized * maxSpeed;
                }

                // 3. Steering
                // Car steering reverses when reversing, so we multiply by sign of vertical input
                float moveDir = vertical >= 0 ? 1 : -1;
                float speedPercent = rb.linearVelocity.magnitude / maxSpeed;
                
                // Only allow turning if we are moving or trying to move
                if (rb.linearVelocity.magnitude > 0.5f || Mathf.Abs(vertical) > 0.1f)
                {
                    float turnFactor = Mathf.Clamp01(speedPercent + 0.2f); // turn slightly even at low speeds
                    Quaternion turn = Quaternion.Euler(0f, horizontal * steerForce * moveDir * turnFactor * Time.fixedDeltaTime, 0f);
                    rb.MoveRotation(rb.rotation * turn);
                }

                // 4. Lateral Friction (Drift Control)
                ApplyLateralFriction();

                if (animator != null)
                {
                    animator.SetBool("Moving", rb.linearVelocity.magnitude > 0.1f);
                }
            }
        }

        void ApplyLateralFriction()
        {
            // Convert velocity to local space
            Vector3 localVelocity = transform.InverseTransformDirection(rb.linearVelocity);
            
            // Reduce X (sideways) velocity to act like rubber tires gripping the road
            localVelocity.x *= (1f - driftFriction);
            
            // Convert back to world space
            rb.linearVelocity = transform.TransformDirection(localVelocity);
        }

        [Command]
        void CmdFire()
        {
            if (projectilePrefab != null && projectileMount != null)
            {
                GameObject projectile = Instantiate(projectilePrefab, projectileMount.position, projectileMount.rotation);
                NetworkServer.Spawn(projectile);
                RpcOnFire();
            }
        }

        [ClientRpc]
        void RpcOnFire()
        {
            if (animator != null) animator.SetTrigger("Shoot");
        }

        void RotateTurret()
        {
            if (turret == null) return;
            
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 100))
            {
                Vector3 lookRotation = new Vector3(hit.point.x, turret.position.y, hit.point.z);
                turret.LookAt(lookRotation);
            }
        }
    }
}
