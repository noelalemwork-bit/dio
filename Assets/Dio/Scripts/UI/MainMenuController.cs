using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;
using Dio.Common;
using Dio.Level;
using Dio.Net;

namespace Dio.UI
{
    /// Drives the entire Main scene UI. The scene structure is built by
    /// MainSceneBuilder (Tools → Dio → Build Main Scene) which wires every
    /// reference below. All logic lives here.
    public class MainMenuController : MonoBehaviour
    {
        [Header("Top bar — name + color")]
        public TMP_InputField nameField;
        public Transform colorSwatchRoot;
        public Button colorSwatchPrefabSrc; // not instantiated; we read its sub-elements
        public CanvasGroup nameLockScrim;   // covers everything but the top bar

        [Header("Idle (host / join)")]
        public GameObject idlePanel;
        public Button hostButton;
        public Button joinButton;

        [Header("Browser")]
        public GameObject browserPanel;
        public Transform browserListRoot;
        public GameObject browserEntryTemplate;
        public Button browserCloseButton;
        public TMP_Text browserStatusLabel;

        [Header("Browser — direct IP fallback")]
        public TMP_InputField directIpField;
        public Button directConnectButton;

        [Header("Lobby")]
        public GameObject lobbyPanel;
        public TMP_Text lobbyTitle;
        public Transform lobbyListRoot;
        public GameObject lobbyEntryTemplate;
        public Button startButton;
        public Button leaveButton;

        [Header("Lobby — level picker")]
        public Transform levelGridRoot;
        public GameObject levelCardTemplate;

        [Header("Net refs")]
        public DioNetworkManager net;
        public DioNetworkDiscovery discovery;

        [Header("Race-start swap")]
        [Tooltip("Hidden when the race begins (top bar, idle, browser, lobby, scrim, accents).")]
        public GameObject menuRoot;
        [Tooltip("Shown when the race begins (minimap + powerup slot + speed).")]
        public GameObject hudRoot;

        [Header("Win banner")]
        public GameObject winPanel;
        public TMP_Text winLabel;
        public Image winColorChip;
        public UIConfetti winConfetti;

        readonly Dictionary<long, DioDiscoveryResponse> _discoveredServers = new();
        readonly List<GameObject> _spawnedBrowserRows = new();
        readonly List<GameObject> _spawnedLobbyRows = new();
        readonly List<Button> _swatchButtons = new();
        readonly List<GameObject> _spawnedLevelCards = new();

        DioPlayer LocalPlayer => NetworkClient.localPlayer != null
            ? NetworkClient.localPlayer.GetComponent<DioPlayer>()
            : null;

        void Awake()
        {
            if (net == null) net = FindAnyObjectByType<DioNetworkManager>();
            if (discovery == null && net != null) discovery = net.discovery;
            if (discovery != null) discovery.OnDioServerFound.AddListener(OnDioServerFound);
        }

        void OnDestroy()
        {
            if (discovery != null) discovery.OnDioServerFound.RemoveListener(OnDioServerFound);
        }

        void Start()
        {
            BuildColorSwatches();

            // Restore prefs.
            string savedName = LocalPrefs.PlayerName;
            int savedColor = LocalPrefs.ColorIndex;
            nameField.text = savedName;
            HighlightSwatch(savedColor);

            nameField.onValueChanged.AddListener(OnNameChanged);
            nameField.onEndEdit.AddListener(OnNameSubmitted);

            hostButton.onClick.AddListener(OnHostClicked);
            joinButton.onClick.AddListener(OnJoinClicked);
            browserCloseButton.onClick.AddListener(() => browserPanel.SetActive(false));
            if (directConnectButton != null) directConnectButton.onClick.AddListener(OnDirectConnectClicked);
            if (directIpField != null)
            {
                directIpField.text = LocalPrefs.LastDirectIp;
                // Enter (or any "submit") in the IP field triggers Connect.
                directIpField.onSubmit.AddListener(_ => OnDirectConnectClicked());
            }
            startButton.onClick.AddListener(OnStartClicked);
            leaveButton.onClick.AddListener(OnLeaveClicked);

            DioPlayer.OnRosterChanged += RefreshLobby;
            DioNetworkManager.OnRaceStarted += OnRaceStartedLocally;
            DioNetworkManager.OnRaceWon     += OnRaceWonLocally;
            DioNetworkManager.OnDisconnectedFromHost += OnDisconnectedFromHost;
            if (winPanel != null) winPanel.SetActive(false);

            RefreshGating();
            ShowIdle();
            RefreshLobby();
        }

