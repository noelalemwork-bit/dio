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
    }
}
