using System.Collections.Generic;
using UnityEngine;

namespace Dio.Level
{
    /// Builds a strip Mesh along the spherical-Bezier chain of a LevelData.
    /// The strip:
    ///   * follows spherical cubic Beziers (slerp-based De Casteljau), not
    ///     straight lines — every sample stays exactly on the planet
    ///   * is lifted slightly above the surface by `surfaceLift` so it doesn't
    ///     z-fight the planet sphere
    ///   * is `level.trackWidth` wide, oriented by the local Frenet frame:
    ///     surface-normal × tangent gives the cross-section direction. C1
    ///     continuity at shared anchors (enforced by the editor) keeps the
    ///     tangent — and therefore the lateral frame — continuous, so two
    ///     adjacent segments share a seamless joint.
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

            // Generate samples per segment: bezier(t) for t in [0..1).
            // Last sample of each segment == first sample of next, so we
            // drop duplicates between segments (except on the final segment
            // where we include t = 1).
            for (int seg = 0; seg < level.points.Count - 1; seg++)
            {
                level.GetSegment(seg, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);

                bool isLastSeg = seg == level.points.Count - 2;
                int sampleCount = isLastSeg ? SegmentsPerArc + 1 : SegmentsPerArc;

                Vector3 prevDir = GeodesicUtil.SphericalBezier(a, ah, bh, b, 0f);
                for (int s = 0; s < sampleCount; s++)
                {
                    float t = (float)s / SegmentsPerArc;
                    Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);

                    // Tangent: finite difference on the bezier.
                    Vector3 dirAhead = GeodesicUtil.SphericalBezier(a, ah, bh, b, Mathf.Min(1f, t + DerivEpsilon));
                    Vector3 tangent = (dirAhead - dir).normalized;
                    if (tangent.sqrMagnitude < 1e-6f) tangent = (dir - prevDir).normalized;
                    if (tangent.sqrMagnitude < 1e-6f) tangent = GeodesicUtil.TangentAt(a, b);

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
                        Vector3 nextDir = GeodesicUtil.SphericalBezier(a, ah, bh, b, (float)(s + 1) / SegmentsPerArc);
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

        /// Build a pair of guard-rail meshes along each side of the track.
        /// Cross-section is a Z-shape (3 segments along the local lateral
        /// frame at each sample) instead of a flat plane:
        ///
        ///                                      .-- top  (lip:  outward 0.05m)
        ///                                      |
        ///   wall-up 0.2m  --------------------'
        ///                                      |
        ///                                  bottom (1m below the surface
        ///                                  → closes any seam between the
        ///                                  ribbon and the low-poly planet
        ///                                  collider, prevents pop-throughs)
        ///
        /// The little outward kink on top stops a car bouncing off the wall
        /// from popping over the lip. Inward-facing winding so cars on the
        /// track see a wall and the back side is invisible.
        public static (Mesh left, Mesh right) BuildGuards(LevelData level,
            float wallHeight = 0.2f, float lipOutward = 0.05f, float skirtDepth = 1f,
            float outwardOffset = 0.05f)
        {
            if (level == null || !level.HasMinimum) return (null, null);
            float r = level.planetRadius;
            float halfW = level.trackWidth * 0.5f;

            var leftVerts  = new List<Vector3>();
            var leftTris   = new List<int>();
            var rightVerts = new List<Vector3>();
            var rightTris  = new List<int>();

            for (int seg = 0; seg < level.points.Count - 1; seg++)
            {
                level.GetSegment(seg, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                bool isLastSeg = seg == level.points.Count - 2;
                int sampleCount = isLastSeg ? SegmentsPerArc + 1 : SegmentsPerArc;

                for (int s = 0; s < sampleCount; s++)
                {
                    float t = (float)s / SegmentsPerArc;
                    Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                    Vector3 dirAhead = GeodesicUtil.SphericalBezier(a, ah, bh, b, Mathf.Min(1f, t + DerivEpsilon));
                    Vector3 tangent = (dirAhead - dir).normalized;
                    if (tangent.sqrMagnitude < 1e-6f) tangent = GeodesicUtil.TangentAt(a, b);

                    Vector3 normal = dir;
                    Vector3 width = Vector3.Cross(tangent, normal).normalized; // +width = "left"

                    // Anchor row on the surface, just outside the ribbon edge.
                    Vector3 leftAnchor  = dir * r + width * (halfW + outwardOffset);
                    Vector3 rightAnchor = dir * r - width * (halfW + outwardOffset);

                    AppendZSection(leftVerts, leftTris, leftAnchor, dir, width, wallHeight, lipOutward, skirtDepth, s, inwardSign: -1);
                    AppendZSection(rightVerts, rightTris, rightAnchor, dir, -1f * width, wallHeight, lipOutward, skirtDepth, s, inwardSign: +1);
                }
            }

            Mesh BuildMesh(string n, List<Vector3> verts, List<int> tris)
            {
                var m = new Mesh
                {
                    name = n,
                    indexFormat = verts.Count > 65000
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16,
                };
                m.SetVertices(verts);
                m.SetTriangles(tris, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }

            return (BuildMesh("DioGuardLeft", leftVerts, leftTris),
                    BuildMesh("DioGuardRight", rightVerts, rightTris));
        }

        // Add four cross-section vertices (bottom → wall-base → wall-top → lip-end)
        // for one sample of one guardrail. Once two consecutive samples exist
        // we can stitch a quad strip (3 quads × 2 tris) between them.
        //
        // `outward` is the unit vector pointing AWAY from the track at this
        // sample (it's `+width` for the left guard, `-width` for the right).
        // `inwardSign` flips winding so both guardrails face inward.
        static void AppendZSection(List<Vector3> verts, List<int> tris,
            Vector3 anchor, Vector3 surfaceUp, Vector3 outward,
            float wallHeight, float lipOutward, float skirtDepth, int sampleIndex, int inwardSign)
        {
            // 4 verts per ring: bottom (under surface), surface, wall-top, lip-end.
            Vector3 vBot     = anchor - surfaceUp * skirtDepth;     // 1m below surface
            Vector3 vSurf    = anchor;                              // on the surface
            Vector3 vWallTop = anchor + surfaceUp * wallHeight;     // 0.2m up
            Vector3 vLip     = vWallTop + outward * lipOutward;     // 0.05m outward

            int baseIdx = verts.Count;
            verts.Add(vBot); verts.Add(vSurf); verts.Add(vWallTop); verts.Add(vLip);

            if (sampleIndex == 0) return;

            // Ring i-1 = baseIdx - 4..baseIdx - 1. Ring i = baseIdx..baseIdx + 3.
            int p = baseIdx - 4;
            int c = baseIdx;
            // Three quads: skirt (bot→surf), wall (surf→wallTop), lip (wallTop→lip).
            EmitQuad(tris, p + 0, p + 1, c + 0, c + 1, inwardSign);
            EmitQuad(tris, p + 1, p + 2, c + 1, c + 2, inwardSign);
            EmitQuad(tris, p + 2, p + 3, c + 2, c + 3, inwardSign);
        }

        // Emit one quad as two triangles. Winding flipped by `sign` so the
        // visible face points inward (toward the track center). Quad layout:
        //
        //   p0 -- p1
        //   |     |
        //   c0 -- c1
        static void EmitQuad(List<int> tris, int p0, int p1, int c0, int c1, int sign)
        {
            if (sign > 0)
            {
                tris.Add(p0); tris.Add(c0); tris.Add(p1);
                tris.Add(p1); tris.Add(c0); tris.Add(c1);
            }
            else
            {
                tris.Add(p0); tris.Add(p1); tris.Add(c0);
                tris.Add(p1); tris.Add(c1); tris.Add(c0);
            }
        }

        /// Build a "spawn pad" — a short straight ribbon extending BACKWARD
        /// from the start anchor along the level's start tangent, plus a
        /// back wall (same Z-shape cross-section as the guardrails) so cars
        /// reversing off the line can't drop into space.
        ///
        /// `length` is in world units along the geodesic; samples are
        /// uniformly spaced along the geodesic from start back to a point
        /// that's `length` away. The strip uses the SAME local Frenet frame
        /// as the start of the track ribbon — same width, same banking — so
        /// the seam between spawn pad and live track is invisible.
        public static (Mesh ribbon, Mesh backWall) BuildSpawnPad(LevelData level, float length,
            float wallHeight = 0.2f, float lipOutward = 0.05f, float skirtDepth = 1f, float surfaceLift = 0.08f)
        {
            if (level == null || !level.HasMinimum || length <= 0.01f) return (null, null);

            float r = level.planetRadius;
            float halfW = level.trackWidth * 0.5f;

            // Start frame.
            Vector3 startDir = level.DirOf(0);
            Vector3 startTan = TangentAt(level, 0f);          // forward along bezier at t=0
            Vector3 startWidth = Vector3.Cross(startTan, startDir).normalized;
            // Backward direction along the geodesic from the start anchor.
            Vector3 backTan = -startTan;

            // Build a great-circle arc going `length` backward. Arc length
            // L = angle * radius → angle = L / r.
            float totalAngle = length / r;
            const int Samples = 24;

            // Generate samples from t=0 (start anchor) to t=1 (length behind).
            // Direction at sample t: rotate startDir by (-t * totalAngle)
            // around the axis cross(startDir, backTan).
            Vector3 axis = Vector3.Cross(startDir, backTan).normalized;
            if (axis.sqrMagnitude < 1e-6f) return (null, null);

            var verts = new List<Vector3>(Samples * 2 + 2);
            var normals = new List<Vector3>(verts.Capacity);
            var uvs = new List<Vector2>(verts.Capacity);
            var tris = new List<int>(Samples * 6);

            // Ribbon: build from t=1 (back) → t=0 (start) so triangle winding
            // matches the live track's forward direction.
            for (int s = 0; s <= Samples; s++)
            {
                float t = 1f - (float)s / Samples;          // 1 at back, 0 at start
                Quaternion q = Quaternion.AngleAxis(t * totalAngle * Mathf.Rad2Deg, axis);
                Vector3 dir = q * startDir;
                Vector3 tan = q * startTan;
                Vector3 w = Vector3.Cross(tan, dir).normalized;
                Vector3 center = dir * (r + surfaceLift);
                Vector3 vL = center + w * halfW;
                Vector3 vR = center - w * halfW;
                verts.Add(vL); verts.Add(vR);
                normals.Add(dir); normals.Add(dir);
                uvs.Add(new Vector2(s, 0)); uvs.Add(new Vector2(s, 1));

                if (verts.Count >= 4)
                {
                    int v = verts.Count - 4;
                    tris.Add(v + 0); tris.Add(v + 2); tris.Add(v + 1);
                    tris.Add(v + 1); tris.Add(v + 2); tris.Add(v + 3);
                }
            }

            var ribbon = new Mesh
            {
                name = "DioSpawnPad",
                indexFormat = verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            ribbon.SetVertices(verts);
            ribbon.SetNormals(normals);
            ribbon.SetUVs(0, uvs);
            ribbon.SetTriangles(tris, 0);
            ribbon.RecalculateBounds();

            // Back wall: same Z cross-section as guardrails, swept across the
            // track width at the back end of the pad. Two anchors (track-left,
            // track-right) at the back, connected by Z sections.
            Quaternion qBack = Quaternion.AngleAxis(totalAngle * Mathf.Rad2Deg, axis);
            Vector3 backDir = qBack * startDir;
            Vector3 backTanRot = qBack * startTan;
            Vector3 backWidth = Vector3.Cross(backTanRot, backDir).normalized;
            Vector3 backCenter = backDir * r;

            var wallVerts = new List<Vector3>();
            var wallTris = new List<int>();
            const int WallSamples = 8;
            // outward = backTan-ish: face inward toward the track (i.e. the +tangent direction).
            // The car is forward of the wall, so the wall's visible face must point along
            // backTan's negative — toward the track direction (forward).
            Vector3 outward = -backTanRot.normalized;
            for (int s = 0; s <= WallSamples; s++)
            {
                float u = (float)s / WallSamples - 0.5f;       // -0.5 .. +0.5
                Vector3 anchor = backCenter + backWidth * (level.trackWidth * u);
                AppendZSection(wallVerts, wallTris, anchor, backDir, outward, wallHeight, lipOutward, skirtDepth, s, inwardSign: +1);
            }

            var backWall = new Mesh
            {
                name = "DioSpawnPadBackWall",
                indexFormat = wallVerts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            backWall.SetVertices(wallVerts);
            backWall.SetTriangles(wallTris, 0);
            backWall.RecalculateNormals();
            backWall.RecalculateBounds();

            return (ribbon, backWall);
        }

        /// World position at parametric distance t in [0..1] along the entire
        /// chain. t is segment-uniform: each segment occupies 1/N of t, so a
        /// 4-segment chain hits t=0.25 at the end of segment 0, etc.
        public static Vector3 SampleAt(LevelData level, float t)
        {
            if (level == null || !level.HasMinimum) return Vector3.zero;
            t = Mathf.Clamp01(t);
            int segments = level.points.Count - 1;
            float scaled = t * segments;
            int seg = Mathf.Min(segments - 1, Mathf.FloorToInt(scaled));
            float local = scaled - seg;
            level.GetSegment(seg, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
            return GeodesicUtil.SphericalBezier(a, ah, bh, b, local) * level.planetRadius;
        }

        /// Tangent direction at parametric t — used to orient powerup boxes
        /// along the track and to set the start-line car heading.
        public static Vector3 TangentAt(LevelData level, float t)
        {
            if (level == null || !level.HasMinimum) return Vector3.forward;
            t = Mathf.Clamp01(t);
            int segments = level.points.Count - 1;
            float scaled = t * segments;
            int seg = Mathf.Min(segments - 1, Mathf.FloorToInt(scaled));
            float local = scaled - seg;
            level.GetSegment(seg, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
            Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, local);
            Vector3 dirAhead = GeodesicUtil.SphericalBezier(a, ah, bh, b, Mathf.Min(1f, local + DerivEpsilon));
            Vector3 tan = (dirAhead - dir).normalized;
            if (tan.sqrMagnitude < 1e-6f) tan = GeodesicUtil.TangentAt(a, b);
            return tan;
        }

        /// Total arc length of the bezier chain in world units (sample-based,
        /// since spherical cubic Beziers don't have a closed-form length).
        public static float TotalArcLength(LevelData level)
        {
            if (level == null || !level.HasMinimum) return 0f;
            float r = level.planetRadius;
            const int Samples = 64;
            float total = 0f;
            for (int seg = 0; seg < level.points.Count - 1; seg++)
            {
                level.GetSegment(seg, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                Vector3 prev = a;
                for (int s = 1; s <= Samples; s++)
                {
                    Vector3 cur = GeodesicUtil.SphericalBezier(a, ah, bh, b, (float)s / Samples);
                    total += Vector3.Angle(prev, cur) * Mathf.Deg2Rad * r;
                    prev = cur;
                }
            }
            return total;
        }

        /// Project a world-space point onto the sampled bezier chain.
        /// Returns the arc length from the start to the projected point.
        /// For lap / finish detection on the server. Sampling-based:
        /// 64 samples per segment, pick the sample with smallest angular
        /// distance to the query direction. Monotonic enough for ranking
        /// and finish-line crossing without surprises.
        ///
        /// `planetCenter`: world-space planet origin — usually
        /// `RaceBootstrap.CurrentPlanet.transform.position`.
        public static float ArcLengthOf(LevelData level, Vector3 worldPos, Vector3 planetCenter)
        {
            if (level == null || !level.HasMinimum) return 0f;

            Vector3 dir = (worldPos - planetCenter).normalized;
            if (dir.sqrMagnitude < 1e-6f) return 0f;

            float r = level.planetRadius;
            const int Samples = 64;

            float bestArc = 0f;
            float bestDist2 = float.MaxValue;
            float arcCursor = 0f;
            Vector3 prev = Vector3.zero;
            bool first = true;

            for (int seg = 0; seg < level.points.Count - 1; seg++)
            {
                level.GetSegment(seg, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                for (int s = 0; s <= Samples; s++)
                {
                    // Skip the duplicate first sample of subsequent segments.
                    if (seg > 0 && s == 0) continue;
                    float t = (float)s / Samples;
                    Vector3 cur = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                    if (!first) arcCursor += Vector3.Angle(prev, cur) * Mathf.Deg2Rad * r;
                    first = false;
                    float d2 = (cur - dir).sqrMagnitude;
                    if (d2 < bestDist2) { bestDist2 = d2; bestArc = arcCursor; }
                    prev = cur;
                }
            }
            return bestArc;
        }
    }
}
