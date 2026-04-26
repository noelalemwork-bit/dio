using UnityEngine;
using Random = UnityEngine.Random;

namespace Dio.Player
{
    /// Apply a VehicleProfile to an already-built Car GameObject at runtime.
    ///
    /// Why this exists: per-variant prefabs (one prefab per VehicleProfile)
    /// looked clean in the editor but each variant had its own asset GUID.
    /// On a multi-machine LAN game, two Unity projects (or a build paired
    /// with an editor) inevitably end up with different `.meta` GUIDs for
    /// the same logical prefab — Mirror's `assetId` is a hash of that GUID,
    /// so the server's `NetworkServer.Spawn(variantPrefab)` sends an
    /// assetId the client can't resolve and you get the
    ///     "Failed to spawn server object, did you forget to add it to the
    ///      NetworkManager?" error chain.
    ///
    /// The fix: spawn ONE `Car.prefab` for every player (single, stable
    /// assetId across machines) and apply the per-player profile locally
    /// from a deterministic Resources scan. The profile's mesh / material
    /// references are local to each peer; the network only carries the
    /// `int profileIndex` SyncVar.
    public static class VehicleProfileApplicator
    {
        public static void Apply(GameObject root, VehicleProfile p)
        {
            if (root == null || p == null) return;
            ApplyBody(root, p);
            ApplyCabin(root, p);
            ApplyWheels(root, p);
            ApplyPhysics(root, p);
        }

        static void ApplyBody(GameObject root, VehicleProfile p)
        {
            var body = root.transform.Find("Body");
            if (body == null) return;
            body.localPosition = p.bodyOffset;
            body.localScale = p.bodyScale;
            var mf = body.GetComponent<MeshFilter>();
            if (mf != null && p.bodyMesh != null) mf.sharedMesh = p.bodyMesh;
            var mr = body.GetComponent<MeshRenderer>();
            if (mr != null && p.bodyMaterial != null) mr.sharedMaterial = p.bodyMaterial;
            // Sync the convex hull collider to the new mesh too — otherwise the
            // car bumps off invisible cube edges from the procedural fallback.
            var mc = body.GetComponent<MeshCollider>();
            if (mc != null && p.bodyMesh != null) mc.sharedMesh = p.bodyMesh;
        }

        static void ApplyCabin(GameObject root, VehicleProfile p)
        {
            var cabin = root.transform.Find("Cabin");
            if (cabin == null) return;
            cabin.gameObject.SetActive(p.includeCabin);
            if (!p.includeCabin) return;
            cabin.localPosition = p.cabinOffset;
            cabin.localScale = p.cabinScale;
            var mf = cabin.GetComponent<MeshFilter>();
            if (mf != null && p.cabinMesh != null) mf.sharedMesh = p.cabinMesh;
            var mr = cabin.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                Material m = p.cabinMaterial != null ? p.cabinMaterial : p.bodyMaterial;
                if (m != null) mr.sharedMaterial = m;
            }
        }

        static void ApplyWheels(GameObject root, VehicleProfile p)
        {
            ApplyOneWheel(root, "FL", p, p.frontLeftXZ);
            ApplyOneWheel(root, "FR", p, p.frontRightXZ);
            ApplyOneWheel(root, "RL", p, p.rearLeftXZ);
            ApplyOneWheel(root, "RR", p, p.rearRightXZ);
        }

