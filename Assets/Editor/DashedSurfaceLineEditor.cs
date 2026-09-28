using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(DashedSurfaceLine))]
[CanEditMultipleObjects]
internal sealed class DashedSurfaceLineEditor : Editor
{
    private bool drawing;
    private int selectedPoint = -1;
    private Tool previousTool;
    private string message;
    private static GUIStyle pointLabelStyle;

    private void OnEnable()
    {
        SceneView.duringSceneGui += DuringSceneGUI;
        Undo.undoRedoPerformed += RebuildAfterUndo;
    }

    private void OnDisable()
    {
        if (drawing)
            Tools.current = previousTool;
        SceneView.duringSceneGui -= DuringSceneGUI;
        Undo.undoRedoPerformed -= RebuildAfterUndo;
    }

    public override void OnInspectorGUI()
    {
        EditorGUILayout.LabelField("Interactive Authoring", EditorStyles.boldLabel);

        if (targets.Length != 1)
        {
            EditorGUILayout.HelpBox("Select one line to edit its control points.", MessageType.Info);
            DrawDefaultInspector();
            return;
        }

        DashedSurfaceLine line = (DashedSurfaceLine)target;
        if (line.TargetCollider == null)
        {
            EditorGUILayout.HelpBox("Assign a surface collider first.", MessageType.Warning);
            DrawDefaultInspector();
            return;
        }

        EditorGUILayout.HelpBox(
            "Click the model to add points, or use 'Add 3D Point'. "
            + "Outside draw mode, select a yellow handle and move it freely in 3D. "
            + "The green marker shows its projection onto the collider. Points are saved in the scene; "
            + "at runtime the path is projected and dashes are spaced uniformly. "
            + "If you rotate the view, update the projection direction. Alt + drag moves the camera.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(Application.isPlaying))
        {
            if (GUILayout.Button(drawing ? "Finish Drawing" : "Draw Points in Scene"))
            {
                if (!drawing)
                {
                    previousTool = Tools.current;
                    Tools.current = Tool.None;
                    drawing = true;
                    FrameProjectionView(line);
                    SceneView.lastActiveSceneView?.Focus();
                }
                else
                {
                    drawing = false;
                    Tools.current = previousTool;
                }
                SceneView.RepaintAll();
            }

            if (GUILayout.Button("Use Scene View Direction for Projection"))
                SetProjectionFromSceneView(line);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add 3D Point"))
                {
                    var updated = new List<Vector3>(line.Points)
                    {
                        NewPointPosition(line)
                    };
                    ReplacePoints(line, updated, "Add 3D point to line");
                    selectedPoint = updated.Count - 1;
                }
                using (new EditorGUI.DisabledScope(selectedPoint < 0 || selectedPoint >= line.Points.Count))
                {
                    if (GUILayout.Button("Remove Selected Point"))
                    {
                        var updated = new List<Vector3>(line.Points);
                        updated.RemoveAt(selectedPoint);
                        ReplacePoints(line, updated, "Remove selected line point");
                        selectedPoint = Mathf.Min(selectedPoint, updated.Count - 1);
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(line.Points.Count == 0))
                {
                    if (GUILayout.Button("Remove Last Point"))
                    {
                        var updated = new List<Vector3>(line.Points);
                        updated.RemoveAt(updated.Count - 1);
                        ReplacePoints(line, updated, "Remove last line point");
                        selectedPoint = Mathf.Min(selectedPoint, updated.Count - 1);
                    }
                    if (GUILayout.Button("Clear All Points"))
                    {
                        ReplacePoints(line, new List<Vector3>(), "Clear line points");
                        selectedPoint = -1;
                    }
                }
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Copy Coordinates"))
            {
                EditorGUIUtility.systemCopyBuffer = ExportCoordinates(line.Points);
                message = $"Copied {line.Points.Count} points (one x;y;z triplet per line).";
            }
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Paste Coordinates"))
                {
                    if (TryParseCoordinates(EditorGUIUtility.systemCopyBuffer, out List<Vector3> parsed, out string error))
                    {
                        ReplacePoints(line, parsed, "Paste line points");
                        selectedPoint = -1;
                        message = $"Pasted {parsed.Count} points.";
                    }
                    else
                    {
                        message = error;
                    }
                }
            }
        }

        if (GUILayout.Button("Copy as C# Code"))
        {
            EditorGUIUtility.systemCopyBuffer = ExportCSharp(line.Points);
            message = $"Copied {line.Points.Count} points as Vector3 values.";
        }

        if (!string.IsNullOrEmpty(message))
            EditorGUILayout.HelpBox(message, MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Settings and Points", EditorStyles.boldLabel);
        DrawDefaultInspector();
        EditorGUILayout.HelpBox($"Reserved mesh: {line.DashCapacity * 2} triangles ({line.DashCapacity} dashes).",
            MessageType.Info);
        if (line.RequiredDashCount > line.DashCapacity)
            EditorGUILayout.HelpBox($"This path needs {line.RequiredDashCount} dashes. Increase Dash Capacity to display all of them.",
                MessageType.Warning);
    }

    private void DuringSceneGUI(SceneView sceneView)
    {
        if (targets.Length != 1 || target == null)
            return;

        DashedSurfaceLine line = (DashedSurfaceLine)target;
        Collider collider = line.TargetCollider;
        if (collider == null)
            return;

        if (selectedPoint >= line.Points.Count)
            selectedPoint = -1;

        var previousZTest = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        Handles.color = Color.yellow;
        if (pointLabelStyle == null)
        {
            pointLabelStyle = new GUIStyle(EditorStyles.boldLabel);
            pointLabelStyle.normal.textColor = Color.black;
        }
        Vector3 previous = default;
        for (int i = 0; i < line.Points.Count; i++)
        {
            Vector3 world = collider.transform.TransformPoint(line.Points[i]);
            float size = HandleUtility.GetHandleSize(world) * 0.045f;
            if (drawing)
                Handles.SphereHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
            else if (Handles.Button(world, Quaternion.identity, size, size * 1.5f, Handles.SphereHandleCap))
            {
                selectedPoint = i;
                Repaint();
            }
            Handles.Label(world + Vector3.up * size, (i + 1).ToString(), pointLabelStyle);
            if (i > 0)
                Handles.DrawLine(previous, world);
            previous = world;
        }
        if (line.ClosedLoop && line.Points.Count > 2)
            Handles.DrawLine(previous, collider.transform.TransformPoint(line.Points[0]));

        if (!drawing && !Application.isPlaying && selectedPoint >= 0)
        {
            Vector3 selectedWorld = collider.transform.TransformPoint(line.Points[selectedPoint]);
            Handles.color = Color.cyan;
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(selectedWorld, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                var updated = new List<Vector3>(line.Points);
                updated[selectedPoint] = collider.transform.InverseTransformPoint(moved);
                ReplacePoints(line, updated, "Move 3D line point");
                selectedWorld = moved;
            }

            if (line.TryProjectPoint(selectedWorld, out Vector3 projected))
            {
                Handles.color = Color.green;
                Handles.DrawDottedLine(selectedWorld, projected, 4f);
                Handles.SphereHandleCap(0, projected, Quaternion.identity,
                    HandleUtility.GetHandleSize(projected) * 0.035f, EventType.Repaint);
            }
            else
            {
                Handles.color = Color.red;
                Handles.Label(selectedWorld + Vector3.up * HandleUtility.GetHandleSize(selectedWorld) * 0.1f,
                    "Outside projection range");
            }
        }
        Handles.zTest = previousZTest;

        if (!drawing || Application.isPlaying)
            return;

        Event current = Event.current;
        if (current.alt)
            return;
        if (current.type == EventType.Layout)
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
        if (collider.Raycast(ray, out RaycastHit preview, 10000f))
        {
            Handles.color = Color.green;
            Handles.DrawWireDisc(preview.point, preview.normal, HandleUtility.GetHandleSize(preview.point) * 0.07f);
        }

        if (current.type == EventType.MouseDown && current.button == 0 && AddPointFromRay(line, ray))
        {
            current.Use();
            selectedPoint = line.Points.Count - 1;
            message = $"Added point {line.Points.Count}.";
            Repaint();
            sceneView.Repaint();
        }
    }

    internal static bool AddPointFromRay(DashedSurfaceLine line, Ray ray)
    {
        Collider collider = line.TargetCollider;
        if (collider == null || !collider.Raycast(ray, out RaycastHit hit, 10000f))
            return false;

        var updated = new List<Vector3>(line.Points)
        {
            collider.transform.InverseTransformPoint(hit.point)
        };
        ReplacePoints(line, updated, "Add line point");
        return true;
    }

    private static void ReplacePoints(DashedSurfaceLine line, List<Vector3> updated, string undoName)
    {
        Undo.RecordObject(line, undoName);
        line.SetPoints(updated, line.ClosedLoop);
        EditorUtility.SetDirty(line);
        EditorSceneManager.MarkSceneDirty(line.gameObject.scene);
        SceneView.RepaintAll();
    }

    private static Vector3 NewPointPosition(DashedSurfaceLine line)
    {
        Collider collider = line.TargetCollider;
        SceneView view = SceneView.lastActiveSceneView;
        Camera camera = view != null ? view.camera : null;
        if (line.Points.Count > 0)
        {
            Vector3 last = collider.transform.TransformPoint(line.Points[line.Points.Count - 1]);
            Vector3 right = camera != null ? camera.transform.right : Vector3.right;
            float step = Mathf.Max(0.05f, collider.bounds.extents.magnitude * 0.12f);
            return collider.transform.InverseTransformPoint(last + right * step);
        }

        if (camera != null
            && collider.Raycast(camera.ViewportPointToRay(new Vector3(0.5f, 0.5f)),
                out RaycastHit hit, camera.farClipPlane))
            return collider.transform.InverseTransformPoint(hit.point);

        Vector3 direction = line.CurrentProjectionMode == DashedSurfaceLine.ProjectionMode.AlongDirection
            && line.ProjectionDirection.sqrMagnitude > 0.000001f
            ? line.ProjectionDirection.normalized : Vector3.down;
        return collider.transform.InverseTransformPoint(
            collider.bounds.center - direction * collider.bounds.extents.magnitude);
    }

    private static void SetProjectionFromSceneView(DashedSurfaceLine line)
    {
        SceneView view = SceneView.lastActiveSceneView;
        if (view == null || view.camera == null)
            return;

        Undo.RecordObject(line, "Set line projection direction");
        SerializedObject serialized = new SerializedObject(line);
        serialized.FindProperty("projectionMode").enumValueIndex = (int)DashedSurfaceLine.ProjectionMode.AlongDirection;
        serialized.FindProperty("projectionDirection").vector3Value = view.camera.transform.forward;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        line.Rebuild();
        EditorUtility.SetDirty(line);
        EditorSceneManager.MarkSceneDirty(line.gameObject.scene);
        SceneView.RepaintAll();
    }

    private static void FrameProjectionView(DashedSurfaceLine line)
    {
        SceneView view = SceneView.lastActiveSceneView;
        Collider collider = line.TargetCollider;
        if (view == null || collider == null || line.CurrentProjectionMode != DashedSurfaceLine.ProjectionMode.AlongDirection)
            return;

        Vector3 direction = line.ProjectionDirection;
        if (direction.sqrMagnitude < 0.000001f)
            return;
        direction.Normalize();
        Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.98f
            ? Vector3.forward : Vector3.up;
        view.LookAt(collider.bounds.center, Quaternion.LookRotation(direction, up),
            collider.bounds.extents.magnitude * 1.6f, true, true);
    }

    private void RebuildAfterUndo()
    {
        if (target is DashedSurfaceLine line)
            line.Rebuild();
        SceneView.RepaintAll();
        Repaint();
    }

    internal static string ExportCoordinates(IReadOnlyList<Vector3> points)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 point = points[i];
            if (i > 0)
                builder.AppendLine();
            builder.Append(point.x.ToString("R", CultureInfo.InvariantCulture)).Append(';')
                .Append(point.y.ToString("R", CultureInfo.InvariantCulture)).Append(';')
                .Append(point.z.ToString("R", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    internal static bool TryParseCoordinates(string source, out List<Vector3> points, out string error)
    {
        points = new List<Vector3>();
        error = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            error = "The clipboard does not contain coordinates.";
            return false;
        }

        string[] rows = source.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int row = 0; row < rows.Length; row++)
        {
            string[] values = rows[row].Split(';');
            if (values.Length != 3
                || !float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
            {
                error = $"Line {row + 1} must contain three x;y;z numbers using decimal points.";
                points.Clear();
                return false;
            }
            points.Add(new Vector3(x, y, z));
        }
        return true;
    }

    private static string ExportCSharp(IReadOnlyList<Vector3> points)
    {
        var builder = new StringBuilder("new Vector3[]\n{\n");
        foreach (Vector3 point in points)
        {
            builder.Append("    new Vector3(")
                .Append(point.x.ToString("R", CultureInfo.InvariantCulture)).Append("f, ")
                .Append(point.y.ToString("R", CultureInfo.InvariantCulture)).Append("f, ")
                .Append(point.z.ToString("R", CultureInfo.InvariantCulture)).AppendLine("f),");
        }
        return builder.Append('}').ToString();
    }
}