        void OnDisable()
        {
            DioPlayer.OnRosterChanged -= RefreshLobby;
            DioNetworkManager.OnRaceStarted -= OnRaceStartedLocally;
            DioNetworkManager.OnRaceWon     -= OnRaceWonLocally;
            DioNetworkManager.OnDisconnectedFromHost -= OnDisconnectedFromHost;
        }

        void Update()
        {
            // Esc / gamepad Select-or-Back: ALWAYS quits whatever the player
            // is currently in. As a host it stops the server (kicking every
            // client → their MainMenuController sees OnClientDisconnect →
            // goes home too). As a client it stops the local connection.
            // Idle Esc is a no-op (we'd "go home" to where we already are).
            bool quitPressed = Input.GetKeyDown(KeyCode.Escape);
            if (!quitPressed)
            {
                var pad = Gamepad.current;
                if (pad != null && pad.selectButton.wasPressedThisFrame) quitPressed = true;
            }
            if (quitPressed)
            {
                if (net != null && (Mirror.NetworkServer.active || Mirror.NetworkClient.isConnected))
                {
                    net.StopAll();
                }
                ReturnToHomeScreen();
            }

            // Gamepad nudge: if there's no current selection AND the user
            // just pressed something on the pad (stick or face buttons),
            // grab the panel's default selectable so D-pad navigation has
            // a starting point. Mouse users keep null selection so they
            // never see a stray highlight ring.
            if (EventSystem.current != null
                && EventSystem.current.currentSelectedGameObject == null
                && JustPressedAnyGamepadNav())
            {
                var first = ChooseFirstSelected();
                if (first != null) EventSystem.current.SetSelectedGameObject(first);
            }
        }

        static bool JustPressedAnyGamepadNav()
        {
            var pad = Gamepad.current;
            if (pad == null) return false;
            if (pad.dpad.up.wasPressedThisFrame || pad.dpad.down.wasPressedThisFrame
                || pad.dpad.left.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame) return true;
            if (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame
                || pad.buttonWest.wasPressedThisFrame || pad.buttonNorth.wasPressedThisFrame) return true;
            // Stick flick — magnitude crossing 0.5 from rest counts as "intent
            // to navigate" without spamming on a held stick.
            float prev = _prevStickMag; float curr = pad.leftStick.ReadValue().magnitude;
            _prevStickMag = curr;
            return prev < 0.5f && curr >= 0.5f;
        }
        static float _prevStickMag;

        GameObject ChooseFirstSelected()
        {
            if (winPanel != null && winPanel.activeInHierarchy) return null; // win is non-interactive
            if (lobbyPanel != null && lobbyPanel.activeInHierarchy)
                return startButton != null && startButton.interactable ? startButton.gameObject
                     : leaveButton != null ? leaveButton.gameObject : null;
            if (browserPanel != null && browserPanel.activeInHierarchy)
            {
                if (_spawnedBrowserRows.Count > 0) return _spawnedBrowserRows[0];
                if (directIpField != null) return directIpField.gameObject;
                if (browserCloseButton != null) return browserCloseButton.gameObject;
            }
            if (idlePanel != null && idlePanel.activeInHierarchy)
                return hostButton != null && hostButton.interactable ? hostButton.gameObject
                     : joinButton != null && joinButton.interactable ? joinButton.gameObject : null;
            return null;
        }

        void OnDisconnectedFromHost()
        {
            // Host quit (or the server crashed). Snap every client back to the
            // idle/home screen — a stale lobby UI on a dead connection is the
            // most confusing state for the user to find themselves in.
            ReturnToHomeScreen();
        }

        void ReturnToHomeScreen()
        {
            if (winPanel != null) winPanel.SetActive(false);
            if (hudRoot != null) hudRoot.SetActive(false);
            if (menuRoot != null) menuRoot.SetActive(true);
            CancelInvoke(nameof(ReturnToLobbyAfterWin));
            ShowIdle();
        }

        // ---------- Top bar ----------