        static void ApplyOneWheel(GameObject root, string side, VehicleProfile p, Vector3 chassisXZ)
        {
            var wcGo  = root.transform.Find(side + "_WC");
            var visGo = root.transform.Find(side + "_Vis");
            if (wcGo == null || visGo == null) return;

            // Position both the WC and the visual root at the chassis-local
            // wheel slot, with the WheelCollider's Y derived from the
            // profile's radius + suspension target so the wheel center sits
            // at y = wheelRadius and its contact point at y = 0.
            var pos = new Vector3(chassisXZ.x, p.WheelColliderY, chassisXZ.z);
            wcGo.localPosition = pos;
            visGo.localPosition = pos;

            // Physics: WheelCollider radius / friction / suspension. The
            // VISUAL scale below is independent of these, so a chunky
            // visual wheel still collides like the profile's collision
            // radius — the user's last note ("2.2× visual scale, no
            // physics impact") is honoured.
            var wc = wcGo.GetComponent<WheelCollider>();
            if (wc != null)
            {
                wc.radius = p.wheelRadius;
                wc.suspensionDistance = p.suspensionDistance;
                var fwd = wc.forwardFriction;   fwd.stiffness = p.forwardStiffness;   wc.forwardFriction = fwd;
                var sf  = wc.sidewaysFriction;  sf.stiffness  = p.sidewaysStiffness;  wc.sidewaysFriction = sf;
                var spring = wc.suspensionSpring;
                spring.spring = p.suspensionSpring;
                spring.damper = p.suspensionDamper;
                spring.targetPosition = p.suspensionTargetPos;
                wc.suspensionSpring = spring;
            }

            var meshTr = visGo.Find("Mesh");
            if (meshTr == null) return;
            float vScale = Mathf.Max(0.01f, p.wheelVisualScale);
            var mf = meshTr.GetComponent<MeshFilter>();
            if (p.wheelMesh != null)
            {
                if (mf != null) mf.sharedMesh = p.wheelMesh;
                // Custom FBX wheels are authored with X-axle orientation —
                // no extra rotation. Scale = (width, diameter, diameter) ×
                // visualScale.
                meshTr.localRotation = Quaternion.identity;
                meshTr.localScale = new Vector3(p.wheelWidth, p.wheelRadius * 2f, p.wheelRadius * 2f) * vScale;
            }
            else
            {
                // Procedural fallback: keep the cylinder primitive scaling
                // & 90° Z spin that CarPrefabBuilder uses for the Default
                // Kart so this code path matches its baseline.
                meshTr.localRotation = Quaternion.Euler(0, 0, 90);
                float diameter = p.wheelRadius * 2f;
                meshTr.localScale = new Vector3(diameter, p.wheelWidth * 0.5f, diameter) * vScale;
            }
            var mr = meshTr.GetComponent<MeshRenderer>();
            if (mr != null && p.wheelMaterial != null) mr.sharedMaterial = p.wheelMaterial;
        }

        static void ApplyPhysics(GameObject root, VehicleProfile p)
        {
            var rb = root.GetComponent<Rigidbody>();
            if (rb == null) return;
            rb.mass = p.mass;
            rb.linearDamping = p.linearDamping;
            rb.angularDamping = p.angularDamping;
        }

        // ---- Runtime profile lookup --------------------------------------
        // Both server and clients use this to resolve a profile by index.
        // Resources.LoadAll bypasses Mirror's GUID-based asset resolution
        // entirely — every peer scans its OWN local copy of the asset
        // files and sorts them by displayName, so the same `int index`
        // means the same profile on every machine as long as the .asset
        // files are version-controlled in sync. No GUID parity required.
        static VehicleProfile[] _cachedPool;

        public static VehicleProfile[] LoadPool()
        {
            if (_cachedPool != null && _cachedPool.Length > 0) return _cachedPool;
            var raw = Resources.LoadAll<VehicleProfile>("Vehicles");
            // Deterministic sort by asset filename (case-insensitive). The
            // sort is only used for "list every profile" UIs / random pick
            // — sync between peers is by `profile.name` alone, never by
            // index, so a stale machine missing one profile won't shift
            // every other car's identity.
            System.Array.Sort(raw, (a, b) =>
                string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
            _cachedPool = raw;
            return _cachedPool;
        }

        // Editor-only helper to refresh the cache after authoring new
        // profile assets. Runtime callers don't need this — the cache
        // builds lazily on first LoadPool().
        public static void InvalidatePoolCache() { _cachedPool = null; }

        /// Find a profile by its ASSET FILENAME (== `profile.name`). This
        /// is the cross-machine-stable identity: a server randomly picks a
        /// name from its local pool, broadcasts it via SyncVar, every
        /// client looks up the same name in its own Resources scan and
        /// gets matching content (filenames are version-controlled, .meta
        /// GUIDs are not).
        public static VehicleProfile GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var pool = LoadPool();
            if (pool == null) return null;
            for (int i = 0; i < pool.Length; i++)
                if (string.Equals(pool[i].name, name, System.StringComparison.Ordinal))
                    return pool[i];
            return null;
        }

        /// Random asset filename from the loaded pool — server uses this to
        /// stamp `DioCar.profileName` on a freshly spawned car.
        public static string PickRandomName()
        {
            var pool = LoadPool();
            if (pool == null || pool.Length == 0) return string.Empty;
            return pool[Random.Range(0, pool.Length)].name;
        }

        public static int Count => LoadPool()?.Length ?? 0;
    }
}
