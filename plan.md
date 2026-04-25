# Dio — Multiplayer Arcade Racer

A multiplayer arcade racer with Mario Kart's playfulness, Beach Buggy Racing's chaos, and Trackmania's flow. Tracks wrap a cartoon globe — every level is a geodesic ribbon around a tiny planet, spawned procedurally from a few control points placed in an in-editor planet view.

## 1. Pillars

- **Spherical worlds.** Every level lives on a sphere of fixed radius (default 200u). Gravity points to the origin. The player's "up" is `(transform.position - planetOrigin).normalized`. The track is a geodesic-following procedural mesh.
- **Arcade physics.** Fast acceleration, generous grip, no spinouts, light tilt-into-turns. Built on Unity `WheelCollider` for stability + ease, with custom helpers to keep it feeling like a kart, not a sim.
- **Networked from day one.** Mirror is the only networking layer. Server-authoritative car kinematics, deterministic-ish track generation seeded by the level asset.
- **Editor-driven content.** Tracks authored in a tiny custom editor window: drop points on a sphere, drag them around, the geodesic between them auto-redraws. Save state continuously to a `LevelData` ScriptableObject.

## 2. Tech stack

- **Unity** 6 (URP, Input System, ProBuilder, Vector Graphics for SVG UI).
- **Mirror Networking** (vendored under `Assets/Mirror`). Discovery via `Mirror.Discovery.NetworkDiscoveryBase`; transport defaults to KCP.
- **C# only.** No Unity Visual Scripting in the gameplay path.

## 3. Project layout

```
Assets/
  Dio/
    Scenes/
      Main.unity            # menu + lobby + connected-clients view
      Race.unity            # gameplay
    Scripts/
      Net/                  # DioNetworkManager, DioNetworkDiscovery, messages, lobby player
      UI/                   # MainMenuController, list entries, palette
      Player/               # SphericalGravity, ArcadeCarController, CarSpawner
      Camera/               # RaceCamera (3 modes)
      Level/                # LevelData, GeodesicUtil, runtime track generator
      Level/Editor/         # LevelEditorWindow + scene builder menu items
      Common/               # ColorPalette, SvgLoader
    Prefabs/                # Car, Player, NetworkManager, MainCanvas
    Levels/                 # *.asset LevelData files
    UI/Svg/                 # *.svg icons
  Mirror/                   # vendored
```

## 4. Networking architecture

### 4.1 Discovery + lobby

- **`DioNetworkDiscovery : NetworkDiscoveryBase<DioDiscoveryRequest, DioDiscoveryResponse>`** — UDP LAN discovery. Response carries `serverId`, `uri`, `serverName`, `hostPlayerName`, `playerCount`, `maxPlayers`, `protocolVersion`.
- **`DioNetworkManager : NetworkManager`** — owns lobby state on the server (`List<DioPlayerInfo>` keyed by `connectionId`). Spawns a `DioPlayer` (NetworkBehaviour, NOT the car) per connection. The car is spawned later when the race starts.
- **Solo testing bypass.** `DioNetworkManager.minPlayers = 1` (configurable). The "Start Race" button is enabled if `connectedPlayers.Count >= minPlayers`. We also expose a `[Header("Debug")] bool soloBypass` that, when on, lets the host start the race immediately even with 0 remote clients.

### 4.2 Player identity

- **`DioPlayer : NetworkBehaviour`** lives for the duration of a session. Holds:
  - `[SyncVar] string playerName` (client-set with `[Command] CmdSetName(string)` — server validates length / sanitises).
  - `[SyncVar] int colorIndex` into `ColorPalette.Colors`.
  - `[SyncVar] bool ready`.
- The host is identified by `isLocalPlayer && isServer`.

### 4.3 Vehicle physics over the network

The plan: **server-authoritative cars + client-side prediction for the local player**, with snapshot interpolation for everyone else. Mirror's `PredictedRigidbody` / `NetworkTransformReliable` are the building blocks.

**Phase A — get it working (current milestone):**
- Server simulates all cars. Each car has `NetworkTransform` (unreliable, syncing position + rotation + velocity at ~30 Hz).
- Local player sends `PlayerInput` commands (steer, throttle, brake, drift) at fixed-rate via `[Command]`. Server applies them to the WheelCollider rig. No prediction yet — expect input lag on the local player but a clean baseline.

