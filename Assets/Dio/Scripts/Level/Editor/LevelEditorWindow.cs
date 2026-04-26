using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dio.Level.EditorTools
{
    /// Tools → Dio → Open Level Editor.
    ///
    /// Author a track on a Globe (world.fbx). Every edge between two anchors
    /// is a spherical cubic Bezier (slerp-based De Casteljau, samples stay on
    /// the unit sphere); the radial distance at each sample comes from a
    /// raycast against the Globe mesh, so anchors hug the actual terrain
    /// rather than an idealised sphere.
    ///
    /// Each anchor stores `inHandle` (control on every edge arriving at it)
    /// and `outHandle` (control on every edge leaving it) as unit directions
    /// from the planet center — shared across all incoming / outgoing edges
    /// so a fork retains tangent continuity automatically.
    ///
    /// Interactions:
    ///   * Drag empty space (LMB or RMB): orbit camera.
    ///   * Wheel: zoom.
    ///   * Click handle dot: drag the handle. The partner handle on the same
    ///     anchor is reflected through the tangent plane to maintain C1.
    ///   * Click anchor: drag the anchor. Both handles ride along via a
    ///     spherical rigid rotation `FromToRotation(oldDir, newDir)`.
    ///   * Shift held: yOffset arrows appear at every anchor; click+drag one
    ///     to lift that anchor above the terrain. Visibility is gated solely
    ///     on the live shift state; key events trigger a repaint so the
    ///     arrows snap on/off cleanly.
    ///   * Ctrl + click endpoint: append (or prepend) a new anchor and drag it.
    ///   * Del: remove the currently dragged anchor (chain must keep ≥2).
    ///
    /// The Globe (world.fbx) is rendered as actual 3D geometry through a
    /// PreviewRenderUtility — track handles are drawn as a UI overlay on top
    /// without depth testing (back-hemisphere points are filtered out instead
    /// of occluded). The Globe is auto-fitted to the level's `circumference`
    /// (designer-facing) on every render. No placeholder sphere — what you
    /// see is the actual asset cars will drive on.
    public class LevelEditorWindow : EditorWindow
    {
        const int BezierSegments = 28;
        const float HandlePickRadius = 14f;
        const float AnchorPickRadius = 22f;
        const float HandleDotRadius = 7f;
        const float AnchorDotRadius = 16f;

        public LevelData level;

        // PreviewRenderUtility renders the Globe (and a translucent track
        // ribbon) into an off-screen RT. We then `GUI.DrawTexture` it into
        // the editor view and overlay handles on top with the same camera math.
        PreviewRenderUtility _pru;

        // Cached snapshots + raycaster. Recomputed only when the source
        // asset or circumference changes — the raycaster does software
        // ray-vs-triangle queries against the snapshots so anchor heights
        // can be resolved without spawning the Globe into a real scene.
        readonly List<MeshSnapshot> _snapshots = new List<MeshSnapshot>();
        GameObject _cachedSource;
        float _cachedCircumference;
        GlobeRaycaster _raycaster;

        // Cached track ribbon mesh for the preview — rebuilt whenever the
        // level changes structurally. We use the real TrackBuilder so the
        // editor matches what cars will drive on.
        Mesh _previewTrackMesh;
        Material _previewTrackMat;
        // Outer-only guard skirts — show road elevation without solid walls
        // hiding the track surface.
        Mesh _previewGuardLeft, _previewGuardRight;
        Material _previewGuardMat;
        int _previewLevelHash;

        // Per-anchor radial height cache. Computing these via raycaster on
        // every OnGUI was the editor's #1 perf hit — N anchors × the FBX's
        // few-thousand triangles per `ResolveSurfaceRadius` call, every
        // frame. Now they recompute only when the level structurally
        // changes (same key as the preview track rebuild) or when the
        // raycaster itself changes.
        readonly System.Collections.Generic.Dictionary<int, float> _anchorHeightCache = new System.Collections.Generic.Dictionary<int, float>();
        int _heightCacheLevelHash;
        GlobeRaycaster _heightCacheRaycaster;

        float CachedAnchorHeight(int anchorIdx)
        {
            if (_heightCacheLevelHash != _previewLevelHash || _heightCacheRaycaster != _raycaster)
            {
                _anchorHeightCache.Clear();
                _heightCacheLevelHash = _previewLevelHash;
                _heightCacheRaycaster = _raycaster;
            }
            if (_anchorHeightCache.TryGetValue(anchorIdx, out float h)) return h;
            h = level.ResolveHeight(anchorIdx, _raycaster);
            _anchorHeightCache[anchorIdx] = h;
            return h;
        }

        // Cached SURFACE radius (= without yOffset). Used by the yOffset
        // arrow path so it can show "from surface to lifted position" cleanly.
        float CachedAnchorSurfaceR(int anchorIdx)
        {
            float h = CachedAnchorHeight(anchorIdx);
            return h - Mathf.Max(0f, level.points[anchorIdx].yOffset);
        }

        // Camera state. Stored as a full Quaternion (not yaw/pitch) so the
        // user can sweep across poles freely.
        Quaternion _orientation = Quaternion.LookRotation(
            -new Vector3(0.7f, 0.5f, 0.5f).normalized,
            Vector3.up);
        float _distance = 600f;
        Vector2 _lastMouse;
        Rect _viewRect;

        // Drag state.
        enum DragKind { None, Anchor, InHandle, OutHandle, Orbit, ShiftExtend, YOffset, Fork }
        DragKind _dragKind = DragKind.None;
        int _draggedIndex = -1;
        // Source anchor when forking. `_draggedIndex` is the NEW (cloned) point's
        // index; `_forkSourceIdx` is the original anchor whose outgoing edge we
        // just added. We keep them separate so the fork drag handler doesn't
        // overwrite the source's shared outHandle.
        int _forkSourceIdx = -1;
        // For C1 reflection: cache partner's angular distance at drag-start
        // so we preserve its "magnitude" while flipping its tangent.
        int _partnerAnchorIdx = -1;
        DragKind _partnerKind = DragKind.None;
        float _partnerAngularDist = 0f;

        [MenuItem("Tools/Dio/Open Level Editor")]
        public static void Open()
        {
            var w = GetWindow<LevelEditorWindow>("Dio Level Editor");
            w.minSize = new Vector2(700, 480);
        }

        [MenuItem("Tools/Dio/New Level")]
        public static void NewLevel()
        {
            var asset = ScriptableObject.CreateInstance<LevelData>();
            asset.circumference = 2f * Mathf.PI * 200f;   // legacy 200u radius default
            asset.trackRatio = 1f;
            asset.points.Add(new TrackPoint { directionFromCenter = Vector3.up });
            asset.points.Add(new TrackPoint { directionFromCenter = Random.onUnitSphere });
            EnsureDefaultHandles(asset);
            asset.EnsureLinearChainNext();

            const string dir = "Assets/Dio/Levels";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/Dio", "Levels");

            string path = AssetDatabase.GenerateUniqueAssetPath($"{dir}/Level.asset");
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            var w = GetWindow<LevelEditorWindow>("Dio Level Editor");
            w.level = asset;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        void OnEnable()
        {
            EnsurePreviewUtility();
            // Backfill handles + adjacency on a level loaded from disk that
            // pre-dates one of those features. Idempotent.
            if (level != null) { EnsureDefaultHandles(level); level.EnsureLinearChainNext(); }
            // Repaint on any modifier-key change so shift-toggled arrows
            // snap on/off without needing mouse motion.
            wantsMouseEnterLeaveWindow = true;
        }

        void OnDisable()
        {
            if (_pru != null) { _pru.Cleanup(); _pru = null; }
        }

        void EnsurePreviewUtility()
        {
            if (_pru != null) return;
            _pru = new PreviewRenderUtility();
            _pru.cameraFieldOfView = 60f;
            _pru.camera.nearClipPlane = 0.1f;
            _pru.camera.farClipPlane = 100000f;
            _pru.camera.clearFlags = CameraClearFlags.Color;
            _pru.camera.backgroundColor = new Color(0.10f, 0.11f, 0.16f, 1f);
            _pru.lights[0].intensity = 1.05f;
            _pru.lights[0].transform.rotation = Quaternion.Euler(40f, -25f, 0f);
            _pru.lights[1].intensity = 0.45f;
            _pru.lights[1].transform.rotation = Quaternion.Euler(-30f, 140f, 0f);
            _pru.ambientColor = new Color(0.32f, 0.34f, 0.42f, 1f);
        }

        // Refresh the snapshot cache + raycaster if the Globe asset or the
        // level's circumference has changed since last call.
        void EnsureSnapshots()
        {
            var src = GlobeFactory.LoadSourceAsset();
            float circ = level != null ? level.circumference : 0f;
            if (_cachedSource == src && Mathf.Approximately(_cachedCircumference, circ) && _raycaster != null)
                return;

            _cachedSource = src;
            _cachedCircumference = circ;
            _snapshots.Clear();
            if (src == null || level == null)
            {
                _raycaster = null;
                return;
            }
            float scale = GlobeFactory.ComputeAutofitScale(src, circ);
            _snapshots.AddRange(GlobeFactory.CollectSnapshots(src, scale));
            _raycaster = new GlobeRaycaster(_snapshots, level.NominalRadius);

            Debug.Log($"[Dio Level Editor] Globe loaded: '{src.name}', " +
                      $"{_snapshots.Count} sub-meshes, circumference={circ:F1}u, " +
                      $"autofit scale={scale:F3}, nominal radius={level.NominalRadius:F1}u");
        }

        // Persistent scratch buffers — TrackBuilder reuses these instead of
        // allocating fresh `List<>`s on every rebuild. The drag loop hits
        // this path on every mouse-move, so the GC saving is significant.
        readonly TrackBuilderScratch _ribbonScratch = new TrackBuilderScratch();
        readonly TrackBuilderScratch _guardScratch  = new TrackBuilderScratch();

        // Rebuild the cached preview track mesh if the level data has changed.
        // Hashing the points list is enough — anchor moves / handle drags /
        // yOffset edits all touch it. While the user is mid-drag we skip the
        // (relatively expensive) guard pass and run the ribbon at quarter
        // resolution; the cheap polyline overlay still reads accurately and
        // the full mesh refreshes the moment the drag ends.
        void EnsurePreviewTrack()
        {
            if (level == null) { _previewTrackMesh = null; return; }
            int hash = ComputeLevelHash(level);
            bool dragging = _dragKind != DragKind.None && _dragKind != DragKind.Orbit;
            int wantSegments = dragging
                ? TrackBuilder.EditorPreviewSegmentsPerArc / 2  // ultra-cheap during drag
                : TrackBuilder.EditorPreviewSegmentsPerArc;
            // Guards are expensive; only rebuild them when idle. While the
            // user is dragging, keep showing the previous (stale) guard mesh.
            // It snaps back into place the moment the mouse releases.
            if (_previewTrackMesh != null && hash == _previewLevelHash && wantSegments == _previewLevelSegments) return;

            _previewLevelHash = hash;
            _previewLevelSegments = wantSegments;
            _previewTrackMesh = TrackBuilder.Build(level, _raycaster,
                surfaceLift: 0.05f,
                segmentsPerArc: wantSegments,
                scratch: _ribbonScratch);

            if (!dragging)
            {
                var guardOpt = TrackBuilder.GuardOptions.Default;
                guardOpt.crossSection   = TrackBuilder.GuardCrossSection.OuterSkirtOnly;
                guardOpt.floorAtSurface = true;
                guardOpt.floorPad       = TrackBuilder.DefaultFloorPad;
                guardOpt.outwardOffset  = 0.05f;
                guardOpt.addEndcaps     = false;
                (_previewGuardLeft, _previewGuardRight) = TrackBuilder.BuildGuards(
                    level, _raycaster, guardOpt,
                    segmentsPerArc: wantSegments,
                    scratch: _guardScratch);
            }

            if (_previewTrackMat == null)
            {
                Shader sh = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
                _previewTrackMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, name = "DioTrackPreview" };
                _previewTrackMat.color = new Color(0.20f, 0.22f, 0.26f, 1f);
            }
            if (_previewGuardMat == null)
            {
                Shader sh = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
                _previewGuardMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, name = "DioGuardPreview" };
                _previewGuardMat.color = new Color(0.32f, 0.30f, 0.36f, 1f);
            }
        }
        int _previewLevelSegments = -1;

        static int ComputeLevelHash(LevelData lv)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + lv.points.Count;
                h = h * 31 + lv.circumference.GetHashCode();
                h = h * 31 + lv.trackWidth.GetHashCode();
                h = h * 31 + lv.trackRatio.GetHashCode();
                for (int i = 0; i < lv.points.Count; i++)
                {
                    var p = lv.points[i];
                    h = h * 31 + p.directionFromCenter.GetHashCode();
                    h = h * 31 + p.inHandle.GetHashCode();
                    h = h * 31 + p.outHandle.GetHashCode();
                    h = h * 31 + p.yOffset.GetHashCode();
                }
                return h;
            }
        }

        void OnGUI()
        {
            // Toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginChangeCheck();
            level = (LevelData)EditorGUILayout.ObjectField("Level", level, typeof(LevelData), false);
            if (EditorGUI.EndChangeCheck() && level != null)
            {
                EnsureDefaultHandles(level);
                level.EnsureLinearChainNext();
            }
            if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(60))) NewLevel();
            GUILayout.FlexibleSpace();
            if (level != null)
                EditorGUILayout.LabelField($"C={level.circumference:0}  pts={level.points.Count}", GUILayout.Width(160));
            EditorGUILayout.EndHorizontal();

            if (level == null)
            {
                EditorGUILayout.HelpBox("Create or pick a Level asset to begin.", MessageType.Info);
                return;
            }

            EditorGUI.BeginChangeCheck();

            // Display name + uniqueness check. Empty field auto-fills to the
            // file name on first scan; a user-typed name that collides with
            // another level is shown in red and refused on save (we still
            // accept the typed value into the field so the user can correct).
            string newName = EditorGUILayout.TextField("Display name", level.displayName ?? string.Empty);
            if (newName != level.displayName)
            {
                level.displayName = newName;
            }
            if (!string.IsNullOrWhiteSpace(level.displayName)
                && Dio.Level.LevelLibrary.IsDisplayNameTaken(level, level.displayName))
            {
                EditorGUILayout.HelpBox(
                    $"Display name \"{level.displayName}\" collides with another level. " +
                    "Pick a unique name — the network catalog keys by (owner, displayName).",
                    MessageType.Error);
            }

            level.circumference = Mathf.Max(1f, EditorGUILayout.FloatField("Circumference", level.circumference));
            level.trackWidth = EditorGUILayout.FloatField("Track width (canonical)", level.trackWidth);
            level.trackRatio = Mathf.Max(0.05f, EditorGUILayout.FloatField("Track width ratio", level.trackRatio));
            EditorGUILayout.LabelField("    Effective width", $"{level.EffectiveTrackWidth:F2}u");
            if (EditorGUI.EndChangeCheck()) Save();

            Rect view = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            HandleViewInput(view);
            DrawScene(view);

            HandleKeyboard();

            EditorGUILayout.LabelField(
                "Drag empty: orbit  ·  Wheel: zoom  ·  Click anchor / handle: drag  ·  Hold Shift: yOffset arrows  ·  Ctrl+endpoint: extend track  ·  Del: remove anchor",
                EditorStyles.miniLabel);
        }

        void HandleKeyboard()
        {
            var e = Event.current;
            // Repaint on shift press / release so the yOffset arrows toggle
            // visibility immediately. Without this, the editor only repaints
            // on mouse motion / focus changes — leaving the arrows in a
            // stale "phantom" state when the user just taps shift.
            if (e.type == EventType.KeyDown || e.type == EventType.KeyUp)
            {
                if (e.keyCode == KeyCode.LeftShift || e.keyCode == KeyCode.RightShift)
                    Repaint();
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Delete)
            {
                if (_dragKind == DragKind.Anchor && _draggedIndex >= 0 && level.points.Count > 2)
                {
                    Undo.RecordObject(level, "Delete Point");
                    level.points.RemoveAt(_draggedIndex);
                    // Repair adjacency lists referencing the deleted index.
                    int dead = _draggedIndex;
                    for (int i = 0; i < level.points.Count; i++)
                    {
                        var p = level.points[i];
                        if (p.next == null) continue;
                        for (int k = p.next.Count - 1; k >= 0; k--)
                        {
                            int v = p.next[k];
                            if (v == dead) p.next.RemoveAt(k);
                            else if (v > dead) p.next[k] = v - 1;
                        }
                        level.points[i] = p;
                    }
                    level.EnsureLinearChainNext();
                    _draggedIndex = -1;
                    _dragKind = DragKind.None;
                    Save();
                    Repaint();
                    e.Use();
                }
            }
        }

        // --- input ---

        void HandleViewInput(Rect rect)
        {
            _viewRect = rect;

            Event e = Event.current;
            int controlID = GUIUtility.GetControlID(FocusType.Passive);

            if (rect.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDown)
                {
                    _lastMouse = e.mousePosition;
                    if (e.button == 0)
                    {
                        var pick = PickAtMouse(e.mousePosition);
                        if (pick.kind == DragKind.Anchor && e.shift)
                        {
                            BeginYOffsetDrag(pick.index);
                        }
                        else if (pick.kind == DragKind.Anchor && e.control && IsEndpoint(pick.index))
                        {
                            BeginExtendFromEndpoint(pick.index, e.mousePosition);
                        }
                        else if (pick.kind != DragKind.None)
                        {
                            BeginPickedDrag(pick);
                        }
                        else
                        {
                            _dragKind = DragKind.Orbit;
                            _draggedIndex = -1;
                        }
                        GUIUtility.hotControl = controlID;
                        e.Use();
                    }
                    else if (e.button == 1)
                    {
                        // Right-click on a non-endpoint anchor = fork the
                        // track from there to a new dragged point. Right-
                        // click on empty space (or on the start/finish) =
                        // orbit camera.
                        var pick = PickAtMouse(e.mousePosition);
                        if (pick.kind == DragKind.Anchor && !IsEndpoint(pick.index))
                        {
                            BeginForkFromAnchor(pick.index, e.mousePosition);
                        }
                        else
                        {
                            _dragKind = DragKind.Orbit;
                        }
                        GUIUtility.hotControl = controlID;
                        e.Use();
                    }
                }
                else if (e.type == EventType.ScrollWheel)
                {
                    float r = level.NominalRadius;
                    _distance = Mathf.Clamp(_distance * (1f + e.delta.y * 0.05f), r * 1.05f, r * 12f);
                    Repaint();
                    e.Use();
                }
            }

            if (GUIUtility.hotControl == controlID && e.type == EventType.MouseDrag)
            {
                Vector2 d = e.mousePosition - _lastMouse;
                _lastMouse = e.mousePosition;

                switch (_dragKind)
                {
                    case DragKind.Orbit:
                        ApplyOrbitDrag(d, e.button);
                        Repaint();
                        break;

                    case DragKind.Anchor:
                        if (_draggedIndex >= 0 && PickDirection(e.mousePosition, out Vector3 dirA))
                        {
                            MoveAnchorRigid(_draggedIndex, dirA);
                            Save(); Repaint();
                        }
                        break;

                    case DragKind.ShiftExtend:
                        if (_draggedIndex >= 0 && PickDirection(e.mousePosition, out Vector3 dirE))
                        {
                            SetAnchorDirOnly(_draggedIndex, dirE);
                            ResetExtendHandles(_draggedIndex);
                            Save(); Repaint();
                        }
                        break;

                    case DragKind.InHandle:
                    case DragKind.OutHandle:
                        if (_draggedIndex >= 0 && PickDirection(e.mousePosition, out Vector3 dirH))
                        {
                            // Direct cursor-to-sphere projection. PickDirection
                            // already returns a unit direction (mesh hit
                            // normalised, or the great-circle approach if the
                            // cursor's in empty space), so the handle dot
                            // tracks the cursor 1:1 around the planet — same
                            // way an anchor drag does.
                            //
                            // The handle stores ONLY the polar direction;
                            // its rendered Y comes from the anchor's
                            // resolved height (see CachedAnchorHeight). So
                            // dragging rotates the handle around the
                            // trackpoint without ever needing a radial
                            // bias mix to keep it "close" — the height is
                            // re-applied at render time. C1 partner
                            // reflection still runs to keep the opposite
                            // handle in sync.
                            SetHandle(_draggedIndex, _dragKind, dirH);
                            ReflectPartnerHandle(_draggedIndex, _dragKind);
                            Save(); Repaint();
                        }
                        break;

                    case DragKind.YOffset:
                        if (_draggedIndex >= 0)
                        {
                            DragYOffset(_draggedIndex, e.mousePosition);
                            Save(); Repaint();
                        }
                        break;

                    case DragKind.Fork:
                        if (_draggedIndex >= 0 && _forkSourceIdx >= 0 && PickDirection(e.mousePosition, out Vector3 dirF))
                        {
                            SetAnchorDirOnly(_draggedIndex, dirF);
                            // The clone keeps the source's handles verbatim;
                            // dragging it doesn't reset them. Visual continuity
                            // is preserved because every connection the source
                            // had now also exists from / to the clone.
                            Save(); Repaint();
                        }
                        break;
                }
                e.Use();
            }

            if (e.type == EventType.MouseUp && GUIUtility.hotControl == controlID)
            {
                GUIUtility.hotControl = 0;
                _dragKind = DragKind.None;
                _draggedIndex = -1;
                _partnerAnchorIdx = -1;
                _forkSourceIdx = -1;
                e.Use();
            }
        }

        // Right-click drag from an interior anchor: clones the anchor at the
        // drag location, adds an outgoing edge from the source to the clone.
        // The source's outHandle is REUSED (shared) by the new edge — that's
        // the user-spec'd "control points shared between original and new".
        // The new clone is a leaf (no outgoing) until the user wires it up.
        // Right-click drag on a non-endpoint anchor creates a PARALLEL branch
        // through a clone of that anchor. The clone duplicates the source's
        // entire topology (handles + incoming + outgoing connections) so
        // dragging it produces a parallel path that diverges and rejoins
        // cleanly — no graph-breaking, no orphans, no leaves left behind.
        //
        // Concretely: if `B → source → C` exists, we end up with both
        // `B → source → C` and `B → clone → C`. As the user drags the clone
        // it pulls one of those parallel paths into a new shape; the source
        // stays put on the original line.
        void BeginForkFromAnchor(int sourceIdx, Vector2 mouse)
        {
            Undo.RecordObject(level, "Fork Track");
            var source = level.points[sourceIdx];

            // Clone every field — handles AND outgoing edges are copied so
            // the clone reaches every downstream the source reaches.
            var clone = new TrackPoint
            {
                directionFromCenter = source.directionFromCenter,
                yOffset = source.yOffset,
                bank = source.bank,
                inHandle = source.inHandle,
                outHandle = source.outHandle,
                next = source.next != null ? new List<int>(source.next) : new List<int>(),
            };
            int newIdx = level.points.Count;
            level.points.Add(clone);

            // Mirror INCOMING connections too — every anchor that pointed to
            // `source` now also points to `clone`, so the clone is reachable
            // from upstream without breaking the existing chain.
            for (int i = 0; i < newIdx; i++)
            {
                var p = level.points[i];
                if (p.next == null) continue;
                if (p.next.Contains(sourceIdx) && !p.next.Contains(newIdx))
                {
                    p.next.Add(newIdx);
                    level.points[i] = p;
                }
            }

            _dragKind = DragKind.Fork;
            _draggedIndex = newIdx;
            _forkSourceIdx = sourceIdx;

            if (PickDirection(mouse, out Vector3 dir))
                SetAnchorDirOnly(newIdx, dir);
            Save();
        }

        struct Pick { public DragKind kind; public int index; }

        // Pick handles first (smaller, drawn on top), then anchors. Use the
        // raycaster to lift dots to their actual rendered position so picking
        // matches what the user sees.
        Pick PickAtMouse(Vector2 mouse)
        {
            Vector3 camDir = ComputeCameraPosition().normalized;

            float bestDist2 = HandlePickRadius * HandlePickRadius;
            Pick best = new Pick { kind = DragKind.None, index = -1 };

            for (int i = 0; i < level.points.Count; i++)
            {
                var p = level.points[i];
                bool hasIn = p.inHandle.sqrMagnitude > 1e-6f;
                bool hasOut = p.outHandle.sqrMagnitude > 1e-6f;
                float anchorH = level.ResolveHeight(i, _raycaster);

                if (hasIn && Vector3.Dot(p.inHandle.normalized, camDir) > 0f
                    && TryWorldToScreen(p.inHandle.normalized * anchorH, out Vector2 sIn))
                {
                    float d = (sIn - mouse).sqrMagnitude;
                    if (d < bestDist2) { bestDist2 = d; best = new Pick { kind = DragKind.InHandle, index = i }; }
                }
                if (hasOut && Vector3.Dot(p.outHandle.normalized, camDir) > 0f
                    && TryWorldToScreen(p.outHandle.normalized * anchorH, out Vector2 sOut))
                {
                    float d = (sOut - mouse).sqrMagnitude;
                    if (d < bestDist2) { bestDist2 = d; best = new Pick { kind = DragKind.OutHandle, index = i }; }
                }
            }

            if (best.kind != DragKind.None) return best;

            float anchorDist2 = AnchorPickRadius * AnchorPickRadius;
            for (int i = 0; i < level.points.Count; i++)
            {
                Vector3 dir = level.DirOf(i);
                if (Vector3.Dot(dir, camDir) <= 0f) continue;
                float anchorH = level.ResolveHeight(i, _raycaster);
                Vector3 wp = dir * anchorH;
                if (TryWorldToScreen(wp, out Vector2 sp))
                {
                    float d = (sp - mouse).sqrMagnitude;
                    if (d < anchorDist2) { anchorDist2 = d; best = new Pick { kind = DragKind.Anchor, index = i }; }
                }
            }
            return best;
        }

        bool IsEndpoint(int i)
        {
            if (i == 0) return true;
            if (i == level.points.Count - 1) return true;
            // A dangling node with no outgoing edges qualifies too — same
            // behaviour but graph-aware (matters once branches arrive).
            var nx = level.points[i].next;
            return nx == null || nx.Count == 0;
        }

        void ApplyOrbitDrag(Vector2 mouseDelta, int mouseButton)
        {
            const float Sensitivity = 0.4f;
            ComputeCameraBasis(out Vector3 _, out Vector3 fwd, out Vector3 right, out Vector3 up);

            if (mouseButton == 0)
            {
                Quaternion qx = Quaternion.AngleAxis(mouseDelta.x * Sensitivity, up);
                Quaternion qy = Quaternion.AngleAxis(mouseDelta.y * Sensitivity, right);
                _orientation = qy * qx * _orientation;
                _orientation.Normalize();
            }
            else if (mouseButton == 1)
            {
                Vector2 center = _viewRect.center;
                Vector2 fromCenter = _lastMouse - center;
                float radius = Mathf.Max(1f, Mathf.Min(_viewRect.width, _viewRect.height) * 0.5f);
                float torque = (fromCenter.x * mouseDelta.y - fromCenter.y * mouseDelta.x) / radius;
                Quaternion qz = Quaternion.AngleAxis(torque * Sensitivity, fwd);
                _orientation = qz * _orientation;
                _orientation.Normalize();
            }
        }

        void BeginPickedDrag(Pick p)
        {
            Undo.RecordObject(level, "Drag");
            _dragKind = p.kind;
            _draggedIndex = p.index;
            _partnerAnchorIdx = -1;
            if (p.kind == DragKind.InHandle || p.kind == DragKind.OutHandle)
                CachePartnerForReflection(p.index, p.kind);
        }

        void BeginYOffsetDrag(int anchorIndex)
        {
            Undo.RecordObject(level, "YOffset Drag");
            _dragKind = DragKind.YOffset;
            _draggedIndex = anchorIndex;
            _partnerAnchorIdx = -1;
        }

        // Drag the cursor along the radial line from the anchor's surface
        // position outward; the projected distance becomes the new yOffset.
        void DragYOffset(int anchorIndex, Vector2 mouse)
        {
            Vector3 anchorDir = level.DirOf(anchorIndex);
            float surfaceR = _raycaster != null ? _raycaster.ResolveSurfaceRadius(anchorDir) : level.NominalRadius;
            Vector3 basePos = anchorDir * surfaceR;
            Ray mouseRay = ScreenPointToRay(mouse, ComputeCameraPosition());
            float s = ClosestPointOnLineToRay(basePos, anchorDir, mouseRay);
            var p = level.points[anchorIndex];
            p.yOffset = Mathf.Max(0f, s);
            level.points[anchorIndex] = p;
        }

        static float ClosestPointOnLineToRay(Vector3 lineOrigin, Vector3 lineDir, Ray ray)
        {
            Vector3 w0 = lineOrigin - ray.origin;
            float a = Vector3.Dot(lineDir, lineDir);
            float b = Vector3.Dot(lineDir, ray.direction);
            float c = Vector3.Dot(ray.direction, ray.direction);
            float d = Vector3.Dot(lineDir, w0);
            float ee = Vector3.Dot(ray.direction, w0);
            float denom = a * c - b * b;
            if (Mathf.Abs(denom) < 1e-6f) return 0f;
            return (b * ee - c * d) / denom;
        }

        void CachePartnerForReflection(int anchor, DragKind draggedKind)
        {
            var p = level.points[anchor];
            Vector3 P = p.directionFromCenter.normalized;
            Vector3 partner = (draggedKind == DragKind.OutHandle) ? p.inHandle : p.outHandle;
            bool partnerExists = partner.sqrMagnitude > 1e-6f;
            // Endpoints can lack a partner — first anchor has no inHandle,
            // last has no outHandle. Skip reflection then.
            bool hasPartnerSide = partnerExists;
            if (hasPartnerSide)
            {
                _partnerAngularDist = Mathf.Acos(Mathf.Clamp(Vector3.Dot(P, partner.normalized), -1f, 1f));
                _partnerAnchorIdx = anchor;
                _partnerKind = (draggedKind == DragKind.OutHandle) ? DragKind.InHandle : DragKind.OutHandle;
            }
        }

        // Ctrl + click an endpoint extends the track. The user wants to keep
        // dragging the SAME anchor they clicked; the OLD position becomes a
        // new interior clone that sits between the previous chain and the
        // dragged anchor. So:
        //   * Insert a clone at the endpoint's index, shifting the endpoint
        //     to a higher index.
        //   * The chain rewires automatically because we adjust every `next`
        //     entry that pointed to the OLD endpoint index — they now point
        //     to the clone (= same index, replaced entity), and the clone's
        //     own `next` is a fresh list pointing to the (shifted) old endpoint.
        //   * We drag the OLD endpoint at its NEW (shifted) index, so the
        //     mouse keeps the anchor the user clicked, never an orphan clone.
        void BeginExtendFromEndpoint(int endpoint, Vector2 mouse)
        {
            Undo.RecordObject(level, "Extend Track");
            int oldCount = level.points.Count;
            var oldEndpoint = level.points[endpoint];

            // Build the clone with appropriate handles preserved:
            //   * Prepend: the OLD start had outHandle pointing at the old
            //     chain. The CLONE becomes the new start (root, no inHandle).
            //     We zero BOTH handles on the clone — the OLD start retains
            //     its original outHandle for the now-existing edge into the
            //     old chain.
            //   * Append: the OLD finish had inHandle pointing at the old
            //     chain. The CLONE keeps that inHandle (so the segment from
            //     [..,B] into the clone is unchanged), and clears outHandle
            //     (we'll set it below via C1 reflection so the new edge
            //     CLONE→DRAGGED_OLD_FINISH continues the existing tangent).
            var clone = oldEndpoint;
            clone.next = new List<int>();
            if (endpoint == 0)
            {
                clone.inHandle = Vector3.zero;
                clone.outHandle = Vector3.zero;
            }
            else
            {
                // clone.inHandle preserved (= old chain shape).
                clone.outHandle = Vector3.zero;
            }

            int draggedIdx;
            if (endpoint == 0)
            {
                level.points.Insert(0, clone);
                draggedIdx = 1;
                for (int i = 0; i < level.points.Count; i++)
                {
                    var pp = level.points[i];
                    if (pp.next != null)
                        for (int k = 0; k < pp.next.Count; k++) pp.next[k] += 1;
                    level.points[i] = pp;
                }
                var c = level.points[0];
                if (c.next == null) c.next = new List<int>();
                if (!c.next.Contains(1)) c.next.Add(1);
                level.points[0] = c;
                // The OLD start (now at index 1, dragged) had no inHandle.
                // We'll set it via C1 reflection in ResetExtendHandles using
                // the still-preserved outHandle as the authoritative side.
            }
            else
            {
                level.points.Insert(endpoint, clone);
                draggedIdx = endpoint + 1;
                for (int i = 0; i < level.points.Count; i++)
                {
                    if (i == endpoint) continue;
                    var pp = level.points[i];
                    if (pp.next != null)
                        for (int k = 0; k < pp.next.Count; k++)
                            if (pp.next[k] > endpoint) pp.next[k] += 1;
                    level.points[i] = pp;
                }
                var c = level.points[endpoint];
                if (c.next == null) c.next = new List<int>();
                if (!c.next.Contains(endpoint + 1)) c.next.Add(endpoint + 1);
                level.points[endpoint] = c;
                // Clear the dragged old endpoint's inHandle — it was the
                // bezier control on the OLD segment B→X. The new edge
                // CLONE→OLD_X is a different bezier; ResetExtendHandles will
                // set a fresh inHandle for it.
                var o = level.points[endpoint + 1];
                o.inHandle = Vector3.zero;
                level.points[endpoint + 1] = o;
            }

            level.EnsureLinearChainNext();

            _dragKind = DragKind.ShiftExtend;
            _draggedIndex = draggedIdx;
            if (PickDirection(mouse, out Vector3 dir))
            {
                SetAnchorDirOnly(draggedIdx, dir);
                ResetExtendHandles(draggedIdx);
            }

            int reachableCount = CountReachableFromAnchor0();
            if (reachableCount != level.points.Count)
                Debug.LogWarning($"[Dio Level Editor] Extend left {level.points.Count - reachableCount} orphan anchor(s). prevCount={oldCount} newCount={level.points.Count}");
            Save();
        }

        // Walk forward from anchor 0 and count distinct reachable anchors.
        // Used for orphan detection after structural mutations.
        int CountReachableFromAnchor0()
        {
            if (level == null || level.points.Count == 0) return 0;
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                if (!seen.Add(cur)) continue;
                var nx = level.points[cur].next;
                if (nx == null) continue;
                for (int k = 0; k < nx.Count; k++) stack.Push(nx[k]);
            }
            return seen.Count;
        }

        void MoveAnchorRigid(int i, Vector3 newDir)
        {
            var p = level.points[i];
            Vector3 oldDir = p.directionFromCenter.sqrMagnitude > 1e-6f
                ? p.directionFromCenter.normalized : Vector3.up;
            if (Vector3.Dot(oldDir, newDir) > 0.99999f) { p.directionFromCenter = newDir; level.points[i] = p; return; }
            Quaternion q = Quaternion.FromToRotation(oldDir, newDir);
            p.directionFromCenter = newDir;
            if (p.inHandle.sqrMagnitude  > 1e-6f) p.inHandle  = (q * p.inHandle.normalized).normalized;
            if (p.outHandle.sqrMagnitude > 1e-6f) p.outHandle = (q * p.outHandle.normalized).normalized;
            level.points[i] = p;
        }

        void SetAnchorDirOnly(int i, Vector3 newDir)
        {
            var p = level.points[i];
            p.directionFromCenter = newDir;
            level.points[i] = p;
        }

        // Set up handles for the new bezier added by extend. Uses the
        // "midpoints opposite" policy: at the joint where the new bezier
        // meets the existing chain, the new control point is the REFLECTION
        // of the preserved one through the tangent plane (C1 continuity),
        // not just a fresh geodesic midpoint. The dragged endpoint gets a
        // simple geodesic midpoint with the joint, since there's no other
        // chain segment past it to constrain.
        void ResetExtendHandles(int newIndex)
        {
            int prevIndex = (newIndex == 0) ? 1 : newIndex - 1;
            Vector3 A = level.DirOf(prevIndex);
            Vector3 B = level.DirOf(newIndex);
            Vector3 fallbackM = Vector3.Slerp(A, B, 0.5f).normalized;

            var prev = level.points[prevIndex];
            var nw   = level.points[newIndex];
            if (newIndex > prevIndex)
            {
                // Append: prev is the inserted clone. clone.IN was preserved
                // from the old chain. Reflect to compute clone.OUT (this is
                // the "midpoints opposite" policy). Dragged.IN: midpoint.
                prev.outHandle = ReflectHandleThroughTangent(prev.inHandle, A);
                if (prev.outHandle.sqrMagnitude < 1e-6f) prev.outHandle = fallbackM;
                nw.inHandle = fallbackM;
            }
            else
            {
                // Prepend: nw is the dragged OLD start (now interior at idx 1).
                // It has the preserved old outHandle (= the segment to the
                // old chain). Reflect to compute its inHandle for C1 across
                // the new joint. Clone.OUT: midpoint with the dragged anchor.
                nw.inHandle = ReflectHandleThroughTangent(nw.outHandle, B);
                if (nw.inHandle.sqrMagnitude < 1e-6f) nw.inHandle = fallbackM;
                prev.outHandle = fallbackM;
            }
            level.points[prevIndex] = prev;
            level.points[newIndex]  = nw;
        }

        // Reflect a unit-direction handle through the tangent plane at
        // `anchorDir` — i.e. flip its tangent component while preserving its
        // angular distance from the anchor. The result is "the opposite of
        // the input handle" in the sense the user described: same magnitude,
        // pointing the other way along the surface.
        static Vector3 ReflectHandleThroughTangent(Vector3 handle, Vector3 anchorDir)
        {
            if (handle.sqrMagnitude < 1e-6f) return Vector3.zero;
            Vector3 P = anchorDir.normalized;
            Vector3 h = handle.normalized;
            Vector3 t = h - Vector3.Dot(h, P) * P;
            if (t.sqrMagnitude < 1e-8f) return Vector3.zero;
            t.Normalize();
            float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(P, h), -1f, 1f));
            return (Mathf.Cos(ang) * P + Mathf.Sin(ang) * (-t)).normalized;
        }

        void SetHandle(int anchor, DragKind kind, Vector3 newDir)
        {
            var p = level.points[anchor];
            if (kind == DragKind.InHandle) p.inHandle = newDir;
            else if (kind == DragKind.OutHandle) p.outHandle = newDir;
            level.points[anchor] = p;
        }

        void ReflectPartnerHandle(int anchor, DragKind draggedKind)
        {
            if (_partnerAnchorIdx != anchor) return;
            var p = level.points[anchor];
            Vector3 P = p.directionFromCenter.normalized;
            Vector3 dragged = (draggedKind == DragKind.InHandle ? p.inHandle : p.outHandle).normalized;
            Vector3 t = dragged - Vector3.Dot(dragged, P) * P;
            if (t.sqrMagnitude < 1e-8f) return;
            t.Normalize();
            float ang = _partnerAngularDist;
            Vector3 partnerDir = Mathf.Cos(ang) * P + Mathf.Sin(ang) * (-t);
            partnerDir.Normalize();
            if (_partnerKind == DragKind.InHandle) p.inHandle = partnerDir;
            else p.outHandle = partnerDir;
            level.points[anchor] = p;
        }

        void EnforceContinuity(int anchor, bool preferReflectInFromOut)
        {
            if (anchor < 0 || anchor >= level.points.Count) return;
            var p = level.points[anchor];
            bool hasIn  = p.inHandle.sqrMagnitude  > 1e-6f;
            bool hasOut = p.outHandle.sqrMagnitude > 1e-6f;
            if (!hasIn || !hasOut) return;

            Vector3 P = p.directionFromCenter.normalized;
            if (preferReflectInFromOut)
            {
                Vector3 outH = p.outHandle.normalized;
                Vector3 t = outH - Vector3.Dot(outH, P) * P;
                if (t.sqrMagnitude < 1e-8f) return;
                t.Normalize();
                float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(P, p.inHandle.normalized), -1f, 1f));
                p.inHandle = (Mathf.Cos(ang) * P + Mathf.Sin(ang) * (-t)).normalized;
            }
            else
            {
                Vector3 inH = p.inHandle.normalized;
                Vector3 t = inH - Vector3.Dot(inH, P) * P;
                if (t.sqrMagnitude < 1e-8f) return;
                t.Normalize();
                float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(P, p.outHandle.normalized), -1f, 1f));
                p.outHandle = (Mathf.Cos(ang) * P + Mathf.Sin(ang) * (-t)).normalized;
            }
            level.points[anchor] = p;
        }

        public static void EnsureDefaultHandles(LevelData lv)
        {
            if (lv == null || !lv.HasMinimum) return;

            for (int i = 0; i < lv.points.Count; i++)
            {
                var p = lv.points[i];
                if (i > 0 && p.inHandle.sqrMagnitude < 1e-6f)
                {
                    Vector3 a = lv.DirOf(i - 1);
                    Vector3 b = lv.DirOf(i);
                    p.inHandle = Vector3.Slerp(a, b, 0.5f).normalized;
                }
                if (i < lv.points.Count - 1 && p.outHandle.sqrMagnitude < 1e-6f)
                {
                    Vector3 a = lv.DirOf(i);
                    Vector3 b = lv.DirOf(i + 1);
                    p.outHandle = Vector3.Slerp(a, b, 0.5f).normalized;
                }
                lv.points[i] = p;
            }

            for (int i = 1; i < lv.points.Count - 1; i++)
            {
                var p = lv.points[i];
                Vector3 P = p.directionFromCenter.normalized;
                Vector3 outH = p.outHandle.normalized;
                Vector3 t = outH - Vector3.Dot(outH, P) * P;
                if (t.sqrMagnitude < 1e-8f) continue;
                t.Normalize();
                float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(P, p.inHandle.normalized), -1f, 1f));
                p.inHandle = (Mathf.Cos(ang) * P + Mathf.Sin(ang) * (-t)).normalized;
                lv.points[i] = p;
            }

            EditorUtility.SetDirty(lv);
        }

        // --- camera / picking math ---

        // Resolve a screen-space mouse position to a unit direction from the
        // planet center — zoom-independent. We try the actual Globe mesh
        // first (so dragging an anchor "lands" wherever the cursor is over
        // the rendered terrain). If the cursor is in empty space outside the
        // silhouette, fall back to "closest point on the camera ray to the
        // origin" — the anchor still tracks the cursor's line of sight, just
        // along the great-circle perpendicular to it. Either way we return a
        // direction; height comes from the raycaster + yOffset elsewhere.
        bool PickDirection(Vector2 mouse, out Vector3 unitDir)
        {
            Vector3 camPos = ComputeCameraPosition();
            Ray ray = ScreenPointToRay(mouse, camPos);

            // Mesh first — this matches what the user sees on screen.
            if (_raycaster != null && _raycaster.Raycast(ray, out Vector3 hit) && hit.sqrMagnitude > 1e-6f)
            {
                unitDir = hit.normalized;
                return true;
            }
            // Empty-space fallback — the great-circle direction the ray comes
            // closest to. Robust at any zoom level (sphere raycasts get jumpy
            // when the camera is close to or inside the silhouette).
            if (GlobeRaycaster.ClosestDirectionFromRay(ray, out unitDir))
                return true;
            unitDir = Vector3.up;
            return false;
        }

        Vector3 ComputeCameraPosition()
        {
            Vector3 forward = _orientation * Vector3.forward;
            return -forward * _distance;
        }

        void ComputeCameraBasis(out Vector3 camPos, out Vector3 fwd, out Vector3 right, out Vector3 up)
        {
            fwd   = _orientation * Vector3.forward;
            right = _orientation * Vector3.right;
            up    = _orientation * Vector3.up;
            camPos = -fwd * _distance;
        }

        Ray ScreenPointToRay(Vector2 mouse, Vector3 camPos)
        {
            Vector2 nm = new Vector2(
                (mouse.x - _viewRect.x) / _viewRect.width * 2f - 1f,
                1f - (mouse.y - _viewRect.y) / _viewRect.height * 2f
            );
            ComputeCameraBasis(out Vector3 _, out Vector3 fwd, out Vector3 right, out Vector3 up);
            float aspect = _viewRect.width / Mathf.Max(1f, _viewRect.height);
            float halfH = Mathf.Tan(30f * Mathf.Deg2Rad);
            float halfW = halfH * aspect;
            Vector3 dir = (fwd + right * (nm.x * halfW) + up * (nm.y * halfH)).normalized;
            return new Ray(camPos, dir);
        }

        bool TryWorldToScreen(Vector3 world, out Vector2 sp)
        {
            ComputeCameraBasis(out Vector3 camPos, out Vector3 fwd, out Vector3 right, out Vector3 up);
            Vector3 toPt = world - camPos;
            float z = Vector3.Dot(toPt, fwd);
            if (z <= 0.01f) { sp = default; return false; }
            float aspect = _viewRect.width / Mathf.Max(1f, _viewRect.height);
            float halfH = Mathf.Tan(30f * Mathf.Deg2Rad);
            float halfW = halfH * aspect;
            float x = Vector3.Dot(toPt, right) / (z * halfW);
            float y = Vector3.Dot(toPt, up) / (z * halfH);
            sp = new Vector2(
                _viewRect.x + (x * 0.5f + 0.5f) * _viewRect.width,
                _viewRect.y + (1f - (y * 0.5f + 0.5f)) * _viewRect.height
            );
            return true;
        }

        // --- drawing ---

        void DrawScene(Rect rect)
        {
            _viewRect = rect;

            // 1) 3D backdrop (Globe + cached track ribbon) via PRU.
            EnsurePreviewUtility();
            EnsureSnapshots();
            EnsurePreviewTrack();
            RenderBackdrop(rect);

            // 2) UI overlay — anchors / handles / beziers — drawn on top with
            // no depth test. Front-hemisphere only so back-side dots can't be
            // accidentally clicked.
            Handles.BeginGUI();

            Vector3 camDir = ComputeCameraPosition().normalized;

            // Beziers between connected anchors. We LERP the two endpoint
            // heights across the bezier, matching what TrackBuilder.Build
            // does — per-sample raycast on the polyline produced visible
            // jitter near steep terrain AND was the editor's biggest
            // per-frame cost (N edges × BezierSegments raycasts every OnGUI).
            foreach (var (i, j) in level.EnumerateEdges())
            {
                level.GetEdge(i, j, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                float startH = CachedAnchorHeight(i);
                float endH   = CachedAnchorHeight(j);
                var pts = new Vector3[BezierSegments + 1];
                for (int s = 0; s <= BezierSegments; s++)
                {
                    float t = (float)s / BezierSegments;
                    Vector3 dir = GeodesicUtil.SphericalBezier(a, ah, bh, b, t);
                    float h = Mathf.Lerp(startH, endH, t);
                    pts[s] = dir * h;
                }
                DrawPolyline(pts, new Color(0.40f, 0.78f, 1.00f, 1f), 3f);
            }

            // Handle leashes — a handle dot sits at its anchor's resolved
            // height (so the leash visualises the tangent direction without
            // also varying with the handle's own raycast result).
            for (int i = 0; i < level.points.Count; i++)
            {
                var p = level.points[i];
                Vector3 anchorDir = level.DirOf(i);
                bool anchorVisible = Vector3.Dot(anchorDir, camDir) > 0f;
                bool hasIn  = p.inHandle.sqrMagnitude  > 1e-6f;
                bool hasOut = p.outHandle.sqrMagnitude > 1e-6f;
                float anchorH = CachedAnchorHeight(i);

                if (anchorVisible && hasIn  && Vector3.Dot(p.inHandle.normalized,  camDir) > 0f)
                    DrawHandleLeash(anchorDir, p.inHandle.normalized, anchorH);
                if (anchorVisible && hasOut && Vector3.Dot(p.outHandle.normalized, camDir) > 0f)
                    DrawHandleLeash(anchorDir, p.outHandle.normalized, anchorH);
            }

            // Anchors — colour by role. Lifted to their resolved height so a
            // yOffset>0 anchor renders visibly above the surface.
            for (int i = 0; i < level.points.Count; i++)
            {
                Vector3 dir = level.DirOf(i);
                if (Vector3.Dot(dir, camDir) <= 0f) continue;
                float anchorH = CachedAnchorHeight(i);
                Vector3 wp = dir * anchorH;
                if (!TryWorldToScreen(wp, out Vector2 sp)) continue;
                Color col = i == 0 ? new Color(1f, 0.30f, 0.30f, 1f)
                          : i == level.points.Count - 1 ? new Color(0.36f, 0.92f, 0.42f, 1f)
                          : new Color(0.95f, 0.95f, 1f, 1f);
                DrawDot(sp, AnchorDotRadius, col);
                GUI.Label(new Rect(sp.x + 14, sp.y - 24, 80, 18), i.ToString(), EditorStyles.whiteMiniLabel);
            }

            // Handle dots — at the anchor's resolved height (NOT the handle's
            // own raycast). This matches the user's spec: control points
            // share the anchor's y, so they don't drift when the anchor's
            // yOffset changes.
            for (int i = 0; i < level.points.Count; i++)
            {
                var p = level.points[i];
                bool hasIn  = p.inHandle.sqrMagnitude  > 1e-6f;
                bool hasOut = p.outHandle.sqrMagnitude > 1e-6f;
                float anchorH = CachedAnchorHeight(i);
                if (hasIn && Vector3.Dot(p.inHandle.normalized, camDir) > 0f
                    && TryWorldToScreen(p.inHandle.normalized * anchorH, out Vector2 sIn))
                    DrawDot(sIn, HandleDotRadius, new Color(1f, 0.85f, 0.30f, 1f));
                if (hasOut && Vector3.Dot(p.outHandle.normalized, camDir) > 0f
                    && TryWorldToScreen(p.outHandle.normalized * anchorH, out Vector2 sOut))
                    DrawDot(sOut, HandleDotRadius, new Color(1f, 0.55f, 0.85f, 1f));
            }

            // yOffset arrows: gated solely on the live shift modifier. The
            // `wantsMouseEnterLeaveWindow` + KeyDown/KeyUp Repaint hook
            // upstream ensures this flips on/off the moment the user hits
            // shift, with no stale frames in between.
            if (Event.current.shift)
            {
                for (int i = 0; i < level.points.Count; i++)
                {
                    Vector3 dir = level.DirOf(i);
                    if (Vector3.Dot(dir, camDir) <= 0f) continue;
                    float surfaceR = CachedAnchorSurfaceR(i);
                    Vector3 basePos = dir * surfaceR;
                    Vector3 liftedPos = basePos + dir * level.points[i].yOffset;
                    if (!TryWorldToScreen(liftedPos, out Vector2 sp)) continue;
                    Vector2 screenUp;
                    {
                        Vector3 wpNext = liftedPos + dir * (level.NominalRadius * 0.08f);
                        if (TryWorldToScreen(wpNext, out Vector2 spNext))
                            screenUp = (spNext - sp).normalized;
                        else
                            screenUp = Vector2.up;
                    }
                    float arrowLen = 28f;
                    Vector2 tip = sp + screenUp * arrowLen;
                    Handles.color = Color.yellow;
                    Handles.DrawAAPolyLine(2.5f, new Vector3[] { new Vector3(sp.x, sp.y, 0), new Vector3(tip.x, tip.y, 0) });
                    Vector2 perp = new Vector2(-screenUp.y, screenUp.x);
                    Handles.DrawAAPolyLine(2.5f, new Vector3[] {
                        new Vector3(tip.x, tip.y, 0),
                        new Vector3(tip.x - screenUp.x * 8f + perp.x * 5f, tip.y - screenUp.y * 8f + perp.y * 5f, 0),
                        new Vector3(tip.x, tip.y, 0),
                        new Vector3(tip.x - screenUp.x * 8f - perp.x * 5f, tip.y - screenUp.y * 8f - perp.y * 5f, 0)
                    });
                    GUI.Label(new Rect(tip.x + 4, tip.y - 10, 60, 16), $"+{level.points[i].yOffset:F1}", EditorStyles.whiteMiniLabel);
                }
            }

            Handles.EndGUI();
        }

        void RenderBackdrop(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (rect.width < 4f || rect.height < 4f) return;

            _pru.BeginPreview(rect, GUIStyle.none);

            ComputeCameraBasis(out Vector3 camPos, out Vector3 fwd, out Vector3 right, out Vector3 up);
            _pru.camera.transform.position = camPos;
            _pru.camera.transform.rotation = Quaternion.LookRotation(fwd, up);
            _pru.camera.fieldOfView = 60f;
            _pru.camera.nearClipPlane = Mathf.Max(0.1f, level.NominalRadius * 0.01f);
            _pru.camera.farClipPlane = level.NominalRadius * 25f;

            // Globe — rendered with its own materials. PreviewRenderUtility
            // can't reliably bind URP shaders, so designers should keep the
            // FBX submeshes on built-in (Standard / Unlit) materials. The
            // submesh material is used as-is; if it's missing we throw so
            // the user sees the asset is broken instead of a silent fail.
            for (int i = 0; i < _snapshots.Count; i++)
            {
                var s = _snapshots[i];
                if (s.mesh == null) continue;
                int subCount = s.mesh.subMeshCount;
                if (s.materials == null || s.materials.Length == 0)
                    throw new System.Exception(
                        $"[Dio Level Editor] Globe submesh {i} has no materials. " +
                        "Ensure the FBX has valid materials assigned.");
                for (int sm = 0; sm < subCount; sm++)
                {
                    Material mat = s.materials[Mathf.Min(sm, s.materials.Length - 1)];
                    if (mat == null)
                        throw new System.Exception(
                            $"[Dio Level Editor] Globe submesh {i}, slot {sm} has null material.");
                    _pru.DrawMesh(s.mesh, s.worldMatrix, mat, sm);
                }
            }

            // Track ribbon preview — actual TrackBuilder mesh, low-res, no
            // guards / collider. So the editor view matches what cars will
            // drive on instead of an idealised polyline.
            if (_previewTrackMesh != null && _previewTrackMat != null)
                _pru.DrawMesh(_previewTrackMesh, Matrix4x4.identity, _previewTrackMat, 0);

            // Outer skirts — render below the ribbon so elevation reads.
            if (_previewGuardMat != null)
            {
                if (_previewGuardLeft  != null) _pru.DrawMesh(_previewGuardLeft,  Matrix4x4.identity, _previewGuardMat, 0);
                if (_previewGuardRight != null) _pru.DrawMesh(_previewGuardRight, Matrix4x4.identity, _previewGuardMat, 0);
            }

            _pru.camera.Render();
            Texture tex = _pru.EndPreview();
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        void DrawHandleLeash(Vector3 anchorDir, Vector3 handleDir, float anchorHeight)
        {
            const int N = 6;
            var pts = new Vector3[N + 1];
            for (int s = 0; s <= N; s++)
            {
                float t = (float)s / N;
                pts[s] = Vector3.Slerp(anchorDir, handleDir, t).normalized * anchorHeight;
            }
            DrawPolyline(pts, new Color(1f, 0.92f, 0.55f, 0.75f), 1.6f);
        }

        void DrawPolyline(Vector3[] worldPts, Color color, float thickness)
        {
            var screen = new List<Vector3>(worldPts.Length);
            Handles.color = color;
            for (int i = 0; i < worldPts.Length; i++)
            {
                if (TryWorldToScreen(worldPts[i], out Vector2 sp)) screen.Add(new Vector3(sp.x, sp.y, 0));
                else
                {
                    if (screen.Count >= 2) Handles.DrawAAPolyLine(thickness, screen.ToArray());
                    screen.Clear();
                }
            }
            if (screen.Count >= 2) Handles.DrawAAPolyLine(thickness, screen.ToArray());
        }

        void DrawDot(Vector2 sp, float size, Color color)
        {
            Handles.color = color;
            Handles.DrawSolidDisc(new Vector3(sp.x, sp.y, 0), Vector3.forward, size);
            Handles.color = Color.black;
            Handles.DrawWireDisc(new Vector3(sp.x, sp.y, 0), Vector3.forward, size);
        }

        void Save()
        {
            EditorUtility.SetDirty(level);
            AssetDatabase.SaveAssetIfDirty(level);
        }
    }
}
