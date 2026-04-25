using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            // Disable Mirror's built-in single-address Invoke; we run our own
            // multi-adapter broadcaster below so VPN-style virtual adapters
            // (Cloudflare WARP, Tailscale, ZeroTier) don't swallow the broadcast.
            enableActiveDiscovery = false;
            base.Start();

            CancelInvoke(nameof(MultiAdapterBroadcast));
            InvokeRepeating(nameof(MultiAdapterBroadcast), 0.1f, 3f);

            Debug.Log($"[Dio Discovery] ready. handshake=0x{DioHandshake:X16} protocol=v{ProtocolVersion} (multi-adapter broadcast)");
        }

        // Replaces Mirror's BroadcastDiscoveryRequest with one that sends to
        // EVERY active adapter's subnet broadcast address, plus 255.255.255.255
        // as a fallback. This works around setups where one adapter (typically
        // a VPN like Cloudflare WARP) hogs the default route and silently
        // intercepts limited-broadcast UDP packets.
        public void MultiAdapterBroadcast()
        {
            if (clientUdpClient == null) return;
            if (NetworkClient.isConnected)
            {
                StopDiscovery();
                return;
            }

            var addresses = GetBroadcastAddresses();
            using (var writer = NetworkWriterPool.Get())
            {
                writer.WriteLong(secretHandshake);
                try { writer.Write(GetRequest()); }
                catch (Exception ex) { Debug.LogException(ex); return; }
                ArraySegment<byte> data = writer.ToArraySegment();

                foreach (var addr in addresses)
                {
                    try
                    {
                        var ep = new IPEndPoint(addr, serverBroadcastListenPort);
                        clientUdpClient.SendAsync(data.Array, data.Count, ep);
                    }
                    catch { /* one bad NIC shouldn't stop the rest */ }
                }
            }
        }

        // Enumerate "up" non-loopback IPv4 adapters and compute each one's
        // subnet broadcast (addr | ~mask). Returns 255.255.255.255 too as a
        // last-ditch fallback for routers that allow limited broadcast.
        static List<IPAddress> GetBroadcastAddresses()
        {
            var result = new List<IPAddress>();
            try
            {
                foreach (var nif in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nif.OperationalStatus != OperationalStatus.Up) continue;
                    if (nif.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var props = nif.GetIPProperties();
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.IPv4Mask == null) continue;
                        byte[] addr = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        if (addr.Length != 4 || mask.Length != 4) continue;
                        var bcast = new byte[4];
                        for (int i = 0; i < 4; i++) bcast[i] = (byte)(addr[i] | ~mask[i]);
                        result.Add(new IPAddress(bcast));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Dio Discovery] adapter enumeration failed: {ex.Message}; falling back to 255.255.255.255 only.");
            }
            if (!result.Contains(IPAddress.Broadcast)) result.Add(IPAddress.Broadcast);
            return result;
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
