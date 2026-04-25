using System.Net;
using Mirror;
using Mirror.Discovery;
using UnityEngine;
using UnityEngine.Events;

namespace Dio.Net
{
    [System.Serializable]
    public class DioServerFoundEvent : UnityEvent<DioDiscoveryResponse> { }

    [DisallowMultipleComponent]
    [AddComponentMenu("Dio/Net/Dio Network Discovery")]
    public class DioNetworkDiscovery : NetworkDiscoveryBase<DioDiscoveryRequest, DioDiscoveryResponse>
    {
        public const ushort ProtocolVersion = 1;

        /// Pinned handshake so every build of Dio agrees with every other build,
        /// regardless of whether OnValidate happened to randomise the base
        /// class's secretHandshake. Bump this if the wire format ever changes.
        public const long DioHandshake = 0x44494F4E45540001L; // "DIONET\0\1"

        [Header("Server advertisement")]
        public string serverName = "Dio Lobby";
        public int defaultMaxPlayers = 8;

        public DioServerFoundEvent OnDioServerFound = new DioServerFoundEvent();

        public override void Start()
        {
            secretHandshake = DioHandshake; // override whatever OnValidate scribbled
            base.Start();
            Debug.Log($"[Dio Discovery] ready. handshake=0x{DioHandshake:X16} protocol=v{ProtocolVersion}");
        }

        protected override DioDiscoveryResponse ProcessRequest(DioDiscoveryRequest request, IPEndPoint endpoint)
        {
            try
            {
                var mgr = NetworkManager.singleton as DioNetworkManager;
                Debug.Log($"[Dio Discovery] server got ping from {endpoint}, replying.");
                return new DioDiscoveryResponse
                {
                    serverId = ServerId,
                    uri = transport.ServerUri(),
                    serverName = string.IsNullOrEmpty(serverName) ? "Dio Lobby" : serverName,
                    hostName = mgr != null ? mgr.HostName : "Host",
                    playerCount = mgr != null ? mgr.ConnectedPlayerCount : NetworkServer.connections.Count,
                    maxPlayers = mgr != null ? mgr.maxConnections : defaultMaxPlayers,
                    protocolVersion = ProtocolVersion,
                };
            }
            catch (System.NotImplementedException)
            {
                Debug.LogError($"Transport {transport} does not support network discovery");
                throw;
            }
        }

        protected override DioDiscoveryRequest GetRequest() => new DioDiscoveryRequest();

        protected override void ProcessResponse(DioDiscoveryResponse response, IPEndPoint endpoint)
        {
            response.EndPoint = endpoint;
            var realUri = new System.UriBuilder(response.uri) { Host = endpoint.Address.ToString() };
            response.uri = realUri.Uri;

            if (response.protocolVersion != ProtocolVersion)
            {
                Debug.LogWarning($"[Dio Discovery] dropping reply from {endpoint}: protocol v{response.protocolVersion} (we want v{ProtocolVersion})");
                return;
            }

            Debug.Log($"[Dio Discovery] found '{response.serverName}' (host={response.hostName}, {response.playerCount}/{response.maxPlayers}) at {endpoint} → {response.uri}");
            OnDioServerFound.Invoke(response);
        }
    }
}
