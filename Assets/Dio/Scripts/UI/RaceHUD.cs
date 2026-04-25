using System.Collections.Generic;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
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
        public Image powerupSlotIcon;     // tinted whichever powerup is held
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

        Dictionary<PowerupKind, Sprite> _iconMap;
        DioCar _localCar;
        PowerupHolder _localHolder;
        ArcadeCarController _localController;

        PowerupKind _lastHeld = PowerupKind.None;
        int _lastCharges = -1;

        float _nextPositionUpdate;
        readonly System.Text.StringBuilder _positionSb = new System.Text.StringBuilder(128);
        readonly List<(string name, float dist, bool isMe)> _scratchEntries = new List<(string, float, bool)>(8);

        void Awake()
        {
            _iconMap = new Dictionary<PowerupKind, Sprite>();
            if (iconEntries != null)
                foreach (var e in iconEntries)
                    if (e.icon != null) _iconMap[e.kind] = e.icon;

            // Hide the slot icon until we have something to show.
            if (powerupSlotIcon != null) powerupSlotIcon.enabled = false;
            if (powerupChargeLabel != null) powerupChargeLabel.text = "";
        }

        void Update()
        {
            if (_localCar == null) FindLocalCar();
            if (_localHolder != null) RefreshPowerupSlot();
            if (_localController != null) RefreshSpeed();
            RefreshPositions();
        }

        // Closest-to-finish proxy for race position. Replaced by a real
        // arc-length-along-track tracker once procedural track meshes land (M2).
        void RefreshPositions()
        {
            if (positionLabel == null) return;
            if (Time.unscaledTime < _nextPositionUpdate) return;
            _nextPositionUpdate = Time.unscaledTime + positionUpdateInterval;

            var level = Dio.Level.RaceBootstrap.CurrentLevel;
            if (level == null || !level.HasMinimum) return;

            Vector3 finishWorld = level.Finish.directionFromCenter.normalized * level.planetRadius;
            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            if (planet != null) finishWorld += planet.transform.position;

            _scratchEntries.Clear();
            foreach (var ni in NetworkClient.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<DioCar>() : null;
                if (car == null) continue;
                float dist = (car.transform.position - finishWorld).magnitude;
                string name = string.IsNullOrEmpty(car.ownerName) ? "Player" : car.ownerName;
                _scratchEntries.Add((name, dist, car.isOwned));
            }
            _scratchEntries.Sort((a, b) => a.dist.CompareTo(b.dist));

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
                    powerupSlotIcon.sprite = null;
                }
                else
                {
                    if (_iconMap.TryGetValue(held, out var sprite))
                    {
                        powerupSlotIcon.sprite = sprite;
                        powerupSlotIcon.enabled = sprite != null;
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
        }
    }
}
