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
        // The level is identified by (ownerConnId, displayName) — every peer
        // already received the full payload via PlayerLevelManifestEntry, so
        // the message just names the chosen one. ownerConnId is the host's
        // own connection id when the host's level was picked.
        public int    levelOwnerConnId;
        public string levelDisplayName;
        public int    seed;
        public double startServerTime;

        // Embedded full payload as a fallback. Useful when a late-joining
        // client hasn't received the manifest entry yet, or when an asset
        // was pulled from the catalog mid-vote. Server always fills this so
        // remote clients can build the visual scene without depending on
        // their own LevelLibrary scan matching.
        public Dio.Level.LevelData level;
    }

    /// Server → all clients. Authoritative GO! signal. The server broadcasts
    /// this exactly once, after its 4 s countdown elapses, and every peer
    /// (host + guests) flips its inputsLocked + fires OnCountdownEnd on
    /// receipt — no NetworkTime convergence math, no per-peer drift. LAN
    /// latency here is sub-millisecond, so all peers unlock effectively
    /// simultaneously.
    public struct RaceGoMessage : NetworkMessage { }

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

    // ---------- Per-player level catalog protocol ----------
    //
    // Each peer scans its own local LevelData assets (via LevelLibrary) and
    // uploads them to the server. The server stores everything keyed by
    // (ownerConnId, displayName) and broadcasts the aggregated manifest to
    // every peer so the lobby UI can list every level brought into the room
    // — host's plus every guest's. When a peer disconnects the server
    // removes their entries and rebroadcasts.

    /// Client → server. One message per local level.
    public struct PlayerLevelUploadMessage : NetworkMessage
    {
        public Dio.Level.LevelData level;
    }

    /// Client → server. Sent on disconnect path or when a designer manually
    /// removes one of their levels.
    public struct PlayerLevelRemoveMessage : NetworkMessage
    {
        public string displayName;
    }

    /// One row in the aggregated manifest broadcast.
    public struct PlayerLevelManifestEntry
    {
        public int    ownerConnId;
        public string ownerName;
        public string displayName;
        // Full payload included so clients don't need their own copy of the
        // level to build the scene at race start. Keeps manifest size up,
        // but avoids round-trip "send me level X" handshakes.
        public Dio.Level.LevelData level;
    }

    /// Server → all clients. Full snapshot — clients replace their cached
    /// catalog wholesale. Sent whenever any player's roster changes.
    public struct PlayerLevelManifestMessage : NetworkMessage
    {
        public PlayerLevelManifestEntry[] entries;
    }
}
