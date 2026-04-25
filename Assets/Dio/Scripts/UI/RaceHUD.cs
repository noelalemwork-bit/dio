using System.Collections.Generic;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Dio.Common;
using Dio.Player;
using Dio.Powerups;

namespace Dio.UI
{
    /// In-race overlay: minimap (placeholder until the render-texture version
    /// from plan §11.4 lands), held-powerup slot, speed readout. Polls the
    /// local owned car each frame for updates — there are no events on the
    /// `charges` SyncVar, so polling is the simplest way to keep the count in
    /// sync without another network message.
    public class RaceHUD : MonoBehaviour
    {
        [System.Serializable]
        public struct PowerupIcon
        {
            public PowerupKind kind;
            public Sprite icon;
        }

        [Header("Minimap")]
        public RectTransform minimapRoot;
        public RectTransform minimapPlayerMarker;

        [Header("Powerup slot")]
        [Tooltip("Graphic that displays the held powerup icon (Image or SVGImage). Sprite is swapped via SvgIconLoader.SetSprite so both renderer types work.")]
        public Graphic powerupSlotIcon;
        [Tooltip("Image with FillMethod = Radial360 used as a Mask over the slot icon. Its fillAmount drives the circular countdown wipe.")]
        public Image powerupTimerImage;
        public TMP_Text powerupChargeLabel;
        public PowerupIcon[] iconEntries;

        [Header("Speed")]
        public TMP_Text speedLabel;
        [Tooltip("Pivot at the bottom-center; 0° = 12 o'clock, +120° = 7 o'clock (idle), -120° = 5 o'clock (max).")]
        public RectTransform speedNeedle;
        [Tooltip("kph displayed = real m/s × 3.6 × this. Arcade-y inflation; tune for feel.")]
        public float speedDisplayMultiplier = 3.0f;
        [Tooltip("Speed (post-multiplier kph) at which the needle hits the right end of the dial.")]
        public float speedMaxKphDisplay = 360f;

        [Header("Position tracker")]
        public TMP_Text positionLabel;
        public int positionTopN = 4;
        public float positionUpdateInterval = 0.4f;

        [Header("Net diagnostics")]
        [Tooltip("Optional. Shows live RTT in ms + connection quality. Hidden when null.")]
        public TMP_Text pingLabel;
        public float pingUpdateInterval = 0.5f;

        [Header("Pickup banner (shown briefly when a powerup is grabbed)")]
        public CanvasGroup pickupBannerGroup;
        public Image pickupBannerIcon;
        public TMP_Text pickupBannerLabel;
        public float pickupBannerHoldSeconds = 1.5f;
        public float pickupBannerFadeSeconds = 0.35f;

        Dictionary<PowerupKind, Sprite> _iconMap;
        DioCar _localCar;
        PowerupHolder _localHolder;
        ArcadeCarController _localController;

        PowerupKind _lastHeld = PowerupKind.None;
        int _lastCharges = -1;

        float _nextPositionUpdate;
        float _nextPingUpdate;
        readonly System.Text.StringBuilder _positionSb = new System.Text.StringBuilder(128);
        readonly List<(string name, float dist, bool isMe)> _scratchEntries = new List<(string, float, bool)>(8);

        float _bannerVisibleUntil;
        bool _holderHooked;

        void Awake()
        {
            _iconMap = new Dictionary<PowerupKind, Sprite>();
            if (iconEntries != null)
                foreach (var e in iconEntries)
                    if (e.icon != null) _iconMap[e.kind] = e.icon;

            // Hide the slot icon + timer until we have something to show.
            if (powerupSlotIcon != null) powerupSlotIcon.enabled = false;
            if (powerupTimerImage != null) powerupTimerImage.fillAmount = 0f;
            if (powerupChargeLabel != null) powerupChargeLabel.text = "";

            if (pickupBannerGroup != null) pickupBannerGroup.alpha = 0f;
        }

