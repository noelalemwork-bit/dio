# Dio — Systems Runbook

A working manual for everyone (humans and AI assistants) extending the project. Read this once, then keep it open while you work.

For the high-level design + open-questions audit see [plan.md](./plan.md).
For the next-session bootstrap (MCP servers, conventions, tech stack pins) see [CLAUDE.md](./CLAUDE.md).
This file is the deep dive: what each subsystem is, how it talks to the others, and the common pitfalls when extending it.

---

## 1. What Dio is, in 60 seconds

Multiplayer arcade racer in Unity 6 (URP). Tracks are spherical-cubic-bezier ribbons drawn on a stylised low-poly planet (the "Globe", `Assets/3d world/world.fbx`). Networking is **Mirror** (vendored in `Assets/Mirror/`, gitignored). Cars are client-authoritative under Mirror's `NetworkTransformReliable` so local input feels lag-free; the server polls car positions for race state. UI + VFX are SVG-driven via `com.unity.vectorgraphics`. Everything that's regenerable (prefabs, default assets, the Main scene) is built by `Tools → Dio → Build → All`.

---

## 2. System map

```
                  +--------------------------+
                  |       LevelData (SO)     |
                  |  - circumference         |
                  |  - trackWidth, trackRatio|
                  |  - List<TrackPoint>      |
                  |     directionFromCenter  |
                  |     yOffset, bank        |
                  |     in/outHandle (shared |
                  |        across edges)     |
                  |     next: List<int>      |
                  +-----+--------+-----------+
                        |        |
   authored by          |        |   consumed by
                        v        v
        +-------------------+   +-----------------------+
        | LevelEditorWindow |   | TrackBuilder (static) |
        |  - PRU rendering  |   |  Build()  Guards()    |
        |  - drag anchors   |   |  SpawnPad() SampleAt()|
        |  - shift = yOffset|   |  ArcLengthOf()        |
        |  - rmb = fork     |   +-----------+-----------+
        +---------+---------+               |
                  |                         | meshes
                  | GlobeRaycaster          v
                  +-----------------> +----------------+
                                      | RaceBootstrap  |
                                      | (per-client)   |
                                      |  - GlobeFactory|
                                      |  - Track GO    |
                                      |  - Guards GO   |
                                      |  - SpawnPad GO |
                                      +--------+-------+
                                               |
                                  + ----------+ - - - - - - +
                                  |                          |
                           +------v-------+        +---------v---------+
                           | Mirror NetMgr |       | Local play state  |
                           | DioNetworkMgr |       | RaceCamera, HUD,  |
                           +------+--------+       | minimap, controls |
                                  |                +-------------------+
                                  |
                          +-------v-------+
                          |  Server-side  |
                          |  race loop    |
                          |  - countdown  |
                          |  - win polling|
                          |  - cleanup    |
                          +---------------+
```

The flow you should keep in your head:
**LevelData → TrackBuilder → mesh GameObjects → physics + rendering → NetworkManager spawns cars on the spawn pad → race loop polls → win → cleanup destroys the visual scene → back to lobby.**

---

## 3. Level authoring (the Editor)

`Tools → Dio → Open Level Editor` (`LevelEditorWindow`).

### Loading a level

`OnEnable` → `EnsureSnapshots()` reads `Assets/3d world/world.fbx` via `GlobeFactory.LoadSourceAsset` and builds two things:
1. A flat `List<MeshSnapshot>` of every nested submesh outside the `Environment` subtree, each tagged with a world matrix that includes the level's autofit scale (`circumference → mean vertex radius`).
2. A `GlobeRaycaster` over those snapshots — the editor's authoritative "where is the ground at this direction" oracle.

Both are cached on `(_cachedSource, _cachedCircumference)`; touching circumference invalidates and rebuilds.

### Anchor + handle interactions

Anchors store `directionFromCenter` (unit vector from origin), `yOffset` (delta above terrain), and shared `inHandle` / `outHandle` (unit-vector bezier controls used by every incoming / outgoing edge respectively).

