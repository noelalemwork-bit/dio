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
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
        }
    }
}
