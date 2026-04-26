using UnityEngine;
using Dio.Level;

namespace Dio.Player
{
    /// Owner-only watchdog: if the car spends more than `graceSeconds` without
    /// a track-tagged collider directly beneath it (the main ribbon or the
    /// spawn-pad ribbon — both carry `SurfaceComponent`; the planet itself
    /// does not), teleport the car back to its most recently crossed
    /// checkpoint at zero velocity, centered on the track midline at t=0 of
    /// the next outgoing edge from that anchor, and oriented along that
    /// edge's tangent.
    ///
    /// Sister to `OffRoadRespawn` which handles the harder failure mode of
    /// "fell into the planet through a seam" or "launched into space" — that
    /// component snaps to the last *driving pose*. This one fires the
    /// moment the car parachutes off a low rim onto the surrounding
    /// scenery (which is the design choice now that guard rails are short
    /// enough to drive over), and resets to the last crossed *checkpoint*.
    ///
    /// Detection: a downward `Physics.SphereCast` toward the planet center
    /// from just above the chassis pivot. Replaces the earlier
    /// `WheelCollider.isGrounded + SurfaceComponent` check, which would
    /// false-fire on heavier Kenney profiles whose `WheelColliderY` placed
    /// the cast endpoint right at the ground line — a single frame of
    /// suspension wobble was enough to flip `isGrounded` off and tick the
    /// off-track timer toward the teleport.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class OffTrackRespawn : MonoBehaviour
    {
        [Tooltip("Seconds the car can spend off any track surface before the respawn snap fires.")]
        public float graceSeconds = 2.0f;
        [Tooltip("Cooldown after a respawn before the timer can start counting again. Stops a stuck car from re-firing every fixed tick.")]
        public float respawnCooldown = 1.0f;
        [Tooltip("Lift the respawned car this many world units above the surface so suspension settles cleanly.")]
        public float respawnLift = 0.6f;
        [Tooltip("Sphere radius for the downward track probe. Should comfortably cover the chassis half-width.")]
        public float probeRadius = 0.6f;
        [Tooltip("Maximum probe distance toward the planet center. Anything farther and we're considered airborne / off-track.")]
        public float probeDistance = 2.0f;
        [Tooltip("Probe origin is shifted this far AWAY from the planet (i.e. above the chassis) so the sphere doesn't start already overlapping a thin track ribbon under the car.")]
        public float probeStartLift = 0.4f;

        Rigidbody _rb;
        DioCar _car;
        ArcadeCarController _ctrl;
        float _lastOnTrackTime;
        float _nextRespawnAllowedTime;
        // Reused scratch buffer for the SphereCast — non-alloc, capped at 16
        // contacts per query (way more than we'll ever see beneath a car).
        static readonly RaycastHit[] _probeBuffer = new RaycastHit[16];

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _car = GetComponent<DioCar>();
            _ctrl = GetComponent<ArcadeCarController>();
            _lastOnTrackTime = Time.time;
        }

        void FixedUpdate()
        {
            // Owner-only: client-authority physics means only the owner has
            // a non-kinematic rb to teleport, and only the owner's writes
            // propagate to other peers via NetworkTransform.
            if (_car != null && !_car.isOwned) return;
            if (_rb == null || _rb.isKinematic) return;
            // Inputs are locked during the countdown intro and after the win
            // banner — don't punish a player who's standing still on the
            // start line.
            if (_ctrl != null && _ctrl.inputsLocked)
            {
                _lastOnTrackTime = Time.time;
                return;
            }

            if (IsOnTrackSurface())
            {
                _lastOnTrackTime = Time.time;
                return;
            }

            if (Time.time - _lastOnTrackTime < graceSeconds) return;
            if (Time.time < _nextRespawnAllowedTime) return;

            TeleportToLastCheckpoint();
            _lastOnTrackTime = Time.time;
            _nextRespawnAllowedTime = Time.time + respawnCooldown;
        }

