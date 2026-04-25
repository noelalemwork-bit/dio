using System.IO;
using Mirror;
using UnityEditor;
using UnityEngine;
using Dio.Player;

namespace Dio.Player.EditorTools
{
    /// Generates Assets/Dio/Prefabs/Car.prefab from an optional VehicleProfile.
    /// Empty profile (or no profile at all) → all-procedural defaults: cube
    /// body, cylinder wheels, procedural Dio shaders.
    ///
    /// CHASSIS-LOCAL CONVENTIONS used here (and matching VehicleProfile):
    ///   * y = 0 is the road plane — wheels touch the ground at this height
    ///     when the chassis pivot is on the road surface.
    ///   * WheelCollider GameObjects sit at y = wheelRadius + suspensionDistance
    ///     × suspensionTargetPos, so the wheel CENTER at rest is at y = radius
    ///     and its bottom at y = 0.
    public static class CarPrefabBuilder
    {
        const string PrefabsDir   = "Assets/Dio/Prefabs";
        const string MaterialsDir = "Assets/Dio/Prefabs/Materials";
        const string ProfilesDir  = "Assets/Dio/Vehicles";
        const string CarPrefabPath = PrefabsDir + "/Car.prefab";
        const string DefaultProfilePath = ProfilesDir + "/DefaultKart.asset";

        [MenuItem("Tools/Dio/Build/Car Prefab")]
        public static GameObject BuildCarPrefab() => BuildCarPrefab(GetOrCreateDefaultProfile());

        public static GameObject BuildCarPrefab(VehicleProfile profile)
        {
            EnsureDir(PrefabsDir);
            EnsureDir(MaterialsDir);
            if (profile == null) profile = GetOrCreateDefaultProfile();

            var root = new GameObject(string.IsNullOrEmpty(profile.displayName) ? "Car" : profile.displayName);

            // -- Rigidbody --
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = profile.mass;
            rb.linearDamping = profile.linearDamping;
            rb.angularDamping = profile.angularDamping;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.useGravity = false; // SphericalGravity drives gravity.

            // -- Body + cabin --
            var bodyMat  = profile.bodyMaterial  != null ? profile.bodyMaterial  : GetOrCreateBodyMaterial();
            var cabinMat = profile.cabinMaterial != null ? profile.cabinMaterial : bodyMat;
            BuildPart(root.transform, "Body", profile.bodyMesh, profile.bodyOffset, profile.bodyScale, bodyMat,
                      addCollider: profile.addBodyCollider);
            if (profile.includeCabin)
                BuildPart(root.transform, "Cabin", profile.cabinMesh, profile.cabinOffset, profile.cabinScale, cabinMat,
                          addCollider: false);

            // -- Wheels --
            var wheelMat = profile.wheelMaterial != null ? profile.wheelMaterial : GetOrCreateWheelMaterial();
            var fl = BuildWheel(root.transform, profile, profile.frontLeftXZ,  "FL", wheelMat);
            var fr = BuildWheel(root.transform, profile, profile.frontRightXZ, "FR", wheelMat);
            var rl = BuildWheel(root.transform, profile, profile.rearLeftXZ,   "RL", wheelMat);
            var rr = BuildWheel(root.transform, profile, profile.rearRightXZ,  "RR", wheelMat);

            // -- Mirror identity + transform sync --
            root.AddComponent<NetworkIdentity>();
            // World-space + every-tick streaming + tight precision = best
            // smoothness for fast-moving cars. See plan.md and Mirror docs:
            // https://mirror-networking.gitbook.io/docs/manual/components/network-transform
            // For racing, onlySyncOnChange=false avoids restart-stutter from
            // quantization gaps; world coords avoid drift if the car is ever
            // reparented (e.g. to a moving platform).
            Dio.Net.EditorTools.NetworkTransformConfigurer.Configure(root, fastCar:true);

            // -- Dio components --
            var grav = root.AddComponent<SphericalGravity>();
            grav.gravity = 30f;
            grav.alignSpeed = 8f;

            var ctrl = root.AddComponent<ArcadeCarController>();
            ctrl.readLocalInput = false;
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

            Debug.Log($"[Dio] Built car prefab '{profile.displayName}' at {CarPrefabPath}");
            return prefab;
        }

        // ---------- helpers ----------

        struct WheelRig { public WheelCollider collider; public Transform visual; }

        static void BuildPart(Transform parent, string name, Mesh mesh, Vector3 offset, Vector3 scale, Material material, bool addCollider)
        {
            GameObject go;
            if (mesh != null)
            {
                go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                if (addCollider)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    mc.convex = true;
                }
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                if (!addCollider)
                {
                    var bc = go.GetComponent<BoxCollider>();
                    if (bc != null) Object.DestroyImmediate(bc);
                }
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = offset;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = material;
        }

