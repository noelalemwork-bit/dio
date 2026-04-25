using UnityEngine;

namespace Dio.Common
{
    /// Continuously rotates a transform — used by mystery powerup boxes etc.
    /// Local-only, every peer animates independently. No network sync needed.
    public class SpinningProp : MonoBehaviour
    {
        public Vector3 degreesPerSecond = new Vector3(0, 60, 0);
        public Space space = Space.Self;
        void Update() => transform.Rotate(degreesPerSecond * Time.deltaTime, space);
    }
}
