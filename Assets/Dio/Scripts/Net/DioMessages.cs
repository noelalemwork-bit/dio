using Mirror;

namespace Dio.Net
{
    public struct DioDiscoveryRequest : NetworkMessage { }

    public struct DioDiscoveryResponse : NetworkMessage
    {
        public long serverId;
        public System.Uri uri;
        public string serverName;
        public string hostName;
        public int playerCount;
        public int maxPlayers;
        public ushort protocolVersion;

        public System.Net.IPEndPoint EndPoint { get; set; }
    }

    public struct RaceStartMessage : NetworkMessage
    {
        public string levelGuid;
        public int seed;
        public double startServerTime;
    }

    public struct RaceWonMessage : NetworkMessage
    {
        public string winnerName;
        public int winnerColorIndex;
        public float cleanupDelay; // seconds before clients return to lobby
    }
}
