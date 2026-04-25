using System.IO;
using Mirror;
using UnityEditor;
using UnityEngine;
using Dio.Player;

namespace Dio.Player.EditorTools
{
    /// Builds the test Car prefab as actual editable assets (body + 4 wheel
    /// colliders + visuals + NetworkIdentity + NetworkTransformReliable +
    /// SphericalGravity + ArcadeCarController + DioCar).
    ///
    /// Re-running overwrites Assets/Dio/Prefabs/Car.prefab. This is the single
    /// source of truth for the car for both solo testing and networked play.
    public static class CarPrefabBuilder
    {
        const string PrefabsDir = "Assets/Dio/Prefabs";
        const string CarPrefabPath = PrefabsDir + "/Car.prefab";

        [MenuItem("Tools/Dio/Build/Car Prefab")]
        public static GameObject BuildCarPrefab()
        {
            EnsureDir(PrefabsDir);

            var root = new GameObject("Car");
            root.tag = "Untagged";

            // -- Rigidbody --
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 800f;
            rb.linearDamping = 0.1f;
            rb.angularDamping = 0.4f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.useGravity = false; // SphericalGravity drives gravity.

            // -- Body visual + collider --
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(1.6f, 0.7f, 3.2f);
            body.transform.localPosition = new Vector3(0, 0.6f, 0);
            // Body collider provides a chassis hull for car↔car / car↔obstacle collisions.
            // Wheel colliders only handle the ground contact — without a body collider, two cars
            // would clip straight through each other.
            // (Cube primitive already gives us a BoxCollider; tune its size to match the visual.)
            // Material applied later for color tinting.

            var cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cabin.name = "Cabin";
            cabin.transform.SetParent(root.transform, false);
            cabin.transform.localScale = new Vector3(1.2f, 0.5f, 1.4f);
            cabin.transform.localPosition = new Vector3(0, 1.15f, -0.2f);
            // Remove cabin collider — only the body cube counts for collisions.
            Object.DestroyImmediate(cabin.GetComponent<BoxCollider>());

            // -- Wheels --
            var fl = BuildWheel(root.transform, new Vector3(-0.8f, 0.4f,  1.3f), "FL");
            var fr = BuildWheel(root.transform, new Vector3( 0.8f, 0.4f,  1.3f), "FR");
            var rl = BuildWheel(root.transform, new Vector3(-0.8f, 0.4f, -1.3f), "RL");
            var rr = BuildWheel(root.transform, new Vector3( 0.8f, 0.4f, -1.3f), "RR");

            // -- Mirror identity + transform sync --
            var ni = root.AddComponent<NetworkIdentity>();
            // NetworkTransformReliable: simple, snapshots position/rotation. Good enough for M1.
            // Phase B (PredictedRigidbody) replaces this for the local player.
            var ntype = System.Type.GetType("Mirror.NetworkTransformReliable, Mirror.Components")
                     ?? System.Type.GetType("Mirror.NetworkTransformUnreliable, Mirror.Components")
                     ?? System.Type.GetType("Mirror.NetworkTransform, Mirror.Components");
            if (ntype != null) root.AddComponent(ntype);
            else Debug.LogWarning("[Dio] No NetworkTransform variant found — install Mirror.Components or wire it manually.");

            // -- Dio components --
            var grav = root.AddComponent<SphericalGravity>();
            grav.gravity = 30f;
            grav.alignSpeed = 8f;

            var ctrl = root.AddComponent<ArcadeCarController>();
            ctrl.readLocalInput = false; // DioCar drives this over the network.
            ctrl.frontAxle = new ArcadeCarController.Axle
            {
                left = fl.collider, right = fr.collider,
                leftVisual = fl.visual, rightVisual = fr.visual,
                steers = true, drives = false, brakes = true,
            };
            ctrl.rearAxle = new ArcadeCarController.Axle
            {
                left = rl.collider, right = rr.collider,
                leftVisual = rl.visual, rightVisual = rr.visual,
                steers = false, drives = true, brakes = true,
            };

            root.AddComponent<DioCar>();
            root.AddComponent<Dio.Powerups.States.CarStateMachine>();
            root.AddComponent<Dio.Powerups.PowerupHolder>();

            // -- Save prefab --
            if (File.Exists(CarPrefabPath)) AssetDatabase.DeleteAsset(CarPrefabPath);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, CarPrefabPath);
            Object.DestroyImmediate(root);

            Debug.Log($"[Dio] Built car prefab at {CarPrefabPath}");
            return prefab;
        }

        struct WheelRig { public WheelCollider collider; public Transform visual; }

        static WheelRig BuildWheel(Transform parent, Vector3 localPos, string name)
        {
            var wcGo = new GameObject(name + "_WC");
            wcGo.transform.SetParent(parent, false);
            wcGo.transform.localPosition = localPos;
            var wc = wcGo.AddComponent<WheelCollider>();
            wc.radius = 0.4f;
            wc.suspensionDistance = 0.25f;
            wc.mass = 20f;
            wc.wheelDampingRate = 0.25f;

            var fwd = wc.forwardFriction;
            fwd.extremumSlip = 0.4f; fwd.extremumValue = 1f;
            fwd.asymptoteSlip = 0.8f; fwd.asymptoteValue = 0.5f;
            fwd.stiffness = 2.0f;
            wc.forwardFriction = fwd;

            var side = wc.sidewaysFriction;
            side.extremumSlip = 0.2f; side.extremumValue = 1f;
            side.asymptoteSlip = 0.5f; side.asymptoteValue = 0.75f;
            side.stiffness = 2.4f; // high → no spinouts
            wc.sidewaysFriction = side;

            var spring = wc.suspensionSpring;
            spring.spring = 35000; spring.damper = 4500; spring.targetPosition = 0.5f;
            wc.suspensionSpring = spring;

            // Visual — cylinder, rotated so its axis is along X (since cylinder's default is Y).
            var visualGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visualGo.name = name + "_Vis";
            visualGo.transform.SetParent(parent, false);
            visualGo.transform.localPosition = localPos;
            visualGo.transform.localRotation = Quaternion.Euler(0, 0, 90);
            visualGo.transform.localScale = new Vector3(0.8f, 0.2f, 0.8f);
            // Wheels don't own collisions — wheel colliders do.
            Object.DestroyImmediate(visualGo.GetComponent<CapsuleCollider>());

            return new WheelRig { collider = wc, visual = visualGo.transform };
        }

        static void EnsureDir(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                Directory.CreateDirectory(path);
                AssetDatabase.Refresh();
            }
        }
    }
}