| Gesture | Action |
| --- | --- |
| LMB drag empty space | orbit camera (quaternion-based, no gimbal lock) |
| RMB drag empty space (or on start/finish) | orbit camera |
| Wheel | zoom |
| LMB drag anchor | move anchor along the geodesic the cursor is over (`PickDirection` does mesh raycast first, then "closest direction from camera ray" fallback) |
| LMB drag handle dot | move bezier handle; partner handle on same anchor reflects through tangent plane → C1 continuity |
| Shift held | yOffset arrows appear at every front-hemisphere anchor; LMB drag an anchor while held = drag yOffset |
| Ctrl + LMB on endpoint | append (or prepend, if start) a new anchor and drag it |
| **RMB drag on a non-endpoint anchor** | **fork — clones the anchor at the drag location, adds a new outgoing edge whose start handle is the source's existing outHandle** |
| Del while dragging anchor | remove that anchor (and repair every `next` list referencing it) |

`PickDirection` is deliberately **zoom-independent**. Sphere-vs-ray intersection breaks when the camera gets close to the planet silhouette; mesh raycast against the actual Globe always works. If the cursor is in space outside the silhouette we fall back to "closest unit direction the camera ray approaches the origin from" so dragging an anchor to a back-hemisphere position works smoothly.

### What the editor renders

`DrawScene` runs three passes inside a `PreviewRenderUtility`:

1. **Globe** — every collected snapshot drawn with its FBX-authored material. PRU can't bind URP shaders, so designers should keep submesh materials on built-in Standard / Unlit. Missing-material fail loud (throws) so you see broken assets immediately.
2. **Track ribbon preview** — the *real* `TrackBuilder.Build` mesh, low-res, plus outer-skirt-only guard rails (`GuardCrossSection.OuterSkirtOnly`). What you see is what you race on, plus a visible elevation wall on either side so lifted sections read on-screen without a solid wall hiding the surface.
3. **Handles overlay** — anchors, handle dots, bezier polylines, yOffset arrows. Drawn screen-space on top, no depth test, front-hemisphere only.

Every visual element that depends on height (anchor dot, handle dot, leash, bezier polyline, yOffset arrow) goes through `level.ResolveHeight(i, _raycaster)`. Handle dots live at the **anchor's** resolved height (not the handle's own raycast) — they don't drift when the anchor's yOffset changes.

### `EnsureLinearChainNext`: graph migration

A level authored before branching has only `points: List<TrackPoint>` and walks linearly i → i+1. To keep that valid under the new graph model, `LevelData.EnsureLinearChainNext()` runs whenever a level loads or the editor mutates one:

* If **no** point has any `next` entries yet → fill in `next = [i+1]` for every non-finish point. Linear chain restored.
* If **any** point already has `next` entries → graph mode is in effect; just normalise null lists to empty and stop. The migration won't accidentally rewire a fork.

### Right-click fork: data model

`BeginForkFromAnchor(sourceIdx)`:
1. Append a clone of the source anchor at the end of `points` (same direction + yOffset).
2. Append the new anchor's index to `points[sourceIdx].next`.
3. Source's `outHandle` is **untouched** — it's now shared between the original outgoing edge and the new one.
4. Drag handler updates only the new anchor's `directionFromCenter` and its own `inHandle` (geodesic 2/3 between source and new). C1 across the fork is automatic because both branches start with identical tangent (= shared outHandle).

This is the foundation for arbitrarily nested forks. A subsequent fork from any interior anchor adds another `next` entry, and so on.

---

## 4. Track generation pipeline

`Dio.Level.TrackBuilder` is a pure static utility — no GameObjects, no scene state, just `LevelData` + optional `GlobeRaycaster` → `Mesh`. The same code runs in the editor preview, in `RaceBootstrap`, and in the minimap render.

### `Build(level, raycaster, surfaceLift)`

