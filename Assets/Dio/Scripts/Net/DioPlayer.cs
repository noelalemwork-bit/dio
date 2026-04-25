using Mirror;
using UnityEngine;
using Dio.Common;

namespace Dio.Net
{
    /// Per-connection lobby/player presence object. Lives for the whole session.
    /// The car is a separate prefab spawned at race-start time.
    public class DioPlayer : NetworkBehaviour
    {
        public const int MaxNameLength = 16;

        // Mirror's weaver requires the hook signature to match the SyncVar's
        // concrete type exactly — no generics. So we have one hook per field
        // and they all forward to NotifyRosterChanged.
        [SyncVar(hook = nameof(OnNameChanged))]   public string playerName = "Player";
        [SyncVar(hook = nameof(OnColorChanged))]  public int colorIndex = 0;
        [SyncVar(hook = nameof(OnReadyChanged))]  public bool ready = false;

        // Each player picks a preferred level. Clients see every player's vote
        // (by their color) overlaid on the level grid. The host's pick is the
        // authoritative one — that's the level the race actually loads.
        // -1 means "no preference yet" / pre-selection.
        [SyncVar(hook = nameof(OnLevelChanged))]  public int preferredLevelIndex = -1;
        // True on the connection that started the server. Clients use this
        // to render the "host pick" highlight on the level grid.
        [SyncVar(hook = nameof(OnHostChanged))]   public bool isHost = false;

        public static System.Action OnRosterChanged;

        public override void OnStartServer()
        {
            // Default name based on connection id; client overrides via CmdSetName
            // as soon as their player object spawns (OnStartLocalPlayer below).
            playerName = $"Player {connectionToClient.connectionId}";
            isHost = (connectionToClient == NetworkServer.localConnection);
        }

        public override void OnStartClient() { NotifyRosterChanged(); }
        public override void OnStopClient() { NotifyRosterChanged(); }

        // Push the local prefs (name, color, default level vote) the moment
        // this client takes ownership of a player object — without this, the
        // lobby briefly shows "Player N" / wrong color / no vote until the
        // first manual change.
        public override void OnStartLocalPlayer()
        {
            base.OnStartLocalPlayer();
            string n = LocalPrefs.PlayerName;
            if (!string.IsNullOrWhiteSpace(n)) CmdSetName(n);
            CmdSetColor(LocalPrefs.ColorIndex);
            int lvl = LocalPrefs.PreferredLevelIndex;
            if (lvl >= 0) CmdSetPreferredLevel(lvl);
        }

        void OnNameChanged(string _, string __) => NotifyRosterChanged();
        void OnColorChanged(int _, int __) => NotifyRosterChanged();
        void OnReadyChanged(bool _, bool __) => NotifyRosterChanged();
        void OnLevelChanged(int _, int __) => NotifyRosterChanged();
        void OnHostChanged(bool _, bool __) => NotifyRosterChanged();

        static void NotifyRosterChanged() => OnRosterChanged?.Invoke();

        [Command]
        public void CmdSetName(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (newName.Length > MaxNameLength) newName = newName.Substring(0, MaxNameLength);
            playerName = newName;
        }

        [Command]
        public void CmdSetColor(int newIndex)
        {
            colorIndex = Mathf.Clamp(newIndex, 0, Dio.Common.ColorPalette.Colors.Length - 1);
        }

        [Command]
        public void CmdSetReady(bool isReady) { ready = isReady; }

        [Command]
        public void CmdSetPreferredLevel(int idx)
        {
            int catalogSize = (NetworkManager.singleton as DioNetworkManager)?.CatalogSize ?? 0;
            if (catalogSize <= 0) return;
            preferredLevelIndex = Mathf.Clamp(idx, 0, catalogSize - 1);
        }

        [Command]
        public void CmdRequestStart()
        {
            // Only the host (the connection that started the server) is allowed.
            if (connectionToClient != NetworkServer.localConnection) return;
            (NetworkManager.singleton as DioNetworkManager)?.ServerStartRace();
        }
    }
}
