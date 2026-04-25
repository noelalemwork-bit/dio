using System.Collections.Generic;
using UnityEngine;

namespace Dio.Level
{
    /// Builds a strip Mesh along the geodesic chain of a LevelData. The strip:
    ///   * follows great-circle arcs (slerp), not straight lines — everything
    ///     stays on the planet
    ///   * is lifted slightly above the surface by `surfaceLift` so it doesn't
    ///     z-fight the planet sphere
    ///   * is `level.trackWidth` wide, oriented by the local Frenet frame:
    ///     surface-normal × tangent gives the cross-section direction
    ///   * has UVs u = arc-length-along-track / track-width (so a square
    ///     texture tile repeats every track-width units), v = 0..1 across.
    public static class TrackBuilder
    {
        const int SegmentsPerArc = 64;
        const float DerivEpsilon = 0.001f;

        public static Mesh Build(LevelData level, float surfaceLift = 0.08f)
        {
            if (level == null || !level.HasMinimum) return null;

            float r = level.planetRadius;
            float halfW = level.trackWidth * 0.5f;

            var verts   = new List<Vector3>(SegmentsPerArc * level.points.Count * 2);
            var normals = new List<Vector3>(verts.Capacity);
            var uvs     = new List<Vector2>(verts.Capacity);
            var tris    = new List<int>(verts.Capacity * 3);

            float arcCursor = 0f;

            // Generate samples per arc: arcs[i].slerp(t) for t in [0..1).
            // Last sample of each arc == first sample of next, so we drop
            // duplicates between arcs.
            for (int arc = 0; arc < level.points.Count - 1; arc++)
            {
                Vector3 a = level.points[arc].directionFromCenter.normalized;
                Vector3 b = level.points[arc + 1].directionFromCenter.normalized;

                bool isLastArc = arc == level.points.Count - 2;
                int sampleCount = isLastArc ? SegmentsPerArc + 1 : SegmentsPerArc;

                Vector3 prevDir = Vector3.Slerp(a, b, 0f).normalized;
                for (int s = 0; s < sampleCount; s++)
                {
                    float t = (float)s / SegmentsPerArc;
                    Vector3 dir = Vector3.Slerp(a, b, t).normalized;

                    // Tangent: finite difference on the slerp.
                    Vector3 dirAhead = Vector3.Slerp(a, b, Mathf.Min(1f, t + DerivEpsilon)).normalized;
                    Vector3 tangent = (dirAhead - dir).normalized;
                    if (tangent.sqrMagnitude < 1e-6f) tangent = (dir - prevDir).normalized;

                    // Local Frenet basis on the sphere.
                    Vector3 normal = dir;                                 // surface up
                    Vector3 width  = Vector3.Cross(tangent, normal).normalized;

                    Vector3 center = dir * (r + surfaceLift);
                    Vector3 vL = center + width * halfW;
                    Vector3 vR = center - width * halfW;

                    verts.Add(vL); verts.Add(vR);
                    normals.Add(normal); normals.Add(normal);

                    float uvU = arcCursor / Mathf.Max(0.01f, level.trackWidth);
                    uvs.Add(new Vector2(uvU, 0f));
                    uvs.Add(new Vector2(uvU, 1f));

                    if (verts.Count >= 4)
                    {
                        int v = verts.Count - 4; // L0, R0, L1, R1
                        // Wind so triangle normals point along surface up.
                        tris.Add(v + 0); tris.Add(v + 2); tris.Add(v + 1);
                        tris.Add(v + 1); tris.Add(v + 2); tris.Add(v + 3);
                    }

                    if (s + 1 < sampleCount)
                    {
                        Vector3 nextDir = Vector3.Slerp(a, b, (float)(s + 1) / SegmentsPerArc).normalized;
                        arcCursor += Vector3.Angle(dir, nextDir) * Mathf.Deg2Rad * r;
                    }
                    prevDir = dir;
                }
            }

            var mesh = new Mesh
            {
                name = "DioTrack",
                indexFormat = verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// World position at parametric distance t in [0..1] along the entire
        /// chain. Used by powerup spawning to find the midpoint.
        public static Vector3 SampleAt(LevelData level, float t)
        {
            if (level == null || !level.HasMinimum) return Vector3.zero;
            t = Mathf.Clamp01(t);

            int segments = level.points.Count - 1;
            float scaled = t * segments;
            int arc = Mathf.Min(segments - 1, Mathf.FloorToInt(scaled));
            float local = scaled - arc;

            Vector3 a = level.points[arc].directionFromCenter.normalized;
            Vector3 b = level.points[arc + 1].directionFromCenter.normalized;
            return Vector3.Slerp(a, b, local).normalized * level.planetRadius;
        }

        /// Tangent direction at parametric t — used to orient powerup boxes
        /// along the track and lay them out perpendicular to it.
        public static Vector3 TangentAt(LevelData level, float t)
        {
            if (level == null || !level.HasMinimum) return Vector3.forward;
            t = Mathf.Clamp01(t);

            int segments = level.points.Count - 1;
            float scaled = t * segments;
            int arc = Mathf.Min(segments - 1, Mathf.FloorToInt(scaled));
            float local = scaled - arc;

            Vector3 a = level.points[arc].directionFromCenter.normalized;
            Vector3 b = level.points[arc + 1].directionFromCenter.normalized;
            Vector3 dir = Vector3.Slerp(a, b, local).normalized;
            Vector3 dirAhead = Vector3.Slerp(a, b, Mathf.Min(1f, local + DerivEpsilon)).normalized;
            Vector3 tan = (dirAhead - dir).normalized;
            if (tan.sqrMagnitude < 1e-6f) tan = GeodesicUtil.TangentAt(a, b);
            return tan;
        }
    }
}
