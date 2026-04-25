using Mirror;
using UnityEngine;

namespace Dio.Net
{
    /// Per-connection lobby/player presence object. Lives for the whole session.
    /// The car is a separate prefab spawned at race-start time.
    public class DioPlayer : NetworkBehaviour
    {
        public const int MaxNameLength = 16;

        [SyncVar(hook = nameof(OnAnyChanged))] public string playerName = "Player";
        [SyncVar(hook = nameof(OnAnyChanged))] public int colorIndex = 0;
        [SyncVar(hook = nameof(OnAnyChanged))] public bool ready = false;

        public static System.Action OnRosterChanged;

        public override void OnStartServer()
        {
            // Default name based on connection id; client overrides via CmdSetName.
            playerName = $"Player {connectionToClient.connectionId}";
        }

        public override void OnStartClient() { OnRosterChanged?.Invoke(); }
        public override void OnStopClient() { OnRosterChanged?.Invoke(); }

        void OnAnyChanged<T>(T _, T __) { OnRosterChanged?.Invoke(); }

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
