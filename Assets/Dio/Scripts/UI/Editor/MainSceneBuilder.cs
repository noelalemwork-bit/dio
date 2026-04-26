using System.IO;
using Mirror;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Dio.Net;
using Dio.Level;
using Dio.UI;
using Dio.Common;
using Dio.Powerups;

namespace Dio.UI.EditorTools
{
    /// One-click rebuild of the Main scene + the DioPlayer / DioNetworkManager
    /// prefabs. Idempotent: re-running overwrites the prefab and rebuilds the scene.
    public static class MainSceneBuilder
    {
        const string DioRoot = "Assets/Dio";
        const string PrefabsDir = DioRoot + "/Prefabs";
        const string ScenesDir = DioRoot + "/Scenes";
        const string PlayerPrefabPath = PrefabsDir + "/DioPlayer.prefab";
        const string NetMgrPrefabPath = PrefabsDir + "/DioNetworkManager.prefab";
        const string MainScenePath = ScenesDir + "/Main.unity";
        const string LevelsDir = DioRoot + "/Levels";
        const string DefaultLevelPath = LevelsDir + "/DefaultLevel.asset";
        const string SvgDir = DioRoot + "/UI/Svg";
        const string ShadersDir = DioRoot + "/Shaders";
        const string SkyboxMaterialPath = ShadersDir + "/SunsetSky.mat";
        const string SkyboxShaderPath = ShadersDir + "/SunsetSky.shader";

        [MenuItem("Tools/Dio/Build/Re-import SVGs")]
        public static int ReimportSvgs()
        {
            int count = 0;
            if (!Directory.Exists(SvgDir)) return 0;
            foreach (var path in Directory.GetFiles(SvgDir, "*.svg"))
            {
                var assetPath = path.Replace('\\', '/');
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                count++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Dio] Re-imported {count} SVG assets" + (SvgIconLoader.VectorGraphicsAvailable ? "" : " (com.unity.vectorgraphics not detected — they will import as plain assets without Sprite output)"));
            return count;
        }

        static Sprite LoadSvg(string svgName)
        {
            var path = $"{SvgDir}/{svgName}.svg";
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        [MenuItem("Tools/Dio/Build/Sunset Sky Material")]
        public static Material BuildSkyboxMaterial()
        {
            EnsureDir(ShadersDir);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(SkyboxShaderPath);
            if (shader == null)
            {
                Debug.LogError($"[Dio] Sky shader not found at {SkyboxShaderPath}");
                return null;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMaterialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, SkyboxMaterialPath);
            }
            else
            {
                mat.shader = shader;
            }

            // Cartoony sunset palette: deep orange horizon, red-orange mid, magenta zenith.
            mat.SetColor("_ColorBottom", new Color(1.00f, 0.55f, 0.20f, 1f));
            mat.SetColor("_ColorMid",    new Color(0.95f, 0.32f, 0.30f, 1f));
            mat.SetColor("_ColorTop",    new Color(0.30f, 0.12f, 0.45f, 1f));
            mat.SetFloat("_MidPoint", 0.45f);

            // Sun: low + slightly off-axis, warm cream.
            mat.SetVector("_SunDir", new Vector4(-0.30f, 0.18f, 1.00f, 0f).normalized);
            mat.SetColor("_SunColor", new Color(1.00f, 0.92f, 0.70f, 1f));
            mat.SetFloat("_SunSize", 0.04f);
            mat.SetFloat("_SunHaloPower", 24f);

            // Soft puffy clouds, drifting slowly.
            mat.SetColor("_CloudColor",  new Color(1.00f, 0.78f, 0.58f, 1f));
            mat.SetColor("_CloudShadow", new Color(0.58f, 0.22f, 0.30f, 1f));
            mat.SetFloat("_CloudCoverage", 0.55f);
            mat.SetFloat("_CloudScale", 4f);
            mat.SetFloat("_CloudSpeed", 0.04f);
            mat.SetFloat("_CloudDensity", 0.85f);

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        [MenuItem("Tools/Dio/Build/All (Player + NetMgr + Car + Powerups + Level + Main Scene)", priority = 0)]
        public static void BuildAll()
        {
            if (TMP_Settings.defaultFontAsset == null)
            {
                Debug.LogError("[Dio] TMP Essential Resources missing. Window > TextMeshPro > Import TMP Essential Resources, then re-run Build.");
                return;
            }

            ReimportSvgs(); // ensure com.unity.vectorgraphics has processed every .svg before we look up sprites
            BuildPlayerPrefab();
            Dio.Player.EditorTools.CarPrefabBuilder.BuildCarPrefab();
            Dio.Powerups.EditorTools.PowerupPrefabBuilder.BuildAll();
            BuildGlobePrefab();
            BuildNetworkManagerPrefab();
            BuildDefaultLevel();
            BuildMainScene();
        }

        [MenuItem("Tools/Dio/Build/Globe Prefab")]
        public static GameObject BuildGlobePrefab()
        {
            // Stamp the world.fbx hierarchy into Resources/Globe.prefab so a
            // built player can find it via Resources.Load("Globe"). Mesh
            // colliders are pre-attached at edit-time so the runtime spawn
            // path doesn't need to walk the hierarchy.
            const string ResourcesDir = "Assets/Dio/Resources";
            const string GlobePrefabPath = ResourcesDir + "/Globe.prefab";
            EnsureDir(ResourcesDir);

            var src = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/3d world/world.fbx");
            if (src == null)
            {
                Debug.LogError("[Dio] world.fbx not found at Assets/3d world/world.fbx — cannot build Globe prefab.");
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(src);
            instance.name = "Globe";
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            AttachCollidersRecursive(instance.transform);

            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, GlobePrefabPath);
            Object.DestroyImmediate(instance);
            Debug.Log($"[Dio] Built Globe prefab at {GlobePrefabPath}");
            return prefab;
        }

        // Walk the FBX hierarchy and add a MeshCollider to every GameObject
        // that has a MeshFilter — except under any subtree named "Environment",
        // which is purely visual props (clouds, skybox extras) and shouldn't
        // be collidable.
        static void AttachCollidersRecursive(Transform t)
        {
            const string EnvName = "Environment";
            if (t.name == EnvName) return;

            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && t.GetComponent<MeshCollider>() == null)
            {
                var mc = t.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
            }
            for (int i = 0; i < t.childCount; i++)
                AttachCollidersRecursive(t.GetChild(i));
        }

        [MenuItem("Tools/Dio/Build/Default Level Asset")]
        public static LevelData BuildDefaultLevel()
        {
            EnsureDir(LevelsDir);
            var lvl = AssetDatabase.LoadAssetAtPath<LevelData>(DefaultLevelPath);
            if (lvl == null)
            {
                lvl = ScriptableObject.CreateInstance<LevelData>();
                lvl.planetRadius = 200f;
                lvl.trackRatio = 1f;
                lvl.points.Add(new TrackPoint { directionFromCenter = Vector3.up });
                lvl.points.Add(new TrackPoint { directionFromCenter = new Vector3(0.7f, 0.4f, 0.6f).normalized });
                AssetDatabase.CreateAsset(lvl, DefaultLevelPath);
            }
            // Always upgrade trackWidth — that's the dimension we just bumped.
            // Custom levels (Tools > Dio > New Level) inherit the LevelData class default.
            lvl.trackWidth = 15f;
            EditorUtility.SetDirty(lvl);
            AssetDatabase.SaveAssets();
            return lvl;
        }

        [MenuItem("Tools/Dio/Build/Player Prefab")]
        public static GameObject BuildPlayerPrefab()
        {
            EnsureDir(PrefabsDir);
            var go = new GameObject("DioPlayer");
            go.AddComponent<NetworkIdentity>();
            go.AddComponent<DioPlayer>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, PlayerPrefabPath);
            Object.DestroyImmediate(go);
            Debug.Log($"[Dio] Built player prefab at {PlayerPrefabPath}");
            return prefab;
        }

