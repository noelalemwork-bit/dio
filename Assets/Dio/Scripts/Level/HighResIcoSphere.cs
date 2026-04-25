using System.Collections.Generic;
using UnityEngine;

namespace Dio.Level
{
    /// Build a unit-radius icosphere mesh by recursively subdividing an
    /// icosahedron. `subdivisions=4` gives 2562 vertices / 5120 faces — smooth
    /// enough that the silhouette looks round at any reasonable camera distance
    /// and that the geodesic track strip aligns visibly with the surface.
    ///
    /// The mesh is unit-radius — caller scales it by `planetRadius` on the
    /// transform. Uses a vertex midpoint cache so each subdivision step is
    /// O(F) rather than O(F log F) duplicate-vertex hunting.
    public static class HighResIcoSphere
    {
        public static Mesh Build(int subdivisions = 4)
        {
            // Start from the 12 icosahedron vertices.
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var verts = new List<Vector3>(12)
            {
                new Vector3(-1,  t, 0).normalized,
                new Vector3( 1,  t, 0).normalized,
                new Vector3(-1, -t, 0).normalized,
                new Vector3( 1, -t, 0).normalized,
                new Vector3(0, -1,  t).normalized,
                new Vector3(0,  1,  t).normalized,
                new Vector3(0, -1, -t).normalized,
                new Vector3(0,  1, -t).normalized,
                new Vector3( t, 0, -1).normalized,
                new Vector3( t, 0,  1).normalized,
                new Vector3(-t, 0, -1).normalized,
                new Vector3(-t, 0,  1).normalized,
            };

            // 20 triangle faces of the base icosahedron.
            var faces = new List<int[]>(20)
            {
                new[] { 0,11, 5}, new[] { 0, 5, 1}, new[] { 0, 1, 7}, new[] { 0, 7,10}, new[] { 0,10,11},
                new[] { 1, 5, 9}, new[] { 5,11, 4}, new[] {11,10, 2}, new[] {10, 7, 6}, new[] { 7, 1, 8},
                new[] { 3, 9, 4}, new[] { 3, 4, 2}, new[] { 3, 2, 6}, new[] { 3, 6, 8}, new[] { 3, 8, 9},
                new[] { 4, 9, 5}, new[] { 2, 4,11}, new[] { 6, 2,10}, new[] { 8, 6, 7}, new[] { 9, 8, 1},
            };

            var midpointCache = new Dictionary<long, int>(faces.Count * 4);
            for (int s = 0; s < subdivisions; s++)
            {
                var newFaces = new List<int[]>(faces.Count * 4);
                foreach (var face in faces)
                {
                    int a = GetMidpoint(verts, midpointCache, face[0], face[1]);
                    int b = GetMidpoint(verts, midpointCache, face[1], face[2]);
                    int c = GetMidpoint(verts, midpointCache, face[2], face[0]);
                    newFaces.Add(new[] { face[0], a, c });
                    newFaces.Add(new[] { face[1], b, a });
                    newFaces.Add(new[] { face[2], c, b });
                    newFaces.Add(new[] { a, b, c });
                }
                faces = newFaces;
            }

            var tris = new int[faces.Count * 3];
            for (int i = 0; i < faces.Count; i++)
            {
                tris[i * 3 + 0] = faces[i][0];
                tris[i * 3 + 1] = faces[i][1];
                tris[i * 3 + 2] = faces[i][2];
            }

            // Normals = positions on a unit sphere.
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++) normals[i] = verts[i];

            var mesh = new Mesh
            {
                name = $"IcoSphere_s{subdivisions}",
                indexFormat = verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static int GetMidpoint(List<Vector3> verts, Dictionary<long, int> cache, int i0, int i1)
        {
            long key = i0 < i1 ? ((long)i0 << 32) | (uint)i1 : ((long)i1 << 32) | (uint)i0;
            if (cache.TryGetValue(key, out int idx)) return idx;
            Vector3 mid = ((verts[i0] + verts[i1]) * 0.5f).normalized;
            verts.Add(mid);
            idx = verts.Count - 1;
            cache[key] = idx;
            return idx;
        }
    }
}
