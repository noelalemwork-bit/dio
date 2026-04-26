using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Dio.Player;

namespace Dio.Player.EditorTools
{
    /// One-shot tool that wires the Kenney FBX cars under
    /// `Assets/3d world/Kenney FBX Cars/` into VehicleProfile assets under
    /// `Assets/Dio/Vehicles/`. Each profile drops a body mesh + four wheel
    /// meshes pulled from the matching FBX, plus tuned scale + physics so
    /// every car feels distinct (kart = nimble + light, truck = heavy +
    /// stable, race = fast + slidey, etc).
    ///
    /// Why we generate instead of authoring by hand:
    ///   * The FBX importer hashes mesh names to fileIDs, so the same names
    ///     across the whole pack share IDs (every body is the same fileID,
    ///     every wheel is the same fileID, etc). Hand-authored YAML easily
    ///     points at the wrong FBX guid, which is exactly what the previous
    ///     5 profiles in this folder were doing — they referenced a stale
    ///     guid and nothing rendered. AssetDatabase lookups by name +
    ///     loaded mesh objects skip that whole class of bug.
    ///   * `CarPrefabBuilder.BuildAllCarPrefabs` then walks `Vehicles/*.asset`
    ///     and produces a prefab per profile, which the network manager
    ///     pulls a random entry from on race start.
    public static class KenneyProfileAuthor
    {
        const string FbxRoot = "Assets/3d world/Kenney FBX Cars";
        // Profiles live under a gitignored Resources/ folder. Each peer
        // re-authors them locally (`Tools/Dio/Build/Author Kenney Vehicle
        // Profiles`) so the asset content is identical across machines.
        // We sync by ASSET FILENAME (`profile.name`), never by array index
        // or GUID, so `.meta` divergence between machines is irrelevant.
        const string GeneratedRoot = "Assets/Dio/_Generated";
        const string ProfilesDir   = GeneratedRoot + "/Resources/Vehicles";
        const string LegacyProfilesDir = "Assets/Dio/Vehicles";
        const string LegacyResourcesDir = "Assets/Dio/Resources/Vehicles";

        // Each entry encodes the design-side knobs we care about per car —
        // visual scale + physics + which Kenney source asset supplies the
        // body mesh. Wheel meshes always come from the same FBX (Kenney
        // packs ship coherent wheels-with-body), but if `wheelFbx` is non-
        // null we override to use a different FBX's wheels.
        struct ProfileSpec
        {
            public string Slug;          // file name & Vehicles asset name
            public string Display;       // shown in lobby
            public string FbxName;       // the body FBX, no extension
            public string WheelFbx;      // null = same as body FBX
            public Vector3 BodyScale;
            public Vector3 BodyOffset;
            public bool   IncludeCabin;
            public Vector3 CabinScale;
            public Vector3 CabinOffset;
            public float WheelRadius;
            public float WheelWidth;
            public Vector3 FL, FR, RL, RR;
            public float Mass;
            public float ForwardStiff;
            public float SidewaysStiff;
            public float SuspensionSpring;
            public float SuspensionDamper;
        }

