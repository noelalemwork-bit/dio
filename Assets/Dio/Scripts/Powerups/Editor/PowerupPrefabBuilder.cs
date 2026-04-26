using System.IO;
using Mirror;
using TMPro;
using UnityEditor;
using UnityEngine;
using Dio.Level.Obstacles;
using Dio.Powerups;

namespace Dio.Powerups.EditorTools
{
    /// Builds placeholder networked prefabs for every powerup-related thing
    /// (obstacles, projectiles, the pickup box). Visuals are colored spheres
    /// + a TMP 3D label so we can SEE which is which during testing.
    /// Re-running overwrites all of them.
    public static class PowerupPrefabBuilder
    {
        const string PrefabsDir = "Assets/Dio/Prefabs/Powerups";
        const string MaterialsDir = "Assets/Dio/Prefabs/Powerups/Materials";

        [MenuItem("Tools/Dio/Build/Powerup Prefabs")]
        public static void BuildAll()
        {
            if (TMP_Settings.defaultFontAsset == null)
            {
                Debug.LogError("[Dio] TMP Essential Resources missing. Window > TextMeshPro > Import TMP Essential Resources, then re-run Build.");
                return;
            }

            EnsureDir(PrefabsDir);
            EnsureDir(MaterialsDir);

            BuildBox();

            BuildBanana();
            BuildOilSlick();
            BuildGreenShell();
            BuildBlueShell();
            BuildBobomb();
            BuildTornado();

            AssetDatabase.SaveAssets();
            Debug.Log("[Dio] Built powerup prefabs.");
        }

        /// Get-or-create a saved Material asset with the given color. Avoids
        /// the "instantiating material in edit mode" warning by going through
        /// `sharedMaterial` and saving the result as a real .mat asset.
        static Material GetOrCreateColoredMat(string materialName, Color color)
        {
            EnsureDir(MaterialsDir);
            string path = $"{MaterialsDir}/{materialName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard");
                mat = new Material(sh);
                mat.color = color;
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.color != color)
            {
                mat.color = color;
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        /// Like GetOrCreateColoredMat but lets the caller pick the shader
        /// (e.g. Dio/MysteryBox), so the saved .mat persists shader properties
        /// that the standard "color" field can't express.
        static Material GetOrCreateMatNamed(string materialName, Shader shader, Color baseColor)
        {
            EnsureDir(MaterialsDir);
            string path = $"{MaterialsDir}/{materialName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader != null ? shader : Shader.Find("Standard"));
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);
                else mat.color = baseColor;
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader && shader != null)
            {
                mat.shader = shader;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        static GameObject NewSphere(string name, Color color, float radius, bool isTrigger)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.localScale = Vector3.one * radius * 2f;
            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = GetOrCreateColoredMat("PU_" + name, color);
            var col = go.GetComponent<SphereCollider>();
            col.isTrigger = isTrigger;
            col.radius = 0.5f;
            return go;
        }

