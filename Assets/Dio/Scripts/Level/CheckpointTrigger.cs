using UnityEngine;
using Dio.Player;

namespace Dio.Level
{
    /// Server-side trigger volume centered on a track anchor. When a car
    /// enters, the car records that anchor as crossed. The volume's radius
    /// scales with the level's effective track width so a wide track gets a
    /// wider catch — plus a small bonus so a player taking creative lines
    /// (jumps, drifts off-line) still passes through the checkpoint.
    ///
    /// Spawned by RaceBootstrap on the server only. Pure clients don't need
    /// the trigger; they read crossedCheckpoints off DioCar via SyncList.
    [RequireComponent(typeof(SphereCollider))]
    public class CheckpointTrigger : MonoBehaviour
    {
        public int anchorIndex;
        public bool isFinish;

        void Reset()
        {
            var sc = GetComponent<SphereCollider>();
            if (sc != null) sc.isTrigger = true;
        }

        void OnTriggerEnter(Collider other)
        {
            if (!Mirror.NetworkServer.active) return;
            var car = other.GetComponentInParent<DioCar>();
            if (car == null) return;
            car.ServerCheckpointHit(anchorIndex);
            if (isFinish)
            {
                var mgr = Dio.Net.DioNetworkManager.Instance;
                if (mgr != null) mgr.ServerNotifyCarFinished(car);
            }
        }
    }
}
