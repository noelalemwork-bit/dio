using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dio.Level.EditorTools
{
    /// Tools → Dio → Open Level Editor.
    ///
    /// Author a track on a planet. Every segment between two anchors is a
    /// spherical cubic Bezier (slerp-based De Casteljau, so samples stay on
    /// the sphere). Each anchor stores `inHandle` (control on the segment
    /// arriving at it) and `outHandle` (control on the segment leaving it),
    /// stored as unit directions from the planet center.
    ///
    /// Interactions:
    ///   * Drag empty space (LMB or RMB): orbit camera.
    ///   * Wheel: zoom.
    ///   * Click handle dot: drag the handle. The partner handle on the
    ///     same anchor is reflected through the tangent plane to maintain
    ///     C1 continuity, with the partner's angular distance cached at
    ///     drag-start so its magnitude is preserved.
    ///   * Click anchor: drag the anchor. Both handles ride along via a
    ///     spherical rigid rotation `FromToRotation(oldDir, newDir)`, so
    ///     their angles relative to the anchor are preserved exactly.
    ///   * Shift + click an endpoint: append (or prepend) a new anchor and
    ///     drag it. The two middle controls of the new segment default to
    ///     the geodesic midpoint between the two anchors; if the previous
    ///     endpoint is already shared with another segment, that anchor's
    ///     other handle is reflected to maintain C1.
    ///   * Del: remove the currently dragged anchor (chain must keep ≥2).
    ///
    /// The world.fbx is rendered as actual 3D geometry through a
    /// PreviewRenderUtility — track handles are drawn as a UI overlay on top
    /// without depth testing (back-hemisphere points are filtered out instead
    /// of occluded). The scale multiplier maps the mesh's longest axis onto
    /// the planet radius; "Auto-fit" recomputes that ratio.
    public class LevelEditorWindow : EditorWindow
    {
        const int BezierSegments = 28;
        const float HandlePickRadius = 14f;
        const float AnchorPickRadius = 22f;   // 2x bigger pick radius for anchors
        const float HandleDotRadius = 7f;
        const float AnchorDotRadius = 16f;    // 2x bigger anchor dots

        public LevelData level;

        // World.fbx backdrop config.
        public GameObject worldMeshAsset;
        public float worldMeshRadiusMultiplier = 1f;
        public bool showWorldMesh = true;
        const string DefaultWorldFbxPath = "Assets/3d world/world.fbx";

        // PreviewRenderUtility renders the world.fbx (and a translucent planet
        // sphere) into an off-screen RT. We then `GUI.DrawTexture` it into the
        // editor view and overlay handles on top with the same camera math.
        PreviewRenderUtility _pru;

        // Cached draw list — flattens the FBX hierarchy into (mesh, matrix,
        // material) entries, recomputed only when the asset / multiplier change.
        struct DrawEntry { public Mesh mesh; public Matrix4x4 matrix; public Material[] materials; }
        readonly List<DrawEntry> _worldDrawList = new List<DrawEntry>();
        GameObject _cachedWorldRoot;
        float _cachedWorldMultiplier;
        Material _fallbackMat;
        Mesh _planetMesh;
        Material _planetMat;

        // Camera state. Stored as a full Quaternion (not yaw/pitch) so the
        // user can sweep across poles freely — no gimbal lock and no axis
        // flip when dragging across the equator.
        // `_orientation * Vector3.forward` = camera forward in world.
        // Camera position = -(_orientation * Vector3.forward) * _distance.
        Quaternion _orientation = Quaternion.LookRotation(
            -new Vector3(0.7f, 0.5f, 0.5f).normalized,  // initial forward (back from camPos)
            Vector3.up);
        float _distance = 600f;
        Vector2 _lastMouse;
        Rect _viewRect;

        // Drag state.
        enum DragKind { None, Anchor, InHandle, OutHandle, Orbit, ShiftExtend }
        DragKind _dragKind = DragKind.None;
        int _draggedIndex = -1;
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
            asset.planetRadius = 200f;
            asset.seed = Random.Range(int.MinValue, int.MaxValue);
            asset.points.Add(new TrackPoint { directionFromCenter = Vector3.up });
            asset.points.Add(new TrackPoint { directionFromCenter = Random.onUnitSphere });
            EnsureDefaultHandles(asset);

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
            TryAutoLoadWorldMesh();
            // Backfill handles on a level that was loaded before the bezier
            // model existed — happens after every domain reload too, since
            // the public `level` field is serialized across reloads.
            if (level != null) EnsureDefaultHandles(level);
        }

        void OnDisable()
        {
            if (_pru != null) { _pru.Cleanup(); _pru = null; }
            // Keep _fallbackMat / _planetMat — they're cleaned up on AppDomain reload by Unity.
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
            // Two-light rim setup so silhouettes read against the dark BG.
            _pru.lights[0].intensity = 1.05f;
            _pru.lights[0].transform.rotation = Quaternion.Euler(40f, -25f, 0f);
            _pru.lights[1].intensity = 0.45f;
            _pru.lights[1].transform.rotation = Quaternion.Euler(-30f, 140f, 0f);
            _pru.ambientColor = new Color(0.32f, 0.34f, 0.42f, 1f);
        }

        void TryAutoLoadWorldMesh()
        {
            if (worldMeshAsset == null)
            {
                var go = AssetDatabase.LoadMainAssetAtPath(DefaultWorldFbxPath) as GameObject;
                if (go == null) return;
                worldMeshAsset = go;
            }
            // Run auto-fit even if the mesh was already serialized — across
            // a domain reload the default multiplier (1) gets restored too,
            // and at planet radius 200 a 1u mesh is invisibly small.
            if (level != null) AutoFitWorldMesh();
        }

        // Auto-fit: scale so the mesh's longest single axis matches the planet
        // radius (not extents.magnitude — for a roughly-spherical mesh of unit
        // radius that's sqrt(3), which under-scaled by 1.7×). Picking the
        // largest axis matches the user-visible "the mesh is this big".
        void AutoFitWorldMesh()
        {
            if (worldMeshAsset == null || level == null || level.planetRadius <= 0f) return;
            Bounds combined = ComputeWorldMeshBounds();
            float maxExtent = Mathf.Max(combined.extents.x, Mathf.Max(combined.extents.y, combined.extents.z));
            if (maxExtent > 1e-5f) worldMeshRadiusMultiplier = level.planetRadius / maxExtent;
            _cachedWorldRoot = null; // force draw-list rebuild with new multiplier
        }

        Bounds ComputeWorldMeshBounds()
        {
            var filters = worldMeshAsset.GetComponentsInChildren<MeshFilter>(true);
            bool any = false;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null) continue;
                Bounds local = mf.sharedMesh.bounds;
                Matrix4x4 mat = WorldMeshLocalToRoot(mf.transform);
                // Take the 8 corners of the local AABB and transform them — this
                // handles rotation correctly. The previous code transformed only
                // the extents vector, which collapsed under non-axis-aligned
                // child rotations and could give bogus tiny bounds.
                Vector3 lc = local.center;
                Vector3 le = local.extents;
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 corner = lc + new Vector3(sx * le.x, sy * le.y, sz * le.z);
                    Vector3 wc = mat.MultiplyPoint3x4(corner);
                    min = Vector3.Min(min, wc);
                    max = Vector3.Max(max, wc);
                }
                Bounds wb = new Bounds((min + max) * 0.5f, (max - min));
                if (!any) { b = wb; any = true; } else b.Encapsulate(wb);
            }
            return b;
        }

        // Walk parents up to (but not including) `worldMeshAsset.transform`
        // to compute the local-to-root matrix without instantiating the prefab.
        Matrix4x4 WorldMeshLocalToRoot(Transform t)
        {
            Matrix4x4 m = Matrix4x4.identity;
            var rootT = worldMeshAsset.transform;
            var cur = t;
            while (cur != null && cur != rootT)
            {
                m = Matrix4x4.TRS(cur.localPosition, cur.localRotation, cur.localScale) * m;
                cur = cur.parent;
            }
            return m;
        }

        void OnGUI()
        {
            // Toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginChangeCheck();
            level = (LevelData)EditorGUILayout.ObjectField("Level", level, typeof(LevelData), false);
            if (EditorGUI.EndChangeCheck() && level != null) EnsureDefaultHandles(level);
            if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(60))) NewLevel();
            GUILayout.FlexibleSpace();
            showWorldMesh = GUILayout.Toggle(showWorldMesh, "World mesh", EditorStyles.toolbarButton, GUILayout.Width(80));
            if (GUILayout.Button("Auto-fit", EditorStyles.toolbarButton, GUILayout.Width(64))) AutoFitWorldMesh();
            if (level != null)
                EditorGUILayout.LabelField($"R={level.planetRadius:0}  pts={level.points.Count}", GUILayout.Width(160));
            EditorGUILayout.EndHorizontal();

            if (level == null)
            {
                EditorGUILayout.HelpBox("Create or pick a Level asset to begin.", MessageType.Info);
                return;
            }

            EditorGUI.BeginChangeCheck();
            level.planetRadius = EditorGUILayout.FloatField("Planet radius", level.planetRadius);
            level.trackWidth = EditorGUILayout.FloatField("Track width", level.trackWidth);
            level.seed = EditorGUILayout.IntField("Seed", level.seed);
            var prevMesh = worldMeshAsset;
            worldMeshAsset = (GameObject)EditorGUILayout.ObjectField("World mesh (FBX)", worldMeshAsset, typeof(GameObject), false);
            float prevMul = worldMeshRadiusMultiplier;
            worldMeshRadiusMultiplier = EditorGUILayout.FloatField("World mesh scale", worldMeshRadiusMultiplier);
            if (worldMeshAsset != prevMesh || !Mathf.Approximately(prevMul, worldMeshRadiusMultiplier))
                _cachedWorldRoot = null;
            if (EditorGUI.EndChangeCheck()) Save();

            Rect view = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            HandleViewInput(view);
            DrawScene(view);

            HandleKeyboard();

            EditorGUILayout.LabelField(
                "Drag empty: orbit  ·  Wheel: zoom  ·  Click anchor / handle: drag  ·  Shift+drag endpoint: extend  ·  Del: remove anchor",
                EditorStyles.miniLabel);
        }

        void HandleKeyboard()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Delete)
            {
                if (_dragKind == DragKind.Anchor && _draggedIndex >= 0 && level.points.Count > 2)
                {
                    Undo.RecordObject(level, "Delete Point");
                    level.points.RemoveAt(_draggedIndex);
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
            // Picking math (RaycastSphere / TryWorldToScreen) reads _viewRect,
            // so set it before they run. DrawScene refreshes it again later;
            // the value is identical either way.
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
                        if (pick.kind == DragKind.Anchor && e.shift && IsEndpoint(pick.index))
                        {
                            BeginExtendFromEndpoint(pick.index, e.mousePosition);
                        }
                        else if (pick.kind != DragKind.None)
                        {
                            BeginPickedDrag(pick);
                        }
                        else
                        {
                            // Empty click → orbit.
                            _dragKind = DragKind.Orbit;
                            _draggedIndex = -1;
                        }
                        GUIUtility.hotControl = controlID;
                        e.Use();
                    }
                    else if (e.button == 1)
                    {
                        _dragKind = DragKind.Orbit;
                        GUIUtility.hotControl = controlID;
                        e.Use();
                    }
                }
                else if (e.type == EventType.ScrollWheel)
                {
                    _distance = Mathf.Clamp(_distance * (1f + e.delta.y * 0.05f),
                        level.planetRadius * 1.05f, level.planetRadius * 12f);
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
                        if (_draggedIndex >= 0 && RaycastSphere(e.mousePosition, out Vector3 hitA))
                        {
                            MoveAnchorRigid(_draggedIndex, hitA.normalized);
                            Save(); Repaint();
                        }
                        break;

                    case DragKind.ShiftExtend:
                        if (_draggedIndex >= 0 && RaycastSphere(e.mousePosition, out Vector3 hitE))
                        {
                            SetAnchorDirOnly(_draggedIndex, hitE.normalized);
                            ResetExtendHandles(_draggedIndex);
                            Save(); Repaint();
                        }
                        break;

                    case DragKind.InHandle:
                    case DragKind.OutHandle:
                        if (_draggedIndex >= 0 && RaycastSphere(e.mousePosition, out Vector3 hitH))
                        {
                            SetHandle(_draggedIndex, _dragKind, hitH.normalized);
                            ReflectPartnerHandle(_draggedIndex, _dragKind);
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
                e.Use();
            }
        }

        struct Pick { public DragKind kind; public int index; }

        // Pick handles first (smaller, drawn on top), then anchors. Ignore
        // points on the back hemisphere so click-throughs don't grab the
        // wrong dot. Returns DragKind.None if nothing was within the
        // tolerance circle.
        Pick PickAtMouse(Vector2 mouse)
        {
            Vector3 camDir = ComputeCameraPosition().normalized;

            float bestDist2 = HandlePickRadius * HandlePickRadius;
            Pick best = new Pick { kind = DragKind.None, index = -1 };

            for (int i = 0; i < level.points.Count; i++)
            {
                var p = level.points[i];
                bool hasIn = (i > 0) && p.inHandle.sqrMagnitude > 1e-6f;
                bool hasOut = (i < level.points.Count - 1) && p.outHandle.sqrMagnitude > 1e-6f;

                if (hasIn && Vector3.Dot(p.inHandle.normalized, camDir) > 0f
                    && TryWorldToScreen(p.inHandle.normalized * level.planetRadius, out Vector2 sIn))
                {
                    float d = (sIn - mouse).sqrMagnitude;
                    if (d < bestDist2) { bestDist2 = d; best = new Pick { kind = DragKind.InHandle, index = i }; }
                }
                if (hasOut && Vector3.Dot(p.outHandle.normalized, camDir) > 0f
                    && TryWorldToScreen(p.outHandle.normalized * level.planetRadius, out Vector2 sOut))
                {
                    float d = (sOut - mouse).sqrMagnitude;
                    if (d < bestDist2) { bestDist2 = d; best = new Pick { kind = DragKind.OutHandle, index = i }; }
                }
            }

            // If a handle is picked, prefer that. Otherwise try anchors.
            if (best.kind != DragKind.None) return best;

            float anchorDist2 = AnchorPickRadius * AnchorPickRadius;
            for (int i = 0; i < level.points.Count; i++)
            {
                Vector3 dir = level.DirOf(i);
                if (Vector3.Dot(dir, camDir) <= 0f) continue;
                Vector3 wp = dir * level.planetRadius;
                if (TryWorldToScreen(wp, out Vector2 sp))
                {
                    float d = (sp - mouse).sqrMagnitude;
                    if (d < anchorDist2) { anchorDist2 = d; best = new Pick { kind = DragKind.Anchor, index = i }; }
                }
            }
            return best;
        }

        bool IsEndpoint(int i) => i == 0 || i == level.points.Count - 1;

        // Quaternion-based orbit. LMB drag rotates the camera around its own
        // current up + right axes (so the cursor tracks any direction across
        // the planet — no equator/pole singularity). RMB drag is an arcball
        // roll: torque = cross(mouseFromCenter, mouseDelta), so dragging
        // sideways at the bottom of the view rolls opposite to dragging
        // sideways at the top — the natural "spin the trackball" feel.
        void ApplyOrbitDrag(Vector2 mouseDelta, int mouseButton)
        {
            const float Sensitivity = 0.4f;
            ComputeCameraBasis(out Vector3 _, out Vector3 fwd, out Vector3 right, out Vector3 up);

            if (mouseButton == 0)
            {
                // Sign convention: drag right → world drags right under the
                // cursor → camera orbits LEFT around the world's up axis.
                // AngleAxis(+angle, up) rotates orientation CCW around up,
                // which sends forward from +Z toward +X, which puts camPos
                // (−forward × distance) at −X — i.e. WEST of the start. So
                // a positive dx maps directly to a positive angle around up.
                //
                // Same idea for dy in Unity's Y-down GUI space: drag down
                // (dy > 0) → camera tilts down to follow the cursor →
                // forward picks up a +Y component → camPos drops below
                // origin. AngleAxis(+angle, right) does that.
                Quaternion qx = Quaternion.AngleAxis(mouseDelta.x * Sensitivity, up);
                Quaternion qy = Quaternion.AngleAxis(mouseDelta.y * Sensitivity, right);
                _orientation = qy * qx * _orientation;
                _orientation.Normalize();
            }
            else if (mouseButton == 1)
            {
                // Arcball roll. _lastMouse is the cursor position AFTER
                // updating in HandleViewInput, i.e. the live pos. Torque is
                // the z-component of cross(fromCenter, mouseDelta) — flips
                // sign across the view's vertical or horizontal centerline,
                // so a left-right drag at the bottom rolls opposite to the
                // same drag at the top (matches the user's spec).
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

        // The partner is the OTHER handle on the same anchor (in vs. out).
        // Cache its angular distance to the anchor so reflection preserves
        // its "magnitude" while flipping the tangent direction.
        void CachePartnerForReflection(int anchor, DragKind draggedKind)
        {
            var p = level.points[anchor];
            Vector3 P = p.directionFromCenter.normalized;
            Vector3 partner = (draggedKind == DragKind.OutHandle) ? p.inHandle : p.outHandle;
            bool partnerExists = partner.sqrMagnitude > 1e-6f;
            // For an interior anchor, both handles must exist. For an
            // endpoint there's no partner — skip reflection.
            bool partnerIsValidSide =
                (draggedKind == DragKind.OutHandle && anchor > 0) ||
                (draggedKind == DragKind.InHandle && anchor < level.points.Count - 1);
            if (partnerExists && partnerIsValidSide)
            {
                _partnerAngularDist = Mathf.Acos(Mathf.Clamp(Vector3.Dot(P, partner.normalized), -1f, 1f));
                _partnerAnchorIdx = anchor;
                _partnerKind = (draggedKind == DragKind.OutHandle) ? DragKind.InHandle : DragKind.OutHandle;
            }
        }

        // Append (or prepend) a clone of the endpoint and put it under the
        // shift-drag cursor. Handles are reset to defaults on each drag tick.
        void BeginExtendFromEndpoint(int endpoint, Vector2 mouse)
        {
            Undo.RecordObject(level, "Extend Track");
            var clone = level.points[endpoint];
            clone.inHandle = Vector3.zero;
            clone.outHandle = Vector3.zero;
            int newIndex;
            if (endpoint == 0)
            {
                level.points.Insert(0, clone);
                newIndex = 0;
            }
            else
            {
                level.points.Add(clone);
                newIndex = level.points.Count - 1;
            }
            _dragKind = DragKind.ShiftExtend;
            _draggedIndex = newIndex;
            if (RaycastSphere(mouse, out Vector3 hit))
            {
                SetAnchorDirOnly(newIndex, hit.normalized);
                ResetExtendHandles(newIndex);
            }
            Save();
        }

        // Move anchor[i] to a new direction, carrying both handles along
        // with the same spherical rotation. FromToRotation maps oldDir →
        // newDir along the shortest great-circle arc, preserving every
        // angular distance to the anchor exactly — so handle "magnitudes"
        // (their angular distance from the anchor) don't drift as the user
        // sweeps the anchor across the sphere.
        void MoveAnchorRigid(int i, Vector3 newDir)
        {
            var p = level.points[i];
            Vector3 oldDir = p.directionFromCenter.sqrMagnitude > 1e-6f
                ? p.directionFromCenter.normalized : Vector3.up;
            // If the move is microscopic, skip — FromToRotation can be
            // unstable near identity / antipodal.
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

        // After the new endpoint moves in shift-extend, set the two middle
        // controls of the new segment to the geodesic midpoint, then enforce
        // C1 at the previous-endpoint anchor (if it now has both handles).
        void ResetExtendHandles(int newIndex)
        {
            int prevIndex = (newIndex == 0) ? 1 : newIndex - 1;
            Vector3 A = level.DirOf(prevIndex);
            Vector3 B = level.DirOf(newIndex);
            Vector3 M = Vector3.Slerp(A, B, 0.5f).normalized;

            var prev = level.points[prevIndex];
            var nw   = level.points[newIndex];
            // For an appended endpoint (newIndex > prevIndex): prev.outHandle = M,
            // newAnchor.inHandle = M. Prepended is mirrored.
            if (newIndex > prevIndex)
            {
                prev.outHandle = M;
                nw.inHandle = M;
            }
            else
            {
                prev.inHandle = M;
                nw.outHandle = M;
            }
            level.points[prevIndex] = prev;
            level.points[newIndex]  = nw;

            // If the previous endpoint has BOTH handles now (i.e., it was
            // already shared with another segment before the extension),
            // reflect the OLDER side through the new tangent so the joint
            // is C1.
            EnforceContinuity(prevIndex, preferReflectInFromOut: newIndex > prevIndex);
        }

        void SetHandle(int anchor, DragKind kind, Vector3 newDir)
        {
            var p = level.points[anchor];
            if (kind == DragKind.InHandle) p.inHandle = newDir;
            else if (kind == DragKind.OutHandle) p.outHandle = newDir;
            level.points[anchor] = p;
        }

        // After dragging one handle, reflect the partner handle on the
        // SAME anchor across the tangent plane so the bezier is C1 at
        // that anchor. The partner's cached angular distance preserves
        // its "magnitude"; only its tangent direction is flipped.
        void ReflectPartnerHandle(int anchor, DragKind draggedKind)
        {
            if (_partnerAnchorIdx != anchor) return;
            var p = level.points[anchor];
            Vector3 P = p.directionFromCenter.normalized;
            Vector3 dragged = (draggedKind == DragKind.InHandle ? p.inHandle : p.outHandle).normalized;
            // Tangent at P toward dragged (project dragged onto tangent plane at P).
            Vector3 t = dragged - Vector3.Dot(dragged, P) * P;
            if (t.sqrMagnitude < 1e-8f) return; // dragged collapsed onto P or its antipode.
            t.Normalize();
            float ang = _partnerAngularDist;
            Vector3 partnerDir = Mathf.Cos(ang) * P + Mathf.Sin(ang) * (-t);
            partnerDir.Normalize();
            if (_partnerKind == DragKind.InHandle) p.inHandle = partnerDir;
            else p.outHandle = partnerDir;
            level.points[anchor] = p;
        }

        // Make in/out handles C1 at this anchor. `preferReflectInFromOut`:
        // if true, take outHandle's tangent as authoritative and rebuild
        // inHandle along -tangent at inHandle's preserved angular distance.
        // If false, the reverse.
        void EnforceContinuity(int anchor, bool preferReflectInFromOut)
        {
            if (anchor < 0 || anchor >= level.points.Count) return;
            var p = level.points[anchor];
            bool hasIn  = p.inHandle.sqrMagnitude  > 1e-6f && anchor > 0;
            bool hasOut = p.outHandle.sqrMagnitude > 1e-6f && anchor < level.points.Count - 1;
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

        // Backfill handles on a level loaded from disk that pre-dates the
        // bezier model — set every missing handle to the geodesic midpoint
        // of its segment so the chain renders as a near-geodesic out of
        // the box, then enforce C1 at every internal anchor.
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

            // C1 at each internal anchor — reflect inHandle from outHandle.
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

        bool RaycastSphere(Vector2 mouse, out Vector3 hit)
        {
            Vector3 camPos = ComputeCameraPosition();
            Ray ray = ScreenPointToRay(mouse, camPos);
            float r = level.planetRadius;
            Vector3 oc = ray.origin;
            float a = Vector3.Dot(ray.direction, ray.direction);
            float b = 2f * Vector3.Dot(oc, ray.direction);
            float c = Vector3.Dot(oc, oc) - r * r;
            float disc = b * b - 4 * a * c;
            if (disc < 0f) { hit = default; return false; }
            float t = (-b - Mathf.Sqrt(disc)) / (2 * a); // near hit (camera-facing surface)
            if (t < 0) t = (-b + Mathf.Sqrt(disc)) / (2 * a);
            if (t < 0) { hit = default; return false; }
            hit = ray.origin + ray.direction * t;
            return true;
        }

        Vector3 ComputeCameraPosition()
        {
            // Camera sits opposite to its forward direction at `_distance`
            // from origin. `forward = orientation * +Z` per Unity convention.
            Vector3 forward = _orientation * Vector3.forward;
            return -forward * _distance;
        }

        // Single source of truth for the editor camera basis — used by both
        // PRU rendering and screen-space picking math, so they always agree.
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

            // 1) 3D backdrop (world.fbx + a translucent planet sphere) via PRU.
            EnsurePreviewUtility();
            RenderBackdrop(rect);

            // 2) UI overlay — anchors / handles / beziers — drawn on top with
            // no depth test. Front-hemisphere only so back-side dots can't be
            // accidentally clicked or visually clutter the view.
            Handles.BeginGUI();

            Vector3 camDir = ComputeCameraPosition().normalized;

            // Beziers between consecutive anchors.
            for (int i = 0; i < level.points.Count - 1; i++)
            {
                level.GetSegment(i, out Vector3 a, out Vector3 ah, out Vector3 bh, out Vector3 b);
                var pts = new Vector3[BezierSegments + 1];
                for (int s = 0; s <= BezierSegments; s++)
                {
                    float t = (float)s / BezierSegments;
                    pts[s] = GeodesicUtil.SphericalBezier(a, ah, bh, b, t) * level.planetRadius;
                }
                DrawPolyline(pts, new Color(0.40f, 0.78f, 1.00f, 1f), 3f);
            }

            // Handle leashes, anchors, handle dots — all front-hemisphere only.
            for (int i = 0; i < level.points.Count; i++)
            {
                var p = level.points[i];
                Vector3 anchorDir = level.DirOf(i);
                bool anchorVisible = Vector3.Dot(anchorDir, camDir) > 0f;
                bool hasIn  = (i > 0) && p.inHandle.sqrMagnitude  > 1e-6f;
                bool hasOut = (i < level.points.Count - 1) && p.outHandle.sqrMagnitude > 1e-6f;

                if (anchorVisible && hasIn  && Vector3.Dot(p.inHandle.normalized,  camDir) > 0f)
                    DrawHandleLeash(anchorDir, p.inHandle.normalized);
                if (anchorVisible && hasOut && Vector3.Dot(p.outHandle.normalized, camDir) > 0f)
                    DrawHandleLeash(anchorDir, p.outHandle.normalized);
            }

            // Anchors.
            for (int i = 0; i < level.points.Count; i++)
            {
                Vector3 dir = level.DirOf(i);
                if (Vector3.Dot(dir, camDir) <= 0f) continue;
                Vector3 wp = dir * level.planetRadius;
                if (!TryWorldToScreen(wp, out Vector2 sp)) continue;
                Color col = i == 0 ? new Color(1f, 0.30f, 0.30f, 1f)
                          : i == level.points.Count - 1 ? new Color(0.36f, 0.92f, 0.42f, 1f)
                          : new Color(0.95f, 0.95f, 1f, 1f);
                DrawDot(sp, AnchorDotRadius, col);
                GUI.Label(new Rect(sp.x + 14, sp.y - 24, 80, 18), i.ToString(), EditorStyles.whiteMiniLabel);
            }

            // Handle dots on top.
            for (int i = 0; i < level.points.Count; i++)
            {
                var p = level.points[i];
                bool hasIn  = (i > 0) && p.inHandle.sqrMagnitude  > 1e-6f;
                bool hasOut = (i < level.points.Count - 1) && p.outHandle.sqrMagnitude > 1e-6f;
                if (hasIn && Vector3.Dot(p.inHandle.normalized, camDir) > 0f
                    && TryWorldToScreen(p.inHandle.normalized * level.planetRadius, out Vector2 sIn))
                    DrawDot(sIn, HandleDotRadius, new Color(1f, 0.85f, 0.30f, 1f));
                if (hasOut && Vector3.Dot(p.outHandle.normalized, camDir) > 0f
                    && TryWorldToScreen(p.outHandle.normalized * level.planetRadius, out Vector2 sOut))
                    DrawDot(sOut, HandleDotRadius, new Color(1f, 0.55f, 0.85f, 1f));
            }

            Handles.EndGUI();
        }

        // Render the 3D backdrop. Skipped during Layout events (zero-size
        // rects until first Repaint cause RenderTexture.Create to fail) and
        // any time the rect collapses (window minimised, between docks).
        void RenderBackdrop(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (rect.width < 4f || rect.height < 4f) return;

            EnsureBackdropAssets();
            // First time we have a mesh + a level, snap the multiplier so
            // the FBX actually appears at the planet's scale instead of as
            // a tiny dot at the origin (default multiplier = 1, mesh ≈ 1u,
            // planet = 200u → mesh is 200× smaller than the sphere).
            if (worldMeshAsset != null && level != null
                && Mathf.Approximately(worldMeshRadiusMultiplier, 1f))
            {
                AutoFitWorldMesh();
            }
            EnsureWorldDrawList();

            _pru.BeginPreview(rect, GUIStyle.none);

            ComputeCameraBasis(out Vector3 camPos, out Vector3 fwd, out Vector3 right, out Vector3 up);
            _pru.camera.transform.position = camPos;
            _pru.camera.transform.rotation = Quaternion.LookRotation(fwd, up);
            _pru.camera.fieldOfView = 60f;
            _pru.camera.nearClipPlane = Mathf.Max(0.1f, level.planetRadius * 0.01f);
            _pru.camera.farClipPlane = level.planetRadius * 25f;

            // Translucent planet sphere — orientation reference. Drawn first
            // so the world.fbx renders ON TOP (matches what cars actually see).
            if (_planetMesh != null && _planetMat != null)
            {
                _pru.DrawMesh(_planetMesh, Matrix4x4.Scale(Vector3.one * level.planetRadius), _planetMat, 0);
            }

            if (showWorldMesh)
            {
                // Force the fallback (Unlit/Color, vibrant green so it
                // pops against the dark planet) for every sub-mesh. The
                // FBX's own URP/Lit materials don't always render through
                // PreviewRenderUtility's camera path under URP — using one
                // pipeline-agnostic unlit material guarantees the mesh
                // shows up.
                for (int i = 0; i < _worldDrawList.Count; i++)
                {
                    var entry = _worldDrawList[i];
                    _pru.DrawMesh(entry.mesh, entry.matrix, _fallbackMat, 0);
                }
            }

            _pru.camera.Render();
            Texture tex = _pru.EndPreview();
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        void EnsureBackdropAssets()
        {
            if (_fallbackMat == null)
            {
                // URP/Unlit first since the project is URP (the planet
                // sphere uses it and renders fine through PRU). Fall back
                // to built-in Unlit/Color for non-URP setups. We avoid
                // anything *Lit because PRU's camera isn't always picked
                // up by URP's frame setup, leaving lit materials invisible.
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
                _fallbackMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _fallbackMat.color = new Color(0.55f, 0.78f, 0.55f, 1f);
            }
            if (_planetMesh == null)
            {
                _planetMesh = HighResIcoSphere.Build(subdivisions: 3);
                _planetMesh.hideFlags = HideFlags.HideAndDontSave;
            }
            if (_planetMat == null)
            {
                Shader sh = Shader.Find("Unlit/Color") ?? Shader.Find("Universal Render Pipeline/Unlit");
                _planetMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _planetMat.color = new Color(0.18f, 0.20f, 0.30f, 1f);
            }
        }

        // Flatten the FBX hierarchy into a list of (mesh, world matrix,
        // shared materials) entries. World matrix = scale * localToRoot, so a
        // 1×1×1 unit mesh ends up at planetRadius after auto-fit.
        void EnsureWorldDrawList()
        {
            if (_cachedWorldRoot == worldMeshAsset
                && Mathf.Approximately(_cachedWorldMultiplier, worldMeshRadiusMultiplier)
                && _worldDrawList.Count > 0)
                return;

            _worldDrawList.Clear();
            _cachedWorldRoot = worldMeshAsset;
            _cachedWorldMultiplier = worldMeshRadiusMultiplier;
            if (worldMeshAsset == null) return;

            Matrix4x4 globalScale = Matrix4x4.Scale(Vector3.one * worldMeshRadiusMultiplier);
            var filters = worldMeshAsset.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 local = WorldMeshLocalToRoot(mf.transform);
                var rend = mf.GetComponent<MeshRenderer>();
                _worldDrawList.Add(new DrawEntry
                {
                    mesh = mf.sharedMesh,
                    matrix = globalScale * local,
                    materials = rend != null ? rend.sharedMaterials : null,
                });
            }
            // One-shot diagnostic so the user (and future-me) can see what
            // got picked up. Helps debug "I selected the FBX but nothing
            // shows up": is it 0 meshes? Is the multiplier ridiculous? etc.
            Bounds b = ComputeWorldMeshBounds();
            Debug.Log($"[Dio Level Editor] world.fbx draw-list rebuilt: {_worldDrawList.Count} sub-meshes, " +
                      $"local bounds size={b.size} (max axis={Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)):F3}u), " +
                      $"multiplier={worldMeshRadiusMultiplier:F2} → world size≈{b.size.magnitude * worldMeshRadiusMultiplier:F1}u, " +
                      $"planet radius={(level != null ? level.planetRadius : 0f):F1}u");
        }

        void DrawHandleLeash(Vector3 anchorDir, Vector3 handleDir)
        {
            const int N = 6;
            var pts = new Vector3[N + 1];
            for (int s = 0; s <= N; s++)
            {
                float t = (float)s / N;
                pts[s] = Vector3.Slerp(anchorDir, handleDir, t).normalized * level.planetRadius;
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
