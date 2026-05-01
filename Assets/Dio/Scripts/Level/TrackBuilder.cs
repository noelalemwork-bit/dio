using System.Collections.Generic;
using UnityEngine;

namespace Dio.Level
{
    /// Procedural mesh generation for the racing track and its guard rails.
    ///
    /// Pipeline:
    ///   1. Walk the LevelData adjacency graph (`points[i].next`) to enumerate
    ///      directed edges. Linear chains, branches, and joins are all handled
    ///      uniformly — branches just emit multiple edges from one anchor.
    ///   2. For each edge, sample its spherical-cubic-Bezier (`SegmentsPerArc`
    ///      samples). At every sample, raycast against the Globe to get the
    ///      true surface radius along that direction; lerp the two anchors'
    ///      yOffsets and add to it. This makes the ribbon hug the actual
    ///      terrain, not an idealised sphere.
    ///   3. Build the lateral L/R verts using the local Frenet frame
    ///      (tangent × surface-normal). Cache anchor-boundary verts in a
    ///      dictionary so two edges meeting at the same anchor share verts —
    ///      eliminates the seam between adjacent edges and (once branches
    ///      arrive) keeps fork joints crack-free.
    ///   4. Effective track width = `level.trackWidth * level.trackRatio`,
    ///      so a per-level "narrow / wide" multiplier flows automatically
    ///      through the ribbon, the guards, and the spawn pad.
    ///
    /// Guard rails are a separate pass that follows the same edge enumeration.
    /// The cross-section is configurable:
    ///   * `Full`          — skirt + wall + lip (the default for runtime).
    ///   * `OuterSkirtOnly`— just the skirt, drawn from each sample down to
    ///                      the surface (regardless of yOffset). Lets the
    ///                      level editor visualise track elevation without
    ///                      walls obscuring the view.
    ///   * `floorAtSurface`— independent flag that anchors the skirt bottom
    ///                      at (surface − floorPad), so an elevated track's
    ///                      skirt always reaches the actual ground.
    ///
    /// Endcaps are optional rails closing off the ends of an open chain
    /// (start gate / finish line). They share the same Z cross-section as
    /// the side guards.
    ///
    /// Spawn pad: a short geodesic ribbon extending backward from the start
    /// anchor along its tangent. Y is held uniform at the start anchor's
    /// resolved height — so cars on the back row stand at the same height
    /// as the polesitter and the front of the live track is invisibly seamed
    /// to the back of the pad.
    /// Reusable buffer pool for procedural mesh generation. Hand it to
    /// `Build` / `BuildGuards` / `BuildSpawnPad` to avoid the per-rebuild GC
    /// hit of allocating fresh `List<Vector3>` and `List<int>` every call —
    /// the editor in particular rebuilds the preview track on every drag
    /// tick, where the lists' capacity stabilises after the first build.
    /// Calling code clears the lists at the start of each build (cheap) and
    /// re-populates in place. Pass `null` if you don't care about churn.
    public class TrackBuilderScratch
    {
        public readonly System.Collections.Generic.List<Vector3> verts   = new System.Collections.Generic.List<Vector3>();
        public readonly System.Collections.Generic.List<Vector3> normals = new System.Collections.Generic.List<Vector3>();
        public readonly System.Collections.Generic.List<Vector2> uvs     = new System.Collections.Generic.List<Vector2>();
        public readonly System.Collections.Generic.List<int>     tris    = new System.Collections.Generic.List<int>();

        public readonly System.Collections.Generic.List<Vector3> guardL  = new System.Collections.Generic.List<Vector3>();
        public readonly System.Collections.Generic.List<int>     guardLT = new System.Collections.Generic.List<int>();
        public readonly System.Collections.Generic.List<Vector3> guardR  = new System.Collections.Generic.List<Vector3>();
        public readonly System.Collections.Generic.List<int>     guardRT = new System.Collections.Generic.List<int>();

        public void ClearRibbon()
        {
            verts.Clear(); normals.Clear(); uvs.Clear(); tris.Clear();
        }
        public void ClearGuards()
        {
            guardL.Clear(); guardLT.Clear(); guardR.Clear(); guardRT.Clear();
        }
    }

    public static class TrackBuilder
    {
        public const int DefaultSegmentsPerArc = 64;
        // Editor preview runs at ~0.75× the previous resolution for snappier
        // iteration; the runtime stays at the full DefaultSegmentsPerArc.
        public const int EditorPreviewSegmentsPerArc = 12;
        const float DerivEpsilon = 0.001f;