        void OnDisable()
        {
            if (_localHolder != null && _holderHooked)
            {
                _localHolder.OnHeldChangedClient -= OnLocalHeldChanged;
                _holderHooked = false;
            }
        }

        void Update()
        {
            if (_localCar == null) FindLocalCar();
            if (_localHolder != null) RefreshPowerupSlot();
            if (_localController != null) RefreshSpeed();
            RefreshPositions();
            RefreshPickupBanner();
            RefreshPing();
        }

        // Live RTT readout for the local client. Hosts always show 0ms because
        // their NetworkTime.rtt is local-loopback. Color-codes the text to give
        // a quick "is this game playable?" glance: green ≤80, amber ≤180, red >180.
        void RefreshPing()
        {
            if (pingLabel == null) return;
            if (Time.unscaledTime < _nextPingUpdate) return;
            _nextPingUpdate = Time.unscaledTime + pingUpdateInterval;

            if (!NetworkClient.active)
            {
                pingLabel.text = "";
                return;
            }
            int ms = Mathf.RoundToInt((float)(NetworkTime.rtt * 1000.0));
            Color c = ms <= 80 ? new Color(0.55f, 0.95f, 0.55f)
                    : ms <= 180 ? new Color(1f, 0.85f, 0.40f)
                                : new Color(0.95f, 0.45f, 0.40f);
            pingLabel.color = c;
            pingLabel.text = $"{ms} ms";
        }

        void RefreshPickupBanner()
        {
            if (pickupBannerGroup == null) return;
            float now = Time.unscaledTime;
            if (now > _bannerVisibleUntil + pickupBannerFadeSeconds)
            {
                pickupBannerGroup.alpha = 0f;
                return;
            }
            float fadeStart = _bannerVisibleUntil;
            if (now <= fadeStart) pickupBannerGroup.alpha = 1f;
            else
            {
                float fadeT = (now - fadeStart) / Mathf.Max(0.01f, pickupBannerFadeSeconds);
                pickupBannerGroup.alpha = Mathf.Clamp01(1f - fadeT);
            }
        }

        void OnLocalHeldChanged(PowerupKind kind)
        {
            if (kind == PowerupKind.None) return; // only flash on grab, not on consume
            if (pickupBannerGroup == null) return;

            if (pickupBannerIcon != null)
            {
                if (_iconMap.TryGetValue(kind, out var sprite))
                {
                    pickupBannerIcon.sprite = sprite;
                    pickupBannerIcon.enabled = sprite != null;
                }
                else pickupBannerIcon.enabled = false;
            }
            if (pickupBannerLabel != null)
                pickupBannerLabel.text = PowerupNames.Display(kind);

            _bannerVisibleUntil = Time.unscaledTime + pickupBannerHoldSeconds;
            pickupBannerGroup.alpha = 1f;
        }

        // Race position uses the server-published `progressArc` SyncVar
        // (arc-length traveled along the geodesic chain). Highest progress
        // wins, so we sort descending. Server is the only writer — clients
        // just read the synced value, so every peer agrees on the order.
        void RefreshPositions()
        {
            if (positionLabel == null) return;
            if (Time.unscaledTime < _nextPositionUpdate) return;
            _nextPositionUpdate = Time.unscaledTime + positionUpdateInterval;

            var level = Dio.Level.RaceBootstrap.CurrentLevel;
            if (level == null || !level.HasMinimum) return;

            _scratchEntries.Clear();
            foreach (var ni in NetworkClient.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<DioCar>() : null;
                if (car == null) continue;
                // ownerName is a SyncVar that propagates from the server's
                // DioCar.ServerInitialize a frame or two after car spawn —
                // fall back to the local saved name for our own car so we
                // never see the "Player" placeholder on screen.
                string name;
                if (!string.IsNullOrEmpty(car.ownerName)) name = car.ownerName;
                else if (car.isOwned && !string.IsNullOrWhiteSpace(Dio.Common.LocalPrefs.PlayerName)) name = Dio.Common.LocalPrefs.PlayerName;
                else name = "Racer";
                _scratchEntries.Add((name, car.progressArc, car.isOwned));
            }
            // Negate the distance value so the existing ascending sort puts
            // the highest progress first (1st place).
            _scratchEntries.Sort((a, b) => b.dist.CompareTo(a.dist));

            _positionSb.Clear();
            int max = Mathf.Min(_scratchEntries.Count, positionTopN);
            for (int i = 0; i < max; i++)
            {
                var (name, _, isMe) = _scratchEntries[i];
                if (i > 0) _positionSb.Append('\n');
                _positionSb.Append(Ordinal(i + 1));
                _positionSb.Append("  ");
                _positionSb.Append(name);
                if (isMe) _positionSb.Append("  (you)");
            }
            positionLabel.text = _positionSb.ToString();
        }

