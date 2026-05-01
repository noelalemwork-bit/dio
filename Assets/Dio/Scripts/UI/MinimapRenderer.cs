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
        public Sprite localPlayerMarker;   // unused now — markers are procedural arrows
        public Sprite remotePlayerMarker;  // unused now — markers are procedural arrows
        public Sprite powerupMarker;       // hud_powerup_box.svg
        [Tooltip("Edge length of the upward-pointing arrow markers in pixels. Tuned ~5x smaller than circle markers so the dots read as 'positions' rather than 'splats'.")]
        public float markerSize = 9f;
        [Tooltip("How much bigger the LOCAL player's marker is, as a multiplier on markerSize.")]
        public float localMarkerScale = 1.4f;

        [Header("Checkpoint tangent ticks (yellow lines)")]
        [Tooltip("Length of the yellow tick drawn at each track anchor, in pixels.")]
        public float checkpointTickLength = 24f;
        [Tooltip("Thickness of the yellow tick in pixels.")]
        public float checkpointTickThickness = 3f;
        public Color checkpointTickColor = new Color(1f, 0.92f, 0.18f, 0.92f);

        Camera _cam;
        RenderTexture _rt;
        Transform _localPlayerTransform;
        readonly Dictionary<NetworkIdentity, RectTransform> _markers = new Dictionary<NetworkIdentity, RectTransform>();
        readonly List<NetworkIdentity> _toDespawn = new List<NetworkIdentity>(8);

        // Yellow checkpoint ticks — one rectangular RT per anchor, repositioned
        // every frame because the minimap pans + spins under the local player.
        // Built lazily once the level is available; rebuilt if the level
        // changes between races.
        readonly List<RectTransform> _checkpointTicks = new List<RectTransform>();
        Dio.Level.LevelData _lastCheckpointLevel;

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
            UpdateCheckpointTicks();
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

        // ------------- checkpoint tangent ticks -------------

        // One thin yellow rectangle per LevelData anchor, oriented along the
        // bezier's tangent at that anchor + projected into minimap space.
        // The track ribbon's own pixels are already in the RT, but a sharp
        // tick at each checkpoint is what designers + racers actually use to
        // read "I'm coming up on the next gate".
        void UpdateCheckpointTicks()
        {
            if (markerRoot == null) return;
            var lvl = Dio.Level.RaceBootstrap.CurrentLevel;
            if (lvl == null || !lvl.HasMinimum)
            {
                if (_checkpointTicks.Count > 0) ClearCheckpointTicks();
                _lastCheckpointLevel = null;
                return;
            }
            if (_lastCheckpointLevel != lvl) { ClearCheckpointTicks(); _lastCheckpointLevel = lvl; }

            // Lazily fill the pool to the right size.
            int n = lvl.points.Count;
            while (_checkpointTicks.Count < n) _checkpointTicks.Add(CreateTick(_checkpointTicks.Count));
            while (_checkpointTicks.Count > n)
            {
                int last = _checkpointTicks.Count - 1;
                if (_checkpointTicks[last] != null) Destroy(_checkpointTicks[last].gameObject);
                _checkpointTicks.RemoveAt(last);
            }

            var planet = Dio.Level.RaceBootstrap.CurrentPlanet;
            Vector3 origin = planet != null ? planet.transform.position : Vector3.zero;
            Vector3 localUp = _localPlayerTransform != null
                ? (_localPlayerTransform.position - origin).normalized
                : Vector3.up;

            var raycaster = Dio.Level.RaceBootstrap.CurrentRaycaster;
            float halfTrackWidth = Mathf.Max(1f, lvl.EffectiveTrackWidth) * 0.5f;
            Vector2 size = markerRoot.rect.size;

            for (int i = 0; i < n; i++)
            {
                var tick = _checkpointTicks[i];
                if (tick == null) continue;

                Vector3 dir = lvl.DirOf(i);
                // Hemisphere cull — same logic as car markers.
                float dot = Vector3.Dot(dir, localUp);
                if (dot < -0.05f) { tick.gameObject.SetActive(false); continue; }

                // World position of the anchor: surface radius along dir + the
                // anchor's authored yOffset.
                Vector3 worldPos = lvl.ResolvePosition(i, raycaster);

                // Tangent: direction-to-next-anchor in tangent plane. Endpoints
                // borrow the previous edge's direction so the last anchor still
                // gets a meaningful tick.
                Vector3 tangentWorld = ComputeTangentAt(lvl, i);
                Vector3 surfaceUp = (worldPos - origin).normalized;
                Vector3 tangentTan = Vector3.ProjectOnPlane(tangentWorld, surfaceUp).normalized;
                if (tangentTan.sqrMagnitude < 1e-4f) tangentTan = Vector3.ProjectOnPlane(Vector3.forward, surfaceUp).normalized;

                // The TICK should run PERPENDICULAR to the tangent (across the
                // track), like a finish-line stripe — that's what a designer
                // reads as "checkpoint at this point". Cross with surfaceUp
                // gives the lateral direction.
                Vector3 lateral = Vector3.Cross(tangentTan, surfaceUp).normalized;

                // Project the centre into the minimap viewport.
                Vector3 vp = _cam.WorldToViewportPoint(worldPos);
                if (vp.z < 0f) { tick.gameObject.SetActive(false); continue; }
                tick.gameObject.SetActive(true);

                Vector2 anchored = new Vector2((vp.x - 0.5f) * size.x, (vp.y - 0.5f) * size.y);
                tick.anchoredPosition = anchored;

                // Tick orientation: convert the lateral world vector into
                // camera-local axes (the minimap's own X / Y), then build the
                // angle so the rectangle's long axis lines up with `lateral`.
                Vector3 camRight = transform.right;
                Vector3 camUp    = transform.up;
                float lx = Vector3.Dot(lateral, camRight);
                float ly = Vector3.Dot(lateral, camUp);
                // Atan2(y, x) in degrees — rotate so the rect's local-X points along (lx, ly).
                float angle = Mathf.Atan2(ly, lx) * Mathf.Rad2Deg;
                tick.localRotation = Quaternion.Euler(0, 0, angle);
            }
        }

        RectTransform CreateTick(int idx)
        {
            var go = new GameObject($"CheckpointTick_{idx}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(markerRoot, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(checkpointTickLength, checkpointTickThickness);
            var img = go.GetComponent<Image>();
            img.color = checkpointTickColor;
            img.raycastTarget = false;
            // Ticks draw UNDER car markers — set as first sibling so cars
            // remain on top.
            go.transform.SetAsFirstSibling();
            return rt;
        }

        void ClearCheckpointTicks()
        {
            for (int i = _checkpointTicks.Count - 1; i >= 0; i--)
                if (_checkpointTicks[i] != null) Destroy(_checkpointTicks[i].gameObject);
            _checkpointTicks.Clear();
        }

        static Vector3 ComputeTangentAt(Dio.Level.LevelData lvl, int i)
        {
            // Forward tangent from anchor i to its first outgoing neighbour.
            // Falls back to the incoming edge for the finish anchor (no
            // outgoing neighbours), and to a stable cross with planet axes
            // if everything else is degenerate.
            var pi = lvl.points[i];
            int next = (pi.next != null && pi.next.Count > 0) ? pi.next[0] : -1;
            if (next >= 0 && next < lvl.points.Count)
            {
                Vector3 a = lvl.DirOf(i);
                Vector3 b = lvl.DirOf(next);
                Vector3 tan = (b - a);
                if (tan.sqrMagnitude > 1e-6f) return tan.normalized;
            }
            // Fallback: scan for a predecessor (any point whose `next` includes i).
            for (int p = 0; p < lvl.points.Count; p++)
            {
                var nx = lvl.points[p].next;
                if (nx == null) continue;
                for (int k = 0; k < nx.Count; k++)
                {
                    if (nx[k] == i)
                    {
                        Vector3 a = lvl.DirOf(p);
                        Vector3 b = lvl.DirOf(i);
                        Vector3 tan = (b - a);
                        if (tan.sqrMagnitude > 1e-6f) return tan.normalized;
                    }
                }
            }
            return Vector3.forward;
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
                    img.color = tint;
                    // Powerup markers keep their SVG; car markers stay on the
                    // procedural arrow. Only swap if the source sprite type
                    // changed.
                    if (sprite != null && img.sprite != sprite && img.sprite != s_arrowSprite) img.sprite = sprite;
                }
                return existing;
            }

            // Marker is a SINGLE Image rotated by PositionMarker. Cars use a
            // procedural up-pointing arrow tinted with the car's chosen
            // colour; powerup boxes keep their SVG. No halo (the previous
            // dark halo on remote cars read as "black blobs" on the minimap).
            var go = new GameObject($"Marker_{ni.netId}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(markerRoot, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(0.5f, 0.5f);

            bool isCar = ni.GetComponent<DioCar>() != null;
            bool isLocal = isCar && (ni.GetComponent<DioCar>().isOwned);
            float baseSize = isCar
                ? (isLocal ? markerSize * Mathf.Max(0.5f, localMarkerScale) : markerSize)
                : markerSize * 1.6f; // powerup boxes a touch bigger
            rt.sizeDelta = new Vector2(baseSize, baseSize);
            go.transform.SetAsLastSibling();

            var img2 = go.GetComponent<Image>();
            img2.sprite = isCar ? GetArrowSprite() : sprite;
            img2.color = tint;
            img2.preserveAspect = true;
            img2.raycastTarget = false;
            _markers[ni] = rt;
            return rt;
        }

        // Procedural up-pointing arrow ("triangle with a notched tail") so
        // the rotation reads as a heading, not a featureless dot. Generated
        // once per process and shared across every car marker. Pixels white
        // — the Image component tints to the car's chosen colour.
        static Sprite s_arrowSprite;
        static Sprite GetArrowSprite()
        {
            if (s_arrowSprite != null) return s_arrowSprite;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "MinimapArrow",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[size * size];
            // Triangle apex at (cx, top); base from (cx - bw, baseY) to (cx + bw, baseY).
            // Notch carved out of the back so the arrow reads as a chevron.
            float cx = (size - 1) * 0.5f;
            float top = size * 0.05f;
            float bottom = size * 0.95f;
            float halfBase = size * 0.42f;
            float notchDepth = size * 0.30f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (y - top) / (bottom - top);   // 0 at apex, 1 at base
                if (u < 0f || u > 1f) { px[y * size + x] = new Color32(255, 255, 255, 0); continue; }
                float widthAt = halfBase * u;
                float dx = Mathf.Abs(x - cx);
                bool inTri = dx <= widthAt;
                // Notch: a triangle cut from the bottom-back of the arrow,
                // running from (cx, bottom - notchDepth) up to the bottom edges.
                float notchU = (y - (bottom - notchDepth)) / notchDepth; // 0 at notch top, 1 at base
                bool inNotch = notchU >= 0f && notchU <= 1f && dx <= halfBase * u * notchU;
                bool fill = inTri && !inNotch;
                // Edge antialias — fade alpha within 1 pixel of the triangle border.
                float edge = widthAt - dx;
                float alpha = fill ? Mathf.Clamp01(edge) : 0f;
                if (inNotch)
                {
                    float notchEdge = (halfBase * u * notchU) - dx;
                    alpha = Mathf.Clamp01(-notchEdge); // outside notch wins
                    if (!inTri) alpha = 0f;
                    else alpha = Mathf.Min(Mathf.Clamp01(edge), Mathf.Clamp01(-notchEdge));
                }
                byte a = (byte)(alpha * 255f);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px); tex.Apply(false, false);
            s_arrowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            s_arrowSprite.name = "MinimapArrow";
            return s_arrowSprite;
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
            // +180° flip: the procedural arrow sprite was authored with its
            // apex pointing UP, but the in-game car's "forward" reads more
            // intuitively when the chevron POINTS along the heading rather
            // than away from it. Rotating the marker an extra half-turn
            // matches what users expect on a top-down minimap.
            marker.localRotation = Quaternion.Euler(0, 0, -angleDeg + 180f);
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
