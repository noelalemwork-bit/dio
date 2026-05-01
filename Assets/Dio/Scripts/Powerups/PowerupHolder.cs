using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using Dio.Player;

namespace Dio.Powerups
{
    /// Lives on each car. Holds the currently-grabbed powerup (single slot)
    /// and forwards local activation input to the server.
    ///
    /// Lifecycle:
    ///   1. Grab (server): box → Grant(kind). Sets `held`, `charges`,
    ///      `heldUntilServerTime = now + DefaultHoldSeconds(kind)`.
    ///   2. Display (client): RaceHUD shows the icon + a circular timer
    ///      driven by `heldUntilServerTime - NetworkTime.time`.
    ///   3. Use (owner → server): E key → CmdActivate fires the effect,
    ///      decrements charges, clears slot when last charge spent.
    ///   4. Auto-expire (server): if the slot is still occupied at the timer's
    ///      end, the powerup vanishes — players can't sit on a held powerup.
    ///
    /// While `held != None` the player blocks new pickups (PowerupBox checks
    /// this before granting). Stacked TripleBoost charges count as a single
    /// "occupied" slot until all charges are spent.
    [RequireComponent(typeof(DioCar))]
    public class PowerupHolder : NetworkBehaviour
    {
        [SyncVar(hook = nameof(OnHeldChanged))] public PowerupKind held = PowerupKind.None;
        [SyncVar] public int charges; // for Triple Boost

        // NetworkTime when the held slot auto-expires. 0 = no active timer.
        // Clients read this to drive the slot's circular countdown UI.
        [SyncVar] public double heldUntilServerTime;

        // Group IDs of powerup-rows already consumed by this player. PowerupBox
        // checks this against its own groupId before granting — so a single
        // mid-track row only ever gives one powerup per car.
        readonly SyncList<int> consumedGroups = new SyncList<int>();

        public InputActionReference activateAction;

        public System.Action<PowerupKind> OnHeldChangedClient;

        DioCar _car;

        public bool HasHeld => held != PowerupKind.None;

        void Awake() { _car = GetComponent<DioCar>(); }

        public override void OnStartAuthority()
        {
            if (activateAction != null) activateAction.action.Enable();
        }

        void Update()
        {
            // Server-side auto-expire so a player who sits on their powerup
            // eventually loses it and can pick up another. Has to run every
            // tick because a SyncVar timestamp can't trigger callbacks on
            // its own — we just compare each frame.
            if (isServer && held != PowerupKind.None && heldUntilServerTime > 0
                && NetworkTime.time >= heldUntilServerTime)
            {
                ClearSlot();
            }

            if (!isOwned) return;
            if (held == PowerupKind.None && charges <= 0) return;

            bool pressed = false;
            if (activateAction != null && activateAction.action.enabled)
                pressed = activateAction.action.WasPressedThisFrame();
            else
            {
                // Activate fallback: E on keyboard, South face button (A on
                // Xbox / Cross on PS) on a gamepad. South is the universal
                // "confirm" button across platforms.
                if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame) pressed = true;
                if (!pressed && Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame) pressed = true;
            }

            if (pressed) CmdActivate();
        }

        [Command]
        void CmdActivate()
        {
            if (PowerupRegistry.TryGetEffect(held, out var fx))
            {
                fx.Activate(_car);
                if (charges > 0) charges--;
                if (charges <= 0) ClearSlot();
            }
        }

        [Server]
        public void Grant(PowerupKind kind, int chargesIfStacked = 1)
        {
            held = kind;
            charges = Mathf.Max(1, chargesIfStacked);
            heldUntilServerTime = NetworkTime.time + DefaultHoldSeconds(kind);
        }

        [Server]
        void ClearSlot()
        {
            held = PowerupKind.None;
            charges = 0;
            heldUntilServerTime = 0;
        }

        /// How long a freshly-grabbed powerup stays in the slot before it
        /// auto-expires (in seconds). Tuned so utility-style powerups linger
        /// a bit (Boost — long enough to plan a chain), while bigger /
        /// disruptive ones (Triple Boost, Lightning) push the player to use
        /// them quickly.
        public static float DefaultHoldSeconds(PowerupKind kind)
        {
            switch (kind)
            {
                case PowerupKind.Boost:       return 7f;   // longest — a steady tool
                case PowerupKind.TripleBoost: return 5f;   // shorter (per user spec)
                case PowerupKind.Star:        return 6f;
                case PowerupKind.Lightning:   return 5f;
                case PowerupKind.Banana:      return 6f;
                case PowerupKind.OilSlick:    return 6f;
                case PowerupKind.GreenShell:  return 6f;
                case PowerupKind.BlueShell:   return 6f;
                case PowerupKind.Bobomb:      return 5f;
                case PowerupKind.Tornado:     return 6f;
                default: return 5f;
            }
        }

        // Owner-invoked debug grant — wired to the PowerupCheats component
        // (number keys 1-9, 0). Server validates that the kind is a real
        // PowerupKind and that the caller actually owns this car.
        // Cheats override the "already holding" rule so testing works.
        [Command]
        public void CmdCheatGrant(PowerupKind kind)
        {
            if (kind == PowerupKind.None) return;
            if (kind == PowerupKind.TripleBoost) Grant(PowerupKind.Boost, 3);
            else Grant(kind, 1);
        }

        // Server tells every peer to draw a transient lightning bolt from the
        // caster to each target position. Called by LightningEffect.Activate.
        // We pass world positions (not netIds) so the bolt renders correctly
        // even if the target gets destroyed an instant later.
        [ClientRpc]
        public void RpcShowLightning(Vector3 caster, Vector3[] targets)
        {
            if (targets == null || targets.Length == 0)
            {
                // Solo-test fallback: still show a bolt so the cheat key
                // produces visible feedback even with no enemies in the race.
                Vector3 forward = transform.forward;
                Effects.LightningZap.Spawn(caster, caster + forward * 18f);
                return;
            }
            for (int i = 0; i < targets.Length; i++)
                Effects.LightningZap.Spawn(caster, targets[i]);
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