        static void AddLabel(Transform parent, string text, float yOffset)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0, yOffset, 0);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 4f;
            tmp.color = Color.white;
            tmp.font = TMP_Settings.defaultFontAsset; // explicit, in case TMP defaults aren't initialized yet
            go.AddComponent<Dio.Common.Billboard>();
        }

        static GameObject SaveAndDestroy(GameObject go, string assetName)
        {
            var path = $"{PrefabsDir}/{assetName}.prefab";
            // SaveAsPrefabAsset overwrites in place when `path` already
            // exists — preserving the asset GUID + Mirror assetId. Delete-
            // then-save would mint a fresh GUID every rebuild and break
            // every running client whose spawnPrefabs list still points
            // at the old GUID (race-time "Failed to spawn server object").
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        static void EnsureDir(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                Directory.CreateDirectory(path);
                AssetDatabase.Refresh();
            }
        }

        // ---------- Concrete builders ----------

        static GameObject BuildBox()
        {
            // Mystery box: a spinning cube with the Dio/MysteryBox shader (yellow
            // base + slow rainbow shimmer) and a question-mark label floating
            // above it. The whole thing is networked via NetworkIdentity +
            // NetworkTransform; the spin itself is local-only (each peer
            // animates independently) since it's purely cosmetic.
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "PowerupBox";
            go.transform.localScale = Vector3.one * 1.4f;
            var box = go.GetComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = Vector3.one * 1.5f;

            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                var sh = Shader.Find("Dio/MysteryBox") ?? Shader.Find("Standard");
                rend.sharedMaterial = GetOrCreateMatNamed("PU_MysteryBox", sh, new Color(0.95f, 0.78f, 0.20f));
            }

            go.AddComponent<NetworkIdentity>();
            Dio.Net.EditorTools.NetworkTransformConfigurer.Configure(go, fastCar:false);
            go.AddComponent<PowerupBox>();
            go.AddComponent<Dio.Common.SpinningProp>();
            AddLabel(go.transform, "?", 1.7f);
            return SaveAndDestroy(go, "PowerupBox");
        }

        static GameObject BuildBanana()
        {
            // Bigger + brighter so it reads on the track. Body sphere is the
            // visible peel; trigger collider is sized to match the visible
            // mesh (radius 1.0 = local diameter 1, scaled by transform.scale).
            var go = NewSphere("Banana", new Color(1.00f, 0.92f, 0.20f, 1f), 1.0f, true);
            // Bump the trigger's *radius* to match the visual: a low-poly
            // sphere primitive has mesh radius 0.5, transform scale = 2 (from
            // NewSphere) → world radius 1.0. SphereCollider radius 0.5 ×
            // transform scale 2 = world radius 1.0. Already matches.
            go.AddComponent<NetworkIdentity>();
            Dio.Net.EditorTools.NetworkTransformConfigurer.Configure(go, fastCar:false);
            go.AddComponent<BananaObstacle>();
            AddLabel(go.transform, "Banana", 1.4f);
            return SaveAndDestroy(go, "Banana");
        }

        static GameObject BuildOilSlick()
        {
            // Bigger + glossy. The visual is a flat-ish disc, but the trigger
            // collider is intentionally TALL (localspace y=20 × scale 0.15 =
            // 3 m world height) so it catches the car's body collider at
            // y=0.25-0.95. Without this the slick visually overlapped the
            // car but the trigger was too thin to fire.
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "OilSlick";
            go.transform.localScale = new Vector3(5f, 0.15f, 5f);
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                // Slightly purple-black, glossy. URP/Lit handles smoothness via _Smoothness.
                Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                EnsureDir(MaterialsDir);
                string path = MaterialsDir + "/PU_OilSlick.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(sh) { name = "PU_OilSlick" };
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.shader = sh;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.04f, 0.03f, 0.06f));
                else mat.color = new Color(0.04f, 0.03f, 0.06f);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.92f);
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.0f);
                EditorUtility.SetDirty(mat);
                r.sharedMaterial = mat;
            }
            var col = go.GetComponent<CapsuleCollider>();
            Object.DestroyImmediate(col);
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            // Trigger volume tall enough to catch the car body even though
            // the visual disc is paper-thin.
            box.size = new Vector3(2f, 20f, 2f);
            go.AddComponent<NetworkIdentity>();
            Dio.Net.EditorTools.NetworkTransformConfigurer.Configure(go, fastCar:false);
            var ob = go.AddComponent<OilSlickObstacle>();
            ob.lifetime = 8f;
            AddLabel(go.transform, "OIL", 2.0f);
            return SaveAndDestroy(go, "OilSlick");
        }

        static GameObject BuildGreenShell()
        {
            var go = NewSphere("GreenShell", new Color(0.36f, 0.86f, 0.40f, 1f), 0.5f, false);
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = 5f;
            rb.linearDamping = 0.2f;
            go.AddComponent<NetworkIdentity>();
            Dio.Net.EditorTools.NetworkTransformConfigurer.Configure(go, fastCar:false);
            go.AddComponent<GreenShellObstacle>();
            AddLabel(go.transform, "Green", 1.0f);
            return SaveAndDestroy(go, "GreenShell");
        }

        static GameObject BuildBlueShell()
        {
            var go = NewSphere("BlueShell", new Color(0.30f, 0.55f, 0.95f, 1f), 0.7f, false);
            // Blue shell glides along the surface — kinematic, not physics-driven.
            // The script positions it directly each frame.
            Object.DestroyImmediate(go.GetComponent<SphereCollider>());
            var trigger = go.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 0.5f;
            go.AddComponent<NetworkIdentity>();
            Dio.Net.EditorTools.NetworkTransformConfigurer.Configure(go, fastCar:false);
            go.AddComponent<BlueShellObstacle>();
            AddLabel(go.transform, "Blue", 1.5f);
            return SaveAndDestroy(go, "BlueShell");
        }

        static GameObject BuildBobomb()
        {
            // Bigger + trigger collider. With server-side cars kinematic,
            // the bomb has to use trigger overlap to detect impacts (two
            // kinematic bodies don't fire OnCollisionEnter).
            var go = NewSphere("Bobomb", new Color(0.10f, 0.10f, 0.12f, 1f), 0.9f, true);
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;
            rb.mass = 8f;
            go.AddComponent<NetworkIdentity>();
            Dio.Net.EditorTools.NetworkTransformConfigurer.Configure(go, fastCar:false);
            go.AddComponent<BobombObstacle>();
            AddLabel(go.transform, "BOOM", 1.4f);
            return SaveAndDestroy(go, "Bobomb");
        }

        static GameObject BuildTornado()
        {
            // Empty root — TornadoObstacle generates the funnel mesh at runtime
            // (procedural cylinder with wavy radius + vertex-colour gradient).
            // The prefab carries only NetworkIdentity, NetworkTransform, the
            // trigger collider that catches passing cars, and the obstacle
            // script with its tunables. Visuals are spawned per-peer.
            var go = new GameObject("Tornado");
            // Trigger volume — capsule centered on the funnel midline. Sized
            // a bit wider than the funnel maxRadius so the hitbox grazes
            // cars driving past, not just driving through dead-center.
            var col = go.AddComponent<CapsuleCollider>();
            col.isTrigger = true;
            col.height = 2f;
            col.radius = 0.7f;
            col.center = new Vector3(0f, 1f, 0f);
            col.direction = 1; // Y axis
            go.AddComponent<NetworkIdentity>();
            Dio.Net.EditorTools.NetworkTransformConfigurer.Configure(go, fastCar:false);
            var t = go.AddComponent<TornadoObstacle>();
            t.lifetime = 10f;
            t.spinDegPerSec = 540f;
            t.forwardSpeed = 16f;
            t.height = 2f;
            t.maxRadius = 0.4f;
            return SaveAndDestroy(go, "Tornado");
        }

    }

}
