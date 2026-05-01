using Mirror;
using UnityEngine;
using Dio.Net;
using Dio.Player;
using Dio.CameraRig;

namespace Dio.Level
{
    /// Client/host-side: builds the visible planet + procedural track strip and
    /// attaches the camera to the local player's car when it spawns. The car
    /// itself is server-spawned by DioNetworkManager.
    public class RaceBootstrap : MonoBehaviour
    {
        /// Set whenever any RaceBootstrap finishes EnsurePlanet. SphericalGravity
        /// uses this for lookup so we don't depend on an unregistered "Planet" tag.
        public static GameObject CurrentPlanet { get; private set; }

        /// The active LevelData for the running race. Used by the HUD's position
        /// tracker and by `TrackBuilder.ArcLengthOf` to project car positions
        /// onto the geodesic chain.
        public static LevelData CurrentLevel { get; private set; }

        /// The procedural track-strip GameObject built by EnsureTrack. Kept
        /// public so the minimap renderer can throw it (and the planet) onto a
        /// dedicated layer.
        public static GameObject CurrentTrack { get; private set; }

        /// Software raycaster against the spawned Globe. Public so the network
        /// manager can resolve correct surface heights at car spawn — without
        /// it we'd be using NominalRadius and cars would clip through the
        /// terrain on uneven planets.
        public static GlobeRaycaster CurrentRaycaster { get; private set; }

        /// Layer index reserved for "things visible to the minimap camera" —
        /// the planet and the track strip. Other things (cars, obstacles, UI)
        /// stay off this layer so the minimap stays clean.
        public const int MinimapLayer = 8;

        [Tooltip("Default level used if no race-start message has been received yet.")]
        public LevelData defaultLevel;

        [Tooltip("If true, build the visible scene immediately on Start (for solo testing without networking).")]
        public bool autoStartInEditor = false;

        GameObject _planet;
        GameObject _track;
        GameObject _guards;
        GameObject _spawnPad;
        GameObject _checkpoints;
        Camera _cam;
        GlobeRaycaster _raycaster;

        void Awake()
        {
            DioNetworkManager.OnRaceStarted += OnRaceStarted;
            DioNetworkManager.OnRaceWon     += OnRaceWon;
        }

        void OnDestroy()
        {
            DioNetworkManager.OnRaceStarted -= OnRaceStarted;
            DioNetworkManager.OnRaceWon     -= OnRaceWon;
        }

        void Start() { if (autoStartInEditor) BuildVisualScene(defaultLevel); }

        // Pull the chosen level out of the start message. The server fills
        // in the FULL serialized payload, so every peer gets exactly the
        // host's pick — no asset-resolution dance, no stale local catalog
        // index. Server-side path uses the live ScriptableObject directly
        // (which IS the same instance the runtime catalog stored).
        void OnRaceStarted(bool _, RaceStartMessage msg)
        {
            var mgr = DioNetworkManager.Instance;
            Dio.Level.LevelData lvl = null;
            // Host: server-resolved active level is the authoritative copy.
            if (Mirror.NetworkServer.active && mgr != null && mgr.ActiveLevelInternal != null)
                lvl = mgr.ActiveLevelInternal;
            // Otherwise consume the deserialized payload from the message.
            if (lvl == null) lvl = msg.level;

            if (lvl == null || !lvl.HasMinimum)
            {
                Debug.LogWarning($"[RaceBootstrap] RaceStartMessage carried no usable level (owner={msg.levelOwnerConnId} name='{msg.levelDisplayName}'). Falling back to defaultLevel.");
                lvl = defaultLevel;
            }
            else
            {
                Debug.Log($"[RaceBootstrap] resolved race level '{lvl.name}' (owner={msg.levelOwnerConnId}, points={lvl.points.Count})");
            }
            ApplyServerRolledSkybox(msg.skyboxIndex);
            BuildVisualScene(lvl);
        }