        static string Ordinal(int n)
        {
            switch (n)
            {
                case 1: return "1st";
                case 2: return "2nd";
                case 3: return "3rd";
                default: return n + "th";
            }
        }

        void RefreshSpeed()
        {
            float kph = _localController.SpeedMps * 3.6f * speedDisplayMultiplier;
            if (speedLabel != null) speedLabel.text = $"{kph:0} km/h";

            if (speedNeedle != null)
            {
                // Map [0..maxKph] -> [+120°..-120°] (lower-left to lower-right via 12 o'clock).
                float t = Mathf.Clamp01(kph / Mathf.Max(1f, speedMaxKphDisplay));
                float angle = Mathf.Lerp(120f, -120f, t);
                var euler = speedNeedle.localEulerAngles;
                euler.z = angle;
                speedNeedle.localEulerAngles = euler;
            }
        }

        void FindLocalCar()
        {
            foreach (var ni in NetworkClient.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<DioCar>() : null;
                if (car == null) continue;
                if (!car.isOwned) continue;
                _localCar = car;
                _localHolder = car.GetComponent<PowerupHolder>();
                _localController = car.GetComponent<ArcadeCarController>();
                if (_localHolder != null && !_holderHooked)
                {
                    _localHolder.OnHeldChangedClient += OnLocalHeldChanged;
                    _holderHooked = true;
                }
                return;
            }
        }

        void RefreshPowerupSlot()
        {
            if (powerupSlotIcon == null) return;
            var held = _localHolder.held;
            int charges = _localHolder.charges;

            if (held != _lastHeld)
            {
                _lastHeld = held;
                if (held == PowerupKind.None)
                {
                    powerupSlotIcon.enabled = false;
                    SvgIconLoader.SetSprite(powerupSlotIcon, null);
                }
                else
                {
                    if (_iconMap.TryGetValue(held, out var sprite) && sprite != null)
                    {
                        SvgIconLoader.SetSprite(powerupSlotIcon, sprite);
                        powerupSlotIcon.enabled = true;
                    }
                    else
                    {
                        powerupSlotIcon.enabled = false;
                    }
                }
            }

            if (charges != _lastCharges)
            {
                _lastCharges = charges;
                if (powerupChargeLabel != null)
                    powerupChargeLabel.text = charges > 1 ? "x" + charges : "";
            }

            // Circular timer mask. fillAmount = remaining/total — wipes from
            // 1 (full circle, icon fully visible) to 0 (nothing left, icon
            // fully hidden) over the kind-specific hold duration.
            if (powerupTimerImage != null)
            {
                if (held == PowerupKind.None || _localHolder.heldUntilServerTime <= 0)
                {
                    powerupTimerImage.fillAmount = 0f;
                }
                else
                {
                    float total = PowerupHolder.DefaultHoldSeconds(held);
                    double remaining = _localHolder.heldUntilServerTime - NetworkTime.time;
                    float t = total > 0 ? Mathf.Clamp01((float)(remaining / total)) : 0f;
                    powerupTimerImage.fillAmount = t;
                }
            }
        }
    }
}
