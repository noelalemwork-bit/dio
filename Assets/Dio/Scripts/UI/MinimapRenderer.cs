using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.UI;
using Dio.Player;
using Dio.Powerups;

namespace Dio.UI
{
    /// Renders the planet + track to a small RenderTexture from a camera placed
    /// directly above the local player, looking at the planet origin. Cars,
    /// pickups, vegetation are intentionally NOT in the camera's culling mask
    /// — they get drawn instead as UI markers in screen space, projected from
    /// world coords into the minimap rect each frame. Markers on the far
    /// hemisphere (i.e. on the other side of the planet from the player) are
    /// hidden so the minimap only shows what's nearby.
    ///
    /// Set `minimapImage` to the RawImage that replaces the static minimap SVG
    /// in the HUD. The renderer creates its own RenderTexture sized from the
    /// rect.
    [RequireComponent(typeof(Camera))]
    public class MinimapRenderer : MonoBehaviour
    {
        [Header("Render target")]
        public RawImage minimapImage;
        public Vector2Int textureSize = new Vector2Int(256, 256);

        [Header("Camera placement")]
        [Tooltip("Distance above the local player along surface-up.")]
        public float cameraAltitude = 80f;
        [Tooltip("Orthographic size — half the planet-tangent radius the minimap covers.")]
        public float orthoSize = 60f;

        [Header("UI markers")]
        public RectTransform markerRoot;
        public Sprite localPlayerMarker;   // hud_player_marker.svg, tinted from ColorPalette
        public Sprite remotePlayerMarker;  // same sprite, different tint
        public Sprite powerupMarker;       // hud_powerup_box.svg
        public float markerSize = 22f;

        Camera _cam;
        RenderTexture _rt;
        Transform _localPlayerTransform;
        readonly Dictionary<NetworkIdentity, RectTransform> _markers = new Dictionary<NetworkIdentity, RectTransform>();
        readonly List<NetworkIdentity> _toDespawn = new List<NetworkIdentity>(8);

        void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = orthoSize;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.04f, 0.04f, 0.08f, 1f);
            _cam.cullingMask = 1 << Dio.Level.RaceBootstrap.MinimapLayer;
            _cam.nearClipPlane = 1f;
            _cam.farClipPlane = 600f;

