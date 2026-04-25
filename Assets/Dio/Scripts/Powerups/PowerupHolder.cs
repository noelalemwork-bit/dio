using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using Dio.Player;

namespace Dio.Powerups
{
    /// Lives on each car. Holds the currently-grabbed powerup (single slot —
    /// extend with TripleBoost-style charges later) and forwards local
    /// activation input to the server.
    [RequireComponent(typeof(DioCar))]
    public class PowerupHolder : NetworkBehaviour
    {
        [SyncVar(hook = nameof(OnHeldChanged))] public PowerupKind held = PowerupKind.None;
        [SyncVar] public int charges; // for Triple Boost

        // Group IDs of powerup-rows already consumed by this player. PowerupBox
        // checks this against its own groupId before granting — so a single
        // mid-track row only ever gives one powerup per car.
        readonly SyncList<int> consumedGroups = new SyncList<int>();

        public InputActionReference activateAction;

        public System.Action<PowerupKind> OnHeldChangedClient;

        DioCar _car;

        void Awake() { _car = GetComponent<DioCar>(); }

        public override void OnStartAuthority()
        {
            if (activateAction != null) activateAction.action.Enable();
        }

        void Update()
        {
            // Authority-based, not player-object based. See DioCar.Update.
            if (!isOwned) return;
            if (held == PowerupKind.None && charges <= 0) return;

            bool pressed = false;
            if (activateAction != null && activateAction.action.enabled)
                pressed = activateAction.action.WasPressedThisFrame();
            else if (Keyboard.current != null)
                pressed = Keyboard.current.eKey.wasPressedThisFrame;

            if (pressed) CmdActivate();
        }

        [Command]
        void CmdActivate()
        {
            if (PowerupRegistry.TryGetEffect(held, out var fx))
            {
                fx.Activate(_car);
                if (charges > 0) charges--;
                if (charges <= 0) held = PowerupKind.None;
            }
        }

        [Server]
        public void Grant(PowerupKind kind, int chargesIfStacked = 1)
        {
            held = kind;
            charges = Mathf.Max(1, chargesIfStacked);
        }

        [Server] public bool HasConsumedGroup(int gid) => gid != 0 && consumedGroups.Contains(gid);
        [Server] public void MarkGroupConsumed(int gid)
        {
            if (gid == 0 || consumedGroups.Contains(gid)) return;
            consumedGroups.Add(gid);
        }
        [Server] public void ClearConsumedGroups() => consumedGroups.Clear();

        void OnHeldChanged(PowerupKind oldVal, PowerupKind newVal)
        {
            OnHeldChangedClient?.Invoke(newVal);
        }
    }
}
