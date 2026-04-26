using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dio.Level
{
    /// One node in the track graph. Position is `directionFromCenter * (surfaceRadius + yOffset)`,
    /// where `surfaceRadius` is resolved at runtime by raycasting against the Globe mesh
    /// in the anchor's direction. `yOffset` is therefore a designer-facing *delta above the
    /// terrain*, not an absolute height — so a track can hug rolling hills with yOffset=0
    /// and lift a section into the air with yOffset>0 regardless of what's underneath.
    ///
    /// The two handles describe spherical-cubic-Bezier control directions; they're shared
    /// across every edge that enters / leaves this node, so a fork retains tangent
    /// continuity without forcing the artist to manage one handle per branch.
    ///
    /// `next` is the adjacency list of outgoing connections. For a simple linear track
    /// it's `[i+1]` (filled in by `EnsureLinearChainNext` when missing). For branches it
    /// can hold multiple indices. Edges are reconstructed from this list in TrackBuilder
    /// rather than stored separately so the data structure stays a single source of truth.
    [Serializable]
    public struct TrackPoint
    {
        [Tooltip("Unit vector from planet center. The world position is direction * (surfaceY + yOffset).")]
        public Vector3 directionFromCenter;

        [Tooltip("Banking angle in degrees, used by the future track-mesh sweep.")]
        public float bank;

        // Spherical-cubic-Bezier control handles — unit directions from the
        // planet center. inHandle is the control on every segment ARRIVING at
        // this node (used as p2). outHandle is the control on every segment
        // LEAVING this node (used as p1). Shared across all incoming / outgoing
        // edges so a branched fork starts with the same tangent.
        [Tooltip("Bezier handle on segments ARRIVING at this point (p2).")]
        public Vector3 inHandle;

        [Tooltip("Bezier handle on segments LEAVING this point (p1).")]
        public Vector3 outHandle;

        [Tooltip("Height offset added on top of the raycast-derived surface radius (>= 0). Use this to lift sections of track above the terrain.")]
        public float yOffset;

        [Tooltip("Outgoing edges from this node (anchor indices). Empty = leaf / finish. Multiple = fork.")]
        public List<int> next;
    }

    [CreateAssetMenu(menuName = "Dio/Level Data", fileName = "NewLevel")]
    public class LevelData : ScriptableObject
    {
        [Tooltip("Designer-facing 'how far around the planet you'd drive', in world units. Internally we resolve a nominal radius = circumference / 2π.")]
        public float circumference = 1256.6f;   // ≈ 2π * 200, matches the legacy 200u radius

        [Tooltip("Per-level multiplier applied to trackWidth. 1.0 = use the canonical 15u width. Tighten for narrow mountain roads, widen for boulevards.")]
        public float trackRatio = 1f;

        [Tooltip("Canonical track width before trackRatio is applied. The effective width used by TrackBuilder is trackWidth * trackRatio.")]
        public float trackWidth = 15f;

        public List<TrackPoint> points = new List<TrackPoint>();

        /// Track width after applying the per-level ratio. Use this — not the
        /// raw `trackWidth` field — anywhere downstream (TrackBuilder, car
        /// spawn spacing, minimap markers, etc.) so a level's narrow/wide
        /// authoring choice flows through every system that depends on width.
        public float EffectiveTrackWidth => Mathf.Max(0.01f, trackWidth * Mathf.Max(0.01f, trackRatio));

        // -------- legacy compat shim --------------------------------------
        // Old levels were authored with a `planetRadius` field. Read+write
        // through this property so existing level assets, build menus, and
        // the editor inspector continue to work while we migrate.
        public float planetRadius
        {
            get => circumference / (2f * Mathf.PI);
            set => circumference = Mathf.Max(0.01f, value * 2f * Mathf.PI);
        }

        /// Nominal radius derived from the circumference. Used as the fallback
        /// when raycasts against the Globe miss, and as the assumed target
        /// "average radius" when scaling the Globe asset to fit.
        public float NominalRadius => circumference / (2f * Mathf.PI);

        public bool HasMinimum => points.Count >= 2;
        public TrackPoint Start => points.Count > 0 ? points[0] : default;
        public TrackPoint Finish => points.Count > 0 ? points[points.Count - 1] : default;

        public Vector3 DirOf(int i) =>
            (points[i].directionFromCenter.sqrMagnitude > 0f
                ? points[i].directionFromCenter.normalized
                : Vector3.up);

        /// Returns the four unit-vector control directions for the spherical
        /// cubic Bezier of edge from points[i] → points[j]. `ah` is the
        /// outgoing control on the start anchor; `bh` is the incoming control
        /// on the end anchor. Falls back to a near-geodesic default
        /// (slerp 1/3, 2/3) when handles are unset.
        public void GetEdge(int i, int j, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b)
        {
            a = DirOf(i);
            b = DirOf(j);
            Vector3 outH = points[i].outHandle;
            Vector3 inH  = points[j].inHandle;
            ah = outH.sqrMagnitude > 1e-6f ? outH.normalized : Vector3.Slerp(a, b, 1f / 3f).normalized;
            bh = inH.sqrMagnitude  > 1e-6f ? inH.normalized  : Vector3.Slerp(a, b, 2f / 3f).normalized;
        }

        /// Legacy linear-chain accessor. Deprecated; new code paths should
        /// iterate the adjacency list via `EnumerateEdges` instead.
        public void GetSegment(int i, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b)
            => GetEdge(i, i + 1, out a, out ah, out bh, out b);

        /// Per-anchor: if no outgoing edge has been authored, default the
        /// anchor to the linear-chain successor (i → i+1). NEVER removes or
        /// rewires an existing edge — explicit forks and joins survive
        /// untouched. Idempotent: rerunning is a no-op once every non-finish
        /// anchor has at least one outgoing edge.
        ///
        /// This shape — "fill in the linear default per anchor independently"
        /// — is critical for level assets with PARTIAL adjacency (e.g. a level
        /// authored with one explicit fork from anchor 1, leaving anchors 0,
        /// 2, 3 with empty `next`). The previous "all-or-nothing" rule
        /// dropped the chain entirely on those levels, leaving most edges
        /// missing — which manifested as "only one powerup row spawns" and
        /// "extension creates orphan points".
        public void EnsureLinearChainNext()
        {
            // 1. Null → empty so EnumerateEdges is always safe to walk.
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p.next == null) { p.next = new List<int>(); points[i] = p; }
            }
            // 2. Default each non-finish anchor with NO outgoing edges to
            //    the linear successor. This is per-anchor — explicit forks
            //    on other anchors don't suppress the default here.
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var p = points[i];
                if (p.next.Count == 0)
                {
                    p.next.Add(i + 1);
                    points[i] = p;
                }
            }
        }

        /// Yield every directed edge (from, to) in the graph, in points-list
        /// order. Used by TrackBuilder to walk the level and by the editor
        /// to draw beziers without baking a separate Edge list.
        public IEnumerable<(int from, int to)> EnumerateEdges()
        {
            for (int i = 0; i < points.Count; i++)
            {
                var nx = points[i].next;
                if (nx == null) continue;
                for (int k = 0; k < nx.Count; k++)
                    yield return (i, nx[k]);
            }
        }

        /// Resolve the world-space position of anchor `i` given the runtime
        /// raycaster. The raycaster returns the planet's surface radius at
        /// the anchor's direction; we add the designer-authored yOffset on
        /// top. Pass `null` to fall back to the nominal sphere radius.
        public Vector3 ResolvePosition(int i, GlobeRaycaster raycaster)
        {
            Vector3 dir = DirOf(i);
            float surface = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : NominalRadius;
            return dir * (surface + Mathf.Max(0f, points[i].yOffset));
        }

        /// Effective height (radial distance from origin) of an anchor under
        /// a given raycaster. Used by track / handle / preview rendering.
        public float ResolveHeight(int i, GlobeRaycaster raycaster)
        {
            Vector3 dir = DirOf(i);
            float surface = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : NominalRadius;
            return surface + Mathf.Max(0f, points[i].yOffset);
        }
    }
}
