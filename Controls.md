# Dio — Controls

Dio supports keyboard + mouse and gamepad as first-class inputs. Both work simultaneously; pick up either one mid-race and it will respond on the next frame. The new Unity Input System drives both paths, so any DirectInput / XInput / DualShock / DualSense controller plugged in shows up as `Gamepad.current` automatically — no remapping screen, no driver dance.

## Driving

| Action       | Keyboard / Mouse              | Gamepad (Xbox-style names)                         |
|--------------|-------------------------------|----------------------------------------------------|
| Steer        | A / D · ← / →                 | Left Stick X · D-Pad ←/→                           |
| Throttle     | W · ↑                         | Left Stick ↑ · Right Trigger · D-Pad ↑             |
| Reverse      | S · ↓                         | Left Stick ↓ · D-Pad ↓                             |
| Brake        | Space                         | Left Trigger (threshold 0.3)                       |
| Handbrake    | Left Shift                    | B / Circle (East button) · Right Shoulder          |
| Camera cycle | Tab                           | Y / Triangle (North button)                        |
| Use powerup  | E                             | A / Cross (South button)                           |

Throttle is analog on the right trigger and the stick — half-pressed = half-throttle. The trigger overrides the stick when it's higher, so you can hold the stick neutral (just for steering) and feather the trigger for speed Forza-style. The left trigger starts braking from ~30% pull, so the brake bites immediately rather than waiting for a full press.

## Race controls

| Action                    | Keyboard | Gamepad           |
|---------------------------|----------|-------------------|
| Restart race (host only)  | R        | Start (Plus)      |
| Quit / leave to home      | Esc      | Select (Share)    |

Both are global — they fire from anywhere, any panel, in or out of a race. Host pressing Restart immediately resets the race for everyone. Host pressing Quit kicks every connected client back to the home screen along with themselves; clients pressing Quit only disconnect themselves.

## UI navigation

The EventSystem uses Unity's `InputSystemUIInputModule`, so menus accept both mouse and gamepad input.

| Action                | Keyboard / Mouse        | Gamepad                        |
|-----------------------|-------------------------|--------------------------------|
| Move selection        | Mouse · Tab             | Left Stick · D-Pad             |
| Activate / confirm    | Click · Enter           | A / Cross (South button)       |
| Cancel / back / quit  | Esc                     | Select (Share)                 |

When you first move the stick or press a face button on the gamepad, Dio auto-selects the most relevant button on whatever panel is open (Host on idle, Start on lobby, first server row in the browser). This way the gamepad has a starting point — you never need to mouse-click first to give the menu focus. Mouse users keep null selection so there's no stray highlight ring; click anywhere to drop selection back to mouse-mode.

## Cheats (debug)

`PowerupCheats` is OFF by default — toggle `enableCheats` on the component to grant powerups directly to the local owned car. Bindings:

| Powerup        | Keyboard       | Gamepad                    |
|----------------|----------------|----------------------------|
| Boost          | 1              | L2                         |
| Triple Boost   | 2              | D-Pad ↑                    |
| Star           | 3              | **R2**                     |
| Lightning      | 4              | Y / Triangle (North)       |
| Banana         | 5              | L1                         |
| Oil Slick      | 6              | D-Pad ↓                    |
| Penalty Shot   | 7              | R1                         |
| Blue Shell     | 8              | X / Square (West)          |
| Bo-bomb        | 9              | D-Pad →                    |
| Tornado        | 0              | D-Pad ←                    |

Gamepad cheats overlap with normal driving inputs (R2 = throttle, L2 = brake, etc.). That's intentional: cheats are dev-only and overlap is fine in cheat sessions. `wasPressedThisFrame` semantics mean holding the trigger for throttle won't spam-grant — only fresh trigger presses fire.

## Where this lives in code

- Driving fallbacks — `Assets/Dio/Scripts/Player/ArcadeCarController.cs` (`ReadMove` / `ReadBrake` / `ReadHandbrake`)
- Powerup activate fallback — `Assets/Dio/Scripts/Powerups/PowerupHolder.cs`
- Restart race hotkey — `Assets/Dio/Scripts/Net/DioNetworkManager.cs::Update`
- Quit / Esc + UI first-selected — `Assets/Dio/Scripts/UI/MainMenuController.cs`
- EventSystem creation — `Assets/Dio/Scripts/UI/Editor/MainSceneBuilder.cs`

If a designer wires explicit `InputActionReference` assets onto the Car prefab (`moveAction`, `brakeAction`, `handbrakeAction`, `cameraCycleAction`) those take precedence over the fallback paths described above. The fallback paths are what runs in the default procedurally-built prefab, since `Tools → Dio → Build → All` doesn't author InputActionAsset bindings.
