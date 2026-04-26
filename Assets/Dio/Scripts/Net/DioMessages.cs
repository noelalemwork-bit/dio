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
        // Identifies the level the server picked. Clients with the same
        // catalog (wired into DioNetworkManager.levelCatalog at build time)
        // resolve via the index; the GUID is a redundant editor-only
        // identifier for play-mode debugging.
        public string levelGuid;
        public int    levelCatalogIndex;
        public int    seed;
        public double startServerTime;
    }

    public struct RaceWonMessage : NetworkMessage
    {
        public string winnerName;
        public int winnerColorIndex;
        public float cleanupDelay; // seconds before clients return to lobby
        // Set when the host is restarting on the same level — UI suppresses
        // the win popup but the visual scene still tears down. The host
        // immediately follows up with a fresh RaceStartMessage.
        public bool  isRestart;
    }
}