For every `(from, to)` edge yielded by `level.EnumerateEdges()`:

1. Sample the spherical cubic bezier `SegmentsPerArc + 1` times.
2. At each sample compute:
   * `dir` — unit direction from origin
   * `tangent` — finite-difference along the bezier
   * `width = cross(tangent, dir)` — lateral axis in the tangent plane
   * `surfaceR = raycaster.ResolveSurfaceRadius(dir)` — actual distance to the Globe along that direction (or `level.NominalRadius` if no raycaster)
   * `yOffset = lerp(from.yOffset, to.yOffset, t)` — designer-authored lift
   * `radial = surfaceR + surfaceLift + yOffset` — final radial distance

3. Emit `verts: vL = dir*radial + width*halfW`, `vR = dir*radial - width*halfW`.
4. Stitch triangles to the previous ring.

`halfW = level.EffectiveTrackWidth * 0.5` where `EffectiveTrackWidth = trackWidth * trackRatio`. Tightening `trackRatio` makes the entire ribbon proportionally narrower without redoing any per-anchor math.

### Seam-free joints

Any anchor where two edges meet (linear chain neighbour, fork, or join) is cached in a `Dictionary<int, (int leftIdx, int rightIdx)>` the first time it's emitted. Subsequent edges sharing the anchor reuse those vert indices verbatim — same world position, same triangle stitching, no floating-point drift between the segments. The cache key is the anchor index alone because the lateral frame at any anchor is uniquely determined by its shared `outHandle` (forks) or `inHandle` (joins).

### `BuildGuards(level, raycaster, GuardOptions)`

`GuardOptions` controls the cross-section:

| Field | Purpose |
| --- | --- |
| `crossSection = Full` | skirt + wall + lip (default; for collision) |
| `crossSection = OuterSkirtOnly` | just the outward skirt — editor preview, shows elevation without occluding the track |
| `wallHeight`, `lipOutward` | wall geometry above the surface |
| `floorAtSurface = true` | skirt bottom anchored at `surface − floorPad`, regardless of yOffset → an elevated track shows its full height in the rail |
| `floorAtSurface = false`, `skirtDepth` | classic mode: skirt bottom = `anchor − skirtDepth` |
| `outwardOffset` | tiny lateral offset from the ribbon edge so the rail doesn't z-fight |
| `addEndcaps` | close the chain at start/finish with a perpendicular wall (Z section swept across the track width) |

### Fork / join guard merging

Naive guard emission would have side-by-side rails overlap any time multiple edges share an anchor. `ComputeForkJoinSkips` runs once per build:

1. Group edges by their shared anchor (outgoing for forks, incoming for joins).
2. For each multi-edge anchor, sort the neighbour list by signed angle around the anchor's surface normal — gives left-to-right ordering of the branches.
3. For every adjacent pair (eA, eB) of branches, walk samples and find the first sample index `s` where eA's right edge and eB's left edge are at least `EffectiveTrackWidth` apart. That index is recorded as a skip count for the inner rail of each.
4. The leftmost branch's left rail and the rightmost branch's right rail are never affected — they remain the outer envelope of the fork.

The main loop then calls `EmitGuardStrip(...)` twice per edge (once per side) with explicit `[sStart, sEnd]` ranges and emits a fresh, self-contained strip — no triangle stitches across skipped samples.

### `BuildSpawnPad(level, length, raycaster)`

A pure great-circle arc extending **backward** from the start anchor along the start tangent at t=0. The whole pad sits at uniform radial Y = `startSurfaceR + surfaceLift + startYOffset` — local terrain variation is intentionally ignored, so cars on the back row stand at the same height as the polesitter and the seam between the pad and the front of the live ribbon is invisible.

A back wall (Z section) is swept across the track width at the far end of the pad to catch reverse-into-space cars. Like the pad it's anchored at the start anchor's resolved height.

### Sampling helpers