        static readonly ProfileSpec[] Specs = new ProfileSpec[]
        {
            // 1) Default kart — nimble, slidey, classic Mario-Kart feel.
            new ProfileSpec
            {
                Slug = "KenneyKart",
                Display = "Kart",
                FbxName = "kart-oobi",
                BodyScale = new Vector3(2.0f, 2.0f, 2.0f),
                BodyOffset = new Vector3(0f, 0.50f, 0f),
                IncludeCabin = false,
                WheelRadius = 0.40f, WheelWidth = 0.30f,
                FL = new Vector3(-0.85f, 0f,  1.10f),
                FR = new Vector3( 0.85f, 0f,  1.10f),
                RL = new Vector3(-0.85f, 0f, -1.10f),
                RR = new Vector3( 0.85f, 0f, -1.10f),
                Mass = 600f,
                ForwardStiff = 2.2f, SidewaysStiff = 2.2f,
                SuspensionSpring = 32000f, SuspensionDamper = 4200f,
            },
            // 2) Sedan-sports — middle-of-the-road balance.
            new ProfileSpec
            {
                Slug = "KenneySedan",
                Display = "Sedan",
                FbxName = "sedan-sports",
                BodyScale = new Vector3(1.5f, 1.5f, 1.5f),
                BodyOffset = new Vector3(0f, 0.45f, 0f),
                IncludeCabin = false,
                WheelRadius = 0.38f, WheelWidth = 0.28f,
                FL = new Vector3(-0.85f, 0f,  1.30f),
                FR = new Vector3( 0.85f, 0f,  1.30f),
                RL = new Vector3(-0.85f, 0f, -1.30f),
                RR = new Vector3( 0.85f, 0f, -1.30f),
                Mass = 850f,
                ForwardStiff = 2.0f, SidewaysStiff = 2.4f,
                SuspensionSpring = 35000f, SuspensionDamper = 4500f,
            },
            // 3) Race car — top speed, less grip on corners.
            new ProfileSpec
            {
                Slug = "KenneyRace",
                Display = "Race",
                FbxName = "race",
                WheelFbx = "wheel-racing",
                BodyScale = new Vector3(1.5f, 1.5f, 1.5f),
                BodyOffset = new Vector3(0f, 0.40f, 0f),
                IncludeCabin = false,
                WheelRadius = 0.36f, WheelWidth = 0.28f,
                FL = new Vector3(-0.85f, 0f,  1.40f),
                FR = new Vector3( 0.85f, 0f,  1.40f),
                RL = new Vector3(-0.85f, 0f, -1.40f),
                RR = new Vector3( 0.85f, 0f, -1.40f),
                Mass = 700f,
                ForwardStiff = 2.4f, SidewaysStiff = 1.8f, // slidey corners
                SuspensionSpring = 38000f, SuspensionDamper = 5000f,
            },
            // 4) SUV — heavy, stable, slower acceleration.
            new ProfileSpec
            {
                Slug = "KenneySUV",
                Display = "SUV",
                FbxName = "suv-luxury",
                BodyScale = new Vector3(1.5f, 1.5f, 1.5f),
                BodyOffset = new Vector3(0f, 0.55f, 0f),
                IncludeCabin = false,
                WheelRadius = 0.42f, WheelWidth = 0.30f,
                FL = new Vector3(-0.95f, 0f,  1.30f),
                FR = new Vector3( 0.95f, 0f,  1.30f),
                RL = new Vector3(-0.95f, 0f, -1.30f),
                RR = new Vector3( 0.95f, 0f, -1.30f),
                Mass = 1100f,
                ForwardStiff = 2.0f, SidewaysStiff = 2.6f,
                SuspensionSpring = 42000f, SuspensionDamper = 5500f,
            },
            // 5) Truck — slow but unstoppable.
            new ProfileSpec
            {
                Slug = "KenneyTruck",
                Display = "Truck",
                FbxName = "truck",
                WheelFbx = "wheel-truck",
                BodyScale = new Vector3(1.5f, 1.5f, 1.5f),
                BodyOffset = new Vector3(0f, 0.55f, 0f),
                IncludeCabin = false,
                WheelRadius = 0.42f, WheelWidth = 0.32f,
                FL = new Vector3(-0.95f, 0f,  1.40f),
                FR = new Vector3( 0.95f, 0f,  1.40f),
                RL = new Vector3(-0.95f, 0f, -1.40f),
                RR = new Vector3( 0.95f, 0f, -1.40f),
                Mass = 1400f,
                ForwardStiff = 2.0f, SidewaysStiff = 2.8f,
                SuspensionSpring = 50000f, SuspensionDamper = 6500f,
            },
            // 6) Hatchback-sports — small + zippy variant.
            new ProfileSpec
            {
                Slug = "KenneyHatchback",
                Display = "Hatchback",
                FbxName = "hatchback-sports",
                BodyScale = new Vector3(1.5f, 1.5f, 1.5f),
                BodyOffset = new Vector3(0f, 0.40f, 0f),
                IncludeCabin = false,
                WheelRadius = 0.36f, WheelWidth = 0.28f,
                FL = new Vector3(-0.80f, 0f,  1.20f),
                FR = new Vector3( 0.80f, 0f,  1.20f),
                RL = new Vector3(-0.80f, 0f, -1.20f),
                RR = new Vector3( 0.80f, 0f, -1.20f),
                Mass = 750f,
                ForwardStiff = 2.2f, SidewaysStiff = 2.2f,
                SuspensionSpring = 33000f, SuspensionDamper = 4400f,
            },
            // 7) Police — tuned like sedan but with grill mesh as bonus geometry.
            new ProfileSpec
            {
                Slug = "KenneyPolice",
                Display = "Police",
                FbxName = "police",
                BodyScale = new Vector3(1.5f, 1.5f, 1.5f),
                BodyOffset = new Vector3(0f, 0.45f, 0f),
                IncludeCabin = false,
                WheelRadius = 0.38f, WheelWidth = 0.28f,
                FL = new Vector3(-0.85f, 0f,  1.30f),
                FR = new Vector3( 0.85f, 0f,  1.30f),
                RL = new Vector3(-0.85f, 0f, -1.30f),
                RR = new Vector3( 0.85f, 0f, -1.30f),
                Mass = 900f,
                ForwardStiff = 2.0f, SidewaysStiff = 2.4f,
                SuspensionSpring = 36000f, SuspensionDamper = 4700f,
            },
        };

        [MenuItem("Tools/Dio/Build/Author Kenney Vehicle Profiles")]
        public static void Author()
        {
            EnsureDir(GeneratedRoot);
            EnsureDir(GeneratedRoot + "/Resources");
            EnsureDir(ProfilesDir);
            // Wipe every legacy / stale profile location so the active
            // pool is JUST what _Generated/Resources/Vehicles/ holds. This
            // keeps the runtime `Resources.LoadAll<VehicleProfile>("Vehicles")`
            // pool deterministic — no leftover tracked profile sneaking in
            // and shifting the sort order on one machine vs another.
            foreach (var dir in new[] { LegacyProfilesDir, LegacyResourcesDir })
            {
                if (!AssetDatabase.IsValidFolder(dir)) continue;
                var stale = AssetDatabase.FindAssets("t:VehicleProfile", new[] { dir });
                foreach (var g in stale) AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(g));
            }
            // Same for any leftover profile inside the active dir from a
            // prior Author run — Specs[] is the single source of truth.
            {
                var stale = AssetDatabase.FindAssets("t:VehicleProfile", new[] { ProfilesDir });
                foreach (var g in stale) AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(g));
            }