        // Bezier tangent. At the endpoints (t=0 / t=1) we use the ANALYTIC
        // direction toward the corresponding handle — `a → ah` at t=0 and
        // `bh → b` at t=1. This is shared between every edge that meets at
        // the same anchor (since `a` / `ah` / `bh` / `b` derive from the
        // anchor's shared in/outHandle), so two edges touching at an anchor
        // produce identical lateral frames — no rail tapering at the joint.
        // Interior samples still use forward finite difference for smoothness
        // through curved sections.
        static Vector3 BezierTangent(Vector3 a, Vector3 ah, Vector3 bh, Vector3 b, float t)
        {
            // Analytic endpoint tangents — same value for every edge sharing
            // the anchor's handle directions, so adjacent edges' rails align.
            if (t <= DerivEpsilon)
            {
                Vector3 tan = GeodesicUtil.TangentAt(a, ah);
                if (tan.sqrMagnitude > 1e-6f) return tan;
            }
            if (t >= 1f - DerivEpsilon)
            {
                Vector3 tan = GeodesicUtil.TangentAt(bh, b);
                if (tan.sqrMagnitude > 1e-6f) return tan;
            }
            // Interior — forward finite difference, with a backward fallback
            // for samples that ended up too close to t=1 to step forward.
            Vector3 dir, dirAhead;
            if (t < 1f - DerivEpsilon)
            {
                dir      = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                dirAhead = GeodesicUtil.SphericalBezier(a, ah, bh, b, t + DerivEpsilon);
                Vector3 fd = (dirAhead - dir).normalized;
                if (fd.sqrMagnitude > 1e-6f) return fd;
            }
            else
            {
                dir      = GeodesicUtil.SphericalBezier(a, ah, bh, b, 1f - DerivEpsilon);
                dirAhead = GeodesicUtil.SphericalBezier(a, ah, bh, b, 1f);
                Vector3 fd = (dirAhead - dir).normalized;
                if (fd.sqrMagnitude > 1e-6f) return fd;
            }
            return GeodesicUtil.TangentAt(a, b);
        }
        // Default surface lift in world units. Larger = the ribbon visibly
        // hovers above the terrain (no z-fighting under any LOD), smaller =
        // hugs more tightly. ~8 cm is the empirical sweet spot.
        public const float DefaultSurfaceLift = 0.08f;
        // Default floor pad: how far past the surface the guard skirt
        // extends. Big enough to bridge any seam between the ribbon and the
        // Globe collider in shallow valleys.
        public const float DefaultFloorPad = 1.0f;

        public enum GuardCrossSection
        {
            /// Skirt → wall → lip. Used at runtime for actual collision walls.
            Full,
            /// Just the outward skirt — for editor previews where seeing the
            /// track elevation matters more than seeing collision boundaries.
            OuterSkirtOnly,
        }

        public struct GuardOptions
        {
            public GuardCrossSection crossSection;
            public float wallHeight;
            public float lipOutward;
            public float skirtDepth;          // used only when floorAtSurface = false
            public float floorPad;            // used when floorAtSurface = true
            public float outwardOffset;
            /// When true, skirt bottom is anchored at (surface − floorPad)
            /// regardless of the sample's yOffset. So an elevated track's
            /// guard skirt visibly extends all the way down to the ground —
            /// the user-visible "height of the road above the planet" reads
            /// naturally on screen.
            public bool floorAtSurface;
            /// Combined toggle. When true, both start and finish endcaps are
            /// emitted. Kept for callers that don't care about the asymmetry.
            public bool addEndcaps;
            /// When true, emit a perpendicular wall at the start anchor.
            /// Usually FALSE when the spawn pad supplies its own back wall —
            /// otherwise two parallel walls land right behind the polesitter.
            public bool addStartEndcap;
            /// When true, emit a perpendicular wall at the finish anchor —
            /// the canonical "finish line" wall.
            public bool addFinishEndcap;

            public static GuardOptions Default => new GuardOptions
            {
                crossSection    = GuardCrossSection.Full,
                wallHeight      = 2.6f,
                lipOutward      = 0.4f,
                skirtDepth      = 1f,
                floorPad        = DefaultFloorPad,
                outwardOffset   = 0.05f,
                floorAtSurface  = true,
                addEndcaps      = false,
                addStartEndcap  = false,
                addFinishEndcap = false,
            };
        }

