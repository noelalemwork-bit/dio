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

        [Tooltip("Car prefabs to choose from randomly on race start. Each must be built from a VehicleProfile using CarPrefabBuilder.")]
        public GameObject[] carPrefabs;

        [Tooltip("Fallback level if no level is selected (no players have voted, no host pick yet).")]
        public Dio.Level.LevelData defaultLevel;

        [Tooltip("Levels selectable from the lobby. Index 0 is the suggested default. The host's preferredLevelIndex picks one from here.")]
        public Dio.Level.LevelData[] levelCatalog;

        public DioNetworkDiscovery discovery;

        public int CatalogSize => levelCatalog != null ? levelCatalog.Length : 0;

        // Active level for the running race — set when ServerStartRace picks
        // from the catalog; consulted by RaceBootstrap so clients build the
        // visuals against the same level the server simulates.
        public Dio.Level.LevelData ActiveLevel => _activeLevel != null ? _activeLevel : defaultLevel;
        Dio.Level.LevelData _activeLevel;

        // Resolve which level the host wants to play. Falls back from the
        // host's vote → first catalog entry → defaultLevel.
        public Dio.Level.LevelData ResolveSelectedLevel()
        {
            DioPlayer host = null;
            foreach (var p in _players.Values)
            {
                if (p != null && p.connectionToClient == NetworkServer.localConnection) { host = p; break; }
            }
            int idx = host != null ? host.preferredLevelIndex : -1;
            if (levelCatalog != null && idx >= 0 && idx < levelCatalog.Length && levelCatalog[idx] != null)
                return levelCatalog[idx];
            if (levelCatalog != null && levelCatalog.Length > 0 && levelCatalog[0] != null)
                return levelCatalog[0];
            return defaultLevel;
        }

        // Server-side roster. Indexed by connection id.
        readonly Dictionary<int, DioPlayer> _players = new Dictionary<int, DioPlayer>();
        public int ConnectedPlayerCount => _players.Count;
        public IReadOnlyDictionary<int, DioPlayer> Players => _players;

        public static DioNetworkManager Instance => singleton as DioNetworkManager;

        // Bool argument: true if this peer is the server (host or dedicated).
        public static System.Action<bool, RaceStartMessage> OnRaceStarted;
        public static System.Action<RaceWonMessage> OnRaceWon;
        public static System.Action OnCountdownEnd;

        bool _raceActive;
        // Crossed the finish when within `FinishArcWindow` meters of the END
        // of the track's arc length. Smaller than the old WinDistanceThreshold
        // because arc-length is monotonic — once you reach the end, you're done,
        // regardless of how wide the track is.
        const float FinishArcWindow = 4f;
        const float DefaultCleanupDelaySec = 5.5f; // duration of the win screen before cleanup
        float _totalTrackLength; // cached at race start; updated when level changes

        public override void Awake()
        {
            base.Awake();
            if (discovery == null) discovery = GetComponent<DioNetworkDiscovery>();

            // Pin the physics tick to 60 Hz so server-side WheelCollider sim
            // matches what every client expects from snapshot interpolation.
            // Unity's default 50 Hz introduces audible "step" jitter on remote
            // peers when their sendInterval is 60 Hz — the timelines drift.
            // 60 Hz fixed step + 60 Hz Mirror sendRate + 60 Hz client input
            // upload means inputs, sim, and snapshots are all on one timeline.
            Time.fixedDeltaTime = 1f / 60f;
            Time.maximumDeltaTime = 0.1f; // safety: don't over-step on hitches

            // Mirror's NetworkManager exposes `sendRate` (Hz). Pin it
            // explicitly so a future package update doesn't quietly change
            // the default and re-introduce jitter.
            sendRate = 60;
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

            if (carPrefabs != null)
            {
                foreach (var p in carPrefabs)
                {
                    if (p != null && !NetworkClient.prefabs.ContainsValue(p))
                        NetworkClient.RegisterPrefab(p);
                }
            }

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

            // Resolve which level we're racing on. Locked in for the duration
            // of the race; clients read it via DioNetworkManager.ActiveLevel
            // (defaultLevel field stays as the bootstrap fallback for editor
            // play-mode tests where no host has voted yet).
            _activeLevel = ResolveSelectedLevel();
            if (_activeLevel == null || !_activeLevel.HasMinimum)
            {
                Debug.LogError("[Dio] ServerStartRace: no valid level resolved (catalog empty + defaultLevel missing).");
                return;
            }

            // Embed the selected level's catalog index (canonical) + GUID
            // (debug-only) in the start message so clients can pick the
            // matching asset from their catalog. The catalog itself is
            // serialised on every clone via the DioNetworkManager prefab
            // built by `Tools → Dio → Build → Network Manager Prefab`.
            int lvlIndex = -1;
            if (levelCatalog != null)
            {
                for (int i = 0; i < levelCatalog.Length; i++)
                    if (levelCatalog[i] == _activeLevel) { lvlIndex = i; break; }
            }
            string lvlGuid = string.Empty;
#if UNITY_EDITOR
            string p2p = UnityEditor.AssetDatabase.GetAssetPath(_activeLevel);
            if (!string.IsNullOrEmpty(p2p)) lvlGuid = UnityEditor.AssetDatabase.AssetPathToGUID(p2p);
#endif

            var msg = new RaceStartMessage
            {
                levelGuid = lvlGuid,
                levelCatalogIndex = lvlIndex,
                seed = Random.Range(int.MinValue, int.MaxValue),
                // Long delay so the cinematic intro gets time to swoop the
                // camera in + run the 3-2-1-GO! countdown. RaceIntro on each
                // peer locks inputs locally until startServerTime.
                startServerTime = NetworkTime.time + 4.0,
            };

            Debug.Log($"[Dio] ServerStartRace level='{_activeLevel.name}' catalogIndex={lvlIndex} guid={lvlGuid}");

            // Build the server-side planet FIRST so newly-spawned cars can find it
            // in their Awake. Otherwise SphericalGravity has nothing to fall back to.
            var bootstrap = FindAnyObjectByType<Dio.Level.RaceBootstrap>();
            if (bootstrap != null) bootstrap.BuildVisualScene(_activeLevel);

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
            _totalTrackLength = Dio.Level.TrackBuilder.TotalArcLength(_activeLevel);
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
            if (carPrefab == null && (carPrefabs == null || carPrefabs.Length == 0)) { Debug.LogError("[Dio] carPrefab and carPrefabs are null/empty on DioNetworkManager."); return; }
            var lvl = ActiveLevel;
            if (lvl == null || !lvl.HasMinimum)
            {
                Debug.LogError("[Dio] active level missing or has fewer than 2 points.");
                return;
            }

            int total = _players.Count;
            var raycaster = Dio.Level.RaceBootstrap.CurrentRaycaster;
            int finishIdx = lvl.points.Count - 1;
            int initialRemaining = Mathf.Max(0, Dio.Level.LevelGraph.MinHopsBetween(lvl, 0, finishIdx));
            int i = 0;
            foreach (var kv in _players)
            {
                var conn = NetworkServer.connections.TryGetValue(kv.Key, out var c) ? c : null;
                var player = kv.Value;
                ComputeStartTransform(lvl, raycaster, i, total, out Vector3 pos, out Quaternion rot);
                Debug.Log($"[Dio] Spawning car slot={i}/{total} pos={pos} fwd={rot * Vector3.forward} up={rot * Vector3.up}");

                var prefab = carPrefabs != null && carPrefabs.Length > 0 ? carPrefabs[Random.Range(0, carPrefabs.Length)] : carPrefab;
                var carGo = Instantiate(prefab, pos, rot);
                NetworkServer.Spawn(carGo, conn);

                var car = carGo.GetComponent<DioCar>();
                if (car != null && player != null) car.ServerInitialize(player);
                if (car != null)
                {
                    car.ServerResetCheckpoints();
                    car.ServerCheckpointHit(0); // crossing the start anchor (x=1 at lights-out)
                    car.checkpointsToFinishY = car.CrossedCount + initialRemaining;
                }
                i++;
            }

            // Solo-bypass: spawn the host a car even if no DioPlayer exists yet (e.g.
            // testing without the lobby).
            if (_players.Count == 0 && soloBypass)
            {
                ComputeStartTransform(lvl, raycaster, 0, 1, out var pos, out var rot);
                Debug.Log($"[Dio] Solo-bypass spawn pos={pos} fwd={rot * Vector3.forward} up={rot * Vector3.up}");
                var prefab = carPrefabs != null && carPrefabs.Length > 0 ? carPrefabs[Random.Range(0, carPrefabs.Length)] : carPrefab;
                var carGo = Instantiate(prefab, pos, rot);
                NetworkServer.Spawn(carGo);
                var soloCar = carGo.GetComponent<DioCar>();
                if (soloCar != null)
                {
                    soloCar.ServerResetCheckpoints();
                    soloCar.ServerCheckpointHit(0);
                    soloCar.checkpointsToFinishY = soloCar.CrossedCount + initialRemaining;
                }
            }
        }

        // For every edge in the track graph, drop one row of 5 mystery boxes
        // across the track at that edge's local midpoint. Each row uses a
        // unique groupId so a player can grab one box per row per pass —
        // PowerupHolder.consumedGroups tracks which rows the player has
        // already used.
        //
        // Edge enumeration walks the level's adjacency graph, so:
        //   * 1 anchor pair (default level) → 1 row of 5 boxes
        //   * 3 anchors / 2 edges → 2 rows
        //   * a fork from an interior anchor → +1 row per new outgoing edge
        // The midpoint is sampled at the EDGE's local t=0.5 (not the chain's
        // overall t=0.5) so a fork's branch row sits at the middle of the
        // forked bezier, not collapsed onto the trunk.
        [Server]
        void ServerSpawnMidTrackPowerups()
        {
            var lvl = ActiveLevel;
            if (lvl == null || !lvl.HasMinimum) return;
            var pu = FindAnyObjectByType<Dio.Powerups.PowerupBootstrap>();
            if (pu == null || pu.powerupBoxPrefab == null)
            {
                Debug.LogWarning("[Dio] No PowerupBootstrap or powerupBoxPrefab — skipping mid-track row.");
                return;
            }
            lvl.EnsureLinearChainNext();

            var raycaster = Dio.Level.RaceBootstrap.CurrentRaycaster;

            // groupId starts at 1 and increments per row; a player who passes
            // through every row in a lap can collect one box from each — same
            // semantics as the original single-row behaviour, just multiplied.
            int nextGroupId = 1;
            int totalRows = 0;
            int totalBoxes = 0;
            foreach (var (from, to) in lvl.EnumerateEdges())
            {
                int spawned = SpawnMidTrackRowForEdge(lvl, raycaster, pu, from, to, nextGroupId);
                if (spawned > 0)
                {
                    nextGroupId++;
                    totalRows++;
                    totalBoxes += spawned;
                }
            }
            Debug.Log($"[Dio] Mid-track powerups: {totalRows} rows × {totalBoxes / Mathf.Max(1, totalRows)} boxes (= {totalBoxes} total).");
        }

        // Spawn one row of 5 boxes at the midpoint of the (from → to) bezier
        // edge. Returns the number of boxes spawned (0 on fail).
        [Server]
        int SpawnMidTrackRowForEdge(Dio.Level.LevelData lvl, Dio.Level.GlobeRaycaster raycaster,
            Dio.Powerups.PowerupBootstrap pu, int from, int to, int groupId)
        {
            // Independently shuffled pool per row — every row gets a fresh
            // 5-of-9 pick so the player's choice differs at each midpoint.
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

            // Sample the edge's bezier at t=0.5 — this is the EDGE's local
            // midpoint, not the chain's overall midpoint. For forks, this
            // means each outgoing branch gets its own row at the middle of
            // its own curve.
            lvl.GetEdge(from, to, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
            const float MidT = 0.5f;
            const float DerivEps = 0.001f;
            Vector3 midDir   = Dio.Level.GeodesicUtil.SphericalBezier(a, ah, bh, b, MidT);
            Vector3 dirAhead = Dio.Level.GeodesicUtil.SphericalBezier(a, ah, bh, b, MidT + DerivEps);
            Vector3 tangent  = (dirAhead - midDir).normalized;
            if (tangent.sqrMagnitude < 1e-6f) tangent = Dio.Level.GeodesicUtil.TangentAt(a, b);

            float yOffset  = Mathf.Lerp(lvl.points[from].yOffset, lvl.points[to].yOffset, MidT);
            float surfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(midDir) : lvl.NominalRadius;
            // Lift boxes a metre above the surface so the chassis pivot can
            // pass under them but the bounds intersect a normal car.
            const float Lift = 1.0f;
            Vector3 mid = midDir * (surfaceR + yOffset + Lift);

            // Right vector in the local tangent plane: up × forward.
            Vector3 right = Vector3.Cross(midDir, tangent).normalized;

            float trackW = lvl.EffectiveTrackWidth;
            float spacing = trackW / (picked.Count + 1f);

            for (int i = 0; i < picked.Count; i++)
            {
                float lateral = -trackW * 0.5f + spacing * (i + 1);
                Vector3 boxPos = mid + right * lateral;
                var go = Instantiate(pu.powerupBoxPrefab, boxPos,
                    Quaternion.LookRotation(tangent, midDir));
                var box = go.GetComponent<Dio.Powerups.PowerupBox>();
                if (box != null)
                {
                    box.forceKind = picked[i];
                    box.groupId = groupId;
                }
                NetworkServer.Spawn(go);
            }

            Debug.Log($"[Dio] Powerup row #{groupId} on edge {from}->{to}: {string.Join(", ", picked)}");
            return picked.Count;
        }

        // Per-car start transform. Up to 3 cars per row, additional rows fold
        // BACKWARD along the geodesic (not the tangent — tangent stepping
        // curves below the surface as the offset grows). Within each row the
        // cars are centered around the track midline so a partial last row
        // (e.g. 2 cars in a 5-player race) still looks symmetric.
        //
        // Critical math:
        //   * Backward step is a great-circle rotation of the start anchor's
        //     direction around the axis perpendicular to (startDir, startTan).
        //     SAME axis the spawn-pad ribbon uses, so cars sit ON the pad.
        //   * Radial height comes from the raycaster (when available) so the
        //     chassis pivot lands on the actual mesh surface, not the
        //     idealised sphere — without this, cars on uneven terrain clip
        //     through the ground at race start. We add startYOffset on top
        //     so a designer-lifted start line still works, and a tiny
        //     clearance so wheels settle on the first frame.
        //   * Lateral offset uses EffectiveTrackWidth (= trackWidth *
        //     trackRatio) and is applied in the LOCAL tangent plane at the
        //     row's position — Cross(rowDir, rowTan) is the local "right".
        //   * Rotation: Quaternion.LookRotation(forward=rowTan, up=rowDir).
        //     Car's +Z = forward along track, +Y = surface up. Standard
        //     Unity convention; matches the car prefab's mesh orientation.
        static void ComputeStartTransform(Dio.Level.LevelData level, Dio.Level.GlobeRaycaster raycaster,
                                          int slot, int totalPlayers,
                                          out Vector3 pos, out Quaternion rot)
        {
            const int perRow = 3;
            const float forwardSpacing = 4.5f;

            Vector3 startDir = level.DirOf(0);
            Vector3 startTan = Dio.Level.TrackBuilder.TangentAt(level, 0f);
            float startYOffset = level.points[0].yOffset;

            int row = slot / perRow;
            int colInRow = slot - row * perRow;
            int totalRows = (totalPlayers + perRow - 1) / perRow;
            int rowCount = (row == totalRows - 1) ? (totalPlayers - row * perRow) : perRow;
            if (rowCount <= 0) rowCount = 1;

            // rowCount=3 → cols are -1, 0, +1; rowCount=2 → -0.5, +0.5; rowCount=1 → 0.
            float colOffset = colInRow - (rowCount - 1) * 0.5f;
            float lateralSpacing = level.EffectiveTrackWidth * 0.25f;
            float lateral = colOffset * lateralSpacing;

            // Geodesic backward step using the same axis the spawn pad
            // arc uses — so each row's car sits ON the pad's geometry.
            // axis = startDir × (-startTan), positive angle rotates startDir
            // in the -tangent direction.
            float radius = level.NominalRadius;
            float backwardArc = row * forwardSpacing;
            float backwardAngle = backwardArc / Mathf.Max(0.01f, radius);

            Vector3 axis = Vector3.Cross(startDir, -startTan);
            Vector3 rowDir, rowTan;
            if (axis.sqrMagnitude < 1e-8f)
            {
                // Degenerate (start tangent is parallel to start direction —
                // shouldn't ever happen for a valid level). Fall back to no
                // rotation so we still produce a sensible spawn.
                rowDir = startDir;
                rowTan = startTan;
            }
            else
            {
                axis.Normalize();
                Quaternion q = Quaternion.AngleAxis(backwardAngle * Mathf.Rad2Deg, axis);
                rowDir = (q * startDir).normalized;
                rowTan = (q * startTan).normalized;
            }

            // Resolve actual surface height at the row's position. Falling
            // back to NominalRadius if no raycaster (server-headless mode
            // without a spawned planet — rare but possible).
            float surfaceR = raycaster != null ? raycaster.ResolveSurfaceRadius(rowDir) : radius;
            float radialHeight = surfaceR + startYOffset + 0.10f; // 10cm clearance

            // Right vector in the local tangent plane: up × forward
            // (Unity left-handed). Lateral offset slides the car sideways
            // across the track without leaving the surface plane.
            Vector3 rowRight = Vector3.Cross(rowDir, rowTan).normalized;
            pos = rowDir * radialHeight + rowRight * lateral;

            // LookRotation(forward, up). With forward = rowTan (along track)
            // and up = rowDir (radial), the car's +Z faces the finish line.
            rot = Quaternion.LookRotation(rowTan, rowDir);
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

        public override void Update()
        {
            base.Update();

            // Host-only restart hotkey (R). Tears down per-race networked
            // state and reuses the SAME level for an immediate rematch — no
            // win popup, no lobby roundtrip. Pure clients and dedicated
            // servers ignore (they have no input or shouldn't decide).
            if (NetworkServer.active && NetworkClient.active && Input.GetKeyDown(KeyCode.R))
            {
                ServerRestartRace();
                return;
            }

            if (!NetworkServer.active || !_raceActive) return;
            var lvl = ActiveLevel;
            if (lvl == null || !lvl.HasMinimum) return;
            if (_totalTrackLength <= 0.01f) return;

            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            Vector3 planetCenter = planet != null ? planet.transform.position : Vector3.zero;
            float finishArc = _totalTrackLength;

            foreach (var ni in NetworkServer.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<DioCar>() : null;
                if (car == null) continue;

                // Update progressArc for legacy systems (minimap, projectile
                // leader pick) that still rank by arc length. Win detection
                // moved to CheckpointTrigger / ServerNotifyCarFinished.
                float arc = Dio.Level.TrackBuilder.ArcLengthOf(lvl, car.transform.position, planetCenter);
                car.progressArc = arc;
            }
        }

        /// Called by CheckpointTrigger when a car enters the finish anchor's
        /// trigger volume. Server-only.
        [Server]
        public void ServerNotifyCarFinished(DioCar car)
        {
            if (!_raceActive || car == null) return;
            DioPlayer winnerPlayer = null;
            if (car.connectionToClient != null
                && _players.TryGetValue(car.connectionToClient.connectionId, out var p))
                winnerPlayer = p;
            ServerEndRace(winnerPlayer, car);
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

        /// Host-pressed-R restart. Tears down per-race networked state with
        /// the `isRestart` flag (clients suppress the win UI), then schedules
        /// a fresh ServerStartRace on the SAME level after a short delay so
        /// destroyed networked objects fully propagate to clients first.
        [Server]
        public void ServerRestartRace()
        {
            Debug.Log("[Dio] ServerRestartRace — host requested rematch on the same level.");
            CancelInvoke(nameof(ServerCleanupRace));
            CancelInvoke(nameof(ServerStartRaceFromRestart));

            _raceActive = false;
            // Lock all car inputs immediately so no commands queue up while
            // we're tearing down.
            foreach (var ni in NetworkServer.spawned.Values)
            {
                var ac = ni != null ? ni.GetComponent<Dio.Player.ArcadeCarController>() : null;
                if (ac != null) ac.inputsLocked = true;
            }

            const float restartCleanupDelay = 0.5f;
            var rmsg = new RaceWonMessage
            {
                winnerName = string.Empty,
                winnerColorIndex = 0,
                cleanupDelay = restartCleanupDelay,
                isRestart = true,
            };
            NetworkServer.SendToAll(rmsg);
            if (!NetworkClient.active) OnRaceWon?.Invoke(rmsg);

            // Cleanup networked race objects on the server. Clients tear down
            // their visual scene via the RaceWonMessage handler.
            ServerCleanupRace();
            // Schedule the new race start. Long enough to let the visual
            // teardown finish on every peer; short enough to feel like a
            // crisp rematch.
            Invoke(nameof(ServerStartRaceFromRestart), restartCleanupDelay + 0.1f);
        }

        [Server]
        void ServerStartRaceFromRestart()
        {
            // Don't reroll the level — same _activeLevel survives across the
            // teardown because we only clear it in ServerCleanupRace, which
            // doesn't touch the field. ServerStartRace re-resolves anyway,
            // and ResolveSelectedLevel returns the same host vote.
            ServerStartRace();
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