        // Swap RenderSettings.skybox to the server's chosen library entry.
        // SkyboxPlayerUpBinder watches RenderSettings.skybox and re-clones
        // its runtime material when the source changes, so the spherical
        // _PlayerUp pipeline keeps working after the switch.
        static void ApplyServerRolledSkybox(int skyboxIndex)
        {
            if (skyboxIndex < 0) return;
            var mat = Dio.Common.SkyboxLibrary.GetOrFallback(skyboxIndex);
            if (mat == null) return;
            RenderSettings.skybox = mat;
            // Refresh ambient lighting from the new sky so the world isn't
            // lit by the previous race's mood.
            DynamicGI.UpdateEnvironment();
            Debug.Log($"[RaceBootstrap] skybox set to library index {skyboxIndex} ('{mat.name}')");
        }

        void OnRaceWon(RaceWonMessage msg)
        {
            // Schedule local-scene teardown to align with the server's cleanup.
            Invoke(nameof(CleanupLocalScene), Mathf.Max(0.1f, msg.cleanupDelay));
        }

        // Destroys the planet + track + camera attachment. Per-car NetworkIdentities
        // are torn down by NetworkServer.Destroy on the server side, so we don't
        // touch them here.
        public void CleanupLocalScene()
        {
            if (_checkpoints != null) { Destroy(_checkpoints); _checkpoints = null; }
            if (_spawnPad != null) { Destroy(_spawnPad); _spawnPad = null; }
            if (_guards != null) { Destroy(_guards); _guards = null; }
            if (_track != null) { Destroy(_track); _track = null; }
            if (_planet != null) { Destroy(_planet); _planet = null; }
            CurrentPlanet = null;
            CurrentLevel = null;
            CurrentTrack = null;
            CurrentRaycaster = null;

            // Detach the race camera so it stops following a destroyed car.
            if (_cam != null)
            {
                var rc = _cam.GetComponent<RaceCamera>();
                if (rc != null)
                {
                    rc.target = null;
                    rc.car = null;
                    rc.gravity = null;
                }
            }
        }

        void Update()
        {
            if (_planet == null) return;
            if (_cam != null && _cam.GetComponent<RaceCamera>() != null && _cam.GetComponent<RaceCamera>().target != null) return;

            DioCar local = null;
            if (NetworkClient.localPlayer != null)
            {
                foreach (var ni in NetworkClient.spawned.Values)
                {
                    var car = ni != null ? ni.GetComponent<DioCar>() : null;
                    if (car == null) continue;
                    if (car.isLocalPlayer) { local = car; break; }
                    if (car.isOwned) { local = car; break; }
                }
            }
            if (local != null) AttachCameraTo(local.transform);
        }

        public void BuildVisualScene(LevelData level)
        {
            if (level == null || !level.HasMinimum)
            {
                Debug.LogWarning("[Dio] No valid level to bootstrap. Add at least a start + finish.");
                return;
            }

            Debug.Log($"[RaceBootstrap] BuildVisualScene level='{level.name}' circumference={level.circumference}");
            CurrentLevel = level;
            EnsurePlanet(level);
            // Build a raycaster from the spawned globe so every height-aware
            // builder (track, guards, spawn pad) hugs the same surface that
            // the cars will physically collide with.
            _raycaster = BuildRaycaster(level);
            EnsureTrack(level);
            EnsureGuards(level);
            EnsureSpawnPad(level);
            EnsureCheckpoints(level);
        }

