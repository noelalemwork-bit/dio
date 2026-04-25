using UnityEngine;
using Dio.Powerups;
using Dio.Powerups.States;

namespace Dio.Level.Obstacles
{
    /// Banana-style hazard: trigger sphere on the ground, on overlap apply a
    /// state to the car and despawn. Not networked physics — just the trigger.
    public class BananaObstacle : Obstacle
    {
        public float slipDuration = 1.4f;

        void OnTriggerEnter(Collider other)
        {
            if (!isServer) return;
            var car = CarOf(other);
            if (car == null || IsOwnerImmune(car)) return;

            ApplyState(car, new SlipperyState { Mode = SlipperyState.SlipMode.RandomSteer }, slipDuration);
            ServerDespawn();
        }
    }

    /// Oil slick: an area hazard. Continuously refreshes the slippery state on
    /// any car inside it; never despawns by overlap (only by lifetime).
    public class OilSlickObstacle : Obstacle
    {
        public float refreshDuration = 0.4f;

        void OnTriggerStay(Collider other)
        {
            if (!isServer) return;
            var car = CarOf(other);
            if (car == null || IsOwnerImmune(car)) return;

            var sm = car.GetComponent<CarStateMachine>();
            if (sm == null) return;

            // Refresh: clear any existing oil-slick slip, reapply.
            sm.Clear(PowerupKind.OilSlick);
            ApplyState(car, new SlipperyState { Mode = SlipperyState.SlipMode.ZeroSteer }, refreshDuration);
        }
    }
}
