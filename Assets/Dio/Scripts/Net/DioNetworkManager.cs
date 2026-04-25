using System.Collections.Generic;
using Mirror;
using UnityEngine;
using Dio.Player;

namespace Dio.Net
{
    [AddComponentMenu("Dio/Net/Dio Network Manager")]
    public class DioNetworkManager : NetworkManager
    {
        [Header("Dio")]
        [Tooltip("Display name advertised to discovery clients.")]
        public string HostName = "Host";

        [Tooltip("Minimum players required to start a race. Set to 1 for solo development.")]
        public int minPlayers = 1;

        [Tooltip("Allow the host to start a race even with 0 connected clients (development only).")]
        public bool soloBypass = true;

        [Header("Race")]
        [Tooltip("Networked car prefab. Spawned per-connection on race start.")]
        public GameObject carPrefab;

        [Tooltip("Default level to load when the race starts. RaceBootstrap reads this on clients too.")]
        public Dio.Level.LevelData defaultLevel;

        public DioNetworkDiscovery discovery;

        // Server-side roster. Indexed by connection id.
        readonly Dictionary<int, DioPlayer> _players = new Dictionary<int, DioPlayer>();
        public int ConnectedPlayerCount => _players.Count;
        public IReadOnlyDictionary<int, DioPlayer> Players => _players;

        public static DioNetworkManager Instance => singleton as DioNetworkManager;

        // Bool argument: true if this peer is the server (host or dedicated).
        public static System.Action<bool, RaceStartMessage> OnRaceStarted;

        public override void Awake()
        {
            base.Awake();
            if (discovery == null) discovery = GetComponent<DioNetworkDiscovery>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            RegisterAllNetworkedPrefabs();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            NetworkClient.ReplaceHandler<RaceStartMessage>(OnClientRaceStart);
            RegisterAllNetworkedPrefabs();
        }

        // base.OnStartClient already registers playerPrefab + spawnPrefabs, but
        // we add carPrefab and the powerup prefabs explicitly too, in case they
        // weren't wired into spawnPrefabs (older scenes, manual setup, hot-
        // reload edge cases). NetworkClient.RegisterPrefab is idempotent — if
        // the prefab's already there it just no-ops.
        void RegisterAllNetworkedPrefabs()
        {
            if (carPrefab != null && !NetworkClient.prefabs.ContainsValue(carPrefab))
                NetworkClient.RegisterPrefab(carPrefab);

            var bootstrap = FindAnyObjectByType<Dio.Powerups.PowerupBootstrap>();
            if (bootstrap != null)
            {
                foreach (var go in bootstrap.AllNetworkedPrefabs)
                {
                    if (go != null && !NetworkClient.prefabs.ContainsValue(go))
                        NetworkClient.RegisterPrefab(go);
                }
            }
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            base.OnServerAddPlayer(conn);
            if (conn.identity != null)
            {
                var p = conn.identity.GetComponent<DioPlayer>();
                if (p != null) _players[conn.connectionId] = p;
            }
            DioPlayer.OnRosterChanged?.Invoke();
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _players.Remove(conn.connectionId);
            base.OnServerDisconnect(conn);
            DioPlayer.OnRosterChanged?.Invoke();
        }

        // ---------- Race start ----------

        public bool CanStartRace()
        {
            if (!NetworkServer.active) return false;
            if (soloBypass) return true;
            return _players.Count >= minPlayers;
        }

        [Server]
        public void ServerStartRace()
        {
            if (!CanStartRace())
            {
                Debug.Log($"[Dio] ServerStartRace denied. players={_players.Count} min={minPlayers} soloBypass={soloBypass}");
                return;
            }

            var msg = new RaceStartMessage
            {
                levelGuid = string.Empty,
                seed = Random.Range(int.MinValue, int.MaxValue),
                startServerTime = NetworkTime.time + 1.0,
            };

            // Build the server-side planet FIRST so newly-spawned cars can find it
            // in their Awake. Otherwise SphericalGravity has nothing to fall back to.
            var bootstrap = FindAnyObjectByType<Dio.Level.RaceBootstrap>();
            if (bootstrap != null && defaultLevel != null)
                bootstrap.BuildVisualScene(defaultLevel);

            // Server-side spawning of cars. Client-side bootstrap (planet, camera)
            // is triggered via OnRaceStarted, which fires from the message handler.
            ServerSpawnCarsForAllPlayers();
            ServerSpawnMidTrackPowerups();

            // Tell every client (host's local client included).
            NetworkServer.SendToAll(msg);

            // Headless dedicated server: the message handler won't fire on the
            // server-only path, so notify ourselves once. In host mode the
            // local NetworkClient receives the message and fires the handler;
            // we don't manually call OnRaceStarted to avoid double-firing.
            if (!NetworkClient.active)
                OnRaceStarted?.Invoke(true, msg);
        }