`SampleAt(level, t, raycaster)` returns the world position at parametric t over the linear-chain order of edges (good enough for HUD position, finish detection, and powerup placement on the canonical path; branched graphs collapse to enumeration order here).
`TangentAt(level, t)` returns the unit tangent for car heading on the start line.
`TotalArcLength(level, raycaster)` walks every sampled edge and accumulates world-space distance — used by the HUD position panel and by `DioNetworkManager` when broadcasting race progress.
`ArcLengthOf(level, worldPos, planetCenter, raycaster)` projects an arbitrary world-space point onto the chain and returns the arc length to the projection — used for win detection on the server.

---

## 5. Globe + raycaster

### What the Globe is

`world.fbx` (`Assets/3d world/`) — an authored stylised planet with terrain detail. Multiple submeshes nested at varying depth; one optional `Environment` child holding visual-only props (clouds, ambient set dressing). Everything inside `Environment` is **excluded** from collision and from raycast queries; everything else IS the planet.

### Editor pipeline

`GlobeFactory.LoadSourceAsset()` returns the FBX in editor (via `AssetDatabase`) and falls back to `Resources.Load<GameObject>("Globe")` at runtime. `CollectSnapshots(source, scale)` walks the hierarchy and yields `(mesh, worldMatrix, materials)` triples; `GlobeRaycaster(snapshots, fallbackRadius)` is the software raycast service.

### Runtime pipeline

`GlobeFactory.Spawn(parent, circumference)` instantiates the FBX (or the baked Globe.prefab in built players), scales it via `ComputeAutofitScale`, and adds a `MeshCollider` to every nested mesh outside `Environment`. The result is a single `GameObject` with the Globe parented under it, ready for cars to drive on. `RaceBootstrap.EnsurePlanet` calls into this; it also builds a fresh `GlobeRaycaster` from the spawned snapshots (so the Track / Guards / SpawnPad meshes built next all consult the same surface).

### Autofit scale

`ComputeAutofitScale(source, circumference) = (circumference / 2π) / mean(|v| over all surface vertices)`.

Mean vertex magnitude is much more representative than max-extent for an irregular mesh: a single tall mountain doesn't pull the scale down, and the result lines up with what designers intuitively call "the planet radius". A perfectly spherical asset of native radius R will autofit to `circumference / (2πR)` — i.e. exactly `1.0` when `circumference = 2πR`.

### Build menu

`Tools → Dio → Build → Globe Prefab` (`MainSceneBuilder.BuildGlobePrefab`) instantiates the FBX, attaches mesh colliders to every leaf outside `Environment`, and saves to `Assets/Dio/Resources/Globe.prefab`. `BuildAll` invokes it. Required for built players (where `AssetDatabase` is unavailable); harmless duplicate work in the editor.

---

## 6. Networking + game lifecycle

### Authority model

Cars are **client-authoritative** under `NetworkTransformReliable.syncDirection = ClientToServer`. The local owner runs full physics (`isKinematic = false`) so input is lag-free; remote peers + the server hold each car kinematic and let `NetworkTransform.OnDeserialize` drive `transform`. Server still owns triggers (powerup pickup, finish detection) because the kinematic body still has a `Rigidbody`.

This was a deliberate switch from a server-authoritative attempt earlier in development: server sim added a full RTT of input lag and joined-clients couldn't accelerate. Project goal is "perfect physics, no latency on LAN", and client authority is the only way to get there without writing a custom prediction layer.

### Race lifecycle

```
[Lobby browse / host]  →  [Vote on level]  →  [RaceStartMessage broadcast]
                                                     │
                                                     v
[RaceBootstrap.BuildVisualScene on every client]
   - GlobeFactory.Spawn  (mesh colliders attached)
   - TrackBuilder.Build / Guards / SpawnPad
   - Local race camera attaches when the local DioCar appears
                                                     │
                                                     v
[ServerStartRace → 4-second countdown via RaceIntro cinematic]
                                                     │
                                                     v
[GO! — input unlocked, race active, server polls progressArc 1×/sec]
                                                     │
                                                     v
[First car crosses TotalArcLength → RaceWonMessage broadcast]
                                                     │
                                                     v
[ServerCleanupRace destroys per-race networked objects after 5.5s]
[Each client RaceBootstrap.OnRaceWon schedules CleanupLocalScene]
                                                     │
                                                     v
[Lobby restored — visual scene torn down, network objects gone]
```

