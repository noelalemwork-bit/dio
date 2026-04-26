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

        // Each player picks a preferred level by (ownerConnId, displayName).
        // The lobby grid shows every player's vote highlight on the chosen
        // entry; the host's vote is authoritative — that's the level the
        // race loads. ownerConnId == -1 means "no preference yet".
        [SyncVar(hook = nameof(OnLevelOwnerChanged))] public int    preferredLevelOwner = -1;
        [SyncVar(hook = nameof(OnLevelNameChanged))]  public string preferredLevelName  = "";
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
            // Every local client uploads its scanned LevelData library to
            // the server right after the lobby player object spawns. The
            // server tags each upload with the sender's connection id so the
            // network catalog can ID a level by (ownerConnId, displayName).
            CmdUploadLocalLevels();
        }

        // Owner-only: scan local LevelData assets and upload each one. We
        // don't include them as SyncVars on DioPlayer (would force a giant
        // serialised SyncList per player); instead we send one
        // PlayerLevelUploadMessage per level via NetworkClient.Send so the
        // server's handler picks them up and rebroadcasts the manifest.
        void CmdUploadLocalLevels()
        {
            if (!isLocalPlayer) return;
            var library = Dio.Level.LevelLibrary.ScanLocal();
            for (int i = 0; i < library.Count; i++)
            {
                var lvl = library[i];
                if (lvl == null || !lvl.HasMinimum) continue;
                NetworkClient.Send(new PlayerLevelUploadMessage { level = lvl });
            }
        }

        void OnNameChanged(string _, string __) => NotifyRosterChanged();
        void OnColorChanged(int _, int __) => NotifyRosterChanged();
        void OnReadyChanged(bool _, bool __) => NotifyRosterChanged();
        void OnLevelOwnerChanged(int _, int __) => NotifyRosterChanged();
        void OnLevelNameChanged(string _, string __) => NotifyRosterChanged();
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
        public void CmdSetPreferredLevel(int ownerConnId, string displayName)
        {
            // Server-side validation: the named level must actually exist in
            // the runtime catalog. Anything else is silently ignored — the
            // sender just keeps their previous vote.
            var mgr = NetworkManager.singleton as DioNetworkManager;
            if (mgr == null) return;
            if (!mgr.RuntimeCatalogHas(ownerConnId, displayName)) return;
            preferredLevelOwner = ownerConnId;
            preferredLevelName  = displayName ?? string.Empty;
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