**Phase B — add prediction:**
- Switch local-player car to use `PredictedRigidbody` (Mirror's built-in). The local player simulates locally and reconciles against server state when it arrives.
- Other players stay on snapshot interpolation.
- Inputs still flow as commands; the server is still authoritative — the local player just pre-applies them.

**Phase C — collisions:**
- Car↔car collisions: server-only. Clients display the result. Sudden corrections will jolt the predicted local player; tune `correctionThreshold` on `PredictedRigidbody`.
- Car↔track: track is a static collider on every peer (deterministic from `LevelData` seed). No sync needed.
- Car↔obstacle: kinematic obstacles (spinning fans, rolling boulders on a fixed path) use deterministic motion (`time`-driven), not Rigidbody dynamics — every peer reproduces them locally without sync. If we ever want true dynamic obstacles, they get `NetworkRigidbody` with server authority.

**What we don't do:**
- No client-side authority on car position. Hosts always win.
- No physics on remote-player cars beyond visual interpolation. Remote cars are kinematic visuals.

### 4.4 Race-start synchronisation

Server sends a `RaceStartMessage { startServerTimeNs, levelGuid, seed }`. Clients schedule their countdown to the shared `NetworkTime.time`. The level is generated from `levelGuid` + `seed` deterministically on every peer.

## 5. Spherical gravity + arcade car

### 5.1 SphericalGravity

A `MonoBehaviour` on each car (and any dynamic body) holding `Transform planet` and `float gravity = 30`. In `FixedUpdate`:
```
Vector3 up = (rb.position - planet.position).normalized;
rb.AddForce(-up * gravity, ForceMode.Acceleration);
// Align the body's "up" toward the surface up, smoothly.
Quaternion targetRot = Quaternion.FromToRotation(rb.rotation * Vector3.up, up) * rb.rotation;
rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, alignSpeed * Time.fixedDeltaTime));
```
The "up" vector is also exposed as a property — the camera and input mapper read it to keep "forward" along the tangent plane.

### 5.2 ArcadeCarController (Unity WheelCollider)

- 4 `WheelCollider`s on a low-CG chassis Rigidbody. Mass ~ 800. Tweaked friction curves to high stiffness (no spinouts).
- **Steering.** Steer angle scales down with speed (slower turning at high speed = stable feel). Front wheels only.
- **Drive.** Rear-drive at first; AWD optional later. Big peak motor torque at zero speed, falling off — gives the punchy kart launch.
- **Brake.** Hand-brake reduces rear lateral stiffness slightly for a tiny drift, then snaps back; we *don't* let the car spin past 90°.
- **Counter-spin guard.** If yaw rate > threshold relative to forward speed, dampen angular velocity around the local up axis.
- **Air control.** Small torque around local up + pitch when wheels are off the ground.
- Inputs: `Vector2 move` (Input System), `bool brake`, `bool boost` (later).

### 5.3 RaceCamera (3 modes)

Single `RaceCamera` with `enum CameraMode { Chase, Bumper, Hood }` and `[Tab]`-cycling. Each `LateUpdate`:
1. Read planet-up from the followed body's `SphericalGravity`.
2. Place camera anchor in the body's local space (chase: behind+up; bumper: at front bumper; hood: just above hood).
3. Build a rotation whose up is planet-up and whose forward is the body's forward projected onto the tangent plane.
4. Smooth-follow with separate position and rotation lerp factors.

The chase camera additionally pulls back / FOVs out a bit at speed.

## 6. Level editor

Tracks are authored on a 3D sphere in a custom editor view.

### 6.1 `LevelData` (ScriptableObject)

```csharp
public class LevelData : ScriptableObject
{
    public float planetRadius = 200f;
    public int seed;
    public List<TrackPoint> points = new();    // first is start (red), last is finish
    public float trackWidth = 6f;
}

[Serializable]
public struct TrackPoint
{
    public Vector3 directionFromCenter; // unit vector; position = directionFromCenter * planetRadius
    public float bank;                  // degrees, for later
}
```

### 6.2 `LevelEditorWindow` (Editor)

A `EditorWindow` that:
- Opens a Scene-view-like preview with a wireframe planet at origin (radius from the asset).
- Lets the user **orbit** with right-mouse-drag, **zoom** with the scroll wheel.
- Renders all points as spheres on the surface; **start = red, finish = green, intermediates = white**.
- Click on the sphere to add a point at that surface position. Drag points to move them (always projected back to the sphere).
- For each consecutive pair, draws a **geodesic arc** (great-circle) sampled into N line segments — visualised with `Handles.DrawAAPolyLine` in blue.
- Auto-saves to the asset on every modification (`EditorUtility.SetDirty` + `AssetDatabase.SaveAssetIfDirty`).

### 6.3 GeodesicUtil

```csharp
// Given two unit directions a, b, returns N points on the great circle
// from a to b (inclusive), each at radius r.
public static Vector3[] Geodesic(Vector3 a, Vector3 b, float r, int segments);
```
Uses `Vector3.Slerp` on the unit directions; multiplies by `r` for positions. Total arc length ≈ `r * angle(a,b)`.

### 6.4 Bootstrap behaviour

When `Tools → Dio → New Level` is clicked:
- Create a `LevelData` asset under `Assets/Dio/Levels/`.
- Seed it with a **red start** at `(0, 1, 0)` and a **green finish** at a random direction.
- Open the editor window focused on the new asset.

### 6.5 Future runtime conversion

The eventual `TrackBuilder` reads `LevelData`, builds the geodesic, sweeps a quad along it (rotated each step so up = surface normal), generates a procedural mesh + `MeshCollider`, and instantiates obstacles from spline parameters. None of that ships in this milestone — only the editing skeleton.

## 7. Main UI

A single `Main.unity` scene. The **same scene** drives:
- Pre-name idle (everything but the name + color picker is greyed out / blocked by a full-screen scrim).
- Browse / host menu (after a name is set).
- In-lobby player list / "Start Race" panel — visible regardless of host or client; only the host can press "Start". Clients see it greyed out.

### Layout (top to bottom)

1. **Top bar**: name input + 6 color swatches.
2. **Center**:
   - **Idle state**: two big buttons — "Host Game" (orange) and "Join Game" (green). The Join button slides up a panel showing discovered servers; tap to connect.
   - **Lobby state**: list of connected players (color chip + name). At the bottom, a "Start Race" button — green for host, grey-disabled for clients.
3. **Visuals**: solid pastel background, two SVGs (a flag + a planet) tucked into corners. SVGs imported via `com.unity.modules.vectorgraphics`.

### Implementation note

I can't open Unity from here, so the scene is built by an **editor menu item**: `Tools → Dio → Build Main Scene`. Running it creates the canvas, all UI elements, and references the `MainMenuController` — guaranteed to compile and look the same on every machine. The user runs it once after pulling.

## 8. Color palette

Trackmania-ish, 6 colors:
- `#F39B2A` orange
- `#5BB85E` green
- `#3DA6E0` cyan
- `#E0533D` red
- `#A87CC9` purple
- `#F1D24D` yellow

Stored in `Assets/Dio/Scripts/Common/ColorPalette.cs` as a `static readonly Color[]`.

## 9. Milestones

- **M1 — this milestone.** Plan, scaffold, networking core, UI, car physics on a sphere, camera modes, level editor skeleton, solo bypass. **Track is a placeholder geodesic visualisation; race scene spawns the car at the start point oriented along the geodesic; no scoring, no finish line yet.**
- **M2.** Procedural track mesh from `LevelData`. Real start grid + finish detection. Race timer, lap counter (loop tracks), per-player ghost telemetry.
- **M3.** Item boxes, Mario-Kart-style power-ups (3 to start: boost, oil slick, shell). Predicted-rigidbody promotion for the local player.
- **M4.** Track theming pass — mountains, trees, water — driven by a `BiomePainter` that runs over the geodesic spline. ProBuilder for early hand-placed props.
- **M5.** Online (NAT-traversed) play — replace LAN discovery with a relay or `Edgegap` (Mirror has the example wired up).

## 10. Powerups

15 designs grouped by behaviour pattern. The first 10 are implemented; the rest are stubbed in `PowerupKind` for M2.

### 10.1 The 15

| # | Name | Pattern | State applied | Notes |
|---|---|---|---|---|
| 1 | **Boost** | self-buff | `nd SpeedBoostState` | +45 % max speed for 1.4 s |
| 2 | **Triple Boost** | self-buff (3 charges) | `SpeedBoostState × 3` | `PowerupHolder.charges` |
| 3 | **Star** | self-buff | `StarState` | +20 % speed, invincible (collision filtering hook on `CarStateMachine.IsInvincible`) |
| 4 | **Lightning** | global | `ShrunkState` to all others | server iterates `NetworkServer.spawned` |
| 5 | **Banana** | static hazard | `SlipperyState (Random)` | drop behind, despawns on touch |
| 6 | **Oil Slick** | static area | `SlipperyState (ZeroSteer)` | refreshes while inside |
| 7 | **Green Shell** | projectile | `TumbleState` | bounces twice, server-physics rigidbody |
| 8 | **Blue Shell** | projectile (tracker) | `TumbleState` AOE | glides surface-locked toward leader, AOE on impact |
| 9 | **Bob-omb** | projectile (fuse) | `TumbleState` AOE | fuse 2 s OR collision → explosion |
| 10 | **Tornado** | wandering kinematic | `TumbleState` + upward impulse | deterministic circle on the surface |
| 11 | Red Shell | projectile (homing) | `TumbleState` | M2 — same prefab, different controller |
| 12 | Freeze Ray | beam | `FrozenState` | M2 — line-cast on trigger |
| 13 | Confusion | targeted debuff | reversed-steer state | M2 — needs target picker UI |
| 14 | Magnet | radial pull | (no state — force only) | M2 |
| 15 | Spring Trap | static hazard | (impulse only) | M2 — `Rigidbody.AddForce(VelocityChange)` |

### 10.2 Architecture

```
CarStateMachine        ─ NetworkBehaviour, server-authoritative
   ├ List<CarState>    ─ active states this frame
   └ SyncList<int>     ─ active kinds for client visuals (shrunk scale, star glow…)

CarState (abstract)    ─ Duration, Elapsed, ModifyInputs(), ModifyController()

PowerupHolder          ─ NetworkBehaviour, one slot + `charges` for stacked
   ├ SyncVar held      ─ PowerupKind
   └ CmdActivate()     ─ asks PowerupRegistry for the effect, runs it server-side

PowerupBox             ─ NetworkBehaviour trigger, despawn + respawn timer
PowerupRegistry (static) ─ Kind → Effect, Kind → Prefab, available pool for boxes
PowerupBootstrap       ─ MonoBehaviour in the scene; registers everything in Awake

PowerupEffect (abstract)
   ├ Self-buff effects → CarStateMachine.Apply(...)
   ├ Global effects    → iterate NetworkServer.spawned, apply to others
   ├ SpawnBehind / SpawnForward effects → Instantiate prefab + NetworkServer.Spawn
   └ Tornado effect    → spawn deterministic kinematic
```

### 10.3 Obstacle archetypes

| Archetype | Base class | Sync model | Examples |
|---|---|---|---|
| **Static** | `Obstacle` (trigger only) | `NetworkServer.Spawn` + NetworkTransform (immobile) | Banana, Spring Trap |
| **Kinematic** | `Obstacle` + deterministic motion | NetworkTransform; can also be reproduced from time + seed locally if we ever want zero-bandwidth | Tornado, future fans / windmills |
| **Projectile** | `Obstacle` + Rigidbody | server-only physics; clients see via NetworkTransform | Green Shell, Bob-omb |

`Obstacle.ownerConnectionId` + `ownerImmunityWindow` keeps you from blowing yourself up the instant you drop a banana.

### 10.4 Why these network primitives

- **Inputs upload-only** at 30 Hz via `[Command]` from the local player to the server. No client-side prediction in M1; the server runs the only authoritative simulation. Phase B (M3) swaps the local-player car onto Mirror's `PredictedRigidbody`.
- **Cars are kinematic on remote clients.** Their `Rigidbody.isKinematic = true` after `OnStartClient` on non-server peers, so `NetworkTransform` interpolation drives them — no double simulation.
- **Obstacles are server-physics only.** Same kinematic-on-remote pattern.
- **State changes are SyncVars + a SyncList of active kinds.** Per-tick remaining time isn't synced — clients only need to know "is shrunk, is starred" for visuals.

## 11. Audit (M1 status)

| Area | Status | Notes |
|---|---|---|
| Networking core (Discovery, Manager, Player, RaceStartMessage) | ✅ done | Solo bypass works; host double-handler bug fixed |
| Server-side car spawn from prefab + authority assignment | ✅ done | `DioNetworkManager.ServerSpawnCarsForAllPlayers` |
| Networked input replication | ✅ done | `DioCar.CmdSendInputs` at 30 Hz, server-authoritative simulation |
| Car prefab (chassis + 4 wheel colliders + visuals + NetworkIdentity + NetworkTransform) | ✅ done | `Tools → Dio → Build → Car Prefab` |
| Spherical gravity + arcade controller + 3-mode camera | ✅ done | Tab cycles modes |
| Powerup state machine, holder, box, 10 effects | ✅ done | See §10 |
| Obstacle base + 6 concrete obstacles (banana, oil slick, green/blue shell, bob-omb, tornado) | ✅ done | Placeholder spheres + 3D-text labels via `Billboard` |
| Main UI scene (Trackmania-style menu, host/join, lobby) with name+color gating | ✅ done | `Tools → Dio → Build → All` |
| Level editor window (orbit, click-add, drag-move, geodesic preview, auto-save) | ✅ done | `Tools → Dio → Open Level Editor` |
| `ITrackSurface` abstraction | ✅ done | `SphereTrackSurface` is the M1 implementation |
| SVG placeholders | ✅ done | Flag, Planet, Bolt — wire into Main scene with `SVGImage` after `com.unity.modules.vectorgraphics` is enabled |
| Procedural track mesh | ❌ M2 | `LevelData` already feeds into a future `TrackMeshBuilder`; today the geodesic is shown via `LineRenderer` only — there's no driveable track surface yet beyond the sphere itself |
| Lap / finish detection | ❌ M2 | Need a per-car arc-length tracker; Blue Shell currently uses a placeholder "leader" picker |
| Predicted rigidbody for local player | ❌ M3 | Plan section §4.3 Phase B |
| SVG icons (menu + HUD + powerups) | ✅ done | 16 icons under `Assets/Dio/UI/Svg/` (menu_host, menu_join, menu_start, menu_leave, hud_minimap, hud_powerup_box, hud_speed, hud_player_marker, powerup_boost/star/banana/shell/blueshell/bomb/tornado/oil + the original flag/planet/bolt). `SvgIconLoader` adds `SVGImage` (vector quality) when `com.unity.vectorgraphics` is installed, falls back to plain `Image` with the SVG-imported sprite, falls back to a flat colored rect. `Tools → Dio → Build → Re-import SVGs` forces the SVGImporter to re-process every `.svg` (run this after first installing the package). |
| Race HUD | ✅ M1 minimum | Minimap (placeholder globe SVG), held-powerup slot with charge count, km/h readout. Built under `RaceHUDRoot`; `MainMenuController` swaps `MenuRoot` ↔ `RaceHUDRoot` on race start. |
| Render-texture minimap | ❌ M2 | See §11.4 — top-down camera + RawImage; cars and pickups become UI markers parented to the minimap RectTransform rather than rendered into the texture. |
| Asmdefs for Dio code | ⏳ deferred | All Dio scripts (runtime + editor) compile into `Assembly-CSharp` / `Assembly-CSharp-Editor`. Verified clean — no stray asmdefs. Revisit when build times bite. |

### 11.1 Known bugs / sharp edges

- **Solo-bypass no-player branch** (`_players.Count == 0`): the spawned car has no authority, so the host can't drive it. Normally never hit because StartHost adds a DioPlayer for the local connection — but if you bypass the menu, you'll see a static car. Workaround: always go through "Host Game" in the menu.
- **Blue Shell target selection** is a placeholder dot-product. Replace with the M2 arc-length-progress tracker.
- **Vector-quality SVG rendering** requires the `com.unity.vectorgraphics` package; without it, SVGs display through plain `Image` (still readable, just rasterised at the import resolution). With the package, install it then run `Tools → Dio → Build → Re-import SVGs` once so the new importer processes the existing `.svg` files.
- **PowerupHolder activation** uses `E` by default — wire `activateAction` in the inspector to a real Input System binding when adding controller support.

### 11.2 How to run the demo

1. Open the project in Unity 6.
2. **First-time only**: `Window → TextMeshPro → Import TMP Essential Resources`. The build menus fail-fast if this is missing.
3. `Tools → Dio → Build → All` — generates prefabs (player, car, all powerup obstacles), default level, network-manager prefab, and the Main scene.
4. Open `Assets/Dio/Scenes/Main.unity`.
5. Press Play. Type a name, pick a color, click **Host Game**.
6. Click **Start Race** — the menu canvas hides, the planet appears, the car spawns at the start point oriented along the geodesic.
7. WASD/arrows to drive, Space brake, Shift handbrake, Tab to cycle camera, E to fire your held powerup.

To test multiplayer: build a standalone, run host in editor, connect with the standalone via discovery.

### 11.3 Editor menu reference

| Menu | What it does |
|---|---|
| `Tools → Dio → Build → All` | One-click build of everything below |
| `Tools → Dio → Build → Player Prefab` | `Assets/Dio/Prefabs/DioPlayer.prefab` (NetworkIdentity + DioPlayer) |
| `Tools → Dio → Build → Car Prefab` | `Assets/Dio/Prefabs/Car.prefab` (chassis + 4 wheels + colliders + Mirror + Dio components) |
| `Tools → Dio → Build → Network Manager Prefab` | `Assets/Dio/Prefabs/DioNetworkManager.prefab` (KCP transport, NetworkDiscovery, wired car + level refs) |
| `Tools → Dio → Build → Default Level Asset` | Creates `DefaultLevel.asset` only if it doesn't exist (idempotent — won't overwrite hand-edits) |
| `Tools → Dio → Build → Main Scene` | Rebuilds `Main.unity` from scratch — drops the canvas, controller, race bootstrap, powerup bootstrap |
| `Tools → Dio → Build → Powerup Prefabs` | All networked obstacle/box prefabs + their material assets |
| `Tools → Dio → New Level` | Creates a *brand new* `LevelData` asset under `Assets/Dio/Levels/` (unique filename) and opens the editor on it. Use this to author additional tracks; **Default Level Asset only ever recreates `DefaultLevel.asset`**. |
| `Tools → Dio → Open Level Editor` | Opens the orbit-and-edit window for whichever level is currently selected |