        void BuildColorSwatches()
        {
            // Clear any pre-existing children.
            for (int i = colorSwatchRoot.childCount - 1; i >= 0; i--)
                Object.Destroy(colorSwatchRoot.GetChild(i).gameObject);
            _swatchButtons.Clear();

            for (int i = 0; i < ColorPalette.Colors.Length; i++)
            {
                int idx = i;
                // Wrapper hosts the spring-scale; the inner image is what
                // shows the color. Picking grows the wrapper instead of
                // covering the inner color with a darker rectangle.
                var go = new GameObject($"Swatch_{i}", typeof(RectTransform), typeof(SpringScale));
                go.transform.SetParent(colorSwatchRoot, false);
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(DioStyle.SwatchBase, DioStyle.SwatchBase);

                // Outer ring — colored lighter, sits BEHIND the swatch and
                // only becomes visible when this swatch is selected (the
                // inner swatch is slightly smaller so the lighter ring
                // pokes out around the edge).
                var ring = new GameObject("LightRing", typeof(RectTransform), typeof(Image));
                ring.transform.SetParent(go.transform, false);
                var rrt = (RectTransform)ring.transform;
                rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one;
                rrt.offsetMin = new Vector2(-DioStyle.SwatchOutline, -DioStyle.SwatchOutline);
                rrt.offsetMax = new Vector2(DioStyle.SwatchOutline, DioStyle.SwatchOutline);
                var ringImg = ring.GetComponent<Image>();
                ringImg.color = DioStyle.Lighten(ColorPalette.Get(i), 0.55f);
                ringImg.raycastTarget = false;
                ring.SetActive(false);

                // Color disc — the click target, sits in front of the ring.
                var disc = new GameObject("Disc", typeof(RectTransform), typeof(Image), typeof(Button));
                disc.transform.SetParent(go.transform, false);
                var drt = (RectTransform)disc.transform;
                drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one;
                drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;
                var dimg = disc.GetComponent<Image>();
                dimg.color = ColorPalette.Get(i);
                var btn = disc.GetComponent<Button>();
                btn.onClick.AddListener(() => OnColorPicked(idx));
                _swatchButtons.Add(btn);

                // Initial scale snap so the spring doesn't pop on first frame.
                var spring = go.GetComponent<SpringScale>();
                spring.Snap(Vector3.one);
            }
        }

        void HighlightSwatch(int idx)
        {
            for (int i = 0; i < _swatchButtons.Count; i++)
            {
                // Walk up to the wrapper (Disc → Swatch_N).
                var wrapper = _swatchButtons[i].transform.parent;
                if (wrapper == null) continue;
                var ring = wrapper.Find("LightRing");
                if (ring != null) ring.gameObject.SetActive(i == idx);
                var spring = wrapper.GetComponent<SpringScale>();
                if (spring != null)
                {
                    float targetScale = (i == idx)
                        ? DioStyle.SwatchPicked / DioStyle.SwatchBase
                        : 1f;
                    spring.SetTargetScale(targetScale);
                }
            }
        }

        void OnColorPicked(int idx)
        {
            LocalPrefs.ColorIndex = idx;
            HighlightSwatch(idx);
            var p = LocalPlayer; if (p != null) p.CmdSetColor(idx);
        }

        void OnNameChanged(string s)
        {
            // Live save trimmed name; gating uses non-whitespace.
            LocalPrefs.PlayerName = s.Trim();
            RefreshGating();
        }

        void OnNameSubmitted(string s)
        {
            string trimmed = s.Trim();
            if (trimmed.Length > DioPlayer.MaxNameLength) trimmed = trimmed.Substring(0, DioPlayer.MaxNameLength);
            nameField.SetTextWithoutNotify(trimmed);
            LocalPrefs.PlayerName = trimmed;
            var p = LocalPlayer; if (p != null) p.CmdSetName(trimmed);
            RefreshGating();
        }

        void RefreshGating()
        {
            bool hasName = !string.IsNullOrWhiteSpace(LocalPrefs.PlayerName);
            nameLockScrim.alpha = hasName ? 0f : 0.6f;
            nameLockScrim.blocksRaycasts = !hasName;
            nameLockScrim.interactable = !hasName;

            hostButton.interactable = hasName;
            joinButton.interactable = hasName;
        }

        // ---------- Idle / Browser ----------

        void ShowIdle()
        {
            idlePanel.SetActive(true);
            browserPanel.SetActive(false);
            lobbyPanel.SetActive(false);
        }

        void OnHostClicked()
        {
            if (net == null) return;
            net.HostName = LocalPrefs.PlayerName;
            net.HostAndAdvertise();
            ShowLobby();
        }

