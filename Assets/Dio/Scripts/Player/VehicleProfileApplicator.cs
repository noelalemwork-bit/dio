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
            // Kenney chassis meshes (and the procedural cube body) often dip
            // into the WheelCollider's downward raycast envelope. Without
            // this pass each WheelCollider would intermittently report the
            // car's own body MeshCollider as the ground surface, producing a
            // chronic micro-bump as suspension snaps back. Doing it last —
            // after ApplyBody has swapped the MeshCollider's sharedMesh to
            // the profile's body mesh — keeps the ignore paired with the
            // current convex hull, not a stale primitive cube.
            IgnoreSelfCollisions(root);
        }

        // Server + every client should call this whenever the profile changes
        // — Physics.IgnoreCollision pairs are runtime state, not serialized
        // through the prefab, so they're per-instance per-process. Cheap.
        public static void IgnoreSelfCollisions(GameObject root)
        {
            if (root == null) return;
            var wheels = root.GetComponentsInChildren<WheelCollider>(true);
            if (wheels == null || wheels.Length == 0) return;
            var allColliders = root.GetComponentsInChildren<Collider>(true);
            for (int wi = 0; wi < wheels.Length; wi++)
            {
                var w = wheels[wi];
                if (w == null) continue;
                for (int ci = 0; ci < allColliders.Length; ci++)
                {
                    var other = allColliders[ci];
                    if (other == null) continue;
                    if (other == w) continue;
                    if (other is WheelCollider) continue;
                    if (other.transform.root != root.transform) continue; // never ignore cross-car
                    try { Physics.IgnoreCollision(w, other, true); }
                    catch { /* terrain colliders / disabled colliders — fine to skip */ }
                }
            }
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

            // The base Car.prefab is a primitive Cube — its BoxCollider is
            // 1×1×1 in mesh-local space, fine for the cube body, wrong for any
            // Kenney mesh that has its own bounds. Without this fit, the box
            // stays cube-sized while the mesh swaps to something taller, so
            // the box's bottom dips below the wheel rest line and the chassis
            // grounds out before the wheels can. Refit on every profile apply.
            var activeMesh = mf != null ? mf.sharedMesh : null;
            FitBodyCollider(body, activeMesh, p);
        }

        // Replace whatever collider exists on Body with a BoxCollider fitted
        // to the active mesh's bounds, with the bottom clamped above the
        // wheel rest plane in chassis-local space. Box (not Mesh) on
        // purpose: Kenney chassis don't always produce sane convex hulls,
        // and a tight box matches the visual silhouette tightly enough for
        // arcade racing while being faster + easier to reason about.
        static void FitBodyCollider(Transform body, Mesh mesh, VehicleProfile p)
        {
            if (!p.addBodyCollider)
            {
                var any = body.GetComponent<Collider>();
                if (any != null) Object.Destroy(any);
                return;
            }
            // Strip any MeshCollider left over from the per-profile prefab
            // path — we standardise on BoxCollider now.
            var oldMesh = body.GetComponent<MeshCollider>();
            if (oldMesh != null) Object.Destroy(oldMesh);

            var box = body.GetComponent<BoxCollider>();
            if (box == null) box = body.gameObject.AddComponent<BoxCollider>();

            // Default: unit box centred at origin (matches the cube primitive).
            Vector3 center = Vector3.zero;
            Vector3 size = Vector3.one;
            if (mesh != null)
            {
                var b = mesh.bounds;       // mesh-local == body pre-scale
                center = b.center;
                size   = b.size;
            }

            // Wheel safety: the box's bottom in CHASSIS-LOCAL space must sit
            // at or above `wheelRadius * 0.65f`, so the WheelCollider's
            // downward suspension raycast (origin at WheelColliderY, length
            // = suspensionDistance + wheelRadius) cannot ever read the body
            // as the ground surface. If the raw mesh bounds dip lower, lift
            // the box bottom and shrink size.y; cap is the box top.
            float scaleY = Mathf.Abs(p.bodyScale.y);
            if (scaleY > 1e-4f)
            {
                float bottomLocal = center.y - size.y * 0.5f;
                float topLocal    = center.y + size.y * 0.5f;
                float bottomChassis = p.bodyOffset.y + bottomLocal * p.bodyScale.y;
                float minBottomChassis = p.wheelRadius * 0.65f;
                if (bottomChassis < minBottomChassis)
                {
                    float newBottomLocal = (minBottomChassis - p.bodyOffset.y) / p.bodyScale.y;
                    if (newBottomLocal < topLocal - 0.02f)
                    {
                        size.y = topLocal - newBottomLocal;
                        center.y = (topLocal + newBottomLocal) * 0.5f;
                    }
                }
            }

            box.center = center;
            box.size   = size;
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