            int count = 0;
            foreach (var spec in Specs)
            {
                var bodyMesh = LoadMesh(spec.FbxName, "body") ?? LoadMesh(spec.FbxName, spec.FbxName);
                var wheelMesh = LoadWheelMesh(spec);
                if (bodyMesh == null) { Debug.LogWarning($"[Dio] Kenney FBX missing body mesh: {spec.FbxName}"); continue; }
                if (wheelMesh == null) Debug.LogWarning($"[Dio] Kenney FBX missing wheel mesh for {spec.FbxName}");

                string path = $"{ProfilesDir}/{spec.Slug}.asset";
                var profile = AssetDatabase.LoadAssetAtPath<VehicleProfile>(path);
                if (profile == null)
                {
                    profile = ScriptableObject.CreateInstance<VehicleProfile>();
                    AssetDatabase.CreateAsset(profile, path);
                }
                profile.bodyMesh = bodyMesh;
                profile.bodyScale = spec.BodyScale;
                profile.bodyOffset = spec.BodyOffset;
                profile.bodyMaterial = LoadColormapMaterial();
                profile.addBodyCollider = true;
                profile.includeCabin = spec.IncludeCabin;
                profile.cabinMesh = null;
                profile.cabinScale = spec.CabinScale;
                profile.cabinOffset = spec.CabinOffset;
                profile.cabinMaterial = null;
                profile.wheelMesh = wheelMesh;
                profile.wheelMaterial = LoadColormapMaterial();
                profile.wheelRadius = spec.WheelRadius;
                profile.wheelWidth = spec.WheelWidth;
                // The Kenney FBX wheel meshes are authored small; scale the
                // visual by 2.2× across the board so they sit nicely in the
                // wheel wells without changing collision radius.
                profile.wheelVisualScale = 2.2f;
                profile.frontLeftXZ = spec.FL;
                profile.frontRightXZ = spec.FR;
                profile.rearLeftXZ = spec.RL;
                profile.rearRightXZ = spec.RR;
                profile.mass = spec.Mass;
                profile.linearDamping = 0.1f;
                profile.angularDamping = 0.4f;
                profile.suspensionDistance = 0.25f;
                profile.suspensionSpring = spec.SuspensionSpring;
                profile.suspensionDamper = spec.SuspensionDamper;
                profile.suspensionTargetPos = 0.5f;
                profile.forwardStiffness = spec.ForwardStiff;
                profile.sidewaysStiffness = spec.SidewaysStiff;

                EditorUtility.SetDirty(profile);
                count++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Dio] Authored {count} Kenney vehicle profiles in {ProfilesDir}.");
        }

        // Walks the FBX's loaded sub-assets and finds the first Mesh whose
        // name matches `meshName`. Returns null if the FBX or mesh doesn't
        // exist — caller decides how to fall back.
        static Mesh LoadMesh(string fbxName, string meshName)
        {
            string path = $"{FbxRoot}/{fbxName}.fbx";
            var subs = AssetDatabase.LoadAllAssetsAtPath(path);
            if (subs == null) return null;
            foreach (var s in subs)
            {
                if (s is Mesh m && m.name == meshName) return m;
            }
            return null;
        }

        // Wheel resolution: prefer the override FBX (e.g. wheel-racing) when
        // the spec asks for one; otherwise pull `wheel-front-left` from the
        // body's own FBX so wheels match the body's visual scale.
        static Mesh LoadWheelMesh(ProfileSpec spec)
        {
            if (!string.IsNullOrEmpty(spec.WheelFbx))
                return LoadMesh(spec.WheelFbx, spec.WheelFbx);
            return LoadMesh(spec.FbxName, "wheel-front-left");
        }

        // The Kenney pack ships a single `colormap.png` everything indexes
        // into. Try to find or build a runtime material that uses it on a
        // URP/Lit shader so all car bodies render with the proper baked
        // colours.
        static Material _cachedColormapMat;
        static Material LoadColormapMaterial()
        {
            if (_cachedColormapMat != null) return _cachedColormapMat;
            string matPath = "Assets/Dio/Prefabs/Materials/KenneyColormap.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (m == null)
            {
                EnsureDir("Assets/Dio/Prefabs/Materials");
                Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                m = new Material(sh) { name = "KenneyColormap" };
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{FbxRoot}/Textures/colormap.png");
                if (tex != null)
                {
                    if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                    if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
                }
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.4f);
                AssetDatabase.CreateAsset(m, matPath);
            }
            _cachedColormapMat = m;
            return m;
        }

        static void EnsureDir(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            System.IO.Directory.CreateDirectory(path);
            AssetDatabase.Refresh();
        }
    }
}