        void OnJoinClicked()
        {
            _discoveredServers.Clear();
            ClearChildren(browserListRoot, _spawnedBrowserRows);
            browserPanel.SetActive(true);
            browserStatusLabel.text = "Searching for games on the local network…";
            if (discovery != null) discovery.StartDiscovery();
        }

        void OnDioServerFound(DioDiscoveryResponse info)
        {
            _discoveredServers[info.serverId] = info;
            RebuildBrowserList();
        }

        void RebuildBrowserList()
        {
            ClearChildren(browserListRoot, _spawnedBrowserRows);
            int n = 0;
            foreach (var info in _discoveredServers.Values)
            {
                var row = Instantiate(browserEntryTemplate, browserListRoot);
                row.SetActive(true);
                var labels = row.GetComponentsInChildren<TMP_Text>(true);
                if (labels.Length > 0) labels[0].text = string.IsNullOrEmpty(info.serverName) ? "Dio Lobby" : info.serverName;
                if (labels.Length > 1) labels[1].text = $"{info.hostName}  ·  {info.playerCount}/{info.maxPlayers}  ·  {info.EndPoint?.Address}";
                var btn = row.GetComponent<Button>();
                if (btn != null)
                {
                    var captured = info;
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(() => Connect(captured));
                }
                _spawnedBrowserRows.Add(row);
                n++;
            }
            browserStatusLabel.text = n == 0
                ? "Searching for games on the local network…"
                : $"Found {n} game{(n == 1 ? "" : "s")}";
        }

        void Connect(DioDiscoveryResponse info)
        {
            if (discovery != null) discovery.StopDiscovery();
            net.StartClient(info.uri);
            browserPanel.SetActive(false);
            ShowLobby();
        }

        void OnDirectConnectClicked()
        {
            if (directIpField == null || net == null) return;
            string ip = directIpField.text.Trim();
            if (string.IsNullOrEmpty(ip)) { browserStatusLabel.text = "Enter an IP first."; return; }

            // Remember it so the next session prefills the field.
            LocalPrefs.LastDirectIp = ip;

            // Pull the host's listening port off the active transport's ServerUri.
            // KCP defaults to 7777; users can override by editing the transport asset.
            int port = 7777;
            if (Mirror.Transport.active != null)
            {
                try { port = Mirror.Transport.active.ServerUri().Port; } catch { /* default 7777 */ }
            }

            var uri = new System.Uri($"kcp://{ip}:{port}");
            if (discovery != null) discovery.StopDiscovery();
            net.networkAddress = ip;
            net.StartClient(uri);
            browserPanel.SetActive(false);
            ShowLobby();
        }

        // ---------- Lobby ----------

        void ShowLobby()
        {
            idlePanel.SetActive(false);
            lobbyPanel.SetActive(true);
            RefreshLobby();
        }

        void RefreshLobby()
        {
            if (!lobbyPanel.activeInHierarchy) return;

            ClearChildren(lobbyListRoot, _spawnedLobbyRows);

            // Snapshot all DioPlayers visible on this peer.
            var roster = new List<DioPlayer>();
            if (NetworkServer.active && net != null)
            {
                foreach (var p in net.Players.Values) if (p != null) roster.Add(p);
            }
            else if (NetworkClient.isConnected)
            {
                foreach (var kv in NetworkClient.spawned)
                {
                    var p = kv.Value != null ? kv.Value.GetComponent<DioPlayer>() : null;
                    if (p != null) roster.Add(p);
                }
            }
            foreach (var p in roster) AddLobbyRow(p);

            bool isHost = NetworkServer.active;
            int count = _spawnedLobbyRows.Count;
            int min = net != null ? net.minPlayers : 1;
            bool soloOk = net != null && net.soloBypass;

            startButton.interactable = isHost && (soloOk || count >= min);

            var img = startButton.GetComponent<Image>();
            if (img != null) img.color = startButton.interactable
                ? DioStyle.Mint
                : new Color(0.6f, 0.6f, 0.6f, 1f);

            var label = startButton.GetComponentInChildren<TMP_Text>();
            if (label != null)
            {
                if (!isHost) label.text = "Waiting for host…";
                else if (count >= min || soloOk) label.text = "Start Race";
                else label.text = $"Need {min - count} more player(s)";
            }

            lobbyTitle.text = isHost ? $"Hosting · {count}/{(net != null ? net.maxConnections : 0)}" : "Lobby";

            // Level grid.
            RefreshLevelGrid(roster, isHost);
        }

        // ---------- Level picker ----------

