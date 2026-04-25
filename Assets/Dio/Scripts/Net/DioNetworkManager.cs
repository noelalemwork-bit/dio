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
        public static System.Action<RaceWonMessage> OnRaceWon;

        bool _raceActive;
        const float WinDistanceThreshold = 8f;     // unit distance for "crossed the finish"
        const float DefaultCleanupDelaySec = 5.5f; // duration of the win screen before cleanup

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
            NetworkClient.ReplaceHandler<RaceWonMessage>(OnClientRaceWon);
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

            // Reset all server-side per-race state on every player object.
            foreach (var p in _players.Values)
            {
                if (p == null) continue;
                var holder = p.GetComponent<Dio.Powerups.PowerupHolder>();
                if (holder != null) holder.ClearConsumedGroups();
            }

            // Server-side spawning of cars. Client-side bootstrap (planet, camera)
            // is triggered via OnRaceStarted, which fires from the message handler.
            ServerSpawnCarsForAllPlayers();
            ServerSpawnMidTrackPowerups();
            _raceActive = true;

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

            int total = _players.Count;
            int i = 0;
            foreach (var kv in _players)
            {
                var conn = NetworkServer.connections.TryGetValue(kv.Key, out var c) ? c : null;
                var player = kv.Value;
                Vector3 pos; Quaternion rot;
                ComputeStartTransform(defaultLevel, i, total, out pos, out rot);

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
                ComputeStartTransform(defaultLevel, 0, 1, out var pos, out var rot);
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

            // All boxes in this row share the same group id, so a player can
            // only grab one of them per pass (PowerupHolder tracks consumed groups).
            const int midRowGroupId = 1;

            for (int i = 0; i < picked.Count; i++)
            {
                float lateral = -trackW * 0.5f + spacing * (i + 1);
                Vector3 boxPos = mid + midDir * 1.0f + right * lateral;
                var go = Instantiate(pu.powerupBoxPrefab, boxPos,
                    Quaternion.LookRotation(tangent, midDir));
                var box = go.GetComponent<Dio.Powerups.PowerupBox>();
                if (box != null)
                {
                    box.forceKind = picked[i];
                    box.groupId = midRowGroupId;
                }
                NetworkServer.Spawn(go);
            }

            Debug.Log($"[Dio] Mid-track row spawned: {string.Join(", ", picked)}");
        }

        // Up to 3 cars per row, additional rows fold backward along the tangent.
        // Within each row the cars are centered around the track midline so a
        // partial last row (e.g. 2 cars in a 5-player race) still looks symmetric.
        // Chassis pivot lands exactly on the surface — wheels touch ground
        // immediately because the prefab's WheelCollider Y is wheelRadius +
        // suspensionDistance × suspensionTargetPos.
        static void ComputeStartTransform(Dio.Level.LevelData level, int slot, int totalPlayers,
                                          out Vector3 pos, out Quaternion rot)
        {
            const int perRow = 3;
            const float forwardSpacing = 4.5f;

            Vector3 startDir = level.points[0].directionFromCenter.normalized;
            Vector3 nextDir  = level.points[1].directionFromCenter.normalized;
            Vector3 tangent  = Dio.Level.GeodesicUtil.TangentAt(startDir, nextDir);
            Vector3 right    = Vector3.Cross(startDir, tangent).normalized;

            int row = slot / perRow;
            int colInRow = slot - row * perRow;
            int totalRows = (totalPlayers + perRow - 1) / perRow;
            int rowCount = (row == totalRows - 1) ? (totalPlayers - row * perRow) : perRow;
            if (rowCount <= 0) rowCount = 1;

            // rowCount=3 → cols are -1, 0, +1; rowCount=2 → -0.5, +0.5; rowCount=1 → 0.
            float colOffset = colInRow - (rowCount - 1) * 0.5f;
            float lateralSpacing = level.trackWidth * 0.25f;
            float lateral = colOffset * lateralSpacing;

            // Rows behind the start line walk backward along the tangent.
            float backward = -row * forwardSpacing;

            // Tiny radial clearance so the wheels settle on first frame instead of
            // embedding into the planet collider.
            Vector3 surface = startDir * level.planetRadius + startDir * 0.05f;
            pos = surface + right * lateral + tangent * backward;
            rot = Quaternion.LookRotation(tangent, startDir);
        }

        void OnClientRaceStart(RaceStartMessage msg)
        {
            // Fires on every client, including the host's local client.
            Debug.Log($"[Dio] Client received RaceStartMessage. isServer={NetworkServer.active} startTime={msg.startServerTime:0.000}");
            OnRaceStarted?.Invoke(NetworkServer.active, msg);
        }

        void OnClientRaceWon(RaceWonMessage msg)
        {
            Debug.Log($"[Dio] Client received RaceWonMessage. winner={msg.winnerName} colour={msg.winnerColorIndex}");
            // Lock every car's input on this peer immediately (server already
            // ignores their inputs, but locking the local controller too prevents
            // the brief "spinning wheels with no movement" frame).
            foreach (var ni in NetworkClient.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<Dio.Player.ArcadeCarController>() : null;
                if (car != null) car.inputsLocked = true;
            }
            OnRaceWon?.Invoke(msg);
        }

        // ----- server-side win detection + cleanup -----

        void Update()
        {
            if (!NetworkServer.active || !_raceActive) return;
            if (defaultLevel == null || !defaultLevel.HasMinimum) return;

            // Distance proxy for "crossed the finish line" until the M2
            // arc-length-along-track tracker lands.
            Vector3 finishWorld = defaultLevel.Finish.directionFromCenter.normalized * defaultLevel.planetRadius;
            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            if (planet != null) finishWorld += planet.transform.position;

            foreach (var ni in NetworkServer.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<DioCar>() : null;
                if (car == null) continue;
                if ((car.transform.position - finishWorld).sqrMagnitude > WinDistanceThreshold * WinDistanceThreshold)
                    continue;

                // Crossed the finish — winner is the connection that owns this car.
                DioPlayer winnerPlayer = null;
                if (car.connectionToClient != null
                    && _players.TryGetValue(car.connectionToClient.connectionId, out var p))
                    winnerPlayer = p;

                ServerEndRace(winnerPlayer, car);
                return;
            }
        }

        [Server]
        void ServerEndRace(DioPlayer winner, DioCar winnerCar)
        {
            if (!_raceActive) return;
            _raceActive = false;

            // Lock every car's inputs immediately so the post-win frames don't
            // see any drift from queued client commands.
            foreach (var ni in NetworkServer.spawned.Values)
            {
                var ac = ni != null ? ni.GetComponent<Dio.Player.ArcadeCarController>() : null;
                if (ac != null) ac.inputsLocked = true;
            }

            string name = winner != null && !string.IsNullOrEmpty(winner.playerName)
                ? winner.playerName
                : (winnerCar != null ? winnerCar.ownerName : "?");
            int color = winner != null ? winner.colorIndex
                                       : (winnerCar != null ? winnerCar.colorIndex : 0);

            var msg = new RaceWonMessage { winnerName = name, winnerColorIndex = color, cleanupDelay = DefaultCleanupDelaySec };
            NetworkServer.SendToAll(msg);
            // Server's local NetworkClient receives the message via the standard pipeline.
            // For dedicated-server (no local client) we still need to schedule cleanup.
            if (!NetworkClient.active) OnRaceWon?.Invoke(msg);

            Invoke(nameof(ServerCleanupRace), DefaultCleanupDelaySec);
            Debug.Log($"[Dio] Server declared winner '{name}'. Cleanup in {DefaultCleanupDelaySec}s.");
        }

        // Tear down all per-race networked state. Player objects (DioPlayer)
        // and the network manager itself stay so the lobby can resume — but
        // every car, every powerup box, and every spawned obstacle goes.
        [Server]
        void ServerCleanupRace()
        {
            var toDestroy = new System.Collections.Generic.List<NetworkIdentity>();
            foreach (var ni in NetworkServer.spawned.Values)
            {
                if (ni == null) continue;
                if (ni.GetComponent<DioPlayer>() != null) continue; // keep lobby player objects
                toDestroy.Add(ni);
            }
            foreach (var ni in toDestroy)
            {
                if (ni != null) NetworkServer.Destroy(ni.gameObject);
            }
            Debug.Log($"[Dio] Server cleanup: destroyed {toDestroy.Count} per-race objects.");
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
