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
        // _Generated/ is gitignored. Everything authored by build menus
        // lives there so two machines never end up with conflicting .meta
        // GUIDs for the same logical asset (which used to break Mirror's
        // assetId lookup at race start).
        const string GeneratedRoot = "Assets/Dio/_Generated";
        const string PrefabsDir   = "Assets/Dio/Prefabs";  // committed: Car.prefab base only
        const string CarsDir      = GeneratedRoot + "/Prefabs/Cars";
        const string MaterialsDir = GeneratedRoot + "/Prefabs/Materials";
        // Profiles live under a Resources/ folder so they're loadable at
        // runtime by every peer in a multiplayer session — sync is by
        // ASSET FILENAME, never by index/guid.
        const string ProfilesDir       = GeneratedRoot + "/Resources/Vehicles";
        const string LegacyProfilesDir = "Assets/Dio/Vehicles";
        const string LegacyResourcesDir = "Assets/Dio/Resources/Vehicles";
        // Default path used when no profile is supplied — kept stable for any
        // legacy code that still references DioNetworkManager.carPrefab.
        const string CarPrefabPath = PrefabsDir + "/Car.prefab";
        const string DefaultProfilePath = ProfilesDir + "/DefaultKart.asset";

        [MenuItem("Tools/Dio/Build/Car Prefab")]
        public static GameObject BuildCarPrefab() => BuildCarPrefab(GetOrCreateDefaultProfile());

        // Build every VehicleProfile asset under Assets/Dio/Vehicles into its
        // own per-profile prefab under Assets/Dio/Prefabs/Cars/. Returns the
        // built prefabs in deterministic order — caller can wire the array
        // straight into DioNetworkManager.carPrefabs so the game randomly
        // assigns one of these on race start.
        [MenuItem("Tools/Dio/Build/All Car Prefabs")]
        public static GameObject[] BuildAllCarPrefabs()
        {
            var profiles = LoadAllProfiles();
            var built = new System.Collections.Generic.List<GameObject>(profiles.Length);
            foreach (var p in profiles)
            {
                if (p == null) continue;
                var go = BuildCarPrefab(p, CarPathFor(p));
                if (go != null) built.Add(go);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Dio] Built {built.Count} car prefabs from {profiles.Length} profiles.");
            return built.ToArray();
        }

        public static VehicleProfile[] LoadAllProfiles()
        {
            EnsureDir(ProfilesDir);
            // Scan only the active _Generated/Resources/Vehicles location.
            // Legacy folders are wiped by the author tool, so anything found
            // in them is stale and shouldn't influence the prefab build.
            var search = new[] { ProfilesDir };
            var guids = AssetDatabase.FindAssets("t:VehicleProfile", search);
            var paths = new System.Collections.Generic.List<string>(guids.Length);
            foreach (var g in guids) paths.Add(AssetDatabase.GUIDToAssetPath(g));
            paths.Sort(); // deterministic order
            var list = new System.Collections.Generic.List<VehicleProfile>(paths.Count);
            foreach (var path in paths)
            {
                var p = AssetDatabase.LoadAssetAtPath<VehicleProfile>(path);
                if (p != null) list.Add(p);
            }
            return list.ToArray();
        }

        // Resolve the per-profile prefab path using the profile's ASSET
        // FILENAME (`profile.name`). Asset filenames are stable across
        // machines and re-builds — there's no array-index ambiguity, no
        // displayName drift. `Car_KenneyKart.prefab` is the same prefab
        // whether you author on the host's machine or a guest's.
        public static string CarPathFor(VehicleProfile profile)
        {
            string slug = profile != null ? profile.name : "Unknown";
            return $"{CarsDir}/Car_{slug}.prefab";
        }

        // (No longer used — prefab paths key off profile.name, which is
        // already a clean asset filename. Kept here for historical context;
        // safe to remove on a future cleanup pass.)

        public static GameObject BuildCarPrefab(VehicleProfile profile)
            => BuildCarPrefab(profile, CarPrefabPath);

        public static GameObject BuildCarPrefab(VehicleProfile profile, string outputPath)
        {
            EnsureDir(PrefabsDir);
            EnsureDir(MaterialsDir);
            if (profile == null) profile = GetOrCreateDefaultProfile();

            var root = new GameObject(string.IsNullOrEmpty(profile.name) ? "Car" : profile.name);

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
            // Owner-side safety net: snap back to last known on-track pose
            // if the car drops below the planet surface or launches off
            // into space. Runs only on the owner; remote peers ignore it.
            root.AddComponent<OffRoadRespawn>();
            // Owner-side: 2-second timer that snaps a car off the track edge
            // back to the last crossed checkpoint at zero speed, oriented
            // along the next outgoing edge. The lower guard rails make
            // falling off a real risk — this keeps the failure forgiving.
            root.AddComponent<OffTrackRespawn>();

            // -- Boost trail (visual fx) --
            // Two short streamers behind the rear wheels; emitting is toggled
            // by CarBoostTrail when SpeedBoost / Star state is active.
            var trailLeft  = BuildBoostTrail(root.transform, "BoostTrailL", new Vector3(-0.6f, 0.2f, -1.5f));
            var trailRight = BuildBoostTrail(root.transform, "BoostTrailR", new Vector3( 0.6f, 0.2f, -1.5f));
            var boostTrail = root.AddComponent<CarBoostTrail>();
            boostTrail.trail = trailLeft;
            // Pair the second renderer up via a child component too — using a
            // flat array of TrailRenderers would be cleaner but a single ref
            // keeps the inspector small. The right-side trail toggles via its
            // own CarBoostTrail (cheap, no extra logic).
            var rightSync = trailRight.gameObject.AddComponent<CarBoostTrail>();
            rightSync.trail = trailRight;

            // -- Save prefab --
            // CRITICAL: do NOT delete first. SaveAsPrefabAsset overwrites
            // an existing prefab IN PLACE, preserving its asset GUID and
            // therefore its Mirror assetId. Deleting + recreating mints a
            // fresh GUID every rebuild, which invalidates every scene
            // reference + every connected peer's spawnPrefabs list — that
            // was producing the "Failed to spawn server object, did you
            // forget to add it to the NetworkManager? assetId=…" errors at
            // race start when a client built before the latest rebuild
            // tried to instantiate a freshly-stamped GUID.
            EnsureDir(System.IO.Path.GetDirectoryName(outputPath).Replace('\\', '/'));
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, outputPath);
            Object.DestroyImmediate(root);

            Debug.Log($"[Dio] Built car prefab '{profile.name}' at {outputPath}");
            return prefab;
        }

        // ---------- helpers ----------

        struct WheelRig { public WheelCollider collider; public Transform visual; }

        // Tail flame trail — color shifts orange → yellow → transparent. Runs
        // through a `Sprites/Default` material because URP's Lit doesn't
        // support TrailRenderer's vertex-color path; using the bundled
        // unlit Sprites shader is the standard "particle/trail" choice.
        static TrailRenderer BuildBoostTrail(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var t = go.AddComponent<TrailRenderer>();
            t.time = 0.45f;
            t.startWidth = 0.55f;
            t.endWidth = 0.05f;
            t.minVertexDistance = 0.12f;
            t.emitting = false;
            t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.receiveShadows = false;
            // Color gradient: hot orange-red → fading yellow.
            var grad = new Gradient();
            grad.SetKeys(
                new[] {
                    new GradientColorKey(new Color(1f, 0.55f, 0.10f), 0f),
                    new GradientColorKey(new Color(1f, 0.92f, 0.35f), 0.55f),
                    new GradientColorKey(new Color(1f, 0.30f, 0.10f), 1f),
                },
                new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0.65f, 0.5f), new GradientAlphaKey(0f, 1f) }
            );
            t.colorGradient = grad;
            // Material — try URP particles first, fall back to legacy.
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                  ?? Shader.Find("Sprites/Default")
                  ?? Shader.Find("Particles/Standard Unlit");
            if (sh != null)
            {
                var mat = new Material(sh) { name = "BoostTrail" };
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                t.material = mat;
            }
            return t;
        }

        static void BuildPart(Transform parent, string name, Mesh mesh, Vector3 offset, Vector3 scale, Material material, bool addCollider)
        {
            GameObject go;
            UnityEngine.Collider createdCol = null;
            if (mesh != null)
            {
                go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                if (addCollider)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    mc.convex = true;
                    createdCol = mc;
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
                else
                {
                    createdCol = go.GetComponent<BoxCollider>();
                }
            }
            // Mario-Kart-style bumping: a slightly bouncy + slick chassis
            // PhysicMaterial. Bounce+frictionless on the body keeps the
            // contact response punchy without bleeding speed when two cars
            // graze. Wheel friction is unaffected (WheelCollider has its own
            // friction curves and ignores this material).
            if (createdCol != null) createdCol.sharedMaterial = GetOrCreateBumperMaterial();
            go.transform.SetParent(parent, false);
            go.transform.localPosition = offset;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = material;
        }

