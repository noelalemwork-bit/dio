using UnityEngine;

namespace Dio.Level
{
    public static class GeodesicUtil
    {
        /// Returns positions along the great-circle arc between two unit directions,
        /// at the given radius. `segments` is the number of *segments*; the resulting
        /// array length is segments + 1 (inclusive endpoints).
        public static Vector3[] Geodesic(Vector3 a, Vector3 b, float radius, int segments, Vector3 center = default)
        {
            segments = Mathf.Max(1, segments);
            a = a.normalized; b = b.normalized;

            // If a and b are nearly antipodal, slerp is undefined; perturb b slightly.
            if (Vector3.Dot(a, b) < -0.9999f)
            {
                Vector3 perp = Vector3.Cross(a, Vector3.up);
                if (perp.sqrMagnitude < 1e-4f) perp = Vector3.Cross(a, Vector3.right);
                b = Quaternion.AngleAxis(0.1f, perp) * b;
            }

            var pts = new Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                pts[i] = center + Vector3.Slerp(a, b, t).normalized * radius;
            }
            return pts;
        }

        /// Approximate length of the arc.
        public static float ArcLength(Vector3 a, Vector3 b, float radius)
        {
            float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(a.normalized, b.normalized), -1f, 1f));
            return angle * radius;
        }

        /// Tangent direction along the geodesic at point a, heading toward b.
        public static Vector3 TangentAt(Vector3 a, Vector3 b)
        {
            a = a.normalized; b = b.normalized;
            return Vector3.ProjectOnPlane(b - a, a).normalized;
        }

        /// Spherical cubic Bezier via De Casteljau, where every linear
        /// interpolation is replaced by Slerp on the unit sphere. All four
        /// inputs are unit directions from the sphere center; the result is
        /// a unit direction. With p1 = p2 = midpoint(p0, p3) this collapses
        /// to a near-geodesic; with p1, p2 pulled away from the chord it
        /// curves on the sphere just like a planar cubic Bezier curves in
        /// the plane — so we can author tracks as Beziers and they stay
        /// exactly on the surface.
        public static Vector3 SphericalBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            Vector3 q0 = Vector3.Slerp(p0, p1, t);
            Vector3 q1 = Vector3.Slerp(p1, p2, t);
            Vector3 q2 = Vector3.Slerp(p2, p3, t);
            Vector3 r0 = Vector3.Slerp(q0, q1, t);
            Vector3 r1 = Vector3.Slerp(q1, q2, t);
            return Vector3.Slerp(r0, r1, t).normalized;
        }
    }
}
