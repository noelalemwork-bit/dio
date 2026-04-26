using UnityEngine;
using Dio.Level;

namespace Dio.Player
{
    /// Owner-only safety net: if the car drops below the planet surface or
    /// climbs absurdly far above it, snap it back to the most recent pose
    /// that was clearly on the track. The component records a sliding "last
    /// known good" pose every fixed tick the car looks safe; when the car
    /// goes off-road, it teleports back, kills velocity, and re-aligns
    /// upright on the surface so the player can drive again immediately.
    ///
    /// Why owner-only: client-authority physics means the OWNER is the
    /// only peer simulating their car. Doing the respawn locally writes
    /// into the rigidbody we already control; NetworkTransform broadcasts
    /// the snap to all other peers automatically. Running on remote peers
    /// would either no-op (kinematic rb can still be moved, but they don't
    /// have authority) or fight the network state.
    ///
    /// "On-track" is approximated by altitude: the car is on-track while
    /// its altitude (distance-from-planet-center − planet-surface-radius)
    /// stays in [-onTrackUpperBand, onTrackUpperBand]. That captures every
    /// realistic driving pose including small jumps + bumps. Off-road
    /// triggers fire when altitude falls below −fallThreshold (fell into
    /// the planet through a seam) OR exceeds escapeAltitude (launched off
    /// into space).
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class OffRoadRespawn : MonoBehaviour
    {
        [Tooltip("Below this altitude the car is treated as having fallen off the world.")]
        public float fallThreshold = 6f;
        [Tooltip("Above this altitude the car is treated as having launched into space.")]
        public float escapeAltitude = 80f;
        [Tooltip("Window around the surface inside which the car is considered safely on the track.")]
        public float onTrackUpperBand = 4f;
        [Tooltip("Cooldown after a respawn before another one can fire — prevents rapid re-fire while resettling.")]
        public float respawnCooldown = 1.0f;
        [Tooltip("After respawn, the car is parked this many world units above the surface so suspension settles cleanly.")]
        public float respawnLift = 0.6f;

        Rigidbody _rb;
        DioCar _car;
        Vector3 _lastSafePos;
        Quaternion _lastSafeRot;
        bool _hasSafePose;
        float _nextRespawnTime;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _car = GetComponent<DioCar>();
        }

        void FixedUpdate()
        {
            // Only the owner runs respawn logic — see class doc.
            if (_car != null && !_car.isOwned) return;
            if (_rb == null || _rb.isKinematic) return;

            var planet = RaceBootstrap.CurrentPlanet;
            if (planet == null) return;

            Vector3 center = planet.transform.position;
            Vector3 toCar = transform.position - center;
            float dist = toCar.magnitude;
            if (dist < 1e-3f) return;

            // Resolve the surface radius right under the car. The raycaster
            // takes the car's direction and walks the globe mesh; if it's
            // not available (early in race start), fall back to the level's
            // nominal radius so we can still bookkeep "fell into the planet".
            Vector3 dir = toCar / dist;
            var raycaster = RaceBootstrap.CurrentRaycaster;
            var lvl = RaceBootstrap.CurrentLevel;
            float surfaceR = raycaster != null
                ? raycaster.ResolveSurfaceRadius(dir)
                : (lvl != null ? lvl.NominalRadius : dist);

            float altitude = dist - surfaceR;

            // Healthy: book the pose so a future off-road event can fall
            // back to here. We only book when the car has at least *some*
            // forward velocity, otherwise the "safe pose" pinned at the
            // grid line would respawn the player onto the start line every
            // time. After the first metre of motion the pose updates
            // continuously.
            bool moving = _rb.linearVelocity.sqrMagnitude > 0.5f;
            if (altitude > -onTrackUpperBand && altitude < onTrackUpperBand && moving)
            {
                _lastSafePos = transform.position;
                _lastSafeRot = transform.rotation;
                _hasSafePose = true;
                return;
            }

            bool fellOff = altitude < -fallThreshold || altitude > escapeAltitude;
            if (!fellOff) return;
            if (Time.time < _nextRespawnTime) return;

            // Off-road: snap back. If we have no recorded safe pose yet
            // (player drove off the line in the first 0.5 s, before the
            // "moving" check booked anything), fall back to the start line
            // by clamping the car onto the surface RIGHT BELOW its current
            // direction. The launch state on next-tick FixedUpdate will
            // give them forward momentum again.
            Vector3 respawnPos;
            Quaternion respawnRot;
            if (_hasSafePose)
            {
                respawnPos = _lastSafePos;
                respawnRot = _lastSafeRot;
            }
            else
            {
                // No safe pose: drop the car back onto the surface in the
                // same direction it currently faces (best-effort save).
                respawnPos = center + dir * (surfaceR + respawnLift);
                Vector3 surfaceUp = dir;
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, surfaceUp).normalized;
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.Cross(surfaceUp, Vector3.right).normalized;
                respawnRot = Quaternion.LookRotation(fwd, surfaceUp);
            }

            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.position = respawnPos;
            _rb.rotation = respawnRot;
            transform.SetPositionAndRotation(respawnPos, respawnRot);
            _nextRespawnTime = Time.time + respawnCooldown;
        }
    }
}
