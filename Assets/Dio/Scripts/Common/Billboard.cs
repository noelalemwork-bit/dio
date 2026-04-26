using UnityEngine;

namespace Dio.Common
{
    /// Tiny billboard so 3D labels above placeholder assets always face the camera.
    public class Billboard : MonoBehaviour
    {
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // Pass the camera's up axis as the LookRotation up so the billboard
            // stays vertically aligned to the viewport (not the world). Without
            // the second argument LookRotation defaults to Vector3.up, which
            // tilts the billboard whenever the camera rolls — and on a planet
            // surface the camera rolls constantly.
            Vector3 fwd = transform.position - cam.transform.position;
            if (fwd.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.LookRotation(fwd, cam.transform.up);
        }
    }
}