        // Spawn one CheckpointTrigger per anchor on the SERVER (the host).
        // Pure clients don't need them — they read crossedCheckpoints off
        // each DioCar via SyncList. Trigger radius = effective track width
        // plus a small bonus so a creative line / jump still passes through.
        void EnsureCheckpoints(LevelData level)
        {
            if (_checkpoints != null) Destroy(_checkpoints);
            // Triggers exist server-side only; clients don't simulate physics
            // against them and would just waste GameObjects.
            if (!Mirror.NetworkServer.active) return;

            _checkpoints = new GameObject("Checkpoints");
            _checkpoints.transform.SetParent(_planet != null ? _planet.transform.parent : null, true);

            float radius = Mathf.Max(2f, level.EffectiveTrackWidth * 0.75f);
            int finishIdx = level.points.Count - 1;
            for (int i = 0; i < level.points.Count; i++)
            {
                Vector3 pos = level.ResolvePosition(i, _raycaster);
                var go = new GameObject($"Checkpoint{i}", typeof(SphereCollider), typeof(CheckpointTrigger));
                go.transform.SetParent(_checkpoints.transform, false);
                go.transform.position = pos;
                var sc = go.GetComponent<SphereCollider>();
                sc.isTrigger = true;
                sc.radius = radius;
                var cp = go.GetComponent<CheckpointTrigger>();
                cp.anchorIndex = i;
                cp.isFinish = (i == finishIdx);
            }
        }

        GlobeRaycaster BuildRaycaster(LevelData level)
        {
            if (_planet == null) return null;
            // Snapshot every MeshFilter under the spawned globe so software
            // raycasts in code paths that lack physics access still work.
            var snaps = GlobeFactory.CollectSnapshots(_planet, 1f);
            var rc = new GlobeRaycaster(snaps, level.NominalRadius);
            CurrentRaycaster = rc;
            return rc;
        }