            EnsureRenderTexture();
        }

        void EnsureRenderTexture()
        {
            if (_rt != null) return;
            _rt = new RenderTexture(textureSize.x, textureSize.y, 16, RenderTextureFormat.ARGB32) { name = "MinimapRT" };
            _rt.Create();
            _cam.targetTexture = _rt;
            if (minimapImage != null) minimapImage.texture = _rt;
        }

        void LateUpdate()
        {
            EnsureRenderTexture();
            FindLocalPlayerIfNeeded();
            UpdateCameraTransform();
            UpdateMarkers();
        }

        void FindLocalPlayerIfNeeded()
        {
            if (_localPlayerTransform != null) return;
            foreach (var ni in NetworkClient.spawned.Values)
            {
                var car = ni != null ? ni.GetComponent<DioCar>() : null;
                if (car != null && car.isOwned) { _localPlayerTransform = car.transform; return; }
            }
        }

        void UpdateCameraTransform()
        {
            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            Vector3 origin = planet != null ? planet.transform.position : Vector3.zero;

            if (_localPlayerTransform != null)
            {
                Vector3 up = (_localPlayerTransform.position - origin).normalized;
                if (up.sqrMagnitude < 0.001f) up = Vector3.up;
                Vector3 camPos = _localPlayerTransform.position + up * cameraAltitude;
                // Camera looks toward planet origin (i.e. straight down through the player).
                Vector3 forward = (origin - camPos).normalized;
                // Use the player's world-forward projected onto the tangent plane as the "up" of the minimap
                // → driving forward maps to "up" in the rendered texture.
                Vector3 worldFwd = _localPlayerTransform.forward;
                Vector3 tangentFwd = Vector3.ProjectOnPlane(worldFwd, up).normalized;
                if (tangentFwd.sqrMagnitude < 0.001f) tangentFwd = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
                transform.SetPositionAndRotation(camPos, Quaternion.LookRotation(forward, tangentFwd));
            }
            _cam.orthographicSize = orthoSize;
        }

        // ------------- markers -------------

        void UpdateMarkers()
        {
            if (markerRoot == null) return;

            // Snapshot existing markers; we'll remove any not seen this pass.
            _toDespawn.Clear();
            foreach (var kv in _markers) _toDespawn.Add(kv.Key);

            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            Vector3 origin = planet != null ? planet.transform.position : Vector3.zero;
            Vector3 localUp = _localPlayerTransform != null
                ? (_localPlayerTransform.position - origin).normalized
                : Vector3.up;

            foreach (var ni in NetworkClient.spawned.Values)
            {
                if (ni == null) continue;
                if (TryGetMarkerSprite(ni, out var sprite, out var tint, out bool isLocal))
                {
                    var rt = GetOrCreateMarker(ni, sprite, tint);
                    PositionMarker(rt, ni.transform, localUp, origin, isLocal);
                    _toDespawn.Remove(ni);
                }
            }

            // Anything that wasn't refreshed this pass is gone — remove its marker.
            foreach (var dead in _toDespawn)
            {
                if (_markers.TryGetValue(dead, out var rt) && rt != null) Destroy(rt.gameObject);
                _markers.Remove(dead);
            }
        }

        bool TryGetMarkerSprite(NetworkIdentity ni, out Sprite sprite, out Color tint, out bool isLocal)
        {
            sprite = null; tint = Color.white; isLocal = false;
            var car = ni.GetComponent<DioCar>();
            if (car != null)
            {
                isLocal = car.isOwned;
                sprite = isLocal ? localPlayerMarker : remotePlayerMarker;
                tint = Dio.Common.ColorPalette.Get(car.colorIndex);
                return sprite != null;
            }
            var box = ni.GetComponent<PowerupBox>();
            if (box != null && powerupMarker != null && box.available)
            {
                sprite = powerupMarker;
                tint = new Color(1f, 0.85f, 0.30f, 1f);
                return true;
            }
            return false;
        }

        RectTransform GetOrCreateMarker(NetworkIdentity ni, Sprite sprite, Color tint)
        {
            if (_markers.TryGetValue(ni, out var existing) && existing != null)
            {
                var img = existing.GetComponent<Image>();
                if (img != null)
                {
                    if (img.sprite != sprite) img.sprite = sprite;
                    img.color = tint;
                }
                return existing;
            }
            var go = new GameObject($"Marker_{ni.netId}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(markerRoot, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(markerSize, markerSize);
            var img2 = go.GetComponent<Image>();
            img2.sprite = sprite;
            img2.color = tint;
            img2.preserveAspect = true;
            img2.raycastTarget = false;
            _markers[ni] = rt;
            return rt;
        }

        void PositionMarker(RectTransform marker, Transform target, Vector3 localUp, Vector3 origin, bool isLocal)
        {
            // Hemisphere cull: a target on the other side of the planet from
            // the local player would project through the camera back to the
            // image plane in a misleading way. Hide it.
            Vector3 targetUp = (target.position - origin).normalized;
            float hemisphereDot = Vector3.Dot(targetUp, localUp);
            bool sameHemi = hemisphereDot > -0.05f;

            // The local player marker always shows (it's at the centre).
            if (!isLocal && !sameHemi) { marker.gameObject.SetActive(false); return; }
            marker.gameObject.SetActive(true);

            // Project target world position into the camera viewport, then
            // anchor on the markerRoot's rect.
            Vector3 viewport = _cam.WorldToViewportPoint(target.position);
            // viewport.z < 0 means behind the cam (shouldn't happen since cam looks down,
            // but guard anyway).
            if (viewport.z < 0f) { marker.gameObject.SetActive(false); return; }

            Vector2 size = markerRoot.rect.size;
            Vector2 anchored = new Vector2(
                (viewport.x - 0.5f) * size.x,
                (viewport.y - 0.5f) * size.y);
            marker.anchoredPosition = anchored;

            // Rotate marker to point along the target's tangent-plane forward.
            Vector3 tgtFwd = Vector3.ProjectOnPlane(target.forward, targetUp).normalized;
            // Convert that direction into camera-space (the minimap's local axes).
            // The minimap "up" was set to the local player's tangent-forward, so
            // a target moving "north" relative to the player points up in the rect.
            Vector3 camRight = transform.right;
            Vector3 camUp    = transform.up;
            float x = Vector3.Dot(tgtFwd, camRight);
            float y = Vector3.Dot(tgtFwd, camUp);
            float angleDeg = Mathf.Atan2(x, y) * Mathf.Rad2Deg;
            marker.localRotation = Quaternion.Euler(0, 0, -angleDeg);
        }

        void OnDestroy()
        {
            if (_rt != null)
            {
                if (_cam != null) _cam.targetTexture = null;
                _rt.Release();
                Destroy(_rt);
            }
        }
    }
}