        /// Build the full track ribbon. Pass a `raycaster` to make every
        /// sample hug the Globe surface; pass null to fall back to the
        /// level's nominal sphere (fine for off-screen render targets that
        /// don't need surface accuracy). `segmentsPerArc` lets the editor
        /// preview run at quarter resolution while the runtime stays at
        /// full quality. `scratch` opt-in reuses the buffer lists between
        /// rebuilds — the editor's drag loop benefits hugely.
        public static Mesh Build(LevelData level, GlobeRaycaster raycaster = null,
            float surfaceLift = DefaultSurfaceLift,
            int segmentsPerArc = DefaultSegmentsPerArc,
            TrackBuilderScratch scratch = null)
        {
            if (level == null || !level.HasMinimum) return null;
            level.EnsureLinearChainNext();
            if (segmentsPerArc < 4) segmentsPerArc = 4;

            float halfW = level.EffectiveTrackWidth * 0.5f;

            List<Vector3> verts;
            List<Vector3> normals;
            List<Vector2> uvs;
            List<int>     tris;
            if (scratch != null)
            {
                scratch.ClearRibbon();
                verts = scratch.verts; normals = scratch.normals; uvs = scratch.uvs; tris = scratch.tris;
            }
            else
            {
                verts   = new List<Vector3>();
                normals = new List<Vector3>();
                uvs     = new List<Vector2>();
                tris    = new List<int>();
            }

            // L/R vertex indices cached per anchor so adjacent edges sharing
            // a node reuse exact same boundary verts. The lateral frame at
            // any anchor is uniquely determined by its shared in/outHandle,
            // so reuse is always seam-free. Cached anchor radial heights
            // also prevent floating-point drift between two edges meeting
            // at the same node.
            var anchorVerts = new Dictionary<int, (int left, int right)>();
            var anchorRadial = new Dictionary<int, float>();

            float arcCursor = 0f;

            foreach (var (i, j) in level.EnumerateEdges())
            {
                level.GetEdge(i, j, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);

                // Compute each endpoint's RADIAL HEIGHT exactly once. Per-
                // sample raycast made the polyline jitter on uneven terrain
                // and produced a visible dip at the endpoint just before t=1
                // (a single raycast missed → fallback to NominalRadius for
                // that one sample). Lerping between two anchor heights
                // matches "normal bezier" behaviour and is smooth by
                // construction.
                if (!anchorRadial.TryGetValue(i, out float startRadial))
                {
                    float surfA = raycaster != null ? raycaster.ResolveSurfaceRadius(a) : level.NominalRadius;
                    startRadial = surfA + level.points[i].yOffset;
                    anchorRadial[i] = startRadial;
                }
                if (!anchorRadial.TryGetValue(j, out float endRadial))
                {
                    float surfB = raycaster != null ? raycaster.ResolveSurfaceRadius(b) : level.NominalRadius;
                    endRadial = surfB + level.points[j].yOffset;
                    anchorRadial[j] = endRadial;
                }

                int prevLeft  = -1;
                int prevRight = -1;
                Vector3 prevWorldCenter = Vector3.zero;
                Vector3 prevDir = Vector3.zero;
                Vector3 prevTan = Vector3.zero;
                bool   havePrevSample = false;

                for (int s = 0; s <= segmentsPerArc; s++)
                {
                    float t = (float)s / segmentsPerArc;
                    bool atStart = s == 0;
                    bool atEnd   = s == segmentsPerArc;
                    int anchorAt = atStart ? i : (atEnd ? j : -1);

                    int left, right;
                    Vector3 worldCenter;

                    if (anchorAt >= 0 && anchorVerts.TryGetValue(anchorAt, out var cached))
                    {
                        left = cached.left;
                        right = cached.right;
                        worldCenter = (verts[left] + verts[right]) * 0.5f;
                        prevDir = worldCenter.normalized;
                        prevTan = BezierTangent(a, ah, bh, b, t);
                        havePrevSample = true;
                    }
                    else
                    {
                        Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                        Vector3 tangent = BezierTangent(a, ah, bh, b, t);

                        // Fold-back guard: on extremely tight turns the
                        // bezier can briefly reverse along the previous
                        // tangent, producing extruded edges that loop back
                        // on themselves. Detect via dot(newDir-prevDir,
                        // prevTan) — if negative, the new sample is "behind"
                        // the previous one. Snap its angular position to
                        // the previous sample (so the cross-section stops
                        // moving) but keep the new lerped Y for smoothness.
                        if (havePrevSample)
                        {
                            Vector3 delta = dir - prevDir;
                            if (Vector3.Dot(delta, prevTan) < 0f)
                            {
                                dir = prevDir;
                                tangent = prevTan;
                            }
                        }

                        Vector3 normal = dir;
                        Vector3 width  = Vector3.Cross(tangent, normal).normalized;

                        float radial = Mathf.Lerp(startRadial, endRadial, t) + surfaceLift;
                        worldCenter = dir * radial;
                        Vector3 vL  = worldCenter + width * halfW;
                        Vector3 vR  = worldCenter - width * halfW;

                        left  = verts.Count;     verts.Add(vL); normals.Add(normal); uvs.Add(default);
                        right = verts.Count;     verts.Add(vR); normals.Add(normal); uvs.Add(default);

                        if (anchorAt >= 0) anchorVerts[anchorAt] = (left, right);
                        prevDir = dir;
                        prevTan = tangent;
                        havePrevSample = true;
                    }

                    if (s > 0)
                        arcCursor += Vector3.Distance(prevWorldCenter, worldCenter);

                    float uvU = arcCursor / Mathf.Max(0.01f, level.EffectiveTrackWidth);
                    uvs[left]  = new Vector2(uvU, 0f);
                    uvs[right] = new Vector2(uvU, 1f);

                    if (s > 0)
                    {
                        tris.Add(prevLeft);  tris.Add(left);  tris.Add(prevRight);
                        tris.Add(prevRight); tris.Add(left);  tris.Add(right);
                    }

                    prevLeft = left;
                    prevRight = right;
                    prevWorldCenter = worldCenter;
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

        /// Build a pair of guard-rail meshes along each side of every edge
        /// in the graph. Endcaps (`addEndcaps = true`) close off open
        /// endpoints with a perpendicular wall.
        ///
        /// At a multi-edge anchor (fork or join) the inner-side guards of
        /// adjacent edges currently overlap until the edges separate by more
        /// than a track width — Pass 2 of the refactor adds a binary search
        /// to find the divergence-t and skip emission up to that point.
        /// For linear chains this is a no-op.
        public static (Mesh left, Mesh right) BuildGuards(LevelData level, GlobeRaycaster raycaster = null,
            GuardOptions? options = null,
            int segmentsPerArc = DefaultSegmentsPerArc,
            TrackBuilderScratch scratch = null)
        {
            if (level == null || !level.HasMinimum) return (null, null);
            level.EnsureLinearChainNext();
            if (segmentsPerArc < 4) segmentsPerArc = 4;
            var opt = options ?? GuardOptions.Default;

            List<Vector3> leftVerts;
            List<int>     leftTris;
            List<Vector3> rightVerts;
            List<int>     rightTris;
            if (scratch != null)
            {
                scratch.ClearGuards();
                leftVerts = scratch.guardL; leftTris = scratch.guardLT;
                rightVerts = scratch.guardR; rightTris = scratch.guardRT;
            }
            else
            {
                leftVerts  = new List<Vector3>();
                leftTris   = new List<int>();
                rightVerts = new List<Vector3>();
                rightTris  = new List<int>();
            }

            // Skip ranges per (from, to) edge: how many initial / trailing
            // samples to drop on the LEFT or RIGHT guard. Populated below for
            // edges meeting at a fork or join. For linear chains every entry
            // is zero and the whole strip is emitted as before.
            var skipStartL = new Dictionary<(int, int), int>();
            var skipStartR = new Dictionary<(int, int), int>();
            var skipEndL   = new Dictionary<(int, int), int>();
            var skipEndR   = new Dictionary<(int, int), int>();
            ComputeForkJoinSkips(level, raycaster, skipStartL, skipStartR, skipEndL, skipEndR, segmentsPerArc);

            foreach (var (i, j) in level.EnumerateEdges())
            {
                int leftStart  = skipStartL.TryGetValue((i, j), out int v1) ? v1 : 0;
                int rightStart = skipStartR.TryGetValue((i, j), out int v2) ? v2 : 0;
                int leftEnd    = skipEndL  .TryGetValue((i, j), out int v3) ? v3 : 0;
                int rightEnd   = skipEndR  .TryGetValue((i, j), out int v4) ? v4 : 0;

                EmitGuardStrip(level, raycaster, opt, i, j, signOfWidth: +1f,
                    leftStart, segmentsPerArc - leftEnd, segmentsPerArc, leftVerts, leftTris);
                EmitGuardStrip(level, raycaster, opt, i, j, signOfWidth: -1f,
                    rightStart, segmentsPerArc - rightEnd, segmentsPerArc, rightVerts, rightTris);
            }

            // Endcap walls at chain endpoints. Independent flags so callers
            // can opt in to just the finish line (typical when the spawn pad
            // already supplies a wall behind the start anchor). Legacy
            // combined `addEndcaps` toggles both for back-compat.
            bool wantStart  = opt.addStartEndcap  || opt.addEndcaps;
            bool wantFinish = opt.addFinishEndcap || opt.addEndcaps;
            if (wantStart)
                AppendEndcap(level, raycaster, opt, leftVerts, leftTris, rightVerts, rightTris, atFinish: false);
            if (wantFinish)
                AppendEndcap(level, raycaster, opt, leftVerts, leftTris, rightVerts, rightTris, atFinish: true);

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

            return (BuildMesh("DioGuardLeft",  leftVerts,  leftTris),
                    BuildMesh("DioGuardRight", rightVerts, rightTris));
        }

        // Emit one continuous strip of guard cross-sections for one side of
        // one edge between sample indices `sStart` and `sEnd` (inclusive).
        // Radial height lerps between the two endpoint heights — same logic
        // as Build() so the rail's top edge tracks the ribbon exactly.
        // The skirt bottom is independent and uses each sample's actual
        // surface raycast (so the rail still hugs uneven terrain DOWNWARD
        // even when the ribbon's top is flat).
        //
        // Per-edge floating rail: if the edge's start anchor sets
        // `guardRailFloating`, override `floorAtSurface = false` and pin the
        // skirt depth to roughly the wall height (so the rail's vertical
        // span is symmetric — wallHeight up, ~wallHeight + 0.05u down). This
        // lets an elevated/looping section keep a tidy short skirt instead
        // of dragging a giant wall down to the ground beneath it.
        static void EmitGuardStrip(LevelData level, GlobeRaycaster raycaster, in GuardOptions opt,
            int from, int to, float signOfWidth, int sStart, int sEnd, int segmentsPerArc,
            List<Vector3> verts, List<int> tris)
        {
            if (sStart > sEnd) return;
            level.GetEdge(from, to, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
            float halfW = level.EffectiveTrackWidth * 0.5f;

            // Per-edge override of the cross-section's floor behaviour.
            GuardOptions edgeOpt = opt;
            if (level.points[from].guardRailFloating)
            {
                edgeOpt.floorAtSurface = false;
                edgeOpt.skirtDepth     = 0.05f + Mathf.Max(0f, opt.wallHeight);
            }

            float startSurfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(a) : level.NominalRadius;
            float endSurfaceR   = raycaster != null ? raycaster.ResolveSurfaceRadius(b) : level.NominalRadius;
            float startTop = startSurfaceR + level.points[from].yOffset;
            float endTop   = endSurfaceR   + level.points[to].yOffset;

            int local = 0;
            // Endpoint tangents come from BezierTangent's analytic branch
            // (anchor → outHandle at t=0, inHandle → anchor at t=1) so two
            // adjacent edges meeting at the same anchor produce identical
            // lateral frames at the joint. No more rail tapering at the
            // start/end of the strip — both ends use the bezier's true
            // direction, not a per-sample finite-difference approximation
            // that varies between adjacent edges.
            Vector3 prevDir = Vector3.zero;
            Vector3 prevTan = Vector3.zero;
            bool havePrev = false;
            for (int s = sStart; s <= sEnd; s++)
            {
                float t = (float)s / segmentsPerArc;
                Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                Vector3 tangent = BezierTangent(a, ah, bh, b, t);

                // Fold-back guard — same logic as Build(). Tight bezier
                // bends can step backward along the previous tangent, which
                // creates inverted rail cross-sections. Snap to the previous
                // sample when that happens.
                if (havePrev)
                {
                    Vector3 delta = dir - prevDir;
                    if (Vector3.Dot(delta, prevTan) < 0f)
                    {
                        dir = prevDir;
                        tangent = prevTan;
                    }
                }

                Vector3 width = Vector3.Cross(tangent, dir).normalized;
                Vector3 sideOutward = signOfWidth > 0 ? width : -width;

                float topRadial = Mathf.Lerp(startTop, endTop, t);
                // Per-sample surface raycast for the SKIRT bottom only, so
                // the floor still hugs the actual ground beneath an elevated
                // ribbon. Falls back to NominalRadius on miss.
                float surfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : level.NominalRadius;

                Vector3 anchor = dir * topRadial + sideOutward * (halfW + edgeOpt.outwardOffset);
                int inwardSign = signOfWidth > 0 ? -1 : +1;
                AppendCross(verts, tris, anchor, dir, surfaceR, sideOutward, edgeOpt, local, inwardSign);
                prevDir = dir;
                prevTan = tangent;
                havePrev = true;
                local++;
            }
        }

        // Find for each fork (multi-out anchor) and join (multi-in anchor) how
        // many initial / trailing samples on the inner-side guards must be
        // dropped to avoid two adjacent edges' rails overlapping. Algorithm:
        //   1. Sort outgoing edges by their initial tangent's angle around
        //      the anchor's surface normal (left-to-right ordering).
        //   2. For each adjacent pair (eA leftward of eB), step samples
        //      forward until eA's right-edge-of-track and eB's left-edge-of-
        //      track are separated by at least `EffectiveTrackWidth`. That
        //      sample index becomes the skip count for both rails.
        //   3. Mirror the logic at multi-in anchors using the FINAL tangent
        //      of each incoming edge (so the rails on the join end converge
        //      cleanly).
        // The leftmost outgoing's left rail and the rightmost outgoing's right
        // rail are never affected — they remain the outer envelope of the fork.
        static void ComputeForkJoinSkips(LevelData level, GlobeRaycaster raycaster,
            Dictionary<(int, int), int> skipStartL, Dictionary<(int, int), int> skipStartR,
            Dictionary<(int, int), int> skipEndL,   Dictionary<(int, int), int> skipEndR,
            int segmentsPerArc)
        {
            float threshold = level.EffectiveTrackWidth;

            // Outgoing groups.
            for (int i = 0; i < level.points.Count; i++)
            {
                var nx = level.points[i].next;
                if (nx == null || nx.Count < 2) continue;
                var sorted = SortBranchesByTangent(level, i, nx, atStart: true);
                for (int k = 0; k + 1 < sorted.Count; k++)
                {
                    int jL = sorted[k];     // leftward branch
                    int jR = sorted[k + 1]; // rightward
                    int sk = FindFanOutSampleIndex(level, raycaster, i, jL, jR, atStart: true, threshold, segmentsPerArc);
                    skipStartR[(i, jL)] = sk;
                    skipStartL[(i, jR)] = sk;
                }
            }

            // Incoming groups: invert via inverse adjacency.
            var incoming = new Dictionary<int, List<int>>();
            for (int i = 0; i < level.points.Count; i++)
            {
                var nx = level.points[i].next;
                if (nx == null) continue;
                for (int k = 0; k < nx.Count; k++)
                {
                    int j = nx[k];
                    if (!incoming.TryGetValue(j, out var list)) { list = new List<int>(); incoming[j] = list; }
                    list.Add(i);
                }
            }
            foreach (var kv in incoming)
            {
                int j = kv.Key;
                var ins = kv.Value;
                if (ins.Count < 2) continue;
                var sorted = SortBranchesByTangent(level, j, ins, atStart: false);
                for (int k = 0; k + 1 < sorted.Count; k++)
                {
                    int iL = sorted[k];
                    int iR = sorted[k + 1];
                    int sk = FindFanOutSampleIndex(level, raycaster, j, iL, iR, atStart: false, threshold, segmentsPerArc);
                    skipEndR[(iL, j)] = sk;
                    skipEndL[(iR, j)] = sk;
                }
            }
        }

        // Sort `neighbours` (indices that share an edge with `anchor`) by the
        // signed angle of each edge's initial / final tangent around the
        // anchor's surface normal. Returns indices left-to-right.
        static List<int> SortBranchesByTangent(LevelData level, int anchor, List<int> neighbours, bool atStart)
        {
            Vector3 normal = level.DirOf(anchor);
            // Reference axis: project the first neighbour's tangent onto the
            // tangent plane and use that as 0°.
            Vector3 refTan = Vector3.zero;
            var tangents = new List<(int idx, Vector3 tan)>(neighbours.Count);
            for (int k = 0; k < neighbours.Count; k++)
            {
                int other = neighbours[k];
                int from = atStart ? anchor : other;
                int to   = atStart ? other  : anchor;
                level.GetEdge(from, to, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                float t = atStart ? DerivEpsilon : 1f - DerivEpsilon;
                float t0 = atStart ? 0f : 1f;
                Vector3 dirA = GeodesicUtil.SphericalBezier(a, ah, bh, b, t0);
                Vector3 dirB = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                Vector3 tan = (dirB - dirA).normalized;
                // Project to tangent plane.
                tan -= Vector3.Dot(tan, normal) * normal;
                if (tan.sqrMagnitude > 1e-8f) tan.Normalize();
                else tan = Vector3.Cross(normal, Vector3.up).normalized;
                if (k == 0) refTan = tan;
                tangents.Add((other, tan));
            }
            var withAngle = new List<(int idx, float angle)>(tangents.Count);
            for (int k = 0; k < tangents.Count; k++)
            {
                float ang = Vector3.SignedAngle(refTan, tangents[k].tan, normal);
                withAngle.Add((tangents[k].idx, ang));
            }
            withAngle.Sort((x, y) => x.angle.CompareTo(y.angle));
            var result = new List<int>(withAngle.Count);
            for (int k = 0; k < withAngle.Count; k++) result.Add(withAngle[k].idx);
            return result;
        }

        // Step sample-by-sample forward (or backward, for joins) along two
        // adjacent edges and find the first sample where their inner rails
        // (eA's right vs eB's left) are at least `threshold` apart.
        static int FindFanOutSampleIndex(LevelData level, GlobeRaycaster raycaster,
            int anchor, int otherL, int otherR, bool atStart, float threshold, int segmentsPerArc)
        {
            int fromL = atStart ? anchor : otherL;
            int toL   = atStart ? otherL : anchor;
            int fromR = atStart ? anchor : otherR;
            int toR   = atStart ? otherR : anchor;
            level.GetEdge(fromL, toL, out Vector3 aL, out Vector3 ahL, out Vector3 bhL, out Vector3 bL);
            level.GetEdge(fromR, toR, out Vector3 aR, out Vector3 ahR, out Vector3 bhR, out Vector3 bR);
            float halfW = level.EffectiveTrackWidth * 0.5f;
            for (int s = 0; s <= segmentsPerArc; s++)
            {
                float t = atStart ? (float)s / segmentsPerArc : 1f - (float)s / segmentsPerArc;
                Vector3 dirL = GeodesicUtil.SphericalBezier(aL, ahL, bhL, bL, t);
                Vector3 dirAheadL = GeodesicUtil.SphericalBezier(aL, ahL, bhL, bL, Mathf.Clamp01(t + DerivEpsilon));
                Vector3 tanL = (dirAheadL - dirL).normalized;
                if (tanL.sqrMagnitude < 1e-6f) tanL = GeodesicUtil.TangentAt(aL, bL);
                Vector3 wL = Vector3.Cross(tanL, dirL).normalized;
                float surfL = raycaster != null ? raycaster.ResolveSurfaceRadius(dirL) : level.NominalRadius;
                Vector3 ptLR = dirL * surfL - wL * halfW;

                Vector3 dirR = GeodesicUtil.SphericalBezier(aR, ahR, bhR, bR, t);
                Vector3 dirAheadR = GeodesicUtil.SphericalBezier(aR, ahR, bhR, bR, Mathf.Clamp01(t + DerivEpsilon));
                Vector3 tanR = (dirAheadR - dirR).normalized;
                if (tanR.sqrMagnitude < 1e-6f) tanR = GeodesicUtil.TangentAt(aR, bR);
                Vector3 wR = Vector3.Cross(tanR, dirR).normalized;
                float surfR = raycaster != null ? raycaster.ResolveSurfaceRadius(dirR) : level.NominalRadius;
                Vector3 ptRL = dirR * surfR + wR * halfW;

                if (Vector3.Distance(ptLR, ptRL) >= threshold)
                    return s;
            }
            return segmentsPerArc;
        }

        // Emit one cross-section sample (skirt → wall → lip, or just skirt
        // for the outer-only editor preview). Caller passes:
        //   `anchor`      = world position at the rail's top corner (surface-
        //                   relative, includes yOffset).
        //   `dir`         = unit direction from origin (surface up).
        //   `surfaceR`    = surface radius at this sample (yOffset-independent).
        //                   Used to anchor the skirt at ground when
        //                   `floorAtSurface` is on.
        //   `outward`     = unit vector pointing AWAY from the track center.
        //   `inwardSign`  = winding flip so both rails face inward.
        static void AppendCross(List<Vector3> verts, List<int> tris,
            Vector3 anchor, Vector3 dir, float surfaceR, Vector3 outward,
            in GuardOptions opt, int sampleIndex, int inwardSign)
        {
            // Skirt bottom: either fixed depth below the rail anchor, or
            // anchored at the actual surface (so an elevated track shows its
            // height in the rail).
            Vector3 vBot = opt.floorAtSurface
                ? dir * (surfaceR - opt.floorPad)
                : anchor - dir * opt.skirtDepth;
            Vector3 vSurf    = anchor;
            Vector3 vWallTop = anchor + dir * opt.wallHeight;
            Vector3 vLip     = vWallTop + outward * opt.lipOutward;

            int baseIdx = verts.Count;

            if (opt.crossSection == GuardCrossSection.OuterSkirtOnly)
            {
                // Just two verts per ring (bottom + surface) — outward face
                // is the visible "side of the road" for editor previews.
                verts.Add(vBot); verts.Add(vSurf);
                if (sampleIndex == 0) return;
                int p = baseIdx - 2;
                int c = baseIdx;
                EmitQuad(tris, p + 0, p + 1, c + 0, c + 1, inwardSign);
                return;
            }

            verts.Add(vBot); verts.Add(vSurf); verts.Add(vWallTop); verts.Add(vLip);
            if (sampleIndex == 0) return;

            int p4 = baseIdx - 4;
            int c4 = baseIdx;
            EmitQuad(tris, p4 + 0, p4 + 1, c4 + 0, c4 + 1, inwardSign);
            EmitQuad(tris, p4 + 1, p4 + 2, c4 + 1, c4 + 2, inwardSign);
            EmitQuad(tris, p4 + 2, p4 + 3, c4 + 2, c4 + 3, inwardSign);
        }

        // A single endcap = a wall sweeping across the track width at a
        // chain endpoint (start or finish). Geometry is the same Z section
        // used on the side guards, swept perpendicular to the chain's
        // tangent.
        static void AppendEndcap(LevelData level, GlobeRaycaster raycaster, in GuardOptions opt,
            List<Vector3> leftVerts, List<int> leftTris, List<Vector3> rightVerts, List<int> rightTris,
            bool atFinish)
        {
            int idx = atFinish ? level.points.Count - 1 : 0;
            Vector3 dir = level.DirOf(idx);
            float surfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : level.NominalRadius;
            float yOffset = level.points[idx].yOffset;
            float topRadial = surfaceR + yOffset;

            Vector3 tan = atFinish ? TangentAt(level, 1f) : TangentAt(level, 0f);
            Vector3 width = Vector3.Cross(tan, dir).normalized;
            // Outward = the chain's exit direction; for the finish that's
            // forward, for the start that's backward.
            Vector3 outward = atFinish ? tan.normalized : -tan.normalized;
            float halfW = level.EffectiveTrackWidth * 0.5f;

            const int WallSamples = 8;
            for (int s = 0; s <= WallSamples; s++)
            {
                float u = (float)s / WallSamples - 0.5f;
                Vector3 anchor = dir * topRadial + width * (level.EffectiveTrackWidth * u);
                AppendCross(leftVerts, leftTris, anchor, dir, surfaceR, outward, opt, s, inwardSign: +1);
            }
            // Mirror on the right buffer is unnecessary — endcaps get appended
            // to the left buffer by convention. They render under the same
            // material as the left guard.
            _ = rightVerts; _ = rightTris;
        }

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

        public struct SpawnPadResult
        {
            public Mesh ribbon;
            public Mesh guardLeft;
            public Mesh guardRight;
            public Mesh backWall;
        }

        /// Short geodesic ribbon extending BACKWARD from the start anchor
        /// along the level's start tangent, with full side guards (Z section)
        /// and a perpendicular back wall at the FAR end (away from the start).
        ///
        /// Conceptually this is "one virtual extension behind the start
        /// anchor". The ribbon's front end shares the same lateral frame and
        /// radial height as the live track at t=0 of edge 0, so the seam
        /// between spawn pad and main track is invisible. Side guards are
        /// generated in the same Z section as the main BuildGuards output —
        /// a car bouncing off a side rail at the back of the grid sees the
        /// same wall it'd see anywhere else on the track.
        ///
        /// Y is held uniform at the start anchor's resolved height across
        /// the entire pad, so back-row cars stand at the same height as the
        /// polesitter and the back wall ends up at the same elevation.
        public static SpawnPadResult BuildSpawnPad(LevelData level, float length,
            GlobeRaycaster raycaster = null,
            GuardOptions? guardOptions = null,
            float surfaceLift = DefaultSurfaceLift)
        {
            if (level == null || !level.HasMinimum || length <= 0.01f) return default;
            level.EnsureLinearChainNext();

            var opt = guardOptions ?? GuardOptions.Default;
            // Lock Y across the entire pad so back-row cars sit at the same
            // elevation as the polesitter and the seam to the live track is
            // invisible (TrackBuilder.Build's cached anchor for index 0 ends
            // up at exactly the same world position).
            float halfW = level.EffectiveTrackWidth * 0.5f;

            Vector3 startDir = level.DirOf(0);
            Vector3 startTan = TangentAt(level, 0f);
            Vector3 backTan = -startTan;
            float startYOffset = level.points[0].yOffset;
            float startSurfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(startDir) : level.NominalRadius;
            float padTop = startSurfaceR + startYOffset;            // before surfaceLift
            float padRibbonY = padTop + surfaceLift;                 // ribbon center radial

            float r = level.NominalRadius;
            float totalAngle = length / r;
            const int Samples = 24;

            Vector3 axis = Vector3.Cross(startDir, backTan).normalized;
            if (axis.sqrMagnitude < 1e-6f) return default;

            var ribbonVerts   = new List<Vector3>(Samples * 2 + 2);
            var ribbonNormals = new List<Vector3>(ribbonVerts.Capacity);
            var ribbonUvs     = new List<Vector2>(ribbonVerts.Capacity);
            var ribbonTris    = new List<int>(Samples * 6);

            var leftVerts  = new List<Vector3>();
            var leftTris   = new List<int>();
            var rightVerts = new List<Vector3>();
            var rightTris  = new List<int>();

            // Walk samples from FAR (s=0, back of pad) to NEAR (s=Samples,
            // start anchor) so the strip's last ring exactly matches the
            // live track's t=0 ring (same direction, same lateral frame,
            // same radial Y). The seam is invisible by construction.
            for (int s = 0; s <= Samples; s++)
            {
                float frac = (float)s / Samples;            // 0 at back, 1 at start
                float arcAngle = (1f - frac) * totalAngle;  // angle from start (0 at start, totalAngle at back)
                Quaternion q = Quaternion.AngleAxis(arcAngle * Mathf.Rad2Deg, axis);
                Vector3 dir = q * startDir;
                Vector3 tan = q * startTan;
                Vector3 w = Vector3.Cross(tan, dir).normalized;
                Vector3 center = dir * padRibbonY;
                Vector3 vL = center + w * halfW;
                Vector3 vR = center - w * halfW;
                ribbonVerts.Add(vL); ribbonVerts.Add(vR);
                ribbonNormals.Add(dir); ribbonNormals.Add(dir);
                ribbonUvs.Add(new Vector2(s, 0)); ribbonUvs.Add(new Vector2(s, 1));

                if (ribbonVerts.Count >= 4)
                {
                    int v = ribbonVerts.Count - 4;
                    ribbonTris.Add(v + 0); ribbonTris.Add(v + 2); ribbonTris.Add(v + 1);
                    ribbonTris.Add(v + 1); ribbonTris.Add(v + 2); ribbonTris.Add(v + 3);
                }

                // Side guards along the entire pad. Same Z section as the
                // main BuildGuards output, so a side rail at the back of the
                // grid feels identical to one mid-track. surfaceR for the
                // skirt is per-sample raycast (so the floor still hugs the
                // ground beneath the elevated pad).
                float surfHere = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : startSurfaceR;
                Vector3 leftAnchor  = dir * padTop + w * (halfW + opt.outwardOffset);
                Vector3 rightAnchor = dir * padTop - w * (halfW + opt.outwardOffset);
                AppendCross(leftVerts,  leftTris,  leftAnchor,  dir, surfHere,  w,        opt, s, inwardSign: -1);
                AppendCross(rightVerts, rightTris, rightAnchor, dir, surfHere, -1f * w,   opt, s, inwardSign: +1);
            }

            var ribbon = new Mesh
            {
                name = "DioSpawnPad",
                indexFormat = ribbonVerts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            ribbon.SetVertices(ribbonVerts);
            ribbon.SetNormals(ribbonNormals);
            ribbon.SetUVs(0, ribbonUvs);
            ribbon.SetTriangles(ribbonTris, 0);
            ribbon.RecalculateBounds();

            Mesh BuildSubMesh(string n, List<Vector3> vv, List<int> tt)
            {
                var m = new Mesh
                {
                    name = n,
                    indexFormat = vv.Count > 65000
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16,
                };
                m.SetVertices(vv);
                m.SetTriangles(tt, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }

            var guardLeft  = BuildSubMesh("DioSpawnPadGuardLeft",  leftVerts,  leftTris);
            var guardRight = BuildSubMesh("DioSpawnPadGuardRight", rightVerts, rightTris);

            // Back wall at the FAR (s=0) end of the pad — perpendicular to
            // the track tangent there, sweeping across the track width. Same
            // Z section as the side guards.
            Quaternion qBack = Quaternion.AngleAxis(totalAngle * Mathf.Rad2Deg, axis);
            Vector3 backDir = qBack * startDir;
            Vector3 backTanRot = qBack * startTan;
            Vector3 backWidth = Vector3.Cross(backTanRot, backDir).normalized;
            float backSurfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(backDir) : startSurfaceR;
            Vector3 backCenter = backDir * padTop;
            Vector3 outward = -backTanRot.normalized;

            var wallVerts = new List<Vector3>();
            var wallTris = new List<int>();
            const int WallSamples = 8;
            for (int s = 0; s <= WallSamples; s++)
            {
                float u = (float)s / WallSamples - 0.5f;
                Vector3 anchor = backCenter + backWidth * (level.EffectiveTrackWidth * u);
                AppendCross(wallVerts, wallTris, anchor, backDir, backSurfaceR, outward, opt, s, inwardSign: +1);
            }
            var backWall = BuildSubMesh("DioSpawnPadBackWall", wallVerts, wallTris);

            return new SpawnPadResult
            {
                ribbon = ribbon,
                guardLeft = guardLeft,
                guardRight = guardRight,
                backWall = backWall,
            };
        }

        /// World position at parametric distance t in [0..1] along the linear
        /// chain. Branched graphs collapse to the linear order returned by
        /// `EnumerateEdges` here — fine for finish detection on the canonical
        /// path, the only consumer for now.
        public static Vector3 SampleAt(LevelData level, float t, GlobeRaycaster raycaster = null)
        {
            if (level == null || !level.HasMinimum) return Vector3.zero;
            level.EnsureLinearChainNext();
            t = Mathf.Clamp01(t);
            int segments = level.points.Count - 1;
            float scaled = t * segments;
            int seg = Mathf.Min(segments - 1, Mathf.FloorToInt(scaled));
            float local = scaled - seg;
            level.GetEdge(seg, seg + 1, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
            float yOffset = Mathf.Lerp(level.points[seg].yOffset, level.points[seg + 1].yOffset, local);
            Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, local);
            float surfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : level.NominalRadius;
            return dir * (surfaceR + yOffset);
        }

        /// Tangent direction at parametric t — used to orient powerup boxes
        /// along the track and to set the start-line car heading.
        public static Vector3 TangentAt(LevelData level, float t)
        {
            if (level == null || !level.HasMinimum) return Vector3.forward;
            level.EnsureLinearChainNext();
            t = Mathf.Clamp01(t);
            int segments = level.points.Count - 1;
            float scaled = t * segments;
            int seg = Mathf.Min(segments - 1, Mathf.FloorToInt(scaled));
            float local = scaled - seg;
            level.GetEdge(seg, seg + 1, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
            Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, local);
            Vector3 dirAhead = GeodesicUtil.SphericalBezier(a, ah, bh, b, Mathf.Min(1f, local + DerivEpsilon));
            Vector3 tan = (dirAhead - dir).normalized;
            if (tan.sqrMagnitude < 1e-6f) tan = GeodesicUtil.TangentAt(a, b);
            return tan;
        }

        /// Total arc length of the track in world units. Linear-chain assumption.
        public static float TotalArcLength(LevelData level, GlobeRaycaster raycaster = null)
        {
            if (level == null || !level.HasMinimum) return 0f;
            level.EnsureLinearChainNext();
            float r = level.NominalRadius;
            const int Samples = 64;
            float total = 0f;
            foreach (var (i, j) in level.EnumerateEdges())
            {
                level.GetEdge(i, j, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                Vector3 prev = GeodesicUtil.SphericalBezier(a, ah, bh, b, 0f) *
                               (raycaster != null ? raycaster.ResolveSurfaceRadius(a) : r);
                for (int s = 1; s <= Samples; s++)
                {
                    float t = (float)s / Samples;
                    Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                    float surfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : r;
                    Vector3 cur = dir * surfaceR;
                    total += Vector3.Distance(prev, cur);
                    prev = cur;
                }
            }
            return total;
        }

        /// Project a world-space point onto the sampled bezier chain.
        /// Returns the arc length from start to the projected point.
        /// Linear-chain assumption — branches use `EnumerateEdges` order.
        public static float ArcLengthOf(LevelData level, Vector3 worldPos, Vector3 planetCenter, GlobeRaycaster raycaster = null)
        {
            if (level == null || !level.HasMinimum) return 0f;
            level.EnsureLinearChainNext();

            Vector3 dir = (worldPos - planetCenter).normalized;
            if (dir.sqrMagnitude < 1e-6f) return 0f;

            float r = level.NominalRadius;
            const int Samples = 64;

            float bestArc = 0f;
            float bestDist2 = float.MaxValue;
            float arcCursor = 0f;
            Vector3 prev = Vector3.zero;
            bool first = true;

            foreach (var (i, j) in level.EnumerateEdges())
            {
                level.GetEdge(i, j, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                for (int s = 0; s <= Samples; s++)
                {
                    if (!first && s == 0) continue;
                    float t = (float)s / Samples;
                    Vector3 cur = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                    float surfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(cur) : r;
                    Vector3 curWorld = cur * surfaceR;
                    if (!first) arcCursor += Vector3.Distance(prev, curWorld);
                    first = false;
                    float d2 = (cur - dir).sqrMagnitude;
                    if (d2 < bestDist2) { bestDist2 = d2; bestArc = arcCursor; }
                    prev = curWorld;
                }
            }
            return bestArc;
        }
    }
}