        [MenuItem("Tools/Dio/Build/Network Manager Prefab")]
        public static GameObject BuildNetworkManagerPrefab()
        {
            EnsureDir(PrefabsDir);

            // Make sure the player prefab exists first so we can wire it.
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (player == null) player = BuildPlayerPrefab();

            var go = new GameObject("DioNetworkManager");
            var mgr = go.AddComponent<DioNetworkManager>();
            var transport = go.AddComponent<kcp2k.KcpTransport>();
            mgr.transport = transport;
            Transport.active = transport;
            mgr.playerPrefab = player;
            mgr.maxConnections = 8;
            mgr.minPlayers = 1;
            mgr.soloBypass = true;
            mgr.HostName = "Host";

            var disc = go.AddComponent<DioNetworkDiscovery>();
            disc.transport = transport;
            mgr.discovery = disc;

            // Wire car prefab + default level if they exist.
            var car = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Dio/Prefabs/Car.prefab");
            if (car != null) mgr.carPrefab = car;
            var lvl = AssetDatabase.LoadAssetAtPath<LevelData>("Assets/Dio/Levels/DefaultLevel.asset");
            if (lvl != null) mgr.defaultLevel = lvl;

            // Populate the level catalog from every LevelData asset under
            // Assets/Dio/Levels (sorted by file name). The host's vote on
            // this catalog is the authoritative pick at race start.
            // The runtime catalog is built at startup by every peer scanning
            // their own LevelData assets — no static catalog wiring here.
            // Old code stamped a `levelCatalog` array into the prefab; that's
            // gone, so we don't carry stale info across rebuilds.

            // Populate spawnPrefabs so base NetworkManager.OnStartClient auto-registers
            // every networked prefab. Without this, server-spawned cars / obstacles
            // arrive at clients with no prefab to instantiate and Mirror logs
            // "Did not find target for sync message" warnings.
            mgr.spawnPrefabs.Clear();
            if (car != null) mgr.spawnPrefabs.Add(car);
            string[] puPaths = {
                "Assets/Dio/Prefabs/Powerups/Banana.prefab",
                "Assets/Dio/Prefabs/Powerups/OilSlick.prefab",
                "Assets/Dio/Prefabs/Powerups/GreenShell.prefab",
                "Assets/Dio/Prefabs/Powerups/BlueShell.prefab",
                "Assets/Dio/Prefabs/Powerups/Bobomb.prefab",
                "Assets/Dio/Prefabs/Powerups/Tornado.prefab",
                "Assets/Dio/Prefabs/Powerups/PowerupBox.prefab",
            };
            foreach (var p in puPaths)
            {
                var pu = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (pu != null) mgr.spawnPrefabs.Add(pu);
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, NetMgrPrefabPath);
            Object.DestroyImmediate(go);
            Debug.Log($"[Dio] Built network manager prefab at {NetMgrPrefabPath}");
            return prefab;
        }

