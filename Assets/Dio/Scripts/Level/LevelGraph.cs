using System.Collections.Generic;
using UnityEngine;

namespace Dio.Level
{
    /// Graph queries against a LevelData's adjacency list. Linear chains and
    /// branched graphs both work — operations rely on `points[i].next` and
    /// nothing else.
    public static class LevelGraph
    {
        /// Minimum number of edges from `fromAnchor` to `toAnchor` along the
        /// directed adjacency. Returns 0 when from == to, -1 when no path.
        /// Breadth-first; capped at points.Count iterations so a corrupt
        /// graph (cycles in the wrong direction, etc.) can't infinite-loop.
        public static int MinHopsBetween(LevelData level, int fromAnchor, int toAnchor)
        {
            if (level == null || level.points.Count == 0) return -1;
            if (fromAnchor == toAnchor) return 0;
            level.EnsureLinearChainNext();

            var visited = new bool[level.points.Count];
            var queue = new Queue<(int idx, int hops)>();
            queue.Enqueue((fromAnchor, 0));
            visited[fromAnchor] = true;
            int cap = level.points.Count + 1;
            while (queue.Count > 0 && cap-- > 0)
            {
                var (idx, hops) = queue.Dequeue();
                var nx = level.points[idx].next;
                if (nx == null) continue;
                for (int k = 0; k < nx.Count; k++)
                {
                    int n = nx[k];
                    if (n < 0 || n >= level.points.Count || visited[n]) continue;
                    if (n == toAnchor) return hops + 1;
                    visited[n] = true;
                    queue.Enqueue((n, hops + 1));
                }
            }
            return -1;
        }

        /// All anchor indices reachable forward from `fromAnchor` (not
        /// including itself). Useful for HUD progress estimates.
        public static HashSet<int> ReachableFrom(LevelData level, int fromAnchor)
        {
            var set = new HashSet<int>();
            if (level == null || level.points.Count == 0) return set;
            level.EnsureLinearChainNext();
            var stack = new Stack<int>();
            stack.Push(fromAnchor);
            int cap = level.points.Count + 1;
            while (stack.Count > 0 && cap-- > 0)
            {
                int cur = stack.Pop();
                var nx = level.points[cur].next;
                if (nx == null) continue;
                for (int k = 0; k < nx.Count; k++)
                {
                    int n = nx[k];
                    if (n < 0 || n >= level.points.Count) continue;
                    if (set.Add(n)) stack.Push(n);
                }
            }
            return set;
        }

        /// World-space position of an anchor under the given raycaster (or
        /// nominal sphere if null) — convenient for placing per-anchor scene
        /// objects like checkpoint triggers.
        public static Vector3 AnchorWorldPosition(LevelData level, int anchorIndex, GlobeRaycaster raycaster)
        {
            if (level == null || anchorIndex < 0 || anchorIndex >= level.points.Count) return Vector3.zero;
            return level.ResolvePosition(anchorIndex, raycaster);
        }
    }
}
