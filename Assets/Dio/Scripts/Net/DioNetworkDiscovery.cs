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

        [Header("Server advertisement")]
        public string serverName = "Dio Lobby";
        public int defaultMaxPlayers = 8;

        public DioServerFoundEvent OnDioServerFound = new DioServerFoundEvent();

        protected override DioDiscoveryResponse ProcessRequest(DioDiscoveryRequest request, IPEndPoint endpoint)
        {
            try
            {
                var mgr = NetworkManager.singleton as DioNetworkManager;
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

            // Resolve to the actual sender IP (mirrors what NetworkDiscovery does).
            var realUri = new System.UriBuilder(response.uri) { Host = endpoint.Address.ToString() };
            response.uri = realUri.Uri;

            if (response.protocolVersion != ProtocolVersion) return;

            OnDioServerFound.Invoke(response);
        }
    }
}