`DioNetworkManager.ServerStartRace` is the choreographer:
1. Picks the level (host vote).
2. Spawns server-authoritative obstacle objects (powerup boxes per `LevelData`).
3. Spawns one `DioCar` per connected client, parented to the spawn pad with positions / rotations from the start tangent.
4. Sends `RaceStartMessage` so every client builds the visual scene locally.
5. Waits 4 s for the cinematic intro, then unlocks input.

`OnRaceWon` runs on the server; clients receive `RaceWonMessage` (with a `cleanupDelay`) and schedule the local visual teardown. Server's `ServerCleanupRace` then destroys the networked race objects via `NetworkServer.Destroy` so the lobby comes back clean.

### Discovery (LAN)

`DioNetworkDiscovery` extends Mirror's `NetworkDiscoveryBase`. Standard implementation only broadcasts on the default adapter — fails under VPNs / WARP. We enumerate every "up" IPv4 NIC and broadcast to each subnet's specific broadcast address (`MultiAdapterBroadcast`). Direct-IP fallback exists in the browser panel; last-used IP is cached in `LocalPrefs.LastDirectIp`.

---

## 7. SVG / UI / VFX

### Where SVGs come from

Every UI icon, marker, and most VFX shapes start as a `.svg` file in `Assets/Dio/UI/Svg/`. We ship them as **plain SVG** (no rasterisation step) so the same asset scales cleanly from minimap-marker size up to fullscreen winner banner.

`com.unity.vectorgraphics` (UPM package) is the importer. After import each SVG produces:
* A `Sprite` (vector-graphics-aware, tessellated on demand) — used by `Image` / `RawImage` UI components.
* Shape data we can also feed into VFX particles for stylised burst effects.

If `com.unity.vectorgraphics` isn't installed, SVGs import as plain `TextAsset` and `SvgIconLoader.VectorGraphicsAvailable` returns false. UI builders fall back to a flat `Image` with a placeholder colour — the layout still works, just without the vector art. **Prefer never relying on this fallback** — keep the package installed.

### Where SVGs are used

| File | Used by |
| --- | --- |
| `winner_banner.svg` | victory popup |
| `mystery_box*.svg` | `MysteryBox` powerup VFX + minimap marker |
| `chevron.svg` | UI navigation buttons |
| `host_join.svg` | main menu lobby buttons |
| `arrow_left/right.svg` | colour picker carousel |
| `minimap_circle.png` (PNG) | minimap mask — vector art is overkill for a circle so this one is rasterised |

`SvgIconLoader.LoadSvg(name)` is the single entry point. It resolves the path, loads via `AssetDatabase` (editor) or `Resources.Load` (runtime), and returns a `Sprite`. Always call this — never load by raw path.

### Re-importing after edits

`Tools → Dio → Build → Re-import SVGs` forces `com.unity.vectorgraphics` to re-process the directory. `BuildAll` runs this first so any tweaks made between builds propagate.

---

## 8. Unity UI layout — the "HTML/CSS in Unity" guide

Designers and AI assistants both keep reaching for HTML/CSS metaphors when laying out Unity UI. Here's the translation that actually works.

### 8.1 The mental model

Unity's `Canvas` is your `<body>`. Children are nested `<div>`s. There is no flow layout — every child is **absolutely positioned** unless you wrap it in a layout group. Anchors are how you say "this position is responsive". RectTransform replaces `top/left/width/height`.

### 8.2 The Canvas Scaler — set this once and forget it