### 11.4 Minimap (current placeholder + future render-texture)

The race HUD's minimap is a static `hud_minimap.svg` (stylised globe) under the canvas. Good enough to ship, but the real version is:

```
[Top-down camera] ─ orthographic, parented over the local player
        │           looking down toward the planet origin
        ▼
[RenderTexture] ─ a small (e.g. 256×256) texture asset
        │
        ▼
[RawImage in canvas] ─ takes the place of today's hud_minimap SVG
```

Cars, pickups, and vegetation are intentionally **excluded from the camera's culling mask**. They become **UI markers** parented to the minimap RectTransform — short-lived `Image` children with `hud_player_marker.svg` etc., positioned by projecting world-space → minimap-local-space each frame. Reasons:

- Vegetation in a top-down render is visual noise; UI labels are crisper.
- Cars are tiny in the texture but readable as iconic UI markers.
- Lets us color-code by player without per-car render layers.

The scene builder already reserves a `Minimap` RectTransform with a `PlayerMarker` child; the future `MinimapController` swaps the SVG sprite on `Minimap` for a `RawImage` referencing the RenderTexture, then drives marker positions in `LateUpdate`.

## 12. Open questions

- **Bank angle** for fast cornering — defer until we have track-mesh sweeping; tracked in `TrackPoint.bank`.
- **Wraparound finish line** vs. **point-to-point** races — both are easy on a geodesic; choose per-level.
- **Min players for "real" game.** 4 is the design target; the code uses `minPlayers = 1` during development.
- **Powerup balance.** 10 powerups in a 4-player race needs probability tuning per leaderboard position (the MK trick where last place gets the good stuff). Stub: `PowerupRegistry.RandomFromBox()` uses uniform — extend to `RandomForCar(DioCar c, int rank)` once a leaderboard exists.
