using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Dio.Common;
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

        [Header("Lobby")]
        public GameObject lobbyPanel;
        public TMP_Text lobbyTitle;
        public Transform lobbyListRoot;
        public GameObject lobbyEntryTemplate;
        public Button startButton;
        public Button leaveButton;

        [Header("Net refs")]
        public DioNetworkManager net;
        public DioNetworkDiscovery discovery;

        [Header("Race-start swap")]
        [Tooltip("Hidden when the race begins (top bar, idle, browser, lobby, scrim, accents).")]
        public GameObject menuRoot;
        [Tooltip("Shown when the race begins (minimap + powerup slot + speed).")]
        public GameObject hudRoot;

        readonly Dictionary<long, DioDiscoveryResponse> _discoveredServers = new();
        readonly List<GameObject> _spawnedBrowserRows = new();
        readonly List<GameObject> _spawnedLobbyRows = new();
        readonly List<Button> _swatchButtons = new();

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
            startButton.onClick.AddListener(OnStartClicked);
            leaveButton.onClick.AddListener(OnLeaveClicked);

            DioPlayer.OnRosterChanged += RefreshLobby;
            DioNetworkManager.OnRaceStarted += OnRaceStartedLocally;

            RefreshGating();
            ShowIdle();
            RefreshLobby();
        }

        void OnDisable()
        {
            DioPlayer.OnRosterChanged -= RefreshLobby;
            DioNetworkManager.OnRaceStarted -= OnRaceStartedLocally;
        }

        // ---------- Top bar ----------

        void BuildColorSwatches()
        {
            // Clear any pre-existing children.
            for (int i = colorSwatchRoot.childCount - 1; i >= 0; i--)
                Object.Destroy(colorSwatchRoot.GetChild(i).gameObject);

            for (int i = 0; i < ColorPalette.Colors.Length; i++)
            {
                int idx = i;
                var go = new GameObject($"Swatch_{i}", typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(colorSwatchRoot, false);
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(36, 36);
                var img = go.GetComponent<Image>();
                img.color = ColorPalette.Get(i);
                var btn = go.GetComponent<Button>();
                btn.onClick.AddListener(() => OnColorPicked(idx));
                _swatchButtons.Add(btn);

                // Tiny outline child to highlight selection.
                var outline = new GameObject("Outline", typeof(RectTransform), typeof(Image));
                outline.transform.SetParent(go.transform, false);
                var ort = (RectTransform)outline.transform;
                ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one;
                ort.offsetMin = new Vector2(-3, -3); ort.offsetMax = new Vector2(3, 3);
                var oimg = outline.GetComponent<Image>();
                oimg.color = new Color(0.1f, 0.1f, 0.1f, 1f);
                oimg.raycastTarget = false;
                outline.transform.SetSiblingIndex(0); // behind
                outline.SetActive(false);
            }
        }

        void HighlightSwatch(int idx)
        {
            for (int i = 0; i < _swatchButtons.Count; i++)
            {
                var t = _swatchButtons[i].transform;
                var outline = t.Find("Outline");
                if (outline != null) outline.gameObject.SetActive(i == idx);
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

            // Build from whichever side we're on.
            if (NetworkServer.active && net != null)
            {
                foreach (var p in net.Players.Values) AddLobbyRow(p);
            }
            else if (NetworkClient.isConnected)
            {
                // On clients we don't have a server roster; iterate spawned identities instead.
                foreach (var kv in NetworkClient.spawned)
                {
                    var p = kv.Value != null ? kv.Value.GetComponent<DioPlayer>() : null;
                    if (p != null) AddLobbyRow(p);
                }
            }

            bool isHost = NetworkServer.active;
            int count = _spawnedLobbyRows.Count;
            int min = net != null ? net.minPlayers : 1;
            bool soloOk = net != null && net.soloBypass;

            startButton.interactable = isHost && (soloOk || count >= min);

            // Visual: green if interactable (host can start), grey otherwise.
            var img = startButton.GetComponent<Image>();
            if (img != null) img.color = startButton.interactable
                ? ColorPalette.Get(1)            // green
                : new Color(0.6f, 0.6f, 0.6f, 1f);

            var label = startButton.GetComponentInChildren<TMP_Text>();
            if (label != null)
            {
                if (!isHost) label.text = "Waiting for host…";
                else if (count >= min || soloOk) label.text = "Start Race";
                else label.text = $"Need {min - count} more player(s)";
            }

            lobbyTitle.text = isHost ? $"Hosting · {count}/{(net != null ? net.maxConnections : 0)}" : "Lobby";
        }

        void AddLobbyRow(DioPlayer p)
        {
            var row = Instantiate(lobbyEntryTemplate, lobbyListRoot);
            row.SetActive(true);
            var img = row.transform.Find("Color")?.GetComponent<Image>();
            if (img != null) img.color = ColorPalette.Get(p.colorIndex);
            var label = row.GetComponentInChildren<TMP_Text>();
            if (label != null) label.text = p.playerName + (p.isLocalPlayer ? "  (you)" : "");
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

        static void ClearChildren(Transform root, List<GameObject> tracked)
        {
            foreach (var go in tracked) if (go != null) Destroy(go);
            tracked.Clear();
        }
    }
}
