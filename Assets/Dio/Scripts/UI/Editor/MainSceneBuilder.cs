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
            BuildNetworkManagerPrefab();
            BuildDefaultLevel();
            BuildMainScene();
        }

        [MenuItem("Tools/Dio/Build/Default Level Asset")]
        public static LevelData BuildDefaultLevel()
        {
            EnsureDir(LevelsDir);
            var existing = AssetDatabase.LoadAssetAtPath<LevelData>(DefaultLevelPath);
            if (existing != null) return existing;

            var lvl = ScriptableObject.CreateInstance<LevelData>();
            lvl.planetRadius = 200f;
            lvl.seed = 42;
            lvl.points.Add(new TrackPoint { directionFromCenter = Vector3.up });
            lvl.points.Add(new TrackPoint { directionFromCenter = new Vector3(0.7f, 0.4f, 0.6f).normalized });
            AssetDatabase.CreateAsset(lvl, DefaultLevelPath);
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
            var lobby = AddPanel(menuRoot.transform, "LobbyPanel", new Color(1, 1, 1, 0.95f));
            var lrt = (RectTransform)lobby.transform;
            lrt.anchorMin = new Vector2(0.5f, 0.5f); lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.sizeDelta = new Vector2(680, 600);
            lobby.SetActive(false);

            var lvg = lobby.AddComponent<VerticalLayoutGroup>();
            lvg.padding = new RectOffset(20, 20, 20, 20); lvg.spacing = 12;
            lvg.childControlWidth = true; lvg.childForceExpandWidth = true;
            lvg.childControlHeight = false; lvg.childForceExpandHeight = false;

            var lobbyTitle = AddText(lobby.transform, "LobbyTitle", "Lobby", 36, FontStyles.Bold);
            var lobbyScroll = AddScrollView(lobby.transform, "LobbyList", out RectTransform lobbyContent);
            var lobbyLE = lobbyScroll.gameObject.AddComponent<LayoutElement>();
            lobbyLE.flexibleHeight = 1; lobbyLE.minHeight = 360;

            var lobbyButtonsRow = new GameObject("Buttons", typeof(RectTransform));
            lobbyButtonsRow.transform.SetParent(lobby.transform, false);
            var lhg = lobbyButtonsRow.AddComponent<HorizontalLayoutGroup>();
            lhg.spacing = 12; lhg.childAlignment = TextAnchor.MiddleCenter;
            lhg.childControlWidth = false; lhg.childControlHeight = false;
            ((RectTransform)lobbyButtonsRow.transform).sizeDelta = new Vector2(0, 64);

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

            // Minimap (top-left). Today: a static globe SVG. Future (plan §11.4):
            // a RenderTexture from a top-down camera over the player; cars and
            // pickups become UI markers parented to this transform so we can
            // skip rendering them in the actual texture.
            var minimapGo = new GameObject("Minimap", typeof(RectTransform));
            minimapGo.transform.SetParent(hudRoot.transform, false);
            var minimapRt = (RectTransform)minimapGo.transform;
            minimapRt.anchorMin = new Vector2(0, 1); minimapRt.anchorMax = new Vector2(0, 1);
            minimapRt.pivot = new Vector2(0, 1);
            minimapRt.anchoredPosition = new Vector2(24, -24);
            minimapRt.sizeDelta = new Vector2(220, 220);
            SvgIconLoader.AttachIcon(minimapGo, LoadSvg("hud_minimap"), Color.white);

            // Player marker as a child of the minimap. The future minimap
            // controller will move this in screen space based on the player's
            // surface position; for now it sits at the centre as a placeholder.
            var markerGo = new GameObject("PlayerMarker", typeof(RectTransform));
            markerGo.transform.SetParent(minimapGo.transform, false);
            var markerRt = (RectTransform)markerGo.transform;
            markerRt.anchorMin = new Vector2(0.5f, 0.5f);
            markerRt.anchorMax = new Vector2(0.5f, 0.5f);
            markerRt.sizeDelta = new Vector2(28, 28);
            markerRt.anchoredPosition = Vector2.zero;
            SvgIconLoader.AttachIcon(markerGo, LoadSvg("hud_player_marker"), Color.white);

            // Powerup slot (bottom-left). Frame is the dashed "?" SVG; the icon
            // sprite that overlays gets switched at runtime by RaceHUD.
            var slotGo = new GameObject("PowerupSlot", typeof(RectTransform));
            slotGo.transform.SetParent(hudRoot.transform, false);
            var slotRt = (RectTransform)slotGo.transform;
            slotRt.anchorMin = new Vector2(0, 0); slotRt.anchorMax = new Vector2(0, 0);
            slotRt.pivot = new Vector2(0, 0);
            slotRt.anchoredPosition = new Vector2(24, 24);
            slotRt.sizeDelta = new Vector2(120, 120);
            SvgIconLoader.AttachIcon(slotGo, LoadSvg("hud_powerup_box"), Color.white);

            var slotIconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            slotIconGo.transform.SetParent(slotGo.transform, false);
            var slotIconRt = (RectTransform)slotIconGo.transform;
            slotIconRt.anchorMin = Vector2.zero; slotIconRt.anchorMax = Vector2.one;
            slotIconRt.offsetMin = new Vector2(14, 14); slotIconRt.offsetMax = new Vector2(-14, -14);
            var slotIconImg = slotIconGo.GetComponent<Image>();
            slotIconImg.preserveAspect = true;
            slotIconImg.raycastTarget = false;
            slotIconImg.enabled = false;

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

            // RaceHUD component, wired with all the HUD refs + powerup icon map.
            var hudCtrl = hudRoot.AddComponent<RaceHUD>();
            hudCtrl.minimapRoot = minimapRt;
            hudCtrl.minimapPlayerMarker = markerRt;
            hudCtrl.powerupSlotIcon = slotIconImg;
            hudCtrl.powerupChargeLabel = chargeLabel;
            hudCtrl.speedLabel = speedText;
            hudCtrl.speedNeedle = needleRt;
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
            ctrl.lobbyPanel = lobby;
            ctrl.lobbyTitle = lobbyTitle;
            ctrl.lobbyListRoot = lobbyContent;
            ctrl.lobbyEntryTemplate = lobbyEntry;
            ctrl.startButton = startBtn;
            ctrl.leaveButton = leaveBtn;
            ctrl.net = netInstance.GetComponent<DioNetworkManager>();
            ctrl.discovery = netInstance.GetComponent<DioNetworkDiscovery>();
            ctrl.menuRoot = menuRoot;
            ctrl.hudRoot = hudRoot;

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

        // ---------- helpers ----------

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
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            ((RectTransform)go.transform).sizeDelta = new Vector2(240, 96);

            // Label fills the button (centered).
            var t = AddText(go.transform, "Label", label, 30, FontStyles.Bold);
            t.alignment = TextAlignmentOptions.Center;
            t.color = Color.white;
            var trt = (RectTransform)t.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

            // Optional SVG icon at the left, anchored to the left edge.
            if (!string.IsNullOrEmpty(svgIconName))
            {
                var iconGo = new GameObject("Icon", typeof(RectTransform));
                iconGo.transform.SetParent(go.transform, false);
                var irt = (RectTransform)iconGo.transform;
                irt.anchorMin = new Vector2(0, 0.5f);
                irt.anchorMax = new Vector2(0, 0.5f);
                irt.pivot = new Vector2(0, 0.5f);
                irt.sizeDelta = new Vector2(56, 56);
                irt.anchoredPosition = new Vector2(16, 0);
                SvgIconLoader.AttachIcon(iconGo, LoadSvg(svgIconName), Color.white);
            }

            return go.GetComponent<Button>();
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
