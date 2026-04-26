using UnityEngine;

namespace Dio.Player
{
    /// Designer-facing description of a vehicle. CarPrefabBuilder consumes this
    /// to produce a Car prefab; null fields fall back to procedural primitives
    /// (cube body, cylinder wheels) so an empty profile still builds a working
    /// car.
    ///
    /// All wheel positions are in CHASSIS-LOCAL space, with the chassis pivot
    /// representing the *bottom* of the car (where wheels touch the ground).
    /// The builder computes the WheelCollider GameObject's actual Y from
    /// `wheelRadius + suspensionDistance * suspensionTargetPos`, so authoring
    /// is just "where on the chassis floor plate does this wheel sit".
    [CreateAssetMenu(menuName = "Dio/Vehicle Profile", fileName = "VehicleProfile")]
    public class VehicleProfile : ScriptableObject
    {
        // Identity is the ASSET FILENAME (`profile.name`). No displayName
        // field — every machine resolves a profile by filename and is
        // therefore guaranteed to look up the same content regardless of
        // .meta GUID or array order. See VehicleProfileApplicator for how
        // sync uses `profile.name` directly.

        [Header("Body (mesh + extra collider hull)")]
        public Mesh bodyMesh;                 // null → primitive Cube
        public Vector3 bodyScale  = new Vector3(1.6f, 0.7f, 3.2f);
        public Vector3 bodyOffset = new Vector3(0f, 0.6f, 0f);
        public Material bodyMaterial;         // null → default procedural CarBodyCamo
        public bool addBodyCollider = true;

        [Header("Cabin (optional)")]
        public bool includeCabin = true;
        public Mesh cabinMesh;
        public Vector3 cabinScale  = new Vector3(1.2f, 0.5f, 1.4f);
        public Vector3 cabinOffset = new Vector3(0f, 1.15f, -0.2f);
        public Material cabinMaterial;        // null → reuse body material

        [Header("Wheels")]
        public Mesh wheelMesh;                // null → primitive Cylinder w/ 90° Z offset
        public Material wheelMaterial;        // null → default procedural CarWheel
        [Tooltip("Physical wheel radius. The visual mesh is scaled to match this radius x2 in the wheel-axis-perpendicular plane.")]
        public float wheelRadius = 0.4f;
        [Tooltip("Wheel width along the axle (X) in world units.")]
        public float wheelWidth = 0.2f;
        [Tooltip("Multiplier on the visual wheel mesh size only. WheelCollider radius / suspension are unchanged, so the car still collides like a wheelRadius-sized wheel — useful when an FBX author intended chunkier-looking wheels than the physics radius implies.")]
        public float wheelVisualScale = 1f;

        [Header("Wheel chassis-local positions (Y is computed from radius + suspension)")]
        public Vector3 frontLeftXZ  = new Vector3(-0.8f, 0f,  1.3f);
        public Vector3 frontRightXZ = new Vector3( 0.8f, 0f,  1.3f);
        public Vector3 rearLeftXZ   = new Vector3(-0.8f, 0f, -1.3f);
        public Vector3 rearRightXZ  = new Vector3( 0.8f, 0f, -1.3f);

        [Header("Physics")]
        public float mass = 800f;
        public float linearDamping = 0.1f;
        public float angularDamping = 0.4f;

        [Header("Suspension")]
        public float suspensionDistance = 0.25f;
        public float suspensionSpring = 35000f;
        public float suspensionDamper = 4500f;
        [Range(0f, 1f)] public float suspensionTargetPos = 0.5f;

        [Header("Friction (high stiffness = no spinout, arcade)")]
        public float forwardStiffness = 2.0f;
        public float sidewaysStiffness = 2.4f;

        // The Y coord that the WheelCollider GameObject sits at (chassis-local)
        // so that the wheel CENTER at rest sits at exactly y = wheelRadius
        // and the wheel BOTTOM kisses the chassis floor (y = 0).
        public float WheelColliderY => wheelRadius + suspensionDistance * suspensionTargetPos;
    }
}