        void RefreshLevelGrid(List<DioPlayer> roster, bool isHost)
        {
            if (levelGridRoot == null || levelCardTemplate == null) return;

            // Manifest is the source of truth — replicated from the server's
            // runtime catalog. Empty manifest = nothing to show; the grid
            // collapses cleanly while we wait for the first broadcast.
            var manifest = DioNetworkManager.CachedManifest ?? System.Array.Empty<PlayerLevelManifestEntry>();
            if (manifest.Length == 0) { ClearChildren(levelGridRoot, _spawnedLevelCards); return; }

            // Identify the host for the authoritative pick.
            DioPlayer host = null;
            foreach (var p in roster) if (p != null && p.isLocalPlayer && NetworkServer.active) { host = p; break; }
            if (host == null)
            {
                foreach (var p in roster)
                {
                    if (p != null && p.connectionToClient == NetworkServer.localConnection) { host = p; break; }
                }
            }
            int hostPickOwner = host != null ? host.preferredLevelOwner : -1;
            string hostPickName = host != null ? (host.preferredLevelName ?? string.Empty) : string.Empty;

            // Bucket players by their (owner, name) vote.
            var votesByKey = new Dictionary<(int, string), List<DioPlayer>>();
            foreach (var p in roster)
            {
                if (p == null) continue;
                if (p.preferredLevelOwner < 0) continue;
                var key = (p.preferredLevelOwner, p.preferredLevelName ?? string.Empty);
                if (!votesByKey.TryGetValue(key, out var list)) { list = new List<DioPlayer>(); votesByKey[key] = list; }
                list.Add(p);
            }

            int needed = manifest.Length;
            while (_spawnedLevelCards.Count < needed)
            {
                var go = Instantiate(levelCardTemplate, levelGridRoot);
                go.SetActive(true);
                _spawnedLevelCards.Add(go);
            }
            while (_spawnedLevelCards.Count > needed)
            {
                int last = _spawnedLevelCards.Count - 1;
                Destroy(_spawnedLevelCards[last]);
                _spawnedLevelCards.RemoveAt(last);
            }

            for (int i = 0; i < needed; i++)
            {
                var entry = manifest[i];
                var card = _spawnedLevelCards[i];
                bool isHostPick = (hostPickOwner == entry.ownerConnId
                                   && string.Equals(hostPickName, entry.displayName, System.StringComparison.OrdinalIgnoreCase));
                votesByKey.TryGetValue((entry.ownerConnId, entry.displayName), out var vs);
                ConfigureLevelCard(card, entry, isHostPick, vs);
            }
        }