        static WheelRig BuildWheel(Transform parent, VehicleProfile p, Vector3 chassisXZ, string name, Material wheelMat)
        {
            // WheelCollider GameObject — at chassis-local (x, WheelColliderY, z),
            // so the wheel CENTER at rest is at y = wheelRadius and the bottom
            // touches y = 0 (road plane = chassis pivot).
            var wcGo = new GameObject(name + "_WC");
            wcGo.transform.SetParent(parent, false);
            wcGo.transform.localPosition = new Vector3(chassisXZ.x, p.WheelColliderY, chassisXZ.z);
            var wc = wcGo.AddComponent<WheelCollider>();
            wc.radius = p.wheelRadius;
            wc.suspensionDistance = p.suspensionDistance;
            wc.mass = 20f;
            wc.wheelDampingRate = 0.25f;

            var fwd = wc.forwardFriction;
            fwd.extremumSlip = 0.4f; fwd.extremumValue = 1f;
            fwd.asymptoteSlip = 0.8f; fwd.asymptoteValue = 0.5f;
            fwd.stiffness = p.forwardStiffness;
            wc.forwardFriction = fwd;

            var side = wc.sidewaysFriction;
            side.extremumSlip = 0.2f; side.extremumValue = 1f;
            side.asymptoteSlip = 0.5f; side.asymptoteValue = 0.75f;
            side.stiffness = p.sidewaysStiffness;
            wc.sidewaysFriction = side;

            var spring = wc.suspensionSpring;
            spring.spring = p.suspensionSpring;
            spring.damper = p.suspensionDamper;
            spring.targetPosition = p.suspensionTargetPos;
            wc.suspensionSpring = spring;

            // -- Visual root: ArcadeCarController writes WC world pose here.
            // The cylinder mesh is a child with a baked 90° Z rotation so its
            // long axis (default Y) becomes the wheel spin axis (X). Scaling
            // the mesh maps the cylinder's natural (1u diameter, 2u height)
            // to (2*radius, width).
            var visualRoot = new GameObject(name + "_Vis");
            visualRoot.transform.SetParent(parent, false);
            visualRoot.transform.localPosition = wcGo.transform.localPosition;

            GameObject mesh;
            if (p.wheelMesh != null)
            {
                mesh = new GameObject("Mesh", typeof(MeshFilter), typeof(MeshRenderer));
                mesh.GetComponent<MeshFilter>().sharedMesh = p.wheelMesh;
                mesh.transform.SetParent(visualRoot.transform, false);
                // Custom meshes must already be authored with X as axle. Apply
                // user's wheel-mesh extra scale + radius/width as a fallback
                // on top of any built-in mesh scale.
                mesh.transform.localScale = new Vector3(p.wheelWidth, p.wheelRadius * 2f, p.wheelRadius * 2f);
            }
            else
            {
                mesh = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                mesh.name = "Mesh";
                mesh.transform.SetParent(visualRoot.transform, false);
                mesh.transform.localRotation = Quaternion.Euler(0, 0, 90);
                // Cylinder primitive: 1u diameter, 2u height. We want diameter = radius*2 and height = width.
                // After 90° around Z, mesh-local Y is the axle, X/Z are diameter axes.
                float diameter = p.wheelRadius * 2f;
                mesh.transform.localScale = new Vector3(diameter, p.wheelWidth * 0.5f, diameter);
                Object.DestroyImmediate(mesh.GetComponent<CapsuleCollider>());
            }
            var rend = mesh.GetComponent<MeshRenderer>();
            if (rend != null) rend.sharedMaterial = wheelMat;

            return new WheelRig { collider = wc, visual = visualRoot.transform };
        }

        // ---------- material + profile asset helpers ----------

        static VehicleProfile GetOrCreateDefaultProfile()
        {
            EnsureDir(ProfilesDir);
            var existing = AssetDatabase.LoadAssetAtPath<VehicleProfile>(DefaultProfilePath);
            if (existing != null) return existing;
            var p = ScriptableObject.CreateInstance<VehicleProfile>();
            p.displayName = "Default Kart";
            AssetDatabase.CreateAsset(p, DefaultProfilePath);
            AssetDatabase.SaveAssets();
            return p;
        }

        static Material GetOrCreateBodyMaterial()
        {
            string path = MaterialsDir + "/CarBodyCamo.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            var sh = Shader.Find("Dio/CarBodyCamo");
            if (sh == null)
            {
                Debug.LogWarning("[Dio] Dio/CarBodyCamo shader not found, falling back to Standard.");
                sh = Shader.Find("Standard");
            }
            m = new Material(sh) { name = "CarBodyCamo" };
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material GetOrCreateWheelMaterial()
        {
            string path = MaterialsDir + "/CarWheel.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            var sh = Shader.Find("Dio/CarWheel");
            if (sh == null)
            {
                Debug.LogWarning("[Dio] Dio/CarWheel shader not found, falling back to Standard.");
                sh = Shader.Find("Standard");
            }
            m = new Material(sh) { name = "CarWheel" };
            AssetDatabase.CreateAsset(m, path);
            return m;
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
