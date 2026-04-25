using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dio.Level
{
    [Serializable]
    public struct TrackPoint
    {
        [Tooltip("Unit vector from planet center. Position = direction * planetRadius.")]
        public Vector3 directionFromCenter;
        [Tooltip("Banking angle in degrees, used by the future track-mesh sweep.")]
        public float bank;

        // Spherical-cubic-Bezier control handles. Each is a unit direction
        // from the planet center (i.e., a point on the unit sphere). A
        // segment from points[i] to points[i+1] is sampled as De Casteljau
        // slerps between (a, points[i].outHandle, points[i+1].inHandle, b)
        // — see GeodesicUtil.SphericalBezier.
        //
        // Zero magnitude means "unset"; LevelData.GetSegment falls back to
        // the geodesic 1/3 / 2/3 defaults so old level assets still build.
        [Tooltip("Bezier handle on the segment ARRIVING at this point (used as p2). Zero = use geodesic default.")]
        public Vector3 inHandle;
        [Tooltip("Bezier handle on the segment LEAVING this point (used as p1). Zero = use geodesic default.")]
        public Vector3 outHandle;
    }

    [CreateAssetMenu(menuName = "Dio/Level Data", fileName = "NewLevel")]
    public class LevelData : ScriptableObject
    {
        public float planetRadius = 200f;
        public int seed = 12345;
        public float trackWidth = 15f;
        public List<TrackPoint> points = new List<TrackPoint>();

        public bool HasMinimum => points.Count >= 2;
        public TrackPoint Start => points.Count > 0 ? points[0] : default;
        public TrackPoint Finish => points.Count > 0 ? points[points.Count - 1] : default;

        public Vector3 PositionOf(int i) => DirOf(i) * planetRadius;

        public Vector3 DirOf(int i) =>
            (points[i].directionFromCenter.sqrMagnitude > 0f
                ? points[i].directionFromCenter.normalized
                : Vector3.up);

        /// Returns the four unit-vector control directions for the spherical
        /// cubic Bezier of segment `i` (between points[i] and points[i+1]).
        /// `ah` is the "outgoing" control on the start anchor; `bh` is the
        /// "incoming" control on the end anchor. Uninitialized handles fall
        /// back to a near-geodesic default (1/3, 2/3 of the great-circle).
        public void GetSegment(int i, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b)
        {
            a = DirOf(i);
            b = DirOf(i + 1);
            Vector3 outH = points[i].outHandle;
            Vector3 inH  = points[i + 1].inHandle;
            ah = outH.sqrMagnitude > 1e-6f ? outH.normalized : Vector3.Slerp(a, b, 1f / 3f).normalized;
            bh = inH.sqrMagnitude  > 1e-6f ? inH.normalized  : Vector3.Slerp(a, b, 2f / 3f).normalized;
        }
    }
}
