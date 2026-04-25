using Mirror;
using UnityEngine;
using Dio.Player;

namespace Dio.Common
{
    /// Pushes the local player's "planet up" vector onto the skybox material so
    /// the gradient + cloud horizon line track the player's surface normal
    /// instead of world-Y. Without this the sky looks tilted everywhere except
    /// the spawn point on a small spherical world.
    ///
    /// Operates on a runtime *copy* of the assigned skybox material so we never
    /// dirty the asset on disk.
    public class SkyboxPlayerUpBinder : MonoBehaviour
    {
        [Tooltip("Shader vector property (xyz used). Must match the skybox shader.")]
        public string upPropertyName = "_PlayerUp";

        [Tooltip("Slerp speed in 1/s. 0 = instant. ~6-10 feels natural for arcade-paced motion.")]
        public float smoothSpeed = 8f;

        Material _runtimeMat;
        Vector3 _currentUp = Vector3.up;
        bool _materialOwned;

        void OnDisable() { ReleaseRuntimeMaterial(); }
        void OnDestroy() { ReleaseRuntimeMaterial(); }

        void ReleaseRuntimeMaterial()
        {
            if (_runtimeMat != null && _materialOwned)
            {
                // Don't leave a dangling reference assigned to RenderSettings.
                if (RenderSettings.skybox == _runtimeMat) RenderSettings.skybox = null;
                Object.Destroy(_runtimeMat);
            }
            _runtimeMat = null;
            _materialOwned = false;
        }

        void EnsureRuntimeMaterial()
        {
            if (_runtimeMat != null) return;
            var shared = RenderSettings.skybox;
            if (shared == null) return;

            // If the shader doesn't declare _PlayerUp, no point cloning — saves a material allocation.
            if (!shared.HasProperty(upPropertyName)) return;

            _runtimeMat = new Material(shared) { name = shared.name + " (runtime up)" };
            _materialOwned = true;
            RenderSettings.skybox = _runtimeMat;
        }

        DioCar FindLocalCar()
        {
            foreach (var ni in NetworkClient.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<DioCar>() : null;
                if (car != null && car.isOwned) return car;
            }
            return null;
        }

        void Update()
        {
            EnsureRuntimeMaterial();
            if (_runtimeMat == null) return;

            Vector3 targetUp = Vector3.up;
            var car = FindLocalCar();
            if (car != null)
            {
                var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
                Vector3 origin = planet != null ? planet.transform.position : Vector3.zero;
                Vector3 d = car.transform.position - origin;
                if (d.sqrMagnitude > 0.001f) targetUp = d.normalized;
            }

            _currentUp = smoothSpeed > 0f
                ? Vector3.Slerp(_currentUp, targetUp, 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime))
                : targetUp;

            _runtimeMat.SetVector(upPropertyName, new Vector4(_currentUp.x, _currentUp.y, _currentUp.z, 0f));
        }
    }
}