        // Cast a small sphere from just above the chassis straight toward the
        // planet center. If anything we hit (and that isn't part of this car)
        // resolves to a `SurfaceComponent` in its parent chain, we're on a
        // track piece. Otherwise we're airborne or on the planet's plain
        // surface — i.e. off the ribbon.
        bool IsOnTrackSurface()
        {
            var planet = RaceBootstrap.CurrentPlanet;
            if (planet == null)
            {
                // No planet yet (very early in race start) — give the player
                // the benefit of the doubt and don't tick the timer.
                return true;
            }

            Vector3 toCenter = (planet.transform.position - transform.position);
            float dist = toCenter.magnitude;
            if (dist < 1e-3f) return true;
            toCenter /= dist;
            Vector3 origin = transform.position - toCenter * probeStartLift;

            int hitCount = Physics.SphereCastNonAlloc(
                origin, probeRadius, toCenter, _probeBuffer, probeDistance,
                ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                var col = _probeBuffer[i].collider;
                if (col == null) continue;
                if (col.transform.IsChildOf(transform)) continue; // ignore self
                if (col.GetComponentInParent<SurfaceComponent>() != null) return true;
            }
            return false;
        }

        void TeleportToLastCheckpoint()
        {
            var lvl = RaceBootstrap.CurrentLevel;
            if (lvl == null || !lvl.HasMinimum) return;
            var planet = RaceBootstrap.CurrentPlanet;
            if (planet == null) return;
            var raycaster = RaceBootstrap.CurrentRaycaster;

            // Pick the anchor we last crossed (server publishes the SyncList
            // and the owner sees its own writes immediately). Default to anchor
            // 0 if we haven't been credited with a checkpoint yet — which
            // matches the user's "centered at t=0 of the next edge from your
            // last checkpoint" rule for the start-line case.
            int last = 0;
            lvl.EnsureLinearChainNext();
            if (_car != null && _car.crossedCheckpoints != null && _car.crossedCheckpoints.Count > 0)
                last = _car.crossedCheckpoints[_car.crossedCheckpoints.Count - 1];
            // Edge case: standing on the finish line (last anchor — has no
            // outgoing edges). Use the previous anchor's edge tangent.
            int finishIdx = lvl.points.Count - 1;
            int edgeFrom = last;
            int edgeTo;
            if (last >= finishIdx || lvl.points[last].next == null || lvl.points[last].next.Count == 0)
            {
                // No outgoing edge — back up one. Won't happen often (you'd
                // have to lose track contact AT the finish line) but staying
                // safe avoids an IndexOutOfRange.
                edgeFrom = Mathf.Max(0, last - 1);
                if (lvl.points[edgeFrom].next != null && lvl.points[edgeFrom].next.Count > 0)
                    edgeTo = lvl.points[edgeFrom].next[0];
                else
                    edgeTo = Mathf.Min(finishIdx, edgeFrom + 1);
            }
            else
            {
                edgeTo = lvl.points[edgeFrom].next[0];
            }

            lvl.GetEdge(edgeFrom, edgeTo, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);

            // t=0: position is the source anchor itself, on the track midline.
            Vector3 dir = a; // already a unit direction
            float surfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(dir) : lvl.NominalRadius;
            float yOffset = lvl.points[edgeFrom].yOffset;
            Vector3 planetCenter = planet.transform.position;
            Vector3 pos = planetCenter + dir * (surfaceR + yOffset + respawnLift);

            // Tangent = derivative of the spherical bezier at t=0.
            const float Eps = 0.001f;
            Vector3 p0 = GeodesicUtil.SphericalBezier(a, ah, bh, b, 0f);
            Vector3 p1 = GeodesicUtil.SphericalBezier(a, ah, bh, b, Eps);
            Vector3 tangent = (p1 - p0).normalized;
            if (tangent.sqrMagnitude < 1e-6f) tangent = GeodesicUtil.TangentAt(a, b);

            Quaternion rot = Quaternion.LookRotation(tangent, dir);

            // Zero velocity — user wants the snap to feel like a full reset,
            // not a hot rejoin.
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.position = pos;
            _rb.rotation = rot;
            transform.SetPositionAndRotation(pos, rot);
        }
    }
}
