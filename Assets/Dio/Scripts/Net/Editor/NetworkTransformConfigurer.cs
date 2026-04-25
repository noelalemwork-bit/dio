using System.Reflection;
using UnityEngine;

namespace Dio.Net.EditorTools
{
    /// Adds + configures a NetworkTransformReliable on a prefab root with
    /// settings tuned for low-jitter, low-latency racing visuals on every peer.
    ///
    /// Reflection-based so the editor builders compile even when Mirror's
    /// component assembly fails to load (rare hot-reload edge case).
    ///
    /// Why these defaults:
    ///   * coordinateSpace = World — robust against the car being reparented
    ///     to the planet, a moving platform, etc. (Local mode reads the local
    ///     transform; if a parent moves, position deltas double-apply.)
    ///   * onlySyncOnChange = false — fast-moving objects are *always*
    ///     changing. Skipping snapshots saves bandwidth on stationary objects
    ///     but introduces a one-frame stutter every time motion restarts after
    ///     a quantized "no change" gap.
    ///   * compressRotation = true — quaternion smallest-three is lossy at the
    ///     ~0.5° level, which is invisible in motion and saves ~75% of
    ///     rotation bandwidth.
    ///   * sendIntervalMultiplier = 1 — every NetworkServer.sendInterval tick
    ///     (which we pin to 60Hz in DioNetworkManager.Awake). Combined with
    ///     `interpolatePosition + interpolateRotation`, this keeps remote
    ///     visuals locked to the snapshot timeline 1–2 frames behind the
    ///     server — the smoothness sweet spot called out in Mirror's docs.
    public static class NetworkTransformConfigurer
    {
        public static Component Configure(GameObject go, bool fastCar)
        {
            var t = System.Type.GetType("Mirror.NetworkTransformReliable, Mirror.Components")
                 ?? System.Type.GetType("Mirror.NetworkTransformUnreliable, Mirror.Components")
                 ?? System.Type.GetType("Mirror.NetworkTransform, Mirror.Components");

            if (t == null)
            {
                Debug.LogWarning("[Dio] No NetworkTransform variant found — install Mirror.Components.");
                return null;
            }

            var nt = go.GetComponent(t);
            if (nt == null) nt = go.AddComponent(t);

            // ALWAYS set target to the GameObject's transform. Mirror's
            // OnValidate sets this in the editor, but reflection-built prefabs
            // can serialize with a null target — which silently breaks every
            // peer's ability to write/read the transform.
            SetField(nt, "target", go.transform);

            // Set common NetworkTransformBase fields.
            SetField(nt, "syncPosition", true);
            SetField(nt, "syncRotation", true);
            SetField(nt, "syncScale", false);
            SetField(nt, "interpolatePosition", true);
            SetField(nt, "interpolateRotation", true);
            SetField(nt, "interpolateScale", false);
            SetField(nt, "compressRotation", true);
            SetField(nt, "timelineOffset", true);

            // CoordinateSpace: enum on Mirror.NetworkTransformBase. Look it up
            // via the Mirror assembly. Try multiple assembly identities since
            // vendored Mirror builds vary on the assembly attribute.
            var coordType = System.Type.GetType("Mirror.CoordinateSpace, Mirror")
                         ?? System.Type.GetType("Mirror.CoordinateSpace, Mirror.Components")
                         ?? FindTypeInLoadedAssemblies("Mirror.CoordinateSpace");
            if (coordType != null)
            {
                var worldVal = System.Enum.Parse(coordType, "World");
                SetField(nt, "coordinateSpace", worldVal);
            }

            // Reliable variants always stream — disable change-suppression so
            // restart-stutter is impossible.
            SetField(nt, "onlySyncOnChange", false);

            if (fastCar)
            {
                // 0.005 m = 5 mm. Below the eye can resolve at race speed but
                // tight enough to catch micro-corrections after collisions.
                SetField(nt, "positionPrecision", 0.005f);

                // CLIENT AUTHORITY for the player car — LAN-friendly: the
                // local owner is the only physics simulator for their own car,
                // pushes its position to the server, server relays to peers.
                // Zero input lag, zero round-trip. Mirror docs:
                // https://mirror-networking.gitbook.io/docs/manual/general/syncdirection
                var sdType = System.Type.GetType("Mirror.SyncDirection, Mirror")
                          ?? FindTypeInLoadedAssemblies("Mirror.SyncDirection");
                if (sdType != null)
                {
                    var c2s = System.Enum.Parse(sdType, "ClientToServer");
                    SetField(nt, "syncDirection", c2s);
                }
            }
            else
            {
                // 1 cm — fine for projectiles + obstacles. These stay
                // server-authoritative (default ServerToClient).
                SetField(nt, "positionPrecision", 0.01f);
            }

            return nt;
        }

        static System.Type FindTypeInLoadedAssemblies(string fullName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        static void SetField(Component c, string name, object value)
        {
            if (c == null) return;
            var f = c.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) { f.SetValue(c, value); return; }
            var p = c.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(c, value); return; }
        }
    }
}
