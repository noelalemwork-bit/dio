using UnityEngine;

namespace Dio.Level
{
    /// Abstraction over "the surface the car drives on". For M1 we use a sphere
    /// planet; M2 swaps in the procedural-sweep track mesh as a TrackSurface.
    /// Anything that needs surface-relative geometry (camera up, track-tangent
    /// projection, kinematic obstacle paths) goes through this.
    public interface ITrackSurface
    {
        Vector3 Center { get; }

        /// Surface-up vector at a given world position.
        Vector3 SurfaceUpAt(Vector3 worldPos);

        /// Snap a world position to the surface (the closest surface point).
        Vector3 SnapToSurface(Vector3 worldPos);

        /// Forward-direction along the active track at this position (the
        /// tangent of the geodesic / spline). Returns Vector3.zero if undefined.
        Vector3 TrackForwardAt(Vector3 worldPos);
    }

    /// Sphere-planet implementation: every direction from the center is "on track".
    /// Track-forward is undefined; consumers fall back to the geodesic from level data.
    public class SphereTrackSurface : MonoBehaviour, ITrackSurface
    {
        public Transform planet;
        public float radius = 200f;

        Vector3 Origin => planet != null ? planet.position : Vector3.zero;

        public Vector3 Center => Origin;
        public Vector3 SurfaceUpAt(Vector3 worldPos) => (worldPos - Origin).normalized;
        public Vector3 SnapToSurface(Vector3 worldPos) => Origin + (worldPos - Origin).normalized * radius;
        public Vector3 TrackForwardAt(Vector3 worldPos) => Vector3.zero;
    }
}
