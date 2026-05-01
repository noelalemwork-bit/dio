using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using Dio.Player;

namespace Dio.Powerups
{
    /// Solo-test powerup grants. Toggle `enableCheats` ON in the inspector
    /// (default OFF — never ships with cheats live). When enabled, the number
    /// keys grant powerups to the LOCAL OWNED car immediately:
    ///
    ///   1 = Boost
    ///   2 = Triple Boost (3 charges)
    ///   3 = Star
    ///   4 = Lightning
    ///   5 = Banana
    ///   6 = Oil Slick
    ///   7 = Green Shell
    ///   8 = Blue Shell
    ///   9 = Bo-bomb
    ///   0 = Tornado
    ///
    /// Use `E` (or your bound activate input) afterward to fire the powerup.
    /// The grant goes via PowerupHolder.CmdCheatGrant — server-validated, so
    /// the powerup behaves identically to one picked up from a box.
    public class PowerupCheats : MonoBehaviour
    {
        [Tooltip("Master switch. Off by default. Enable in the editor to test powerups solo (or in dev builds).")]
        public bool enableCheats = false;

        [Tooltip("If true, also accept the numpad 1-0 keys as aliases for the top-row digits.")]
        public bool acceptNumpad = true;

        // Indexed by digit 0-9; PowerupKind.None means "no binding for this key".
        static readonly PowerupKind[] DigitToKind =
        {
            PowerupKind.Tornado,    // 0
            PowerupKind.Boost,      // 1
            PowerupKind.TripleBoost,// 2
            PowerupKind.Star,       // 3
            PowerupKind.Lightning,  // 4
            PowerupKind.Banana,     // 5
            PowerupKind.OilSlick,   // 6
            PowerupKind.GreenShell, // 7
            PowerupKind.BlueShell,  // 8
            PowerupKind.Bobomb,     // 9
        };

        void Update()
        {
            if (!enableCheats) return;

            var kb = Keyboard.current;
            if (kb != null)
            {
                // Top-row digits.
                if (kb.digit0Key.wasPressedThisFrame) TryGrant(0);
                else if (kb.digit1Key.wasPressedThisFrame) TryGrant(1);
                else if (kb.digit2Key.wasPressedThisFrame) TryGrant(2);
                else if (kb.digit3Key.wasPressedThisFrame) TryGrant(3);
                else if (kb.digit4Key.wasPressedThisFrame) TryGrant(4);
                else if (kb.digit5Key.wasPressedThisFrame) TryGrant(5);
                else if (kb.digit6Key.wasPressedThisFrame) TryGrant(6);
                else if (kb.digit7Key.wasPressedThisFrame) TryGrant(7);
                else if (kb.digit8Key.wasPressedThisFrame) TryGrant(8);
                else if (kb.digit9Key.wasPressedThisFrame) TryGrant(9);

                if (acceptNumpad)
                {
                    if (kb.numpad0Key.wasPressedThisFrame) TryGrant(0);
                    else if (kb.numpad1Key.wasPressedThisFrame) TryGrant(1);
                    else if (kb.numpad2Key.wasPressedThisFrame) TryGrant(2);
                    else if (kb.numpad3Key.wasPressedThisFrame) TryGrant(3);
                    else if (kb.numpad4Key.wasPressedThisFrame) TryGrant(4);
                    else if (kb.numpad5Key.wasPressedThisFrame) TryGrant(5);
                    else if (kb.numpad6Key.wasPressedThisFrame) TryGrant(6);
                    else if (kb.numpad7Key.wasPressedThisFrame) TryGrant(7);
                    else if (kb.numpad8Key.wasPressedThisFrame) TryGrant(8);
                    else if (kb.numpad9Key.wasPressedThisFrame) TryGrant(9);
                }
            }

            // Gamepad cheats — only fire while cheats are enabled, so the
            // overlap with normal driving inputs (right trigger = throttle,
            // left trigger = brake, etc.) is acceptable: in cheat sessions
            // you're not racing competitively anyway. `wasPressedThisFrame`
            // on a trigger fires once per fresh press past Unity's default
            // press-point (~0.5), so holding the trigger for throttle won't
            // spam grants. Map chosen to spell out as memorisable as
            // possible: R2 = Star (the headline cheat the user asked for),
            // and shoulders/face buttons cover the rest.
            var pad = Gamepad.current;
            if (pad != null)
            {
                if (pad.rightTrigger.wasPressedThisFrame) TryGrant(3);   // R2 → Star
                else if (pad.leftTrigger.wasPressedThisFrame) TryGrant(1);   // L2 → Boost
                else if (pad.rightShoulder.wasPressedThisFrame) TryGrant(7);   // R1 → Penalty Shot (GreenShell)
                else if (pad.leftShoulder.wasPressedThisFrame) TryGrant(5);   // L1 → Banana
                else if (pad.buttonNorth.wasPressedThisFrame) TryGrant(4);   // Y/Triangle → Lightning
                else if (pad.buttonWest.wasPressedThisFrame) TryGrant(8);   // X/Square → Blue Shell
                else if (pad.dpad.up.wasPressedThisFrame) TryGrant(2);   // D-Pad ↑ → Triple Boost
                else if (pad.dpad.down.wasPressedThisFrame) TryGrant(6);   // D-Pad ↓ → Oil Slick
                else if (pad.dpad.left.wasPressedThisFrame) TryGrant(0);   // D-Pad ← → Tornado
                else if (pad.dpad.right.wasPressedThisFrame) TryGrant(9);   // D-Pad → → Bo-bomb
            }
        }

        void TryGrant(int digit)
        {
            if (digit < 0 || digit >= DigitToKind.Length) return;
            var kind = DigitToKind[digit];
            if (kind == PowerupKind.None) return;

            var holder = FindLocalHolder();
            if (holder == null)
            {
                Debug.Log($"[PowerupCheats] No local owned car to grant {kind} to.");
                return;
            }
            holder.CmdCheatGrant(kind);
            Debug.Log($"[PowerupCheats] Granted {kind} (key {digit})");
        }

        static PowerupHolder FindLocalHolder()
        {
            if (!NetworkClient.active) return null;
            foreach (var ni in NetworkClient.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<DioCar>() : null;
                if (car == null || !car.isOwned) continue;
                return car.GetComponent<PowerupHolder>();
            }
            return null;
        }
    }
}