#if UNITY_6000_0_OR_NEWER
        static PhysicsMaterial _cachedBumperMat;
        static PhysicsMaterial GetOrCreateBumperMaterial()
        {
            if (_cachedBumperMat != null) return _cachedBumperMat;
            EnsureDir(MaterialsDir);
            string path = MaterialsDir + "/CarBumper.physicMaterial";
            var existing = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (existing != null) { _cachedBumperMat = existing; return existing; }
            var pm = new PhysicsMaterial("CarBumper")
            {
                bounciness = 0.55f,
                dynamicFriction = 0.05f,
                staticFriction = 0.05f,
                bounceCombine = PhysicsMaterialCombine.Maximum,
                frictionCombine = PhysicsMaterialCombine.Minimum,
            };
            AssetDatabase.CreateAsset(pm, path);
            _cachedBumperMat = pm;
            return pm;
        }
#else
        static PhysicMaterial _cachedBumperMat;
        static PhysicMaterial GetOrCreateBumperMaterial()
        {
            if (_cachedBumperMat != null) return _cachedBumperMat;
            EnsureDir(MaterialsDir);
            string path = MaterialsDir + "/CarBumper.physicMaterial";
            var existing = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
            if (existing != null) { _cachedBumperMat = existing; return existing; }
            var pm = new PhysicMaterial("CarBumper")
            {
                bounciness = 0.55f,
                dynamicFriction = 0.05f,
                staticFriction = 0.05f,
                bounceCombine = PhysicMaterialCombine.Maximum,
                frictionCombine = PhysicMaterialCombine.Minimum,
            };
            AssetDatabase.CreateAsset(pm, path);
            _cachedBumperMat = pm;
            return pm;
        }
#endif

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

            // `wheelVisualScale` is applied to the visual mesh ONLY — the
            // WheelCollider above keeps the unscaled `p.wheelRadius` so
            // physics behaves identically. Lets a profile (e.g. the Kenney
            // pack) draw chunkier wheels than the collision radius would
            // suggest without messing up handling.
            float vScale = Mathf.Max(0.01f, p.wheelVisualScale);
            GameObject mesh;
            if (p.wheelMesh != null)
            {
                mesh = new GameObject("Mesh", typeof(MeshFilter), typeof(MeshRenderer));
                mesh.GetComponent<MeshFilter>().sharedMesh = p.wheelMesh;
                mesh.transform.SetParent(visualRoot.transform, false);
                // Custom meshes must already be authored with X as axle. Apply
                // user's wheel-mesh extra scale + radius/width as a fallback
                // on top of any built-in mesh scale.
                mesh.transform.localScale = new Vector3(p.wheelWidth, p.wheelRadius * 2f, p.wheelRadius * 2f) * vScale;
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
                mesh.transform.localScale = new Vector3(diameter, p.wheelWidth * 0.5f, diameter) * vScale;
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
            // Default profile name = filename slug. No displayName field
            // anymore — the asset filename IS the identity.
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
