using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

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

        [Tooltip("Scene to load when the race starts. Leave empty to stay in the current scene (additive spawn).")]
        public string raceSceneName = "Race";

        public DioNetworkDiscovery discovery;

        // Server-side roster. Indexed by connection id.
        readonly Dictionary<int, DioPlayer> _players = new Dictionary<int, DioPlayer>();
        public int ConnectedPlayerCount => _players.Count;
        public IReadOnlyDictionary<int, DioPlayer> Players => _players;

        public static DioNetworkManager Instance => singleton as DioNetworkManager;

        public static System.Action<bool> OnRaceStarted; // bool isServer

        public override void Awake()
        {
            base.Awake();
            if (discovery == null) discovery = GetComponent<DioNetworkDiscovery>();
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            base.OnServerAddPlayer(conn);
            if (conn.identity != null)
            {
                var p = conn.identity.GetComponent<DioPlayer>();
                if (p != null) _players[conn.connectionId] = p;
            }
            DioPlayer.OnRosterChanged?.Invoke(); // best-effort; hooks also fire from SyncVars.
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _players.Remove(conn.connectionId);
            base.OnServerDisconnect(conn);
        }

        // ---------- Race start ----------

        public bool CanStartRace()
        {
            if (!NetworkServer.active) return false;
            if (soloBypass) return true;
            return _players.Count >= minPlayers;
        }

        public void ServerStartRace()
        {
            if (!CanStartRace())
            {
                Debug.Log($"[Dio] ServerStartRace denied. players={_players.Count} min={minPlayers} soloBypass={soloBypass}");
                return;
            }
            var msg = new RaceStartMessage
            {
                levelGuid = string.Empty, // wired up later by track loader
                seed = Random.Range(int.MinValue, int.MaxValue),
                startServerTime = NetworkTime.time + 1.0,
            };
            NetworkServer.SendToAll(msg);
            HandleRaceStartLocal(msg, isServer: true);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            NetworkServer.RegisterHandler<RaceStartMessage>(_ => { /* server ignores its own broadcast */ });
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            NetworkClient.RegisterHandler<RaceStartMessage>(msg => HandleRaceStartLocal(msg, isServer: false));
        }

        void HandleRaceStartLocal(RaceStartMessage msg, bool isServer)
        {
            Debug.Log($"[Dio] RaceStart received. server={isServer} startTime={msg.startServerTime} seed={msg.seed}");

            // For now we don't switch scenes; the race scene is built/spawned in-place by RaceBootstrap.
            OnRaceStarted?.Invoke(isServer);
        }

        // ---------- Discovery convenience ----------

        public void HostAndAdvertise()
        {
            StartHost();
            if (discovery != null) discovery.AdvertiseServer();
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
