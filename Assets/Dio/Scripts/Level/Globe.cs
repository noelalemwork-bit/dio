using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Dio.Level
{
    /// One static snapshot of one mesh in the Globe hierarchy: the mesh itself
    /// plus the local-to-world matrix that places it at the right spot under
    /// the Globe root. Built once per (FBX × circumference) and reused for
    /// rendering AND for ray-vs-triangle queries.
    public struct MeshSnapshot
    {
        public Mesh mesh;
        public Matrix4x4 worldMatrix;
        public Material[] materials;     // optional, for rendering paths that need them
    }

    /// Factory + raycaster for the world planet ("Globe").
    ///
    /// The Globe is the world.fbx asset (a low-poly stylised planet). We treat
    /// it as the canonical track substrate — every height query in the level
    /// editor and at runtime is a raycast against this mesh, not against an
    /// idealised sphere. The mesh has multiple submeshes nested at varying
    /// depth; we recurse through the whole hierarchy when building snapshots
    /// or attaching colliders.
    ///
    /// One subtree is special: any GameObject named `EnvironmentChildName`
    /// ("Environment") is purely visual — clouds, ambient props — and is
    /// skipped for both physics colliders and raycast queries.
    ///
    /// Scaling is driven by `circumference`: we resize the mesh so its largest
    /// world-space half-extent equals `circumference / (2π)`. That gives
    /// designers a single intuitive knob ("how far you'd drive around the
    /// planet equator") without pretending the mesh is a perfect sphere.
    public static class GlobeFactory
    {
        public const string FbxAssetPath = "Assets/3d world/world.fbx";
        public const string ResourcesPath = "Globe";       // Resources/Globe.prefab at runtime
        public const string EnvironmentChildName = "Environment";

        /// Convert a designer-facing circumference value to the radius we use
        /// internally everywhere the math wants a sphere. A real Globe is not
        /// spherical, but we still use this as the "nominal" radius for the
        /// scale-to-circumference fit and as a default fallback when raycasts
        /// miss.
        public static float CircumferenceToRadius(float circumference) =>
            circumference / (2f * Mathf.PI);

        /// Find the GameObject we should use as the world mesh source. In the
        /// editor we prefer the FBX directly (designers may iterate on it).
        /// At runtime we look for a Globe prefab dropped into Resources by
        /// the build pipeline.
        public static GameObject LoadSourceAsset()
        {
#if UNITY_EDITOR
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxAssetPath);
            if (fbx != null) return fbx;
#endif
            return Resources.Load<GameObject>(ResourcesPath);
        }

        /// Walk the FBX hierarchy and collect (mesh, worldMatrix, materials)
        /// snapshots for every MeshFilter not under the Environment subtree.
        /// `globalScale` is applied as a uniform pre-scale on top of every
        /// node's local transform — so a unit-radius asset can be placed
        /// at any planet circumference.
        public static List<MeshSnapshot> CollectSnapshots(GameObject source, float globalScale)
        {
            var list = new List<MeshSnapshot>();
            if (source == null) return list;
            var rootScale = Matrix4x4.Scale(Vector3.one * globalScale);
            CollectRecursive(source.transform, rootScale, Matrix4x4.identity, list);
            return list;
        }

        static void CollectRecursive(Transform t, Matrix4x4 globalScale, Matrix4x4 parentLocal, List<MeshSnapshot> outList)
        {
            if (t.name == EnvironmentChildName) return;

            var local = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
            var combined = parentLocal * local;

            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var rend = t.GetComponent<MeshRenderer>();
                outList.Add(new MeshSnapshot
                {
                    mesh = mf.sharedMesh,
                    worldMatrix = globalScale * combined,
                    materials = rend != null ? rend.sharedMaterials : null,
                });
            }

            for (int i = 0; i < t.childCount; i++)
                CollectRecursive(t.GetChild(i), globalScale, combined, outList);
        }

        /// AABB of the source mesh in its native (un-globally-scaled) space,
        /// used by autofit logic to compute the multiplier needed to hit a
        /// target radius. Caller can pass globalScale=1 if they only care
        /// about the asset's intrinsic dimensions.
        public static Bounds ComputeBounds(GameObject source, float globalScale = 1f)
        {
            var snaps = CollectSnapshots(source, globalScale);
            if (snaps.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);

            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            for (int i = 0; i < snaps.Count; i++)
            {
                var s = snaps[i];
                var lb = s.mesh.bounds;
                Vector3 lc = lb.center, le = lb.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    var corner = lc + new Vector3(sx * le.x, sy * le.y, sz * le.z);
                    var w = s.worldMatrix.MultiplyPoint3x4(corner);
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                }
            }
            return new Bounds((min + max) * 0.5f, max - min);
        }

        /// Average vertex distance from origin — the "mean radius" of the
        /// mesh treated as a not-quite-sphere. For an uneven Globe with some
        /// terrain on top, this is much more stable than max-extent (which is
        /// biased upward by tall mountains) and naturally matches what
        /// designers think of as "the planet radius". Environment subtree is
        /// excluded so visual props can't skew the autofit.
        public static float ComputeNativeRadius(GameObject source)
        {
            if (source == null) return 0f;
            var snaps = CollectSnapshots(source, 1f);
            if (snaps.Count == 0) return 0f;

            double sum = 0;
            long count = 0;
            for (int i = 0; i < snaps.Count; i++)
            {
                var s = snaps[i];
                if (s.mesh == null) continue;
                var verts = s.mesh.vertices;
                for (int k = 0; k < verts.Length; k++)
                {
                    Vector3 w = s.worldMatrix.MultiplyPoint3x4(verts[k]);
                    sum += w.magnitude;
                    count++;
                }
            }
            return count > 0 ? (float)(sum / count) : 0f;
        }

        /// Compute the global scale factor that resizes the source asset so
        /// its largest extent matches the radius implied by `circumference`.
        public static float ComputeAutofitScale(GameObject source, float circumference)
        {
            float native = ComputeNativeRadius(source);
            if (native < 1e-5f) return 1f;
            return CircumferenceToRadius(circumference) / native;
        }

        /// Runtime spawn — instantiates the source asset under `parent`,
        /// scales it to match `circumference`, and adds a MeshCollider to
        /// every mesh in the hierarchy outside the Environment subtree so
        /// physics raycasts work everywhere on the surface.
        public static GameObject Spawn(Transform parent, float circumference)
        {
            var source = LoadSourceAsset();
            if (source == null)
            {
                Debug.LogError("[Globe] world.fbx not found at " + FbxAssetPath +
                               " and no Resources/" + ResourcesPath + " prefab. Cannot spawn Globe.");
                return null;
            }

            var instance = Object.Instantiate(source, parent);
            instance.name = "Globe";

            float scale = ComputeAutofitScale(source, circumference);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one * scale;

            AttachColliders(instance.transform);
            return instance;
        }

        static void AttachColliders(Transform t)
        {
            if (t.name == EnvironmentChildName) return;

            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && t.GetComponent<MeshCollider>() == null)
            {
                var mc = t.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;   // static mesh colliders → concave is allowed and correct here
            }

            for (int i = 0; i < t.childCount; i++)
                AttachColliders(t.GetChild(i));
        }
    }

    /// Software ray-vs-triangle-mesh intersection. Used by the level editor,
    /// where the Globe is drawn into a PreviewRenderUtility's isolated scene
    /// (no Physics queries against it) but we still need to know which radius
    /// each track anchor sits at. Brute force over all triangles in every
    /// snapshot — fine for the FBX's few-thousand-tri count and the cache
    /// makes repeated queries cheap.
    public class GlobeRaycaster
    {
        readonly List<MeshSnapshot> _snapshots;
        readonly List<Vector3[]> _vertsCache = new List<Vector3[]>();
        readonly List<int[]>     _trisCache  = new List<int[]>();
        readonly float _fallbackRadius;

        public IReadOnlyList<MeshSnapshot> Snapshots => _snapshots;
        public float FallbackRadius => _fallbackRadius;

        public GlobeRaycaster(List<MeshSnapshot> snapshots, float fallbackRadius)
        {
            _snapshots = snapshots ?? new List<MeshSnapshot>();
            _fallbackRadius = fallbackRadius;
            for (int i = 0; i < _snapshots.Count; i++)
            {
                var m = _snapshots[i].mesh;
                _vertsCache.Add(m != null ? m.vertices : null);
                _trisCache .Add(m != null ? m.triangles : null);
            }
        }

        /// From a point well above the surface along `unitDirection`,
        /// raycast INWARD to find the planet surface. The first hit's radial
        /// distance from origin = the surface radius.
        ///
        /// We cast inward from `2 × _fallbackRadius` (well past any reasonable
        /// terrain) rather than outward from the origin. Casting from origin
        /// has two failure modes that cause the editor's bezier polyline to
        /// visibly dip toward the planet center:
        ///   1. Origin can lie ON or NEAR a triangle (some FBX assets have
        ///      "inside" geometry near the center) → first hit is a stray
        ///      inner triangle, returning a tiny radius.
        ///   2. Floating-point edge cases at the ray origin can cause the
        ///      Möller-Trumbore test to miss the front-facing triangle,
        ///      then hit the BACK of the planet at a much larger t — but
        ///      since `bestT` keeps the smallest hit we'd take whichever
        ///      was found first, with no guarantee it's the visible surface.
        /// Casting inward from outside avoids both.
        public float ResolveSurfaceRadius(Vector3 unitDirection)
        {
            float startDist = _fallbackRadius * 2f;
            Vector3 origin = unitDirection * startDist;
            var ray = new Ray(origin, -unitDirection);
            if (RaycastAny(ray, out float t))
            {
                // ray origin is at radius `startDist`; after travelling t we're
                // at radius `startDist - t`. That's the surface radius.
                return Mathf.Max(0f, startDist - t);
            }
            return _fallbackRadius;
        }

        /// General ray/mesh intersection. Returns the parametric `t` of the
        /// closest front-facing hit along `ray`. False = miss. Used by the
        /// editor to drop track anchors onto the visible terrain regardless
        /// of zoom level (sphere raycasts stop matching the visible surface
        /// once the camera gets close enough).
        public bool RaycastAny(Ray ray, out float t)
        {
            t = 0f;
            if (_snapshots.Count == 0) return false;
            float bestT = float.PositiveInfinity;
            for (int i = 0; i < _snapshots.Count; i++)
            {
                var verts = _vertsCache[i];
                var tris  = _trisCache[i];
                if (verts == null || tris == null) continue;
                var m = _snapshots[i].worldMatrix;

                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    Vector3 a = m.MultiplyPoint3x4(verts[tris[k + 0]]);
                    Vector3 b = m.MultiplyPoint3x4(verts[tris[k + 1]]);
                    Vector3 c = m.MultiplyPoint3x4(verts[tris[k + 2]]);
                    if (RayTriangle(ray, a, b, c, out float ti) && ti > 0f && ti < bestT)
                        bestT = ti;
                }
            }
            if (float.IsPositiveInfinity(bestT)) return false;
            t = bestT;
            return true;
        }

        /// Where on the mesh does this ray land? `hit` is the world-space hit
        /// point. Use the hit's `.normalized` to get the unit direction from
        /// the planet center — that's the "polar position" the level editor
        /// uses to drop anchors at the cursor.
        public bool Raycast(Ray ray, out Vector3 hit)
        {
            if (RaycastAny(ray, out float t))
            {
                hit = ray.origin + ray.direction * t;
                return true;
            }
            hit = default;
            return false;
        }

        /// Closest unit direction (from origin) the ray approaches. Used as a
        /// graceful fallback when the cursor is in empty space — the camera
        /// is past the planet's silhouette but the user still expects the
        /// anchor to track the cursor's line of sight. Returns false only if
        /// the ray actually passes through the origin (degenerate).
        public static bool ClosestDirectionFromRay(Ray ray, out Vector3 unitDirection)
        {
            // Closest point on ray to origin: O - dot(O, d)*d  (with d unit).
            Vector3 d = ray.direction.sqrMagnitude > 1e-8f ? ray.direction.normalized : Vector3.forward;
            Vector3 o = ray.origin;
            Vector3 closest = o - Vector3.Dot(o, d) * d;
            if (closest.sqrMagnitude < 1e-8f) { unitDirection = Vector3.up; return false; }
            unitDirection = closest.normalized;
            return true;
        }

        /// Möller-Trumbore. No backface culling — designers may have flipped
        /// normals on individual submeshes, and we still want to hit the
        /// closest face.
        public static bool RayTriangle(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = 0f;
            Vector3 e1 = v1 - v0;
            Vector3 e2 = v2 - v0;
            Vector3 h = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-8f) return false;
            float invDet = 1f / det;
            Vector3 s = ray.origin - v0;
            float u = invDet * Vector3.Dot(s, h);
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = invDet * Vector3.Dot(ray.direction, q);
            if (v < 0f || u + v > 1f) return false;
            float tt = invDet * Vector3.Dot(e2, q);
            if (tt <= 1e-6f) return false;
            t = tt;
            return true;
        }
    }
}