Every Canvas in this project uses `Canvas Scaler` set to:
* **UI Scale Mode**: `Scale With Screen Size`
* **Reference Resolution**: `1920 × 1080`
* **Match**: `0.5` (split width/height equally — phones in landscape and 4:3 monitors both look acceptable)
* **Reference Pixels Per Unit**: `100`

*Nothing else.* Don't use Constant Pixel Size unless you genuinely want pixel-fixed UI (we don't).

This is the equivalent of `html { font-size: 16px; }` + a `vw`-based root — every `RectTransform.sizeDelta` value you write is "pixels at 1920×1080" and scales linearly with the actual screen.

### 8.3 Anchors are flexbox / grid-area, not coordinates

CSS muscle memory: "I'll put it at top:20px, right:30px". In Unity:

| You want… | Set anchors to | Set pivot to | sizeDelta means |
| --- | --- | --- | --- |
| centered, fixed size (a button) | `(0.5, 0.5) → (0.5, 0.5)` | `(0.5, 0.5)` | actual width/height |
| top-right, fixed size (a HUD card) | `(1, 1) → (1, 1)` | `(1, 1)` | actual width/height |
| stretch full width, top fixed height | `(0, 1) → (1, 1)` | `(0.5, 1)` | (extra width, height) — use `offsetMin`/`offsetMax` for left/right margins |
| fully stretch (background) | `(0, 0) → (1, 1)` | `(0.5, 0.5)` | (extra width, extra height) usually `(0,0)` |
| split column 1/3 (sidebar) | `(0, 0) → (0.333, 1)` | `(0.5, 0.5)` | `(0, 0)` for full-fit |

Rule of thumb: **set the anchor's min and max to the same value when the element should be a fixed size relative to the parent's corner; spread them apart when you want responsive scaling**. This is the equivalent of `width: 200px` vs `width: 30%`.

### 8.4 Layout groups = flexbox / grid

* `Vertical Layout Group` ≈ `display: flex; flex-direction: column;`
* `Horizontal Layout Group` ≈ `display: flex; flex-direction: row;`
* `Grid Layout Group` ≈ `display: grid; grid-template-columns: repeat(N, ...);`

Each child of a layout group is laid out **without** their RectTransform anchors mattering — the layout group rewrites their position. Set the child's `Layout Element` component to control flex behaviour:

| Layout Element field | CSS analogue |
| --- | --- |
| `Min Width / Height` | `min-width / min-height` |
| `Preferred Width / Height` | `width / height` (the default size before flex) |
| `Flexible Width / Height` | `flex-grow` (fraction of leftover space) |
| `Ignore Layout` | `position: absolute` for that one child |

### 8.5 Auto-sizing (overflow control)

CSS gives you `box-sizing: border-box; min-height: 0;` to prevent overflow. Unity gives you:

* `Content Size Fitter` on a parent — make its size driven by its layout group's preferred size (= "shrink-wrap to content").
* `Layout Element.Min*` / `Preferred*` on children — the constraints the parent shrink-wrap respects.
* For text: `TextMeshPro.enableAutoSizing = true` + a `Min Font Size / Max Font Size` range. This is the closest equivalent of `font-size: clamp(...)`.
* For wrapping: TMP `textWrappingMode = Normal` and `overflowMode = Truncate` (or `Ellipsis`).

Common "why is my text overflowing?" sequence:
1. Is the parent a layout group? If not, add one or use a `Content Size Fitter`.
2. Is the text's `Layout Element.Preferred Height` reasonable? Don't leave it at 0 — TMP can't compute it from a stretched RectTransform.
3. Is `enableWordWrapping` (now `textWrappingMode`) on?
4. Is the parent's RectTransform tall enough? If anchored to `(0, 1) → (1, 1)`, stretching only horizontally, the height is fixed by `sizeDelta.y` — TMP will overflow vertically out of the stretch.

### 8.6 SVG sprites in UI

`Image` ≈ `<img>`. Drop the SVG-derived sprite onto its `source image` slot and you're done — `com.unity.vectorgraphics` handles tessellation and the sprite scales cleanly to any size.