        void ConfigureLevelCard(GameObject card, PlayerLevelManifestEntry entry, bool isHostPick, List<DioPlayer> voters)
        {
            // Title — show "Level Name · by Owner" so room members can tell
            // whose track this is.
            var title = card.transform.Find("Title")?.GetComponent<TMP_Text>();
            if (title != null)
            {
                string nm = string.IsNullOrEmpty(entry.displayName) ? "(unnamed)" : entry.displayName;
                string owner = string.IsNullOrEmpty(entry.ownerName) ? "?" : entry.ownerName;
                title.text = $"{nm}\n<size=70%>by {owner}</size>";
            }

            // Host's authoritative pick = a thick gold ring around the card.
            var hostRing = card.transform.Find("HostRing");
            if (hostRing != null) hostRing.gameObject.SetActive(isHostPick);

            // Local player's preference highlight.
            var local = LocalPlayer;
            bool isMine = local != null
                          && local.preferredLevelOwner == entry.ownerConnId
                          && string.Equals(local.preferredLevelName, entry.displayName, System.StringComparison.OrdinalIgnoreCase);
            var spring = card.GetComponent<SpringScale>();
            if (spring != null) spring.SetTargetScale(isMine ? 1.06f : 1f);
            var meRing = card.transform.Find("MeRing")?.GetComponent<Image>();
            if (meRing != null)
            {
                meRing.gameObject.SetActive(isMine);
                if (isMine && local != null) meRing.color = DioStyle.Lighten(ColorPalette.Get(local.colorIndex), 0.45f);
            }

            // Voter dots.
            var dots = card.transform.Find("Voters");
            if (dots != null)
            {
                for (int c = dots.childCount - 1; c >= 0; c--) Destroy(dots.GetChild(c).gameObject);
                if (voters != null)
                {
                    foreach (var v in voters)
                    {
                        var dot = new GameObject($"Vote_{v.netId}", typeof(RectTransform), typeof(Image));
                        dot.transform.SetParent(dots, false);
                        ((RectTransform)dot.transform).sizeDelta = new Vector2(22, 22);
                        var im = dot.GetComponent<Image>();
                        im.color = ColorPalette.Get(v.colorIndex);
                        im.raycastTarget = false;
                    }
                }
            }

            int ownerConn = entry.ownerConnId;
            string displayName = entry.displayName;
            var btn = card.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => OnLevelCardClicked(ownerConn, displayName));
            }
        }

        void OnLevelCardClicked(int ownerConnId, string displayName)
        {
            var p = LocalPlayer;
            if (p != null) p.CmdSetPreferredLevel(ownerConnId, displayName);
        }

        void AddLobbyRow(DioPlayer p)
        {
            var row = Instantiate(lobbyEntryTemplate, lobbyListRoot);
            row.SetActive(true);
            var img = row.transform.Find("Color")?.GetComponent<Image>();
            if (img != null) img.color = ColorPalette.Get(p.colorIndex);
            var label = row.GetComponentInChildren<TMP_Text>();
            if (label != null)
            {
                // Skip the placeholder if it hasn't been replaced yet — show
                // "joining…" for an instant rather than "Player 3" until the
                // CmdSetName round-trip completes.
                string name = string.IsNullOrEmpty(p.playerName) || p.playerName.StartsWith("Player ")
                    ? (p.isLocalPlayer ? LocalPrefs.PlayerName : "Joining…")
                    : p.playerName;
                if (string.IsNullOrEmpty(name)) name = "Joining…";
                label.text = name + (p.isLocalPlayer ? "  (you)" : "");
            }
            _spawnedLobbyRows.Add(row);
        }

        void OnStartClicked()
        {
            var p = LocalPlayer;
            if (p == null) return;
            p.CmdRequestStart();
        }

        void OnLeaveClicked()
        {
            net.StopAll();
            ShowIdle();
        }

        void OnRaceStartedLocally(bool _, RaceStartMessage __)
        {
            if (winPanel != null) winPanel.SetActive(false);
            if (hudRoot != null) hudRoot.SetActive(true);

            if (menuRoot != null)
            {
                menuRoot.SetActive(false);
                return;
            }

            // Defensive path — menuRoot wasn't wired (old scene from before the
            // menuRoot/hudRoot split). Walk the canvas and hide every sibling
            // EXCEPT the HUD root. We must NOT hide the canvas itself, because
            // the HUD lives on it.
            var canvas = hudRoot != null
                ? hudRoot.GetComponentInParent<Canvas>()
                : GetComponentInParent<Canvas>();
            if (canvas == null) return;

            for (int i = 0; i < canvas.transform.childCount; i++)
            {
                var child = canvas.transform.GetChild(i).gameObject;
                if (hudRoot != null && child == hudRoot) continue;
                child.SetActive(false);
            }
        }

        void OnRaceWonLocally(RaceWonMessage msg)
        {
            // Restart path: the host pressed R and we're tearing down the
            // visual scene to immediately rebuild on the same level. Skip
            // the win popup AND the lobby return — RaceStartMessage will
            // arrive in a moment and re-enter the racing UI.
            if (msg.isRestart) return;

            if (winPanel != null)
            {
                winPanel.SetActive(true);
                if (winLabel != null) winLabel.text = $"{msg.winnerName}\nWINS!";
                if (winColorChip != null) winColorChip.color = ColorPalette.Get(msg.winnerColorIndex);
                if (winConfetti != null) winConfetti.Burst();
            }

            // After the cleanup delay, swap back to the lobby. Server-side cleanup
            // (NetworkServer.Destroy of cars / boxes / obstacles) is scheduled in
            // DioNetworkManager.ServerCleanupRace; this just restores the local UI.
            float delay = Mathf.Max(0.1f, msg.cleanupDelay);
            CancelInvoke(nameof(ReturnToLobbyAfterWin));
            Invoke(nameof(ReturnToLobbyAfterWin), delay);
        }

        void ReturnToLobbyAfterWin()
        {
            if (winPanel != null) winPanel.SetActive(false);
            if (hudRoot != null) hudRoot.SetActive(false);
            if (menuRoot != null) menuRoot.SetActive(true);
            ShowLobby();
        }

        static void ClearChildren(Transform root, List<GameObject> tracked)
        {
            foreach (var go in tracked) if (go != null) Destroy(go);
            tracked.Clear();
        }
    }
}