        // Short straight ribbon behind the start line + a back wall, so cars
        // initially placed at slot N (for N > 0, rows behind the front row)
        // have actual track under them and can't reverse off the edge of the
        // world. Length scales with the number of starting columns; we pick
        // a comfortable buffer (10m + per-row spacing) regardless of player
        // count, so even solo races get a back wall.
        void EnsureSpawnPad(Dio.Level.LevelData level)
        {
            if (_spawnPad != null) Destroy(_spawnPad);
            const float backBuffer = 12f;
            const float perRowSpacing = 4.5f;
            const int worstCaseRows = 4;       // 8 cars / 3 per row → 3 rows; +1 buffer
            float length = backBuffer + worstCaseRows * perRowSpacing;

            // Use the same Z-section options as the main track guards so the
            // pad's side rails feel identical to the rest of the track —
            // including the visible elevation skirt for elevated start lines.
            // Low rim (≈ 35cm) keeps the surrounding scenery visible and lets
            // a hard collision still throw the car off; matches the main
            // track's lowered profile.
            var guardOpt = TrackBuilder.GuardOptions.Default;
            guardOpt.wallHeight = 0.35f;
            guardOpt.lipOutward = 0.05f;
            guardOpt.outwardOffset = 0.05f;
            guardOpt.floorAtSurface = true;
            guardOpt.floorPad = 1f;
            guardOpt.addEndcaps = false;
            var pad = TrackBuilder.BuildSpawnPad(level, length, _raycaster, guardOpt);
            if (pad.ribbon == null) return;

            _spawnPad = new GameObject("SpawnPad");
            _spawnPad.transform.SetParent(_planet != null ? _planet.transform.parent : null, true);

            // Same URP-aware shader resolution as the main track. Fallbacks
            // catch non-URP projects.
            Shader sh = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Universal Render Pipeline/Unlit")
                        ?? Shader.Find("Standard");
            var asphalt = sh != null ? new Material(sh) { name = "SpawnPad (runtime)" } : null;
            if (asphalt != null)
            {
                asphalt.color = new Color(0.10f, 0.10f, 0.12f, 1f);
                if (asphalt.HasProperty("_BaseColor")) asphalt.SetColor("_BaseColor", asphalt.color);
            }

            var ribbonGo = new GameObject("Ribbon", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            ribbonGo.transform.SetParent(_spawnPad.transform, false);
            ribbonGo.layer = MinimapLayer;
            ribbonGo.GetComponent<MeshFilter>().sharedMesh = pad.ribbon;
            if (asphalt != null) ribbonGo.GetComponent<MeshRenderer>().sharedMaterial = asphalt;
            var rmc = ribbonGo.GetComponent<MeshCollider>();
            rmc.sharedMesh = pad.ribbon; rmc.convex = false;
            // Tag as track surface so OffTrackRespawn doesn't yank starting-row
            // cars who haven't crossed onto the main ribbon yet.
            ribbonGo.AddComponent<SurfaceComponent>().type = SurfaceType.Asphalt;

            // Side rails — same material as the main guards so they read as
            // the natural continuation of the track behind the start line.
            Shader wsh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var railMat = new Material(wsh) { name = "SpawnPadRail (runtime)" };
            railMat.color = new Color(0.20f, 0.18f, 0.22f, 1f);
            if (railMat.HasProperty("_Cull")) railMat.SetFloat("_Cull", 0f);

            void AttachMesh(string name, Mesh mesh, Material mat)
            {
                if (mesh == null) return;
                var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
                go.transform.SetParent(_spawnPad.transform, false);
                go.layer = MinimapLayer;
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.GetComponent<MeshRenderer>().sharedMaterial = mat;
                var mc = go.GetComponent<MeshCollider>();
                mc.sharedMesh = mesh; mc.convex = false;
            }
            AttachMesh("GuardLeft",  pad.guardLeft,  railMat);
            AttachMesh("GuardRight", pad.guardRight, railMat);
            AttachMesh("BackWall",   pad.backWall,   railMat);
        }

        // Build invisible-but-collidable guard walls along both edges of the
        // track ribbon. Cars that drift off the track edge bounce off the
        // wall instead of plunging into space. The wall slightly overhangs
        // the surface (skirtDepth = 3m below) so micro-jitter at the
        // ribbon-planet seam can't punch a car through.
        void EnsureGuards(LevelData level)
        {
            if (_guards != null) Destroy(_guards);

            // Low rim rail (~ 35cm tall, 5cm lip). Tall enough to scrape
            // the wheels and discourage cutting the track edge, low enough
            // to keep the surrounding planet/scenery visible AND let a hard
            // bounce throw the car clean over the edge — that's the design
            // choice the user wants for the "risk falling off" feel. The
            // OffTrackRespawn component on each car snaps a fallen car back
            // to the last checkpoint after a couple seconds of no track
            // contact. 1m skirt covers the seam to the low-poly planet
            // collider so the rail doesn't float visually on uneven ground.
            var opt = TrackBuilder.GuardOptions.Default;
            opt.wallHeight = 0.35f;
            opt.lipOutward = 0.05f;
            opt.outwardOffset = 0.05f;
            opt.floorAtSurface = true;          // skirt always reaches the actual ground
            opt.floorPad = 1f;
            // Only the FINISH endpoint gets a perpendicular wall here. The
            // start anchor's "rear gate" is the far back of the spawn pad —
            // generating one at the start anchor too would put two parallel
            // walls right behind the polesitter.
            opt.addStartEndcap  = false;
            opt.addFinishEndcap = true;
            opt.addEndcaps      = false;        // legacy combined toggle
            var (left, right) = TrackBuilder.BuildGuards(level, _raycaster, opt);
            if (left == null || right == null) return;

            _guards = new GameObject("Guards");
            _guards.transform.SetParent(_planet != null ? _planet.transform.parent : null, true);
            _guards.layer = MinimapLayer; // hide from main minimap-isolate camera

            // Subtle dark stripe — readable but not loud. Backface culling
            // turned off so the rails render from both sides (helpful when
            // the camera dips behind a rail during the cinematic intro)
            // and so the rails read as solid walls rather than one-sided
            // strips when seen from outside the track.
            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh) { name = "GuardRail (runtime)" };
            mat.color = new Color(0.20f, 0.18f, 0.22f, 1f);
            // Cull mode 0 = Off in URP/Standard's _Cull property. Set both
            // names so it works whichever shader Unity resolved to.
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", 0f);

            BuildGuardChild(_guards.transform, "GuardLeft", left, mat);
            BuildGuardChild(_guards.transform, "GuardRight", right, mat);
        }