        [Server]
        void ServerSpawnCarsForAllPlayers()
        {
            if (carPrefab == null) { Debug.LogError("[Dio] carPrefab is null on DioNetworkManager."); return; }
            if (defaultLevel == null || !defaultLevel.HasMinimum)
            {
                Debug.LogError("[Dio] defaultLevel missing or has fewer than 2 points.");
                return;
            }

            int i = 0;
            foreach (var kv in _players)
            {
                var conn = NetworkServer.connections.TryGetValue(kv.Key, out var c) ? c : null;
                var player = kv.Value;
                Vector3 pos; Quaternion rot;
                ComputeStartTransform(defaultLevel, i, out pos, out rot);

                var carGo = Instantiate(carPrefab, pos, rot);
                NetworkServer.Spawn(carGo, conn);

                var car = carGo.GetComponent<DioCar>();
                if (car != null && player != null) car.ServerInitialize(player);
                i++;
            }

            // Solo-bypass: spawn the host a car even if no DioPlayer exists yet (e.g.
            // testing without the lobby).
            if (_players.Count == 0 && soloBypass)
            {
                ComputeStartTransform(defaultLevel, 0, out var pos, out var rot);
                var carGo = Instantiate(carPrefab, pos, rot);
                NetworkServer.Spawn(carGo);
            }
        }

        // Drops 5 mystery boxes in a row across the track at the geodesic
        // midpoint. Each box is locked to a unique PowerupKind via SyncVar so
        // every client sees the same payload distribution.
        [Server]
        void ServerSpawnMidTrackPowerups()
        {
            if (defaultLevel == null || !defaultLevel.HasMinimum) return;
            var pu = FindAnyObjectByType<Dio.Powerups.PowerupBootstrap>();
            if (pu == null || pu.powerupBoxPrefab == null)
            {
                Debug.LogWarning("[Dio] No PowerupBootstrap or powerupBoxPrefab — skipping mid-track row.");
                return;
            }

            // Pool of distinct kinds the mid-track row can dispense.
            var pool = new System.Collections.Generic.List<Dio.Powerups.PowerupKind>
            {
                Dio.Powerups.PowerupKind.Boost,
                Dio.Powerups.PowerupKind.Star,
                Dio.Powerups.PowerupKind.Lightning,
                Dio.Powerups.PowerupKind.Banana,
                Dio.Powerups.PowerupKind.OilSlick,
                Dio.Powerups.PowerupKind.GreenShell,
                Dio.Powerups.PowerupKind.BlueShell,
                Dio.Powerups.PowerupKind.Bobomb,
                Dio.Powerups.PowerupKind.Tornado,
            };

            const int rowSize = 5;
            var picked = new System.Collections.Generic.List<Dio.Powerups.PowerupKind>(rowSize);
            for (int i = 0; i < rowSize && pool.Count > 0; i++)
            {
                int idx = Random.Range(0, pool.Count);
                picked.Add(pool[idx]);
                pool.RemoveAt(idx);
            }

            // Build the local Frenet frame at the geodesic midpoint and lay
            // the row out evenly across the track width.
            Vector3 mid = Dio.Level.TrackBuilder.SampleAt(defaultLevel, 0.5f);
            Vector3 midDir = mid.normalized;
            Vector3 tangent = Dio.Level.TrackBuilder.TangentAt(defaultLevel, 0.5f);
            Vector3 right = Vector3.Cross(tangent, midDir).normalized;

            float trackW = defaultLevel.trackWidth;
            float spacing = trackW / (picked.Count + 1f);

            for (int i = 0; i < picked.Count; i++)
            {
                float lateral = -trackW * 0.5f + spacing * (i + 1);
                Vector3 boxPos = mid + midDir * 1.0f + right * lateral;
                var go = Instantiate(pu.powerupBoxPrefab, boxPos,
                    Quaternion.LookRotation(tangent, midDir));
                var box = go.GetComponent<Dio.Powerups.PowerupBox>();
                if (box != null) box.forceKind = picked[i];
                NetworkServer.Spawn(go);
            }

            Debug.Log($"[Dio] Mid-track row spawned: {string.Join(", ", picked)}");
        }

        static void ComputeStartTransform(Dio.Level.LevelData level, int slot, out Vector3 pos, out Quaternion rot)
        {
            Vector3 startDir = level.points[0].directionFromCenter.normalized;
            Vector3 nextDir  = level.points[1].directionFromCenter.normalized;

            // Stagger slots ~2 m to the side along the start tangent.
            Vector3 tangent = Dio.Level.GeodesicUtil.TangentAt(startDir, nextDir);
            Vector3 right = Vector3.Cross(startDir, tangent).normalized;
            float lateral = (slot % 2 == 0 ? -1f : 1f) * (2f * (slot / 2 + 1));

            pos = startDir * level.planetRadius + startDir * 1.5f + right * lateral;
            rot = Quaternion.LookRotation(tangent, startDir);
        }

        void OnClientRaceStart(RaceStartMessage msg)
        {
            // Fires on every client, including the host's local client.
            Debug.Log($"[Dio] Client received RaceStartMessage. isServer={NetworkServer.active} startTime={msg.startServerTime:0.000}");
            OnRaceStarted?.Invoke(NetworkServer.active, msg);
        }

        // ---------- Discovery convenience ----------

        public void HostAndAdvertise()
        {
            StartHost();
            if (discovery != null)
            {
                discovery.AdvertiseServer();
                Debug.Log("[Dio] Hosting + advertising on UDP 47777. If clients can't see this server, check Windows Firewall (Unity / your standalone needs UDP inbound on this port). Direct-IP connect is available in the Browser panel as a fallback.");
            }
        }

        public void StopAll()
        {
            if (NetworkServer.active && NetworkClient.isConnected) StopHost();
            else if (NetworkClient.isConnected) StopClient();
            else if (NetworkServer.active) StopServer();
            if (discovery != null) discovery.StopDiscovery();
        }
    }
}