For markers / icons that need to recolour on hover, set the `Image.color` rather than authoring multiple SVGs. The vector tessellation respects vertex colours; tinting is free.

For buttons with text + icon, use a `Horizontal Layout Group` parent with two children (`Image` + `TextMeshProUGUI`), and put a `Layout Element.Preferred Width` on the icon so it doesn't stretch.

### 8.7 Common patterns we use in Dio

* **Full-screen overlay** (countdown, winner banner): a Canvas Scaler at 1920×1080, a single child `RectTransform` anchored `(0,0)→(1,1)`, content centered with sub-anchors at `(0.5, 0.5)`. See `RaceIntro` and the winner popup in `RaceHUD`.
* **HUD card pinned to a corner**: anchor + pivot in the same corner (e.g. `(1,1) → (1,1)`, pivot `(1,1)`) + fixed `sizeDelta`. See the ping HUD in `RaceHUD`.
* **Responsive grid (lobby browser rows)**: parent has `Vertical Layout Group` + `Content Size Fitter` (Vertical = Preferred). Each row child has `Layout Element.Preferred Height = 48`, `Flexible Width = 1`. Adding rows = adding children; the layout group does the rest.
* **Centered control with vertical stack**: nested `Vertical Layout Group` parents — outer anchored full-screen, inner with `Content Size Fitter` + `Padding/Spacing` for a CSS `gap`-like rhythm.

### 8.8 The cardinal sins

* Hard-coded screen pixel positions (`anchoredPosition = new Vector2(420, 260)`). Use anchors + offsets instead.
* Mixing layout groups with manual position changes — once you set a layout group on a parent, only `Layout Element` controls the children. Setting `RectTransform.anchoredPosition` does nothing.
* Forgetting `LayoutRebuilder.ForceRebuildLayoutImmediate` after programmatically changing children. Layout groups update on the *next frame* by default; if you read sizes the same frame you'll get stale values.
* `Canvas Scaler.UI Scale Mode = Constant Pixel Size` for game UI. Never. Pick one Scaler config per-game, stick to it.
* Loading SVGs with `Resources.Load<Sprite>` directly. Always go through `SvgIconLoader.LoadSvg` so the editor / runtime / fallback paths are consistent.

---

## 9. Recipes

### 9.1 Add a new track Level

1. `Tools → Dio → New Level` — creates `Assets/Dio/Levels/Level.asset` with two anchors and default handles. Bumps the asset name automatically.
2. Drag anchors with LMB. Hold Shift to lift sections off the surface. Hold Ctrl + click an endpoint to extend.
3. Set `Track Width` (canonical) and `Track Width Ratio` (per-level multiplier). Effective width is shown live below.
4. RMB-drag from any interior anchor to fork — useful for shortcuts or alternative loops.
5. Re-run `Tools → Dio → Build → All` afterwards if you want the network manager's level catalog to pick up the new asset (it loads every `LevelData` under `Assets/Dio/Levels`).

### 9.2 Add a new powerup

1. Create a `MonoBehaviour` (extending `BasePowerupObstacle` if it's a track-spawned obstacle, or implementing `IPowerupEffect` if it's a held active).
2. Add the prefab build step to `PowerupPrefabBuilder.BuildAll`.
3. Add it to `DioNetworkManager.spawnPrefabs` (also done automatically by `BuildNetworkManagerPrefab` from the `Assets/Dio/Prefabs/Powerups/` directory).
4. Wire the SVG icon in `SvgIconLoader` and add a `PowerupSlotIcon` mapping.
5. `Tools → Dio → Build → Powerup Prefabs` to regenerate. Then `Build → Network Manager Prefab` so it gets caught up.

### 9.3 Swap the planet mesh