        static void BuildGuardChild(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            go.transform.SetParent(parent, false);
            go.layer = MinimapLayer;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            // Convex would inflate the mesh into a hull (wrong); keep concave
            // and uncheck convex. Concave mesh colliders are static-only,
            // which is fine here — guards never move.
            var mc = go.GetComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = false;
        }

        void EnsurePlanet(LevelData level)
        {
            if (_planet != null) { CurrentPlanet = _planet; return; }
            // Spawn the Globe (world.fbx) at the level's circumference. The
            // factory walks every nested mesh and adds MeshColliders so cars
            // collide with the actual terrain — no idealised sphere, no
            // SphereCollider radius hack. The "Environment" subtree (visual-
            // only props) is excluded from collision attachment.
            _planet = GlobeFactory.Spawn(transform, level.circumference);
            if (_planet == null)
            {
                Debug.LogError("[RaceBootstrap] Globe spawn failed; cannot build a level without it.");
                return;
            }
            // Layer cascades to children for the minimap render filter.
            SetLayerRecursive(_planet, MinimapLayer);
            CurrentPlanet = _planet;
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }

        void EnsureTrack(LevelData level)
        {
            if (_track != null) Destroy(_track);

            var mesh = TrackBuilder.Build(level, _raycaster);
            if (mesh == null) return;

            _track = new GameObject("Track", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            _track.layer = MinimapLayer;
            _track.transform.SetParent(_planet != null ? _planet.transform.parent : null, true);
            _track.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mc = _track.GetComponent<MeshCollider>();
            mc.sharedMesh = mesh;

            var rend = _track.GetComponent<MeshRenderer>();
            // URP project — `Shader.Find("Standard")` returns null and leaves
            // the material in fallback magenta. Use URP/Lit (with Unlit and
            // Standard fallbacks for non-URP projects). Same dark asphalt
            // colour as the guard rails so the rail and ribbon read as one
            // continuous piece of road.
            var trackShader = Shader.Find("Universal Render Pipeline/Lit")
                              ?? Shader.Find("Universal Render Pipeline/Unlit")
                              ?? Shader.Find("Standard");
            var mat = trackShader != null ? new Material(trackShader) : null;
            if (mat != null)
            {
                mat.name = "TrackAsphalt (runtime)";
                mat.color = new Color(0.10f, 0.10f, 0.12f, 1f);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", mat.color); // URP/Lit takes _BaseColor
                // Two-sided: track ribbons authored on a sphere can twist —
                // a strict back-face cull would punch a hole in the surface
                // when the camera dips below it (tunnels, undersides). 0 =
                // Cull Off; URP/Lit and Standard both honour `_Cull`.
                if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
                if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", 0f);
                rend.sharedMaterial = mat;
            }
            // Tag the track with its surface type so wheels colliding against
            // it can pick the right physics profile. Default = asphalt.
            var surface = _track.AddComponent<SurfaceComponent>();
            surface.type = SurfaceType.Asphalt;

            CurrentTrack = _track;
        }

        void AttachCameraTo(Transform target)
        {
            _cam = Camera.main;
            if (_cam == null)
            {
                var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camGo.tag = "MainCamera";
                _cam = camGo.GetComponent<Camera>();
            }
            var rc = _cam.gameObject.GetComponent<RaceCamera>();
            if (rc == null) rc = _cam.gameObject.AddComponent<RaceCamera>();
            rc.target = target;
            rc.car = target.GetComponent<ArcadeCarController>();
            rc.gravity = target.GetComponent<SphericalGravity>();
            rc.mode = RaceCameraMode.Chase;
            // Match the countdown window in DioNetworkManager.ServerStartRace
            // (startServerTime = NetworkTime.time + 4.0). Ease the cinematic
            // pose back to chase exactly as the GO! lands.
            rc.StartCinematicIntro(duration: 4.0f);
            Debug.Log($"[RaceBootstrap] camera attached to '{target.name}' (local owned car)");
        }
    }
}
