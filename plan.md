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

## 10. Open questions

- **Bank angle** for fast cornering — defer until we have track-mesh sweeping; tracked in `TrackPoint.bank`.
- **Wraparound finish line** vs. **point-to-point** races — both are easy on a geodesic; choose per-level.
- **Min players for "real" game.** 4 is the design target; the code uses `minPlayers = 1` during development.
