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
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
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
            var go = NewSphere("PowerupBox", new Color(1f, 0.85f, 0.2f, 1f), 1.0f, true);
            go.AddComponent<NetworkIdentity>();
            var nt = AddNetworkTransform(go);
            go.AddComponent<PowerupBox>();
            AddLabel(go.transform, "?", 1.5f);
            return SaveAndDestroy(go, "PowerupBox");
        }

        static GameObject BuildBanana()
        {
            var go = NewSphere("Banana", new Color(0.95f, 0.85f, 0.25f, 1f), 0.6f, true);
            go.AddComponent<NetworkIdentity>();
            AddNetworkTransform(go);
            go.AddComponent<BananaObstacle>();
            AddLabel(go.transform, "Banana", 1.0f);
            return SaveAndDestroy(go, "Banana");
        }

        static GameObject BuildOilSlick()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "OilSlick";
            go.transform.localScale = new Vector3(4f, 0.05f, 4f);
            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = GetOrCreateColoredMat("PU_OilSlick", new Color(0.06f, 0.06f, 0.08f, 1f));
            var col = go.GetComponent<CapsuleCollider>();
            // Replace capsule with a flat box trigger.
            Object.DestroyImmediate(col);
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(2f, 0.5f, 2f);
            go.AddComponent<NetworkIdentity>();
            AddNetworkTransform(go);
            var ob = go.AddComponent<OilSlickObstacle>();
            ob.lifetime = 8f;
            AddLabel(go.transform, "Oil", 1.5f);
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
            AddNetworkTransform(go);
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
            AddNetworkTransform(go);
            go.AddComponent<BlueShellObstacle>();
            AddLabel(go.transform, "Blue", 1.5f);
            return SaveAndDestroy(go, "BlueShell");
        }

        static GameObject BuildBobomb()
        {
            var go = NewSphere("Bobomb", new Color(0.15f, 0.15f, 0.18f, 1f), 0.5f, false);
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = 8f;
            go.AddComponent<NetworkIdentity>();
            AddNetworkTransform(go);
            go.AddComponent<BobombObstacle>();
            AddLabel(go.transform, "Bomb", 1.0f);
            return SaveAndDestroy(go, "Bobomb");
        }

        static GameObject BuildTornado()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Tornado";
            go.transform.localScale = new Vector3(4f, 6f, 4f);
            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = GetOrCreateColoredMat("PU_Tornado", new Color(0.7f, 0.85f, 0.95f, 0.6f));
            var col = go.GetComponent<CapsuleCollider>();
            // Convert to trigger.
            col.isTrigger = true;
            go.AddComponent<NetworkIdentity>();
            AddNetworkTransform(go);
            var t = go.AddComponent<TornadoObstacle>();
            t.lifetime = 10f;
            t.orbitRadius = 30f;
            t.angularSpeed = 0.5f;
            AddLabel(go.transform, "Tornado", 7f);
            return SaveAndDestroy(go, "Tornado");
        }

        static Component AddNetworkTransform(GameObject go)
        {
            var ntype = System.Type.GetType("Mirror.NetworkTransformReliable, Mirror.Components")
                     ?? System.Type.GetType("Mirror.NetworkTransformUnreliable, Mirror.Components")
                     ?? System.Type.GetType("Mirror.NetworkTransform, Mirror.Components");
            return ntype != null ? go.AddComponent(ntype) : null;
        }
    }

}
