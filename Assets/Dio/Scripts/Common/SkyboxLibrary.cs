using UnityEngine;

namespace Dio.Common
{
    /// Runtime lookup for the pool of skybox materials the server can pick from
    /// at race start. Materials are authored under `Resources/Skies/Sky_*.mat`
    /// by `MainSceneBuilder.BuildSkyboxLibrary`; this class loads them on
    /// first use and caches by index.
    public static class SkyboxLibrary
    {
        public const int Count = 6;
        const string PathPrefix = "Skies/Sky_";

        static readonly Material[] _cache = new Material[Count];

        public static Material Get(int index)
        {
            if (index < 0 || index >= Count) return null;
            if (_cache[index] != null) return _cache[index];
            var mat = Resources.Load<Material>(PathPrefix + index);
            if (mat == null)
                Debug.LogWarning($"[Dio] SkyboxLibrary: missing Resources/{PathPrefix}{index}.mat — falling back to current skybox.");
            _cache[index] = mat;
            return mat;
        }

        /// Return a deterministic fallback (sunset) when the requested index
        /// is out of bounds or the asset failed to load.
        public static Material GetOrFallback(int index)
        {
            return Get(index) ?? Get(0);
        }
    }
}