        [MenuItem("Tools/Dio/Build/Main Scene")]
        public static void BuildMainScene()
        {
            if (TMP_Settings.defaultFontAsset == null)
            {
                Debug.LogError("[Dio] TMP Essential Resources missing. Window > TextMeshPro > Import TMP Essential Resources, then re-run Build.");
                return;
            }

            EnsureDir(ScenesDir);

            // Make sure prefabs exist + are up to date.
            BuildPlayerPrefab();
            var netMgrPrefab = BuildNetworkManagerPrefab();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Camera + EventSystem.
            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.backgroundColor = new Color(0.94f, 0.92f, 0.86f, 1f);
            cam.orthographic = false;

            new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.EventSystems.StandaloneInputModule));

            // Warm directional sun light, low on the horizon to match the sunset skybox.
            var sunGo = new GameObject("Sun Light", typeof(Light));
            var sun = sunGo.GetComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.00f, 0.82f, 0.58f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(12f, 30f, 0f);

            // Runtime binder that keeps the skybox's _PlayerUp aligned with the
            // local player's surface normal as they drive around the planet.
            sunGo.AddComponent<Dio.Common.SkyboxPlayerUpBinder>();
            // Publishes _DioTime (Mirror.NetworkTime.time) as a shader global so
            // sky / mystery-box / car shaders animate in lock-step on every peer.
            sunGo.AddComponent<Dio.Common.NetworkTimeBinder>();

            // Sunset skybox.
            var skyMat = BuildSkyboxMaterial();
            if (skyMat != null)
            {
                RenderSettings.skybox = skyMat;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor    = new Color(0.55f, 0.30f, 0.45f);
                RenderSettings.ambientEquatorColor = new Color(0.95f, 0.55f, 0.40f);
                RenderSettings.ambientGroundColor  = new Color(0.30f, 0.18f, 0.20f);
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = new Color(1.00f, 0.55f, 0.40f);
                RenderSettings.fogDensity = 0.0025f;
                DynamicGI.UpdateEnvironment();
            }

            // Network manager instance from prefab.
            var netInstance = (GameObject)PrefabUtility.InstantiatePrefab(netMgrPrefab);
            netInstance.name = "DioNetworkManager";

            // Race bootstrap (handles client-side scene visuals + camera attach).
            var raceGo = new GameObject("RaceBootstrap");
            var rb = raceGo.AddComponent<RaceBootstrap>();
            rb.defaultLevel = AssetDatabase.LoadAssetAtPath<LevelData>(DefaultLevelPath);
            rb.autoStartInEditor = false;

            // Powerup bootstrap (registers effects + spawnable prefabs).
            var puGo = new GameObject("PowerupBootstrap");
            var pu = puGo.AddComponent<Dio.Powerups.PowerupBootstrap>();
            pu.bananaPrefab     = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Dio/Prefabs/Powerups/Banana.prefab");
            pu.oilSlickPrefab   = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Dio/Prefabs/Powerups/OilSlick.prefab");
            pu.greenShellPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Dio/Prefabs/Powerups/GreenShell.prefab");
            pu.blueShellPrefab  = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Dio/Prefabs/Powerups/BlueShell.prefab");
            pu.bobombPrefab     = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Dio/Prefabs/Powerups/Bobomb.prefab");
            pu.tornadoPrefab    = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Dio/Prefabs/Powerups/Tornado.prefab");
            pu.powerupBoxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Dio/Prefabs/Powerups/PowerupBox.prefab");

            // Solo-test cheats. OFF by default; toggle "Enable Cheats" in
            // the inspector at runtime to grant powerups via number keys
            // 1-9 + 0. Lives on its own GO so the toggle is one click away.
            var cheatsGo = new GameObject("PowerupCheats");
            cheatsGo.AddComponent<Dio.Powerups.PowerupCheats>();

            // Canvas.
            var canvasGo = new GameObject("MainCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // ---- MenuRoot wraps every piece of menu UI so we can hide it as a
            // single unit when the race starts. RaceHUDRoot (built below) is the
            // sibling that takes over.
            var menuRoot = AddPanel(canvasGo.transform, "MenuRoot", new Color(1, 1, 1, 0));
            var menuRootRT = (RectTransform)menuRoot.transform;
            menuRootRT.anchorMin = Vector2.zero; menuRootRT.anchorMax = Vector2.one;
            menuRootRT.offsetMin = Vector2.zero; menuRootRT.offsetMax = Vector2.zero;
            // The menu root's Image isn't drawn — kill its raycastTarget so it doesn't eat clicks.
            var menuRootImg = menuRoot.GetComponent<Image>(); if (menuRootImg != null) menuRootImg.raycastTarget = false;

            // Background.
            var bg = AddImage(menuRoot.transform, "BG", new Color(1.00f, 0.94f, 0.78f), stretch:true);
            bg.transform.SetSiblingIndex(0);
            bg.raycastTarget = false;

            // Decorative corner accents. If the com.unity.vectorgraphics package is
            // installed and the SVG files in Assets/Dio/UI/Svg/ have been imported
            // as Sprites, we use them directly via SVGImage; otherwise we fall
            // back to flat colored rects with the brand colors.
            AddCornerAccent(menuRoot.transform, new Color(0.95f, 0.61f, 0.16f, 1f), Vector2.up + Vector2.left,    "TopLeft",     "flag");
            AddCornerAccent(menuRoot.transform, new Color(0.36f, 0.72f, 0.37f, 1f), Vector2.right + Vector2.down, "BottomRight", "planet");

            // ---- Top bar ----
            var topBar = AddPanel(menuRoot.transform, "TopBar", new Color(1f, 1f, 1f, 0.85f));
            var topRT = (RectTransform)topBar.transform;
            topRT.anchorMin = new Vector2(0, 1); topRT.anchorMax = new Vector2(1, 1);
            topRT.pivot = new Vector2(0.5f, 1);
            topRT.anchoredPosition = new Vector2(0, 0);
            topRT.sizeDelta = new Vector2(0, 96);

            var hg = topBar.AddComponent<HorizontalLayoutGroup>();
            hg.padding = new RectOffset(24, 24, 16, 16);
            hg.spacing = 16;
            hg.childAlignment = TextAnchor.MiddleLeft;
            hg.childControlWidth = false; hg.childControlHeight = true;
            hg.childForceExpandWidth = false; hg.childForceExpandHeight = false;

            var nameLabel = AddText(topBar.transform, "NameLabel", "Name", 28, FontStyles.Bold);
            ((RectTransform)nameLabel.transform).sizeDelta = new Vector2(80, 56);

            var nameInputGo = AddInputField(topBar.transform, "NameInput", "Pick a name…");
            ((RectTransform)nameInputGo.transform).sizeDelta = new Vector2(380, 56);
            var nameField = nameInputGo.GetComponent<TMP_InputField>();
            nameField.characterLimit = DioPlayer.MaxNameLength;

            // Spacer.
            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(topBar.transform, false);
            spacer.GetComponent<LayoutElement>().flexibleWidth = 1;

            var swatchRoot = new GameObject("Swatches", typeof(RectTransform));
            swatchRoot.transform.SetParent(topBar.transform, false);
            var sg = swatchRoot.AddComponent<HorizontalLayoutGroup>();
            sg.spacing = 8; sg.childAlignment = TextAnchor.MiddleCenter;
            sg.childControlWidth = false; sg.childControlHeight = false;
            ((RectTransform)swatchRoot.transform).sizeDelta = new Vector2(280, 56);

            // Scrim that locks the rest of the UI until a name is set.
            var scrimGo = new GameObject("NameLockScrim", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            scrimGo.transform.SetParent(menuRoot.transform, false);
            var srt = (RectTransform)scrimGo.transform;
            srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
            srt.offsetMin = new Vector2(0, 0); srt.offsetMax = new Vector2(0, -96); // leave the top bar above
            scrimGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var scrimCg = scrimGo.GetComponent<CanvasGroup>();
            scrimCg.alpha = 0.6f;
            scrimCg.blocksRaycasts = true;

            // ---- Idle panel (host / join) ----
            var idle = AddPanel(menuRoot.transform, "IdlePanel", new Color(1, 1, 1, 0));
            var irt = (RectTransform)idle.transform;
            irt.anchorMin = new Vector2(0.5f, 0.5f); irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.sizeDelta = new Vector2(560, 220);
            var ihg = idle.AddComponent<HorizontalLayoutGroup>();
            ihg.spacing = 24; ihg.childAlignment = TextAnchor.MiddleCenter;
            ihg.childControlWidth = false; ihg.childControlHeight = false;

            var hostBtn = AddBigButton(idle.transform, "HostButton", "Host Game", new Color(0.95f, 0.61f, 0.16f, 1f), "menu_host");
            var joinBtn = AddBigButton(idle.transform, "JoinButton", "Join Game", new Color(0.36f, 0.72f, 0.37f, 1f), "menu_join");

            // ---- Browser panel ----
            var browser = AddPanel(menuRoot.transform, "BrowserPanel", new Color(1, 1, 1, 0.95f));
            var brt = (RectTransform)browser.transform;
            brt.anchorMin = new Vector2(0.5f, 0.5f); brt.anchorMax = new Vector2(0.5f, 0.5f);
            brt.sizeDelta = new Vector2(680, 520);
            browser.SetActive(false);

            var bvg = browser.AddComponent<VerticalLayoutGroup>();
            bvg.padding = new RectOffset(20, 20, 20, 20); bvg.spacing = 12;
            bvg.childControlWidth = true; bvg.childForceExpandWidth = true;
            bvg.childControlHeight = false; bvg.childForceExpandHeight = false;

            var browserTitle = AddText(browser.transform, "Title", "Open Games", 36, FontStyles.Bold);
            var browserStatus = AddText(browser.transform, "Status", "Searching…", 18, FontStyles.Italic);

            var browserScroll = AddScrollView(browser.transform, "ServerList", out RectTransform browserContent);
            var browserListLE = browserScroll.gameObject.AddComponent<LayoutElement>();
            browserListLE.flexibleHeight = 1; browserListLE.minHeight = 320;

            // Direct-IP fallback row: when LAN discovery is blocked (Windows firewall,
            // weird router, same-machine testing), users can punch in an IP directly.
            var directRow = new GameObject("DirectConnectRow", typeof(RectTransform));
            directRow.transform.SetParent(browser.transform, false);
            ((RectTransform)directRow.transform).sizeDelta = new Vector2(0, 64);
            var drhg = directRow.AddComponent<HorizontalLayoutGroup>();
            drhg.spacing = 10; drhg.childAlignment = TextAnchor.MiddleLeft;
            drhg.childControlWidth = false; drhg.childControlHeight = true;

            var directLabel = AddText(directRow.transform, "Label", "Or direct IP:", 18, FontStyles.Italic);
            ((RectTransform)directLabel.transform).sizeDelta = new Vector2(140, 56);

            var ipFieldGo = AddInputField(directRow.transform, "IPField", "192.168.1.x");
            ((RectTransform)ipFieldGo.transform).sizeDelta = new Vector2(280, 56);
            var ipField = ipFieldGo.GetComponent<TMP_InputField>();
            ipField.lineType = TMP_InputField.LineType.SingleLine;

            var directConnectBtn = AddBigButton(directRow.transform, "DirectConnect", "Connect", new Color(0.36f, 0.72f, 0.37f, 1f), "menu_join");
            ((RectTransform)directConnectBtn.transform).sizeDelta = new Vector2(180, 56);

            var browserClose = AddBigButton(browser.transform, "CloseButton", "Cancel", new Color(0.85f, 0.32f, 0.24f, 1f), "menu_leave");
            ((RectTransform)browserClose.transform).sizeDelta = new Vector2(220, 56);

            // Server entry template (inactive).
            var browserEntry = new GameObject("ServerEntryTemplate", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            browserEntry.transform.SetParent(browser.transform, false);
            browserEntry.SetActive(false);
            browserEntry.GetComponent<Image>().color = new Color(0.96f, 0.96f, 0.92f, 1f);
            browserEntry.GetComponent<LayoutElement>().minHeight = 64;
            ((RectTransform)browserEntry.transform).sizeDelta = new Vector2(0, 64);

            var entryVG = browserEntry.AddComponent<VerticalLayoutGroup>();
            entryVG.padding = new RectOffset(14, 14, 8, 8); entryVG.spacing = 2;
            entryVG.childControlWidth = true; entryVG.childForceExpandWidth = true;
            entryVG.childControlHeight = false;

            AddText(browserEntry.transform, "Title", "Server Name", 22, FontStyles.Bold);
            AddText(browserEntry.transform, "Subtitle", "Host · 0/8 · 0.0.0.0", 16, FontStyles.Normal);

            // ---- Lobby panel ----
            // Stretches to fit the screen with margins instead of a fixed
            // 680×600 — older fixed sizes overlapped on small windows.
            var lobby = AddPanel(menuRoot.transform, "LobbyPanel", new Color(1, 1, 1, 0.95f));
            var lrt = (RectTransform)lobby.transform;
            lrt.anchorMin = new Vector2(0.5f, 0.5f); lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.sizeDelta = new Vector2(1100, 720);
            lobby.SetActive(false);

            // childControlHeight=true so the layout RESPECTS each child's
            // LayoutElement.preferredHeight / flexibleHeight. With the old
            // `false` setting, children that didn't manually set sizeDelta
            // (LobbyMain in particular) were laid out at zero height and
            // their children rendered outside the parent rect — which is
            // why the level grid was visible behind / on top of the
            // start/leave buttons row.
            var lvg = lobby.AddComponent<VerticalLayoutGroup>();
            lvg.padding = new RectOffset(20, 20, 20, 20); lvg.spacing = 12;
            lvg.childControlWidth = true; lvg.childForceExpandWidth = true;
            lvg.childControlHeight = true; lvg.childForceExpandHeight = false;

            var lobbyTitle = AddText(lobby.transform, "LobbyTitle", "Lobby", 36, FontStyles.Bold);
            var titleLE = lobbyTitle.gameObject.AddComponent<LayoutElement>();
            titleLE.preferredHeight = 50; titleLE.flexibleHeight = 0;

            // Two-column row that takes the remaining vertical space.
            var lobbyMain = new GameObject("LobbyMain", typeof(RectTransform));
            lobbyMain.transform.SetParent(lobby.transform, false);
            var lmle = lobbyMain.AddComponent<LayoutElement>();
            lmle.flexibleHeight = 1; lmle.minHeight = 280;
            var lmHg = lobbyMain.AddComponent<HorizontalLayoutGroup>();
            lmHg.spacing = 12; lmHg.childControlHeight = true; lmHg.childForceExpandHeight = true;
            lmHg.childControlWidth = true; lmHg.childForceExpandWidth = false;

            // Roster scroll (left).
            var lobbyScroll = AddScrollView(lobbyMain.transform, "LobbyList", out RectTransform lobbyContent);
            var lobbyLE = lobbyScroll.gameObject.AddComponent<LayoutElement>();
            lobbyLE.preferredWidth = 280; lobbyLE.minWidth = 240;

            // Level picker (right) — fills the rest of the row.
            var pickerPanel = new GameObject("LevelPicker", typeof(RectTransform), typeof(Image));
            pickerPanel.transform.SetParent(lobbyMain.transform, false);
            pickerPanel.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.55f);
            var ple = pickerPanel.AddComponent<LayoutElement>();
            ple.flexibleWidth = 1; ple.preferredWidth = 800; ple.minWidth = 480;
            var pickerVG = pickerPanel.AddComponent<VerticalLayoutGroup>();
            pickerVG.padding = new RectOffset(12, 12, 12, 12); pickerVG.spacing = 8;
            pickerVG.childControlWidth = true; pickerVG.childForceExpandWidth = true;
            pickerVG.childControlHeight = true; pickerVG.childForceExpandHeight = false;

            var pickerTitle = AddText(pickerPanel.transform, "PickerTitle", "Pick a level (host's choice is authoritative)", 18, FontStyles.Bold);
            var pickerTitleLE = pickerTitle.gameObject.AddComponent<LayoutElement>();
            pickerTitleLE.preferredHeight = 28; pickerTitleLE.flexibleHeight = 0;

            // Grid stretches to fill remaining vertical space.
            var grid = new GameObject("LevelGrid", typeof(RectTransform), typeof(GridLayoutGroup));
            grid.transform.SetParent(pickerPanel.transform, false);
            var glg = grid.GetComponent<GridLayoutGroup>();
            glg.cellSize = new Vector2(220, 130);
            glg.spacing = new Vector2(12, 12);
            glg.startCorner = GridLayoutGroup.Corner.UpperLeft;
            glg.startAxis = GridLayoutGroup.Axis.Horizontal;
            glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = 3;
            glg.childAlignment = TextAnchor.UpperLeft;
            glg.padding = new RectOffset(4, 4, 4, 4);
            var glgLe = grid.AddComponent<LayoutElement>();
            glgLe.flexibleHeight = 1; glgLe.minHeight = 280;

            // Card template (instantiated by the controller per catalog entry).
            var levelCard = BuildLevelCardTemplate(grid.transform);
            levelCard.SetActive(false);

            var lobbyButtonsRow = new GameObject("Buttons", typeof(RectTransform));
            lobbyButtonsRow.transform.SetParent(lobby.transform, false);
            var lhg = lobbyButtonsRow.AddComponent<HorizontalLayoutGroup>();
            lhg.spacing = 12; lhg.childAlignment = TextAnchor.MiddleCenter;
            lhg.childControlWidth = false; lhg.childControlHeight = false;
            ((RectTransform)lobbyButtonsRow.transform).sizeDelta = new Vector2(0, 96);
            var btnRowLE = lobbyButtonsRow.AddComponent<LayoutElement>();
            btnRowLE.preferredHeight = 96; btnRowLE.flexibleHeight = 0;

            var leaveBtn = AddBigButton(lobbyButtonsRow.transform, "LeaveButton", "Leave", new Color(0.85f, 0.32f, 0.24f, 1f), "menu_leave");
            ((RectTransform)leaveBtn.transform).sizeDelta = new Vector2(180, 56);
            var startBtn = AddBigButton(lobbyButtonsRow.transform, "StartButton", "Start Race", new Color(0.36f, 0.72f, 0.37f, 1f), "menu_start");
            ((RectTransform)startBtn.transform).sizeDelta = new Vector2(260, 56);

            // Lobby entry template.
            var lobbyEntry = new GameObject("LobbyEntryTemplate", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            lobbyEntry.transform.SetParent(lobby.transform, false);
            lobbyEntry.SetActive(false);
            lobbyEntry.GetComponent<Image>().color = new Color(0.96f, 0.96f, 0.92f, 1f);
            lobbyEntry.GetComponent<LayoutElement>().minHeight = 56;
            ((RectTransform)lobbyEntry.transform).sizeDelta = new Vector2(0, 56);
            var lehg = lobbyEntry.AddComponent<HorizontalLayoutGroup>();
            lehg.padding = new RectOffset(14, 14, 8, 8); lehg.spacing = 12;
            lehg.childAlignment = TextAnchor.MiddleLeft;
            lehg.childControlWidth = false; lehg.childControlHeight = true;

            var colorChip = new GameObject("Color", typeof(RectTransform), typeof(Image));
            colorChip.transform.SetParent(lobbyEntry.transform, false);
            ((RectTransform)colorChip.transform).sizeDelta = new Vector2(28, 28);
            colorChip.GetComponent<Image>().color = Color.white;

            AddText(lobbyEntry.transform, "Name", "Player Name", 22, FontStyles.Normal);

            // ---- Race HUD root ----
            // Hidden initially; MainMenuController shows it on race start.
            var hudRoot = AddPanel(canvasGo.transform, "RaceHUDRoot", new Color(1, 1, 1, 0));
            var hudRootRT = (RectTransform)hudRoot.transform;
            hudRootRT.anchorMin = Vector2.zero; hudRootRT.anchorMax = Vector2.one;
            hudRootRT.offsetMin = Vector2.zero; hudRootRT.offsetMax = Vector2.zero;
            var hudRootImg = hudRoot.GetComponent<Image>(); if (hudRootImg != null) hudRootImg.raycastTarget = false;
            hudRoot.SetActive(false);

            // Minimap (top-left). The structure:
            //   MinimapHolder (positioning)
            //     CircleMask (Image + Mask, circle sprite — clips children to a disc)
            //       RawImage (the RT)
            //       MarkerRoot (player + powerup icons, on TOP of the RT)
            // Mask requires a Graphic on the same node, so we put the circular
            // alpha sprite there and let `Mask` clip everything inside it.
            var minimapHolder = new GameObject("Minimap", typeof(RectTransform));
            minimapHolder.transform.SetParent(hudRoot.transform, false);
            var minimapRt = (RectTransform)minimapHolder.transform;
            minimapRt.anchorMin = new Vector2(0, 1); minimapRt.anchorMax = new Vector2(0, 1);
            minimapRt.pivot = new Vector2(0, 1);
            minimapRt.anchoredPosition = new Vector2(24, -24);
            minimapRt.sizeDelta = new Vector2(330, 330);

            var maskGo = new GameObject("CircleMask", typeof(RectTransform), typeof(Image), typeof(Mask));
            maskGo.transform.SetParent(minimapHolder.transform, false);
            var maskRt = (RectTransform)maskGo.transform;
            maskRt.anchorMin = Vector2.zero; maskRt.anchorMax = Vector2.one;
            maskRt.offsetMin = Vector2.zero; maskRt.offsetMax = Vector2.zero;
            var maskImg = maskGo.GetComponent<Image>();
            maskImg.sprite = GetOrCreateCircleSprite();
            maskImg.color = Color.white;
            maskImg.raycastTarget = false;
            maskGo.GetComponent<Mask>().showMaskGraphic = true;

            var rawGo = new GameObject("RT", typeof(RectTransform), typeof(RawImage));
            rawGo.transform.SetParent(maskGo.transform, false);
            var rawRt = (RectTransform)rawGo.transform;
            rawRt.anchorMin = Vector2.zero; rawRt.anchorMax = Vector2.one;
            rawRt.offsetMin = Vector2.zero; rawRt.offsetMax = Vector2.zero;
            var minimapImage = rawGo.GetComponent<RawImage>();
            minimapImage.color = Color.white;
            minimapImage.raycastTarget = false;

            // Marker root sits ON TOP of the RawImage as a sibling. Markers are
            // dynamic Image children added by MinimapRenderer at runtime. Using
            // the parent Mask means they'll be clipped to the circle too.
            var markerRootGo = new GameObject("MarkerRoot", typeof(RectTransform));
            markerRootGo.transform.SetParent(maskGo.transform, false);
            var markerRt = (RectTransform)markerRootGo.transform;
            markerRt.anchorMin = Vector2.zero; markerRt.anchorMax = Vector2.one;
            markerRt.offsetMin = Vector2.zero; markerRt.offsetMax = Vector2.zero;

            // Dedicated minimap camera. Lives on its own GameObject; MinimapRenderer.LateUpdate
            // re-positions it each frame above the local player.
            var minimapCamGo = new GameObject("MinimapCam", typeof(Camera));
            var mr = minimapCamGo.AddComponent<Dio.UI.MinimapRenderer>();
            mr.minimapImage = minimapImage;
            mr.markerRoot = markerRt;
            mr.localPlayerMarker = LoadSvg("hud_player_marker");
            mr.remotePlayerMarker = LoadSvg("hud_player_marker");
            mr.powerupMarker = LoadSvg("hud_powerup_box");
            mr.markerSize = 36f;       // bigger so they're easy to spot at a glance
            mr.orthoSize = 130f;       // zoom out — covers a meaningful slice of the planet

            // Powerup slot (bottom-left). Layered children:
            //   slotGo (frame: hud_powerup_box.svg via SvgIconLoader)
            //     └ TimerMask  — circular Image (Filled/Radial360) + Mask
            //          └ Icon — the held powerup's SVG, swapped by RaceHUD
            //   The Mask's `showMaskGraphic = false` so the wedge itself is
            //   invisible; only its alpha-shape clips the child Icon. As the
            //   timer drains (fillAmount 1 → 0), the visible portion of the
            //   icon shrinks like a clock hand sweeping it away.
            //
            //   Charges label sits on the slot itself (outside the mask) so
            //   the "x3" stays readable even when the timer wedge is small.
            var slotGo = new GameObject("PowerupSlot", typeof(RectTransform));
            slotGo.transform.SetParent(hudRoot.transform, false);
            var slotRt = (RectTransform)slotGo.transform;
            slotRt.anchorMin = new Vector2(0, 0); slotRt.anchorMax = new Vector2(0, 0);
            slotRt.pivot = new Vector2(0, 0);
            slotRt.anchoredPosition = new Vector2(24, 24);
            slotRt.sizeDelta = new Vector2(120, 120);
            SvgIconLoader.AttachIcon(slotGo, LoadSvg("hud_powerup_box"), Color.white);

            // Mask host — circular Image with Filled/Radial360. Used as a Mask
            // so the icon child is clipped to the visible wedge AND drawn so
            // the player can SEE the timer drain. With Mask.showMaskGraphic
            // = true, the colored wedge renders BEHIND the icon child; the
            // icon is on top, clipped to the same shape. As fillAmount drains
            // from 1 → 0, both shrink in lockstep.
            var slotMaskGo = new GameObject("TimerMask", typeof(RectTransform), typeof(Image), typeof(Mask));
            slotMaskGo.transform.SetParent(slotGo.transform, false);
            var slotMaskRt = (RectTransform)slotMaskGo.transform;
            slotMaskRt.anchorMin = Vector2.zero; slotMaskRt.anchorMax = Vector2.one;
            slotMaskRt.offsetMin = new Vector2(10, 10); slotMaskRt.offsetMax = new Vector2(-10, -10);
            var slotTimerImg = slotMaskGo.GetComponent<Image>();
            slotTimerImg.sprite = GetOrCreateCircleSprite();
            // Soft amber backdrop so the wedge is clearly visible behind the
            // icon — bright enough to read, transparent enough not to hide
            // the icon's own colors.
            slotTimerImg.color = new Color(1f, 0.78f, 0.20f, 0.85f);
            slotTimerImg.type = Image.Type.Filled;
            slotTimerImg.fillMethod = Image.FillMethod.Radial360;
            slotTimerImg.fillOrigin = (int)Image.Origin360.Top;
            slotTimerImg.fillClockwise = true;
            slotTimerImg.fillAmount = 0f;
            slotTimerImg.raycastTarget = false;
            // SHOW the mask graphic so the timer wedge is visible.
            slotMaskGo.GetComponent<Mask>().showMaskGraphic = true;

            // Icon child INSIDE the mask. SvgIconLoader so VectorGraphics
            // SVGImage is used when available (regular Image silently fails
            // to display VectorGraphics-imported sprites in some cases).
            var slotIconGo = new GameObject("Icon", typeof(RectTransform));
            slotIconGo.transform.SetParent(slotMaskGo.transform, false);
            var slotIconRt = (RectTransform)slotIconGo.transform;
            slotIconRt.anchorMin = Vector2.zero; slotIconRt.anchorMax = Vector2.one;
            slotIconRt.offsetMin = new Vector2(4, 4); slotIconRt.offsetMax = new Vector2(-4, -4);
            var slotIconGraphic = SvgIconLoader.AttachIcon(slotIconGo, null, Color.white);
            slotIconGraphic.enabled = false;

            var chargeLabelGo = new GameObject("Charges", typeof(RectTransform));
            chargeLabelGo.transform.SetParent(slotGo.transform, false);
            var chargeRt = (RectTransform)chargeLabelGo.transform;
            chargeRt.anchorMin = new Vector2(1, 0); chargeRt.anchorMax = new Vector2(1, 0);
            chargeRt.pivot = new Vector2(1, 0);
            chargeRt.anchoredPosition = new Vector2(-4, 4);
            chargeRt.sizeDelta = new Vector2(60, 36);
            var chargeLabel = chargeLabelGo.AddComponent<TextMeshProUGUI>();
            chargeLabel.text = "";
            chargeLabel.fontSize = 26; chargeLabel.fontStyle = FontStyles.Bold;
            chargeLabel.color = Color.white;
            chargeLabel.alignment = TextAlignmentOptions.BottomRight;
            chargeLabel.font = TMP_Settings.defaultFontAsset;

            // Speed (bottom-right). Gauge SVG + km/h readout.
            var speedGo = new GameObject("Speed", typeof(RectTransform));
            speedGo.transform.SetParent(hudRoot.transform, false);
            var speedRt = (RectTransform)speedGo.transform;
            speedRt.anchorMin = new Vector2(1, 0); speedRt.anchorMax = new Vector2(1, 0);
            speedRt.pivot = new Vector2(1, 0);
            speedRt.anchoredPosition = new Vector2(-24, 24);
            speedRt.sizeDelta = new Vector2(280, 120);

            var gaugeGo = new GameObject("Gauge", typeof(RectTransform));
            gaugeGo.transform.SetParent(speedGo.transform, false);
            var gaugeRt = (RectTransform)gaugeGo.transform;
            gaugeRt.anchorMin = new Vector2(0, 0.5f); gaugeRt.anchorMax = new Vector2(0, 0.5f);
            gaugeRt.pivot = new Vector2(0, 0.5f);
            gaugeRt.sizeDelta = new Vector2(96, 96);
            SvgIconLoader.AttachIcon(gaugeGo, LoadSvg("hud_speed"), Color.white);

            // Speedometer needle: a thin rectangle pivoted at the gauge's "center pin"
            // (the dot at SVG coords (32, 50) inside a 64×64 viewBox = 28% from the bottom of the rect).
            var needleGo = new GameObject("Needle", typeof(RectTransform), typeof(Image));
            needleGo.transform.SetParent(gaugeGo.transform, false);
            var needleRt = (RectTransform)needleGo.transform;
            needleRt.anchorMin = new Vector2(0.5f, 0.28f);
            needleRt.anchorMax = new Vector2(0.5f, 0.28f);
            needleRt.pivot = new Vector2(0.5f, 0f); // bottom of the rectangle = the pin
            needleRt.sizeDelta = new Vector2(4, 36);
            needleRt.anchoredPosition = Vector2.zero;
            var needleImg = needleGo.GetComponent<Image>();
            needleImg.color = new Color(0.88f, 0.32f, 0.24f, 1f);
            needleImg.raycastTarget = false;

            var speedTextGo = new GameObject("Readout", typeof(RectTransform));
            speedTextGo.transform.SetParent(speedGo.transform, false);
            var speedTextRt = (RectTransform)speedTextGo.transform;
            speedTextRt.anchorMin = new Vector2(1, 0); speedTextRt.anchorMax = new Vector2(1, 1);
            speedTextRt.pivot = new Vector2(1, 0.5f);
            speedTextRt.sizeDelta = new Vector2(170, 0);
            speedTextRt.anchoredPosition = Vector2.zero;
            var speedText = speedTextGo.AddComponent<TextMeshProUGUI>();
            speedText.text = "0 km/h";
            speedText.fontSize = 44; speedText.fontStyle = FontStyles.Bold;
            speedText.color = Color.white;
            speedText.alignment = TextAlignmentOptions.MidlineRight;
            speedText.font = TMP_Settings.defaultFontAsset;

            // Pickup banner (top-center). Hidden initially; RaceHUD fades it in
            // for ~1.5s when the local player grabs a powerup. Built behind the
            // position panel in sibling order so PositionPanel sits on top of any overlap.
            var bannerGo = new GameObject("PickupBanner", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            bannerGo.transform.SetParent(hudRoot.transform, false);
            var bannerRt = (RectTransform)bannerGo.transform;
            bannerRt.anchorMin = new Vector2(0.5f, 1);
            bannerRt.anchorMax = new Vector2(0.5f, 1);
            bannerRt.pivot = new Vector2(0.5f, 1);
            bannerRt.anchoredPosition = new Vector2(0, -36);
            bannerRt.sizeDelta = new Vector2(560, 100);
            var bannerBg = bannerGo.GetComponent<Image>();
            bannerBg.color = new Color(0, 0, 0, 0.65f);
            bannerBg.raycastTarget = false;
            var bannerCg = bannerGo.GetComponent<CanvasGroup>();
            bannerCg.alpha = 0f;

            var bhg = bannerGo.AddComponent<HorizontalLayoutGroup>();
            bhg.padding = new RectOffset(20, 20, 12, 12);
            bhg.spacing = 20;
            bhg.childAlignment = TextAnchor.MiddleCenter;
            bhg.childControlWidth = false; bhg.childControlHeight = false;
            bhg.childForceExpandWidth = false; bhg.childForceExpandHeight = false;

            var bannerIconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            bannerIconGo.transform.SetParent(bannerGo.transform, false);
            ((RectTransform)bannerIconGo.transform).sizeDelta = new Vector2(72, 72);
            var bannerIcon = bannerIconGo.GetComponent<Image>();
            bannerIcon.preserveAspect = true;
            bannerIcon.raycastTarget = false;

            var bannerLabelGo = new GameObject("Label", typeof(RectTransform));
            bannerLabelGo.transform.SetParent(bannerGo.transform, false);
            ((RectTransform)bannerLabelGo.transform).sizeDelta = new Vector2(420, 72);
            var bannerLabel = bannerLabelGo.AddComponent<TextMeshProUGUI>();
            bannerLabel.text = "";
            bannerLabel.fontSize = 44;
            bannerLabel.fontStyle = FontStyles.Bold;
            bannerLabel.alignment = TextAlignmentOptions.MidlineLeft;
            bannerLabel.color = Color.white;
            bannerLabel.font = TMP_Settings.defaultFontAsset;

            // Position tracker (top-right). Compact panel with a header + list.
            var posPanel = AddPanel(hudRoot.transform, "PositionPanel", new Color(0f, 0f, 0f, 0.4f));
            var posRt = (RectTransform)posPanel.transform;
            posRt.anchorMin = new Vector2(1, 1); posRt.anchorMax = new Vector2(1, 1);
            posRt.pivot = new Vector2(1, 1);
            posRt.anchoredPosition = new Vector2(-24, -24);
            posRt.sizeDelta = new Vector2(280, 180);

            var posVG = posPanel.AddComponent<VerticalLayoutGroup>();
            posVG.padding = new RectOffset(14, 14, 10, 10);
            posVG.spacing = 4;
            posVG.childAlignment = TextAnchor.UpperLeft;
            posVG.childControlWidth = true; posVG.childForceExpandWidth = true;
            posVG.childControlHeight = false;

            var posHeader = AddText(posPanel.transform, "Header", "POSITION", 18, FontStyles.Bold);
            posHeader.color = new Color(1f, 0.85f, 0.55f, 1f);
            posHeader.alignment = TextAlignmentOptions.MidlineLeft;

            var posListGo = new GameObject("List", typeof(RectTransform));
            posListGo.transform.SetParent(posPanel.transform, false);
            var posListLE = posListGo.AddComponent<LayoutElement>();
            posListLE.flexibleHeight = 1;
            var posList = posListGo.AddComponent<TextMeshProUGUI>();
            posList.text = "—";
            posList.fontSize = 22;
            posList.fontStyle = FontStyles.Bold;
            posList.color = Color.white;
            posList.alignment = TextAlignmentOptions.TopLeft;
            posList.font = TMP_Settings.defaultFontAsset;
            posList.lineSpacing = 4f;

            // Ping readout — pinned just under the position panel. Shows live
            // RTT + colour-codes for connection quality. Stays empty on the
            // host (their RTT is loopback-near-zero anyway).
            var pingGo = new GameObject("Ping", typeof(RectTransform));
            pingGo.transform.SetParent(hudRoot.transform, false);
            var pingRt = (RectTransform)pingGo.transform;
            pingRt.anchorMin = new Vector2(1, 1); pingRt.anchorMax = new Vector2(1, 1);
            pingRt.pivot = new Vector2(1, 1);
            pingRt.anchoredPosition = new Vector2(-24, -212);
            pingRt.sizeDelta = new Vector2(280, 36);
            var pingText = pingGo.AddComponent<TextMeshProUGUI>();
            pingText.text = "";
            pingText.fontSize = 22;
            pingText.fontStyle = FontStyles.Bold;
            pingText.alignment = TextAlignmentOptions.MidlineRight;
            pingText.font = TMP_Settings.defaultFontAsset;
            pingText.color = Color.white;
            pingText.raycastTarget = false;

            // ---- Cinematic countdown overlay ----
            // Centered, large TMP text inside a CanvasGroup so RaceIntro can
            // pop+fade it. The text starts hidden (alpha 0) and is driven by
            // the IntroSequence coroutine.
            var countdownGo = new GameObject("Countdown", typeof(RectTransform), typeof(CanvasGroup));
            countdownGo.transform.SetParent(hudRoot.transform, false);
            var countdownRt = (RectTransform)countdownGo.transform;
            countdownRt.anchorMin = new Vector2(0.5f, 0.5f);
            countdownRt.anchorMax = new Vector2(0.5f, 0.5f);
            countdownRt.pivot = new Vector2(0.5f, 0.5f);
            countdownRt.sizeDelta = new Vector2(720, 360);
            var countdownCg = countdownGo.GetComponent<CanvasGroup>();
            countdownCg.alpha = 0f;
            countdownCg.blocksRaycasts = false;
            countdownCg.interactable = false;

            var countdownLabelGo = new GameObject("Label", typeof(RectTransform));
            countdownLabelGo.transform.SetParent(countdownGo.transform, false);
            var countdownLabelRt = (RectTransform)countdownLabelGo.transform;
            countdownLabelRt.anchorMin = Vector2.zero; countdownLabelRt.anchorMax = Vector2.one;
            countdownLabelRt.offsetMin = Vector2.zero; countdownLabelRt.offsetMax = Vector2.zero;
            var countdownText = countdownLabelGo.AddComponent<TextMeshProUGUI>();
            countdownText.text = "";
            countdownText.fontSize = 280;
            countdownText.fontStyle = FontStyles.Bold;
            countdownText.alignment = TextAlignmentOptions.Center;
            countdownText.color = new Color(0.95f, 0.65f, 0.20f, 1f);
            countdownText.font = TMP_Settings.defaultFontAsset;
            countdownText.raycastTarget = false;
            // Hot pop drop-shadow via TMP's outline setting — visible on both
            // light and dark backgrounds.
            countdownText.outlineColor = new Color32(20, 12, 8, 255);
            countdownText.outlineWidth = 0.25f;

            // RaceIntro must Awake BEFORE OnRaceStarted fires, but the HUD
            // root starts inactive (the menu hides it). Park RaceIntro on a
            // dedicated always-active GameObject and just reference the
            // countdown text fields.
            var introGo = new GameObject("RaceIntro");
            var raceIntro = introGo.AddComponent<RaceIntro>();
            raceIntro.countdownGroup = countdownCg;
            raceIntro.countdownLabel = countdownText;

            // ---- Win panel (siblings the menu/hud roots; sits ON TOP of both) ----
            // Independent of menuRoot/hudRoot because the win UI replaces both
            // for ~5 seconds before the lobby is restored.
            var winPanel = AddPanel(canvasGo.transform, "WinPanel", new Color(0, 0, 0, 0.55f));
            var winRt = (RectTransform)winPanel.transform;
            winRt.anchorMin = Vector2.zero; winRt.anchorMax = Vector2.one;
            winRt.offsetMin = winRt.offsetMax = Vector2.zero;
            winPanel.SetActive(false);

            // SVG banner background, centered.
            var winBgGo = new GameObject("Banner", typeof(RectTransform));
            winBgGo.transform.SetParent(winPanel.transform, false);
            var winBgRt = (RectTransform)winBgGo.transform;
            winBgRt.anchorMin = new Vector2(0.5f, 0.5f); winBgRt.anchorMax = new Vector2(0.5f, 0.5f);
            winBgRt.sizeDelta = new Vector2(720, 280);
            SvgIconLoader.AttachIcon(winBgGo, LoadSvg("winner_banner"), Color.white);

            // Player color chip (top-center of banner).
            var chipGo = new GameObject("ColorChip", typeof(RectTransform), typeof(Image));
            chipGo.transform.SetParent(winPanel.transform, false);
            var chipRt = (RectTransform)chipGo.transform;
            chipRt.anchorMin = new Vector2(0.5f, 0.5f); chipRt.anchorMax = new Vector2(0.5f, 0.5f);
            chipRt.sizeDelta = new Vector2(48, 48);
            chipRt.anchoredPosition = new Vector2(0, 130);
            var chipImg = chipGo.GetComponent<Image>();
            chipImg.color = Color.white;
            chipImg.raycastTarget = false;

            // Winner label.
            var winLabelGo = new GameObject("Label", typeof(RectTransform));
            winLabelGo.transform.SetParent(winPanel.transform, false);
            var winLabelRt = (RectTransform)winLabelGo.transform;
            winLabelRt.anchorMin = new Vector2(0.5f, 0.5f); winLabelRt.anchorMax = new Vector2(0.5f, 0.5f);
            winLabelRt.sizeDelta = new Vector2(680, 200);
            winLabelRt.anchoredPosition = new Vector2(0, -10);
            var winLabel = winLabelGo.AddComponent<TextMeshProUGUI>();
            winLabel.text = "—";
            winLabel.fontSize = 72;
            winLabel.fontStyle = FontStyles.Bold;
            winLabel.alignment = TextAlignmentOptions.Center;
            winLabel.color = Color.white;
            winLabel.font = TMP_Settings.defaultFontAsset;

            // Confetti host (full-rect; UIConfetti spawns children here).
            var confettiHostGo = new GameObject("Confetti", typeof(RectTransform));
            confettiHostGo.transform.SetParent(winPanel.transform, false);
            var confettiRt = (RectTransform)confettiHostGo.transform;
            confettiRt.anchorMin = Vector2.zero; confettiRt.anchorMax = Vector2.one;
            confettiRt.offsetMin = Vector2.zero; confettiRt.offsetMax = Vector2.zero;
            var confetti = confettiHostGo.AddComponent<Dio.UI.UIConfetti>();
            confetti.pieceSprite = LoadSvg("confetti_piece");

            // RaceHUD component, wired with all the HUD refs + powerup icon map.
            var hudCtrl = hudRoot.AddComponent<RaceHUD>();
            hudCtrl.minimapRoot = minimapRt;
            hudCtrl.minimapPlayerMarker = markerRt; // unused now; the MarkerRoot is the dynamic-spawn parent
            hudCtrl.powerupSlotIcon = slotIconGraphic;
            hudCtrl.powerupTimerImage = slotTimerImg;
            hudCtrl.powerupChargeLabel = chargeLabel;
            hudCtrl.speedLabel = speedText;
            hudCtrl.speedNeedle = needleRt;
            hudCtrl.positionLabel = posList;
            hudCtrl.pingLabel = pingText;
            hudCtrl.pickupBannerGroup = bannerCg;
            hudCtrl.pickupBannerIcon = bannerIcon;
            hudCtrl.pickupBannerLabel = bannerLabel;
            hudCtrl.iconEntries = BuildPowerupIconMap();

            // ---- Wire MainMenuController on a top-level controller GO ----
            var ctrlGo = new GameObject("MenuController");
            var ctrl = ctrlGo.AddComponent<MainMenuController>();
            ctrl.nameField = nameField;
            ctrl.colorSwatchRoot = swatchRoot.transform;
            ctrl.nameLockScrim = scrimCg;
            ctrl.idlePanel = idle;
            ctrl.hostButton = hostBtn;
            ctrl.joinButton = joinBtn;
            ctrl.browserPanel = browser;
            ctrl.browserListRoot = browserContent;
            ctrl.browserEntryTemplate = browserEntry;
            ctrl.browserCloseButton = browserClose;
            ctrl.browserStatusLabel = browserStatus;
            ctrl.directIpField = ipField;
            ctrl.directConnectButton = directConnectBtn;
            ctrl.lobbyPanel = lobby;
            ctrl.lobbyTitle = lobbyTitle;
            ctrl.lobbyListRoot = lobbyContent;
            ctrl.lobbyEntryTemplate = lobbyEntry;
            ctrl.startButton = startBtn;
            ctrl.leaveButton = leaveBtn;
            ctrl.levelGridRoot = grid.transform;
            ctrl.levelCardTemplate = levelCard;
            ctrl.net = netInstance.GetComponent<DioNetworkManager>();
            ctrl.discovery = netInstance.GetComponent<DioNetworkDiscovery>();
            ctrl.menuRoot = menuRoot;
            ctrl.hudRoot = hudRoot;
            ctrl.winPanel = winPanel;
            ctrl.winLabel = winLabel;
            ctrl.winColorChip = chipImg;
            ctrl.winConfetti = confetti;

            EditorSceneManager.SaveScene(scene, MainScenePath);

            // Ensure scene is in build settings as the first scene.
            var existing = EditorBuildSettings.scenes;
            bool found = false;
            for (int i = 0; i < existing.Length; i++) if (existing[i].path == MainScenePath) { found = true; break; }
            if (!found)
            {
                var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(existing);
                list.Insert(0, new EditorBuildSettingsScene(MainScenePath, true));
                EditorBuildSettings.scenes = list.ToArray();
            }

            Debug.Log($"[Dio] Built Main scene at {MainScenePath}");
        }

        // The level catalog is now scanned at runtime by every peer via
        // `Dio.Level.LevelLibrary.ScanLocal()` — see DioPlayer.OnStartLocalPlayer
        // which uploads each scanned level to the server, and
        // DioNetworkManager.BroadcastManifest which mirrors the aggregated
        // catalog onto every connected client. No static array is baked
        // into the network-manager prefab anymore.

        // Card template — title at top, voter dot row at bottom, two
        // visibility-toggled rings (gold = host's pick, lighter player-color
        // = local player's vote).
        static GameObject BuildLevelCardTemplate(Transform parent)
        {
            var card = new GameObject("LevelCardTemplate",
                typeof(RectTransform), typeof(Image), typeof(Button), typeof(Dio.Common.SpringScale));
            card.transform.SetParent(parent, false);
            card.GetComponent<Image>().color = DioStyle.PaperLight;
            ((RectTransform)card.transform).sizeDelta = new Vector2(180, 110);

            // Gold "host pick" ring (slightly larger than the card itself).
            var gold = new GameObject("HostRing", typeof(RectTransform), typeof(Image));
            gold.transform.SetParent(card.transform, false);
            var grt = (RectTransform)gold.transform;
            grt.anchorMin = Vector2.zero; grt.anchorMax = Vector2.one;
            grt.offsetMin = new Vector2(-6, -6); grt.offsetMax = new Vector2(6, 6);
            var gimg = gold.GetComponent<Image>();
            gimg.color = DioStyle.Sun;
            gimg.raycastTarget = false;
            gold.transform.SetSiblingIndex(0);
            gold.SetActive(false);

            // Local-vote outline ring (lighter player color, behind card).
            var me = new GameObject("MeRing", typeof(RectTransform), typeof(Image));
            me.transform.SetParent(card.transform, false);
            var mert = (RectTransform)me.transform;
            mert.anchorMin = Vector2.zero; mert.anchorMax = Vector2.one;
            mert.offsetMin = new Vector2(-3, -3); mert.offsetMax = new Vector2(3, 3);
            var meImg = me.GetComponent<Image>();
            meImg.color = Color.white; // controller overwrites with player color
            meImg.raycastTarget = false;
            me.transform.SetSiblingIndex(1);
            me.SetActive(false);

            // Title.
            var titleGo = new GameObject("Title", typeof(RectTransform));
            titleGo.transform.SetParent(card.transform, false);
            var trt = (RectTransform)titleGo.transform;
            trt.anchorMin = new Vector2(0, 0.4f); trt.anchorMax = new Vector2(1, 1);
            trt.offsetMin = new Vector2(10, 4); trt.offsetMax = new Vector2(-10, -4);
            var ttext = titleGo.AddComponent<TextMeshProUGUI>();
            ttext.text = "Level"; ttext.fontSize = 22; ttext.fontStyle = FontStyles.Bold;
            ttext.color = DioStyle.InkDark;
            ttext.alignment = TextAlignmentOptions.Midline;
            ttext.font = TMP_Settings.defaultFontAsset;
            ttext.raycastTarget = false;

            // Voter dot row — controller spawns dots into here at refresh.
            var voters = new GameObject("Voters", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            voters.transform.SetParent(card.transform, false);
            var vrt = (RectTransform)voters.transform;
            vrt.anchorMin = new Vector2(0, 0); vrt.anchorMax = new Vector2(1, 0.4f);
            vrt.offsetMin = new Vector2(8, 6); vrt.offsetMax = new Vector2(-8, -2);
            var vh = voters.GetComponent<HorizontalLayoutGroup>();
            vh.spacing = 4; vh.childAlignment = TextAnchor.MiddleCenter;
            vh.childControlWidth = false; vh.childControlHeight = false;
            vh.childForceExpandWidth = false; vh.childForceExpandHeight = false;

            return card;
        }

        // ---------- helpers ----------

        // Procedural circular sprite for the minimap mask. Cached as a project
        // asset so re-running the builder returns the same Texture2D rather
        // than spawning a new one every time. The texture itself is a 256×256
        // RGBA disc with smooth alpha falloff at the edge.
        const string CircleSpritePath = "Assets/Dio/UI/Generated/MinimapCircle.png";
        static Sprite _cachedCircleSprite;
        static Sprite GetOrCreateCircleSprite()
        {
            if (_cachedCircleSprite != null) return _cachedCircleSprite;
            EnsureDir("Assets/Dio/UI/Generated");
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(CircleSpritePath);
            if (existing != null) { _cachedCircleSprite = existing; return existing; }

            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - r + 0.5f;
                    float dy = y - r + 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // 1px feather at the edge for anti-aliasing.
                    float a = Mathf.Clamp01(r - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();

            var bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes(CircleSpritePath, bytes);
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(CircleSpritePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(CircleSpritePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                // CRITICAL: Single, not Multiple. Multiple-sprite mode with no
                // slices makes LoadAssetAtPath<Sprite> return null.
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }
            _cachedCircleSprite = AssetDatabase.LoadAssetAtPath<Sprite>(CircleSpritePath);
            return _cachedCircleSprite;
        }

        static void EnsureDir(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                Directory.CreateDirectory(path);
                AssetDatabase.Refresh();
            }
        }

        static GameObject AddPanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        static Image AddImage(Transform parent, string name, Color color, bool stretch)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            if (stretch)
            {
                var rt = (RectTransform)go.transform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            }
            return img;
        }

        static void AddCornerAccent(Transform parent, Color fallbackColor, Vector2 anchor, string name, string svgAssetName)
        {
            var go = new GameObject($"Accent_{name}", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var sprite = LoadSvg(svgAssetName);
            // SvgIconLoader picks SVGImage if vectorgraphics is installed (vector-quality);
            // otherwise plain Image with the sprite (still raster-displays the SVG); if no
            // sprite is available we get a flat-tinted rect with the brand color.
            var graphic = SvgIconLoader.AttachIcon(go, sprite, sprite != null ? Color.white : fallbackColor);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(Mathf.Clamp01(anchor.x), Mathf.Clamp01(anchor.y));
            rt.anchorMax = rt.anchorMin;
            rt.pivot = rt.anchorMin;
            rt.sizeDelta = new Vector2(220, 220);
            rt.anchoredPosition = Vector2.zero;
        }

        static TMP_Text AddText(Transform parent, string name, string text, int size, FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.fontStyle = style;
            t.color = new Color(0.12f, 0.12f, 0.14f, 1f);
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.font = TMP_Settings.defaultFontAsset;
            return t;
        }

        static GameObject AddInputField(Transform parent, string name, string placeholder)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 1f);
            var input = go.AddComponent<TMP_InputField>();

            // Text area.
            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            var art = (RectTransform)area.transform;
            art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one;
            art.offsetMin = new Vector2(12, 6); art.offsetMax = new Vector2(-12, -6);

            // Placeholder.
            var ph = AddText(area.transform, "Placeholder", placeholder, 22, FontStyles.Italic);
            ph.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            var phRT = (RectTransform)ph.transform;
            phRT.anchorMin = Vector2.zero; phRT.anchorMax = Vector2.one;
            phRT.offsetMin = Vector2.zero; phRT.offsetMax = Vector2.zero;

            // Visible text.
            var txt = AddText(area.transform, "Text", string.Empty, 22, FontStyles.Normal);
            var txtRT = (RectTransform)txt.transform;
            txtRT.anchorMin = Vector2.zero; txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = Vector2.zero; txtRT.offsetMax = Vector2.zero;

            input.textViewport = (RectTransform)area.transform;
            input.textComponent = (TMP_Text)txt;
            input.placeholder = ph;
            input.lineType = TMP_InputField.LineType.SingleLine;
            return go;
        }

        static Button AddBigButton(Transform parent, string name, string label, Color color, string svgIconName = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Dio.Common.SpringScale));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            ((RectTransform)go.transform).sizeDelta = new Vector2(240, 96);

            const float IconSize = 56f;
            const float IconPad = 14f;
            bool hasIcon = !string.IsNullOrEmpty(svgIconName);

            // Label fills the button MINUS the icon column on the left when
            // an icon is present. This is the missing piece in the previous
            // version — the label was anchored full-width with center
            // alignment, so it sat exactly under the icon.
            var t = AddText(go.transform, "Label", label, 28, FontStyles.Bold);
            t.alignment = TextAlignmentOptions.Center;
            t.color = Color.white;
            t.enableAutoSizing = true;
            t.fontSizeMin = 16; t.fontSizeMax = 30;
            t.enableWordWrapping = false;
            var trt = (RectTransform)t.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            float leftInset = hasIcon ? (IconPad + IconSize + IconPad) : 12f;
            trt.offsetMin = new Vector2(leftInset, 6); trt.offsetMax = new Vector2(-12, -6);
            t.raycastTarget = false;

            if (hasIcon)
            {
                var iconGo = new GameObject("Icon", typeof(RectTransform));
                iconGo.transform.SetParent(go.transform, false);
                var irt = (RectTransform)iconGo.transform;
                irt.anchorMin = new Vector2(0, 0.5f);
                irt.anchorMax = new Vector2(0, 0.5f);
                irt.pivot = new Vector2(0, 0.5f);
                irt.sizeDelta = new Vector2(IconSize, IconSize);
                irt.anchoredPosition = new Vector2(IconPad, 0);
                SvgIconLoader.AttachIcon(iconGo, LoadSvg(svgIconName), Color.white);
            }

            // Press-feedback: gentle spring pulse on click. SpringScale's
            // settle threshold is small enough this feels snappy without
            // looking like a glitch.
            var btn = go.GetComponent<Button>();
            var spring = go.GetComponent<Dio.Common.SpringScale>();
            spring.Snap(Vector3.one);
            btn.onClick.AddListener(() =>
            {
                spring.Snap(Vector3.one * 0.94f);
                spring.SetTargetScale(1f);
            });

            return btn;
        }

        // Pulls the SVG sprite for each PowerupKind so the RaceHUD can swap them in.
        static RaceHUD.PowerupIcon[] BuildPowerupIconMap()
        {
            return new[]
            {
                new RaceHUD.PowerupIcon { kind = PowerupKind.Boost,       icon = LoadSvg("powerup_boost") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.TripleBoost, icon = LoadSvg("powerup_boost") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.Star,        icon = LoadSvg("powerup_star") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.Lightning,   icon = LoadSvg("bolt") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.Banana,      icon = LoadSvg("powerup_banana") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.OilSlick,    icon = LoadSvg("powerup_oil") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.GreenShell,  icon = LoadSvg("powerup_shell") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.BlueShell,   icon = LoadSvg("powerup_blueshell") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.Bobomb,      icon = LoadSvg("powerup_bomb") },
                new RaceHUD.PowerupIcon { kind = PowerupKind.Tornado,     icon = LoadSvg("powerup_tornado") },
            };
        }

        static ScrollRect AddScrollView(Transform parent, string name, out RectTransform content)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.4f);
            ((RectTransform)go.transform).sizeDelta = new Vector2(0, 320);

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(go.transform, false);
            var vrt = (RectTransform)viewport.transform;
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
            vrt.offsetMin = Vector2.zero; vrt.offsetMax = Vector2.zero;
            viewport.GetComponent<Image>().color = new Color(1, 1, 1, 0.01f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewport.transform, false);
            content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = Vector2.zero;
            var clayout = contentGo.GetComponent<VerticalLayoutGroup>();
            clayout.spacing = 8; clayout.padding = new RectOffset(8, 8, 8, 8);
            clayout.childControlWidth = true; clayout.childForceExpandWidth = true;
            clayout.childControlHeight = false;
            var fitter = contentGo.GetComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var sr = go.GetComponent<ScrollRect>();
            sr.viewport = vrt;
            sr.content = content;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;

            return sr;
        }
    }
}