1. Drop the new FBX over `Assets/3d world/world.fbx` (keeping the path so `GlobeFactory.LoadSourceAsset` finds it). If the new mesh has visual-only props, parent them under a child named `Environment` so they're excluded from collision and from raycast queries.
2. Run `Tools → Dio → Build → Globe Prefab` to refresh `Assets/Dio/Resources/Globe.prefab`.
3. Open the level editor and tweak `Circumference` if the new mesh's mean radius is significantly different — no need to autofit by hand, the math is `mean(|v|)` over the new mesh.
4. Test in Play.

---

## 10. Common pitfalls

* **Editor preview shows a faceted blob, not the FBX**: PreviewRenderUtility couldn't bind the material. Check the FBX submeshes use Standard / Unlit, not URP/Lit. PRU can't run URP shaders — that's a Unity limitation, not our bug.
* **Track ribbon flickers on the surface**: bump `surfaceLift` in the `TrackBuilder.Build` call, or check that the Globe collider isn't overshooting the visible mesh (sometimes happens with non-convex auto-bake).
* **Anchor drag jumps wildly when zoomed in**: ensure `_raycaster` is non-null in the editor (it's set by `EnsureSnapshots` on every OnGUI). If it's null, `PickDirection` falls back to "closest direction from ray", which is stable but less precise on the back hemisphere.
* **Cars on the back row sit lower than the polesitter**: the spawn pad uses uniform Y (= start anchor's resolved height), independent of the local terrain at the back rows. If they're STILL different the issue is the start tangent's geodesic axis — verify `TangentAt(level, 0f)` returns a unit vector roughly along the track's forward direction.
* **`level.planetRadius` warnings after upgrading**: that's the legacy compat property — reads still work, writes still work, but new code should use `level.circumference` and `level.NominalRadius` directly.
* **Forks render but rails overlap badly**: `ComputeForkJoinSkips` failed to find a divergence sample within `SegmentsPerArc`. Either the two branches share their full shape (same handles + same endpoint), or one is much shorter than the bezier resolution. Move the new endpoint farther away from the source.
* **`Tools → Dio → Build → All` errors with TMP missing**: `Window → TextMeshPro → Import TMP Essential Resources` first. One-time per fresh clone.

---

## 11. Where to look

| Concern | First file |
| --- | --- |
| Track shape | [Assets/Dio/Scripts/Level/LevelData.cs](Assets/Dio/Scripts/Level/LevelData.cs) |
| Track mesh | [Assets/Dio/Scripts/Level/TrackBuilder.cs](Assets/Dio/Scripts/Level/TrackBuilder.cs) |
| Editor | [Assets/Dio/Scripts/Level/Editor/LevelEditorWindow.cs](Assets/Dio/Scripts/Level/Editor/LevelEditorWindow.cs) |
| Globe + raycaster | [Assets/Dio/Scripts/Level/Globe.cs](Assets/Dio/Scripts/Level/Globe.cs) |
| Race scene assembly | [Assets/Dio/Scripts/Level/RaceBootstrap.cs](Assets/Dio/Scripts/Level/RaceBootstrap.cs) |
| Networking + lifecycle | [Assets/Dio/Scripts/Net/DioNetworkManager.cs](Assets/Dio/Scripts/Net/DioNetworkManager.cs) |
| Discovery | [Assets/Dio/Scripts/Net/DioNetworkDiscovery.cs](Assets/Dio/Scripts/Net/DioNetworkDiscovery.cs) |
| Cars | [Assets/Dio/Scripts/Player/DioCar.cs](Assets/Dio/Scripts/Player/DioCar.cs) |
| HUD | [Assets/Dio/Scripts/UI/RaceHUD.cs](Assets/Dio/Scripts/UI/RaceHUD.cs) |
| Build menus | [Assets/Dio/Scripts/UI/Editor/MainSceneBuilder.cs](Assets/Dio/Scripts/UI/Editor/MainSceneBuilder.cs) |
| SVG loading | [Assets/Dio/Scripts/UI/SvgIconLoader.cs](Assets/Dio/Scripts/UI/SvgIconLoader.cs) |
