using UnityEngine;

namespace Dio.Common
{
    /// Publishes a `_DioTime` shader global from `Mirror.NetworkTime.time`. All
    /// time-driven Dio shaders (sky clouds, mystery box shimmer, etc.) sample
    /// this instead of `_Time.y` so every peer sees the same phase. Without it
    /// the shimmer "flickers" differently across machines because each Unity
    /// instance has its own `_Time.y`.
    public class NetworkTimeBinder : MonoBehaviour
    {
        static readonly int DioTimeId = Shader.PropertyToID("_DioTime");

        void Update()
        {
            // NetworkTime.time is a synchronized double — same value on every
            // connected peer (within snapshot interpolation accuracy). Cast to
            // float for the shader; the wrap point is at ~16M seconds (185 days)
            // which is fine for a session.
            float t = (float)Mirror.NetworkTime.time;
            Shader.SetGlobalFloat(DioTimeId, t);
        }
    }
}
