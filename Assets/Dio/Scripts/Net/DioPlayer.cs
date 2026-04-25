using Mirror;
using UnityEngine;

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

        public static System.Action OnRosterChanged;

        public override void OnStartServer()
        {
            // Default name based on connection id; client overrides via CmdSetName.
            playerName = $"Player {connectionToClient.connectionId}";
        }

        public override void OnStartClient() { NotifyRosterChanged(); }
        public override void OnStopClient() { NotifyRosterChanged(); }

        void OnNameChanged(string _, string __) => NotifyRosterChanged();
        void OnColorChanged(int _, int __) => NotifyRosterChanged();
        void OnReadyChanged(bool _, bool __) => NotifyRosterChanged();

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
        public void CmdRequestStart()
        {
            // Only the host (the connection that started the server) is allowed.
            if (connectionToClient != NetworkServer.localConnection) return;
            (NetworkManager.singleton as DioNetworkManager)?.ServerStartRace();
        }
    }
}
