# Dio

Multiplayer arcade racer in the spirit of Mario Kart, Beach Buggy Racing, and Trackmania — every track is a geodesic ribbon around a tiny cartoon planet. Mirror Networking, Unity 6, URP.

The full design + audit + roadmap lives in [plan.md](./plan.md). This file is the cold-start checklist for cloning, getting it to compile, and running the demo.

## Prerequisites

- **Unity 6** (URP). The project pin lives in `ProjectSettings/ProjectVersion.txt`.
- **TextMeshPro essentials**, after the first project open: `Window → TextMeshPro → Import TMP Essential Resources`. Build menus fail-fast if this is missing.

## Packages you need to install

These are vendored or fetched but **not committed**, so a fresh clone won't have them. You need to install them yourself — either via the Unity Package Manager or by dropping them into `Assets/`:

### Required

| Package | What it is | Why |
|---|---|---|
| **Mirror Networking** | LAN/online networking, vendored under `Assets/Mirror/` | Everything multiplayer; `KcpTransport`, `NetworkDiscoveryBase`, `NetworkTransform`, `PredictedRigidbody` |
| Unity built-in modules | URP, Input System, ProBuilder, vehicles, UI, TextMeshPro | Listed in `Packages/manifest.json` — Unity restores them on open |

**Installing Mirror**: download the latest `.unitypackage` from [the asset store](https://assetstore.unity.com/packages/tools/network/mirror-129321) or the [GitHub releases](https://github.com/MirrorNetworking/Mirror/releases) and import it into `Assets/Mirror/`. The `.gitignore` excludes `Assets/Mirror/` so your local copy stays out of the repo.

### Optional but recommended

| Package | What it enables |
|---|---|
| `com.unity.vectorgraphics` | Imports `.svg` files as Sprite assets so the corner accents in the main menu render as actual SVGs instead of flat colored rects. Without it, you get the fallback colored rects (no errors). Install via Package Manager → "Vector Graphics". |

## First-run checklist

```text
1. git clone <this repo>
2. Open in Unity 6 (let it generate Library/, Temp/, etc.)
3. Window → TextMeshPro → Import TMP Essential Resources
4. Import Mirror Networking into Assets/Mirror/
5. (optional) Install com.unity.vectorgraphics for SVG menu icons
6. Tools → Dio → Build → All
7. Open Assets/Dio/Scenes/Main.unity
8. Press Play
```

## Controls

| Key | Action |
|---|---|
| **W / S / Arrows** | Throttle / brake-reverse |
| **A / D** | Steer |
| **Space** | Brake |
| **Left Shift** | Handbrake |
| **Tab** | Cycle camera (Chase / Bumper / Hood) |
| **E** | Activate held powerup |

## How to run multiplayer (LAN demo)

- **Solo testing**: just click *Host Game* then *Start Race*. The host's `minPlayers = 1` + `soloBypass = true` lets you race alone.
- **Two clients**: build a standalone (`File → Build Settings → Build`), run host in editor, run the standalone, click *Join Game* on the standalone — the host appears in the LAN browser via `Mirror.Discovery`.

### Troubleshooting: "I can't see hosted games on my LAN"

LAN discovery uses UDP broadcast on port `47777`. If clients see an empty list:

1. **Windows Firewall.** Almost always the cause. The first time Unity / your build tries to bind UDP `47777`, Windows pops a "Allow access" dialog — if you missed it or hit Cancel, the inbound side is dropped. Open *Windows Security → Firewall & network protection → Allow an app through firewall* and tick **Private** for Unity (and your standalone build's `.exe`). On macOS / Linux the equivalent is `pfctl` / `ufw`.
2. **Same-machine testing.** Windows doesn't reliably loopback `255.255.255.255` broadcasts between two processes on the same PC. Either test across two machines, *or* use the **Direct IP** fallback in the Browser panel (see below).
3. **Direct IP fallback.** The browser panel has an "Or direct IP" input + Connect button. Enter the host's LAN IP (`ipconfig` on Windows, look for the `IPv4 Address` of your active adapter) and click **Connect**. Bypasses discovery entirely, just opens a KCP connection. Default port is 7777.
4. **Console diagnostics.** With this build, the host logs `[Dio Discovery] ready. handshake=0x… protocol=v1` on startup and `[Dio Discovery] server got ping from …` for each request received. Clients log `[Dio Discovery] found '…' at …` on each successful response. If the host logs the ready line but never the "got ping" line, the broadcast isn't reaching it — go back to step 1.

## Project layout

```text
Assets/
  Dio/
    Scenes/             Main.unity (built by editor menu)
    Scripts/
      Net/              DioNetworkManager, DioNetworkDiscovery, DioPlayer, messages
      UI/               MainMenuController + Editor scene builder
      Player/           SphericalGravity, ArcadeCarController, DioCar + Editor prefab builder
      Camera/           RaceCamera (Chase / Bumper / Hood)
      Level/            LevelData, GeodesicUtil, RaceBootstrap, ITrackSurface
      Level/Editor/     Level editor window (orbit + click-to-place points)
      Level/Obstacles/  Static / kinematic / projectile obstacle base + 6 concrete
      Powerups/         States, holder, box, registry, bootstrap, 10 effects
      Common/           ColorPalette, LocalPrefs, Billboard
    Prefabs/            Car, DioPlayer, DioNetworkManager, Powerups/*  (built by editor menu)
    Levels/             *.asset LevelData files
    UI/Svg/             Flag.svg, Planet.svg, Bolt.svg
  Mirror/               vendored, NOT committed
plan.md                 design, networking architecture, powerup catalog, audit
```

## Editor menus

See [plan.md §11.3](./plan.md) for the full reference. Quick version:

- `Tools → Dio → Build → All` — rebuilds every prefab + the Main scene from scratch
- `Tools → Dio → New Level` — creates a fresh `LevelData` and opens the editor on it
- `Tools → Dio → Open Level Editor` — orbit a sphere, click to add waypoints, drag to move

## Status

This is the **M1 milestone**: scaffold, networking core, server-authoritative arcade car physics on a sphere, three-mode camera, level-editor skeleton, 10 of 15 designed powerups, all networked obstacles. The track itself is a placeholder geodesic line — procedural track-mesh generation is M2. See [plan.md §11](./plan.md) for the full audit, known gaps, and roadmap.
