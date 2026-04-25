using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dio.Level.EditorTools
{
    /// Tools → Dio → Open Level Editor.
    /// Lets you author a track on a sphere: orbit, click to add points, drag to
    /// move them, see the geodesic chain. The first point is forced red (start),
    /// the last green (finish). Auto-saves on every change.
    public class LevelEditorWindow : EditorWindow
    {
        const int GeodesicSegments = 32;

        public LevelData level;

        // Editor camera state (orbit around the planet origin).
        Vector2 _orbit = new Vector2(20f, 20f);
        float _distance = 600f;
        Vector2 _lastMouse;
        int _draggedPointIndex = -1;

        [MenuItem("Tools/Dio/Open Level Editor")]
        public static void Open()
        {
            var w = GetWindow<LevelEditorWindow>("Dio Level Editor");
            w.minSize = new Vector2(600, 400);
        }

        [MenuItem("Tools/Dio/New Level")]
        public static void NewLevel()
        {
            var asset = ScriptableObject.CreateInstance<LevelData>();
            asset.planetRadius = 200f;
            asset.seed = Random.Range(int.MinValue, int.MaxValue);

            // Start at top of planet.
            asset.points.Add(new TrackPoint { directionFromCenter = Vector3.up });
            // Finish at a random direction.
            Vector3 r = Random.onUnitSphere;
            asset.points.Add(new TrackPoint { directionFromCenter = r });

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

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            level = (LevelData)EditorGUILayout.ObjectField("Level", level, typeof(LevelData), false);
            if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(60))) NewLevel();
            GUILayout.FlexibleSpace();
            if (level != null)
            {
                EditorGUILayout.LabelField($"R={level.planetRadius:0}  pts={level.points.Count}", GUILayout.Width(160));
            }
            EditorGUILayout.EndHorizontal();

            if (level == null)
            {
                EditorGUILayout.HelpBox("Create or pick a Level asset to begin.", MessageType.Info);
                return;
            }

            // Inspector strip.
            EditorGUI.BeginChangeCheck();
            level.planetRadius = EditorGUILayout.FloatField("Planet radius", level.planetRadius);
            level.trackWidth = EditorGUILayout.FloatField("Track width", level.trackWidth);
            level.seed = EditorGUILayout.IntField("Seed", level.seed);
            if (EditorGUI.EndChangeCheck()) Save();

            // Reserve the rest of the window for the 3D view.
            Rect view = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            HandleViewInput(view);
            DrawScene(view);

            // Shortcuts.
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Delete)
            {
                if (_draggedPointIndex >= 0 && level.points.Count > 2)
                {
                    Undo.RecordObject(level, "Delete Point");
                    level.points.RemoveAt(_draggedPointIndex);
                    _draggedPointIndex = -1;
                    Save();
                    Repaint();
                    Event.current.Use();
                }
            }

            EditorGUILayout.LabelField("Right-drag: orbit · Wheel: zoom · Click sphere: add point · Drag point: move · Del: remove", EditorStyles.miniLabel);
        }

        // --- camera / picking ---

        Matrix4x4 _viewMatrix;
        Matrix4x4 _projMatrix;
        Rect _viewRect;

        void HandleViewInput(Rect rect)
        {
            Event e = Event.current;
            int controlID = GUIUtility.GetControlID(FocusType.Passive);

            if (rect.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDown)
                {
                    _lastMouse = e.mousePosition;
                    if (e.button == 0)
                    {
                        // Try to pick an existing point first.
                        int pickedIndex = PickPoint(e.mousePosition);
                        if (pickedIndex >= 0)
                        {
                            _draggedPointIndex = pickedIndex;
                        }
                        else
                        {
                            // Try to add a new point on the sphere.
                            if (RaycastSphere(e.mousePosition, out Vector3 hit))
                            {
                                Undo.RecordObject(level, "Add Point");
                                // Insert before the finish (last) so the finish stays last.
                                int insertAt = Mathf.Max(1, level.points.Count - 1);
                                level.points.Insert(insertAt, new TrackPoint { directionFromCenter = hit.normalized });
                                _draggedPointIndex = insertAt;
                                Save();
                            }
                        }
                        GUIUtility.hotControl = controlID;
                        e.Use();
                    }
                    else if (e.button == 1)
                    {
                        GUIUtility.hotControl = controlID;
                        e.Use();
                    }
                }
                else if (e.type == EventType.ScrollWheel)
                {
                    _distance = Mathf.Clamp(_distance * (1f + e.delta.y * 0.05f), level.planetRadius * 1.2f, level.planetRadius * 8f);
                    Repaint();
                    e.Use();
                }
            }

            if (GUIUtility.hotControl == controlID && e.type == EventType.MouseDrag)
            {
                Vector2 d = e.mousePosition - _lastMouse;
                _lastMouse = e.mousePosition;

                if (e.button == 1)
                {
                    _orbit.x += d.x * 0.4f;
                    _orbit.y = Mathf.Clamp(_orbit.y + d.y * 0.4f, -85f, 85f);
                    Repaint();
                }
                else if (e.button == 0 && _draggedPointIndex >= 0)
                {
                    if (RaycastSphere(e.mousePosition, out Vector3 hit))
                    {
                        Undo.RecordObject(level, "Move Point");
                        var p = level.points[_draggedPointIndex];
                        p.directionFromCenter = hit.normalized;
                        level.points[_draggedPointIndex] = p;
                        Save();
                        Repaint();
                    }
                }
                e.Use();
            }

            if (e.type == EventType.MouseUp)
            {
                if (GUIUtility.hotControl == controlID)
                {
                    GUIUtility.hotControl = 0;
                    _draggedPointIndex = -1;
                    e.Use();
                }
            }
        }

        int PickPoint(Vector2 mouse)
        {
            float bestDist = 16f * 16f;
            int bestIdx = -1;
            for (int i = 0; i < level.points.Count; i++)
            {
                Vector3 world = level.PositionOf(i);
                if (TryWorldToScreen(world, out Vector2 sp))
                {
                    float d2 = (sp - mouse).sqrMagnitude;
                    if (d2 < bestDist) { bestDist = d2; bestIdx = i; }
                }
            }
            return bestIdx;
        }

        bool RaycastSphere(Vector2 mouse, out Vector3 hit)
        {
            Vector3 camPos = ComputeCameraPosition();
            Ray ray = ScreenPointToRay(mouse, camPos);
            // Sphere at origin, radius = planetRadius.
            float r = level.planetRadius;
            Vector3 oc = ray.origin;
            float a = Vector3.Dot(ray.direction, ray.direction);
            float b = 2f * Vector3.Dot(oc, ray.direction);
            float c = Vector3.Dot(oc, oc) - r * r;
            float disc = b * b - 4 * a * c;
            if (disc < 0f) { hit = default; return false; }
            float t = (-b - Mathf.Sqrt(disc)) / (2 * a);
            if (t < 0) t = (-b + Mathf.Sqrt(disc)) / (2 * a);
            if (t < 0) { hit = default; return false; }
            hit = ray.origin + ray.direction * t;
            return true;
        }

        Vector3 ComputeCameraPosition()
        {
            float yaw = _orbit.x * Mathf.Deg2Rad;
            float pitch = _orbit.y * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Cos(pitch) * Mathf.Cos(yaw),
                Mathf.Sin(pitch),
                Mathf.Cos(pitch) * Mathf.Sin(yaw)
            ) * _distance;
        }

        Ray ScreenPointToRay(Vector2 mouse, Vector3 camPos)
        {
            // Normalize mouse to [-1,1] over the view rect.
            Vector2 nm = new Vector2(
                (mouse.x - _viewRect.x) / _viewRect.width * 2f - 1f,
                1f - (mouse.y - _viewRect.y) / _viewRect.height * 2f
            );
            // Build basis for the camera looking at origin.
            Vector3 fwd = (-camPos).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
            Vector3 up = Vector3.Cross(fwd, right);
            float aspect = _viewRect.width / Mathf.Max(1f, _viewRect.height);
            float halfH = Mathf.Tan(30f * Mathf.Deg2Rad);
            float halfW = halfH * aspect;
            Vector3 dir = (fwd + right * (nm.x * halfW) + up * (nm.y * halfH)).normalized;
            return new Ray(camPos, dir);
        }

        bool TryWorldToScreen(Vector3 world, out Vector2 sp)
        {
            Vector3 camPos = ComputeCameraPosition();
            Vector3 fwd = (-camPos).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
            Vector3 up = Vector3.Cross(fwd, right);
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

            // Background.
            EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.17f, 1f));

            Handles.BeginGUI();
            // Hand-drawn-ish wireframe of the planet using equator + meridians, projected.
            DrawSphereWire(rect, level.planetRadius, new Color(0.45f, 0.45f, 0.55f, 0.6f));

            // Geodesic chain.
            for (int i = 0; i < level.points.Count - 1; i++)
            {
                Vector3 a = level.points[i].directionFromCenter.normalized;
                Vector3 b = level.points[i + 1].directionFromCenter.normalized;
                Vector3[] arc = GeodesicUtil.Geodesic(a, b, level.planetRadius, GeodesicSegments);
                Color blue = new Color(0.30f, 0.65f, 0.95f, 1f);
                DrawPolyline(rect, arc, blue);
            }

            // Points.
            for (int i = 0; i < level.points.Count; i++)
            {
                Vector3 world = level.PositionOf(i);
                if (!TryWorldToScreen(world, out Vector2 sp)) continue;
                Color col = i == 0 ? Color.red
                          : i == level.points.Count - 1 ? new Color(0.36f, 0.86f, 0.40f, 1f)
                          : Color.white;
                DrawDot(sp, 8f, col);
                GUI.Label(new Rect(sp.x + 8, sp.y - 18, 80, 18), i.ToString(), EditorStyles.whiteMiniLabel);
            }

            Handles.EndGUI();
        }

        void DrawSphereWire(Rect rect, float radius, Color color)
        {
            const int latLines = 6, lonLines = 8, segs = 64;
            // Latitudes.
            for (int j = 1; j < latLines; j++)
            {
                float t = j / (float)latLines;
                float lat = Mathf.Lerp(-Mathf.PI * 0.5f, Mathf.PI * 0.5f, t);
                Vector3[] pts = new Vector3[segs + 1];
                for (int i = 0; i <= segs; i++)
                {
                    float lon = (i / (float)segs) * Mathf.PI * 2f;
                    pts[i] = new Vector3(
                        Mathf.Cos(lat) * Mathf.Cos(lon),
                        Mathf.Sin(lat),
                        Mathf.Cos(lat) * Mathf.Sin(lon)
                    ) * radius;
                }
                DrawPolyline(rect, pts, color);
            }
            // Meridians.
            for (int j = 0; j < lonLines; j++)
            {
                float lon = (j / (float)lonLines) * Mathf.PI * 2f;
                Vector3[] pts = new Vector3[segs + 1];
                for (int i = 0; i <= segs; i++)
                {
                    float lat = Mathf.Lerp(-Mathf.PI * 0.5f, Mathf.PI * 0.5f, i / (float)segs);
                    pts[i] = new Vector3(
                        Mathf.Cos(lat) * Mathf.Cos(lon),
                        Mathf.Sin(lat),
                        Mathf.Cos(lat) * Mathf.Sin(lon)
                    ) * radius;
                }
                DrawPolyline(rect, pts, color);
            }
        }

        void DrawPolyline(Rect rect, Vector3[] worldPts, Color color)
        {
            var screen = new List<Vector3>(worldPts.Length);
            for (int i = 0; i < worldPts.Length; i++)
            {
                if (TryWorldToScreen(worldPts[i], out Vector2 sp)) screen.Add(sp);
                else
                {
                    if (screen.Count >= 2) Handles.DrawAAPolyLine(2.5f, screen.ToArray());
                    screen.Clear();
                }
            }
            if (screen.Count >= 2)
            {
                Handles.color = color;
                Handles.DrawAAPolyLine(2.5f, screen.ToArray());
            }
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
