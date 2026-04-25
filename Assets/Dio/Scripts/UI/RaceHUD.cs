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

        Dictionary<PowerupKind, Sprite> _iconMap;
        DioCar _localCar;
        PowerupHolder _localHolder;
        ArcadeCarController _localController;

        PowerupKind _lastHeld = PowerupKind.None;
        int _lastCharges = -1;

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
            if (_localController != null && speedLabel != null)
                speedLabel.text = $"{_localController.SpeedMps * 3.6f:0} km/h";
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
