using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class DashedSurfaceLine : MonoBehaviour
{
    public enum ProjectionMode
    {
        AlongDirection,
        TowardColliderCenter
    }

    [Header("Surface and collider-local control points")]
    [SerializeField] private Collider targetCollider;
    [Tooltip("Editable 3D control points. They are projected onto the collider when the line is built.")]
    [SerializeField] private List<Vector3> points = new List<Vector3>();
    [SerializeField] private bool closedLoop;

    [Header("Line")]
    [SerializeField, Min(0.001f)] private float width = 0.045f;
    [SerializeField, Min(0.001f)] private float dashLength = 0.18f;
    [SerializeField, Min(0.001f)] private float gapLength = 0.12f;
    [SerializeField, Min(0.005f)] private float maxSampleSpacing = 0.035f;
    [SerializeField, Min(0f)] private float surfaceOffset = 0.001f;
    [SerializeField, Min(1)]
    [Tooltip("Fixed dash capacity. Each dash uses two triangles; unused slots remain invisible.")]
    private int dashCapacity = 32;
    [SerializeField] private Color color = Color.red;

    [Header("Interaction")]
    [SerializeField, Min(0f)]
    [Tooltip("Maximum world-space distance from a click to the center of a visible dash.")]
    private float clickTolerance = 0.035f;

    [Header("Projection")]
    [SerializeField] private ProjectionMode projectionMode = ProjectionMode.AlongDirection;
    [SerializeField] private Vector3 projectionDirection = Vector3.down;
    [SerializeField, Min(0.01f)] private float projectionDistance = 1f;

    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<Vector3> surfaceNormals = new List<Vector3>();
    private readonly List<Color32> vertexColors = new List<Color32>();
    private readonly List<int> triangles = new List<int>();
    private readonly List<DashSpan> dashSpans = new List<DashSpan>();
    private readonly List<DashCenterSegment> visibleSegments = new List<DashCenterSegment>();
    private readonly HashSet<int> removedDashes = new HashSet<int>();
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh generatedMesh;
    private Material generatedMaterial;
    private MaterialPropertyBlock propertyBlock;

    public Collider TargetCollider => targetCollider;
    public IReadOnlyList<Vector3> Points => points;
    public bool ClosedLoop => closedLoop;
    public ProjectionMode CurrentProjectionMode => projectionMode;
    public Vector3 ProjectionDirection => projectionDirection;
    public int DashCapacity => Mathf.Max(1, dashCapacity);
    public int RequiredDashCount { get; private set; }

    private struct DashCenterSegment
    {
        public Vector3 start;
        public Vector3 end;
        public int dashIndex;
    }

    private struct DashSpan
    {
        public Vector3 start;
        public Vector3 end;
        public Vector3 startNormal;
        public Vector3 endNormal;
        public bool hasStart;
    }

    private struct ProjectedSample
    {
        public Vector3 position;
        public Vector3 normal;
    }

    public void SetTarget(Collider collider)
    {
        targetCollider = collider;
        removedDashes.Clear();
        Rebuild();
    }

    // Coordinates are in the target Collider's local space.
    public void SetPoints(IReadOnlyList<Vector3> localPoints, bool close = false)
    {
        points.Clear();
        if (localPoints != null)
        {
            for (int i = 0; i < localPoints.Count; i++)
                points.Add(localPoints[i]);
        }
        closedLoop = close;
        removedDashes.Clear();
        Rebuild();
    }

    public bool TryProjectPoint(Vector3 worldPoint, out Vector3 surfacePoint)
    {
        surfacePoint = default;
        if (targetCollider == null || !TryProject(worldPoint, out RaycastHit hit))
            return false;
        surfacePoint = hit.point;
        return true;
    }

    public bool TryRemoveNearestDash(Vector3 worldPoint)
    {
        float limitSquared = clickTolerance * clickTolerance;
        float closestSquared = limitSquared;
        int closestDash = -1;

        for (int i = 0; i < visibleSegments.Count; i++)
        {
            DashCenterSegment segment = visibleSegments[i];
            if (removedDashes.Contains(segment.dashIndex))
                continue;
            Vector3 start = transform.TransformPoint(segment.start);
            Vector3 end = transform.TransformPoint(segment.end);
            float distanceSquared = DistanceToSegmentSquared(worldPoint, start, end);
            if (distanceSquared <= closestSquared)
            {
                closestSquared = distanceSquared;
                closestDash = segment.dashIndex;
            }
        }

        if (closestDash < 0)
            return false;

        removedDashes.Add(closestDash);
        SetDashAlpha(closestDash, 0);
        return true;
    }

    private void SetDashAlpha(int dashIndex, byte alpha)
    {
        int first = dashIndex * 4;
        if (generatedMesh == null || dashIndex < 0 || first + 4 > vertexColors.Count)
            return;

        for (int i = first; i < first + 4; i++)
        {
            Color32 vertexColor = vertexColors[i];
            vertexColor.a = alpha;
            vertexColors[i] = vertexColor;
        }
        generatedMesh.SetVertexBufferData(vertexColors, first, first, 4, 1,
            MeshUpdateFlags.DontRecalculateBounds);
    }

    private static float DistanceToSegmentSquared(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 direction = end - start;
        float lengthSquared = direction.sqrMagnitude;
        float t = lengthSquared > 0f
            ? Mathf.Clamp01(Vector3.Dot(point - start, direction) / lengthSquared)
            : 0f;
        return (point - (start + t * direction)).sqrMagnitude;
    }

    [ContextMenu("Rebuild Line")]
    public void Rebuild()
    {
        if (targetCollider != null && targetCollider.gameObject == gameObject)
        {
            Debug.LogError("Place DashedSurfaceLine on a separate child object to preserve the surface mesh.", this);
            return;
        }

        EnsureResources();
        int capacity = DashCapacity;
        EnsureFixedMesh(capacity);
        ResizeBuffers(capacity);
        visibleSegments.Clear();
        RequiredDashCount = 0;

        float dash = Mathf.Max(0.001f, dashLength);
        float gap = Mathf.Max(0.001f, gapLength);
        float period = dash + gap;
        double distanceAlongSurface = 0d;
        ProjectedSample previousSample = default;
        bool hasPreviousSample = false;

        if (targetCollider != null && points.Count >= 2)
        {
            int segmentCount = closedLoop ? points.Count : points.Count - 1;
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                Vector3 start = targetCollider.transform.TransformPoint(points[segmentIndex]);
                Vector3 end = targetCollider.transform.TransformPoint(points[(segmentIndex + 1) % points.Count]);
                float segmentLength = Vector3.Distance(start, end);
                if (segmentLength < 0.0001f)
                    continue;

                int sampleCount = Mathf.Max(1, Mathf.CeilToInt(segmentLength / Mathf.Max(0.005f, maxSampleSpacing)));
                for (int sampleIndex = segmentIndex == 0 ? 0 : 1; sampleIndex <= sampleCount; sampleIndex++)
                {
                    Vector3 requested = Vector3.Lerp(start, end, sampleIndex / (float)sampleCount);
                    if (!TryProject(requested, out RaycastHit hit))
                    {
                        hasPreviousSample = false;
                        continue;
                    }

                    ProjectedSample currentSample = new ProjectedSample { position = hit.point, normal = hit.normal };
                    if (hasPreviousSample)
                    {
                        float projectedStep = Vector3.Distance(previousSample.position, currentSample.position);
                        float maximumStep = Mathf.Max(0.005f, maxSampleSpacing) * 8f;
                        if (projectedStep <= maximumStep && Vector3.Dot(previousSample.normal, currentSample.normal) > -0.5f)
                            AppendProjectedSegment(previousSample, currentSample, dash, period, ref distanceAlongSurface);
                    }

                    previousSample = currentSample;
                    hasPreviousSample = true;
                }
            }
        }

        RequiredDashCount = Mathf.CeilToInt((float)(distanceAlongSurface / period));
        WriteDashQuads(capacity);
        generatedMesh.SetVertexBufferData(vertices, 0, 0, vertices.Count, 0, MeshUpdateFlags.DontRecalculateBounds);
        generatedMesh.SetVertexBufferData(surfaceNormals, 0, 0, surfaceNormals.Count, 2, MeshUpdateFlags.DontRecalculateBounds);
        generatedMesh.SetVertexBufferData(vertexColors, 0, 0, vertexColors.Count, 1, MeshUpdateFlags.DontRecalculateBounds);
        generatedMesh.RecalculateBounds();
        ApplyColor();
    }

    private void EnsureFixedMesh(int capacity)
    {
        int vertexCount = capacity * 4;
        int indexCount = capacity * 6;
        if (generatedMesh.vertexCount == vertexCount && generatedMesh.subMeshCount == 1
            && generatedMesh.GetIndexCount(0) == indexCount)
            return;

        generatedMesh.Clear();
        generatedMesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        generatedMesh.SetVertexBufferParams(vertexCount,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 1),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 2));
        triangles.Clear();
        for (int slot = 0; slot < capacity; slot++)
        {
            int first = slot * 4;
            // Face the ribbon outward so Cull Back hides it from the opposite side.
            triangles.Add(first);
            triangles.Add(first + 2);
            triangles.Add(first + 1);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
            triangles.Add(first + 3);
        }
        generatedMesh.SetTriangles(triangles, 0, false);
    }

    private void ResizeBuffers(int capacity)
    {
        while (dashSpans.Count < capacity)
            dashSpans.Add(default);
        if (dashSpans.Count > capacity)
            dashSpans.RemoveRange(capacity, dashSpans.Count - capacity);
        for (int slot = 0; slot < capacity; slot++)
            dashSpans[slot] = default;

        int vertexCount = capacity * 4;
        while (vertices.Count < vertexCount)
            vertices.Add(Vector3.zero);
        while (vertexColors.Count < vertexCount)
            vertexColors.Add(new Color32(255, 255, 255, 0));
        while (surfaceNormals.Count < vertexCount)
            surfaceNormals.Add(Vector3.zero);
        if (vertices.Count > vertexCount)
            vertices.RemoveRange(vertexCount, vertices.Count - vertexCount);
        if (vertexColors.Count > vertexCount)
            vertexColors.RemoveRange(vertexCount, vertexColors.Count - vertexCount);
        if (surfaceNormals.Count > vertexCount)
            surfaceNormals.RemoveRange(vertexCount, surfaceNormals.Count - vertexCount);
    }

    private void AppendProjectedSegment(ProjectedSample start, ProjectedSample end, float dash, float period,
        ref double distanceAlongSurface)
    {
        double segmentLength = Vector3.Distance(start.position, end.position);
        if (segmentLength < 0.000001d)
            return;
        double traveled = 0d;

        while (traveled < segmentLength - 0.00000001d)
        {
            int dashIndex = (int)System.Math.Floor((distanceAlongSurface + 0.000000001d) / period);
            double phase = distanceAlongSurface - dashIndex * (double)period;
            bool visible = phase < dash - 0.000000001d;
            double toBoundary = visible ? dash - phase : period - phase;
            double length = System.Math.Min(segmentLength - traveled, toBoundary);
            if (length <= 0.00000001d)
            {
                distanceAlongSurface += 0.00000001d;
                traveled += 0.00000001d;
                continue;
            }

            if (visible && dashIndex >= 0 && dashIndex < dashSpans.Count)
            {
                DashSpan span = dashSpans[dashIndex];
                float startFraction = (float)(traveled / segmentLength);
                float endFraction = (float)((traveled + length) / segmentLength);
                if (!span.hasStart)
                {
                    span.start = Vector3.Lerp(start.position, end.position, startFraction);
                    span.startNormal = Vector3.Lerp(start.normal, end.normal, startFraction).normalized;
                    span.hasStart = true;
                }
                span.end = Vector3.Lerp(start.position, end.position, endFraction);
                span.endNormal = Vector3.Lerp(start.normal, end.normal, endFraction).normalized;
                dashSpans[dashIndex] = span;
            }

            traveled += length;
            distanceAlongSurface += length;
        }
    }

    private void WriteDashQuads(int capacity)
    {
        for (int slot = 0; slot < capacity; slot++)
        {
            int first = slot * 4;
            DashSpan span = dashSpans[slot];
            if (!span.hasStart || (span.end - span.start).sqrMagnitude < 0.00000001f)
            {
                for (int i = first; i < first + 4; i++)
                {
                    vertices[i] = Vector3.zero;
                    surfaceNormals[i] = Vector3.zero;
                    vertexColors[i] = new Color32(255, 255, 255, 0);
                }
                continue;
            }

            Vector3 tangent = (span.end - span.start).normalized;
            Vector3 normal = projectionMode == ProjectionMode.AlongDirection && projectionDirection.sqrMagnitude > 0.000001f
                ? -projectionDirection.normalized
                : (span.startNormal + span.endNormal).normalized;
            if (normal.sqrMagnitude < 0.5f)
                normal = span.startNormal;
            Vector3 side = Vector3.Cross(normal, tangent).normalized;
            if (side.sqrMagnitude < 0.5f)
                side = Vector3.Cross(Vector3.up, tangent).normalized;
            if (side.sqrMagnitude < 0.5f)
                side = Vector3.Cross(Vector3.forward, tangent).normalized;
            Vector3 halfWidth = side * (Mathf.Max(0.001f, width) * 0.5f);
            Vector3 startCenter = span.start + (projectionMode == ProjectionMode.AlongDirection ? normal : span.startNormal)
                * Mathf.Max(0f, surfaceOffset);
            Vector3 endCenter = span.end + (projectionMode == ProjectionMode.AlongDirection ? normal : span.endNormal)
                * Mathf.Max(0f, surfaceOffset);
            vertices[first] = transform.InverseTransformPoint(startCenter - halfWidth);
            vertices[first + 1] = transform.InverseTransformPoint(startCenter + halfWidth);
            vertices[first + 2] = transform.InverseTransformPoint(endCenter - halfWidth);
            vertices[first + 3] = transform.InverseTransformPoint(endCenter + halfWidth);
            surfaceNormals[first] = transform.InverseTransformDirection(span.startNormal).normalized;
            surfaceNormals[first + 1] = surfaceNormals[first];
            surfaceNormals[first + 2] = transform.InverseTransformDirection(span.endNormal).normalized;
            surfaceNormals[first + 3] = surfaceNormals[first + 2];

            byte alpha = removedDashes.Contains(slot) ? (byte)0 : (byte)255;
            for (int i = first; i < first + 4; i++)
                vertexColors[i] = new Color32(255, 255, 255, alpha);

            visibleSegments.Add(new DashCenterSegment
            {
                start = transform.InverseTransformPoint(startCenter),
                end = transform.InverseTransformPoint(endCenter),
                dashIndex = slot
            });
        }
    }

    private bool TryProject(Vector3 requested, out RaycastHit hit)
    {
        float distance = Mathf.Max(0.01f, projectionDistance);
        Vector3 direction;
        if (projectionMode == ProjectionMode.TowardColliderCenter)
        {
            direction = targetCollider.bounds.center - requested;
            if (direction.sqrMagnitude < 0.000001f)
            {
                hit = default;
                return false;
            }
            direction.Normalize();
        }
        else
        {
            direction = projectionDirection;
            if (direction.sqrMagnitude < 0.000001f)
            {
                hit = default;
                return false;
            }
            direction.Normalize();
        }

        Ray ray = new Ray(requested - direction * distance, direction);
        return targetCollider.Raycast(ray, out hit, distance * 2f);
    }

    private void EnsureResources()
    {
        if (meshFilter == null)
            meshFilter = GetComponent<MeshFilter>();
        if (meshRenderer == null)
            meshRenderer = GetComponent<MeshRenderer>();
        if (generatedMesh == null)
        {
            generatedMesh = new Mesh { name = "Dashed Surface Line", hideFlags = HideFlags.DontSave };
            generatedMesh.MarkDynamic();
            meshFilter.sharedMesh = generatedMesh;
        }
        if (meshRenderer.sharedMaterial == null && generatedMaterial == null)
        {
            Shader shader = Shader.Find("Custom/DashedSurfaceVertexColor");
            if (shader != null)
            {
                generatedMaterial = new Material(shader) { name = "Temporary Red Line", hideFlags = HideFlags.DontSave };
                meshRenderer.sharedMaterial = generatedMaterial;
            }
        }
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
    }

    private void ApplyColor()
    {
        if (propertyBlock == null)
            propertyBlock = new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetColor("_Color", color);
        meshRenderer.SetPropertyBlock(propertyBlock);
    }

    private void OnEnable()
    {
        Rebuild();
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (targetCollider == null || points == null)
            return;

        Color previousColor = Gizmos.color;
        Gizmos.color = Color.yellow;
        float radius = Mathf.Max(0.008f, targetCollider.bounds.extents.magnitude * 0.01f);
        Vector3 previous = default;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 world = targetCollider.transform.TransformPoint(points[i]);
            Gizmos.DrawSphere(world, radius);
            if (i > 0)
                Gizmos.DrawLine(previous, world);
            previous = world;
        }
        if (closedLoop && points.Count > 2)
            Gizmos.DrawLine(previous, targetCollider.transform.TransformPoint(points[0]));
        Gizmos.color = previousColor;
    }

    private void OnValidate()
    {
        UnityEditor.EditorApplication.delayCall -= RebuildAfterValidation;
        UnityEditor.EditorApplication.delayCall += RebuildAfterValidation;
    }

    private void RebuildAfterValidation()
    {
        if (this != null && isActiveAndEnabled)
            Rebuild();
    }
#endif

    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.delayCall -= RebuildAfterValidation;
#endif
        if (meshFilter != null && meshFilter.sharedMesh == generatedMesh)
            meshFilter.sharedMesh = null;
        if (meshRenderer != null && meshRenderer.sharedMaterial == generatedMaterial)
            meshRenderer.sharedMaterial = null;
        if (meshRenderer != null)
            meshRenderer.SetPropertyBlock(null);

        if (generatedMesh != null)
        {
            if (Application.isPlaying) Destroy(generatedMesh);
            else DestroyImmediate(generatedMesh);
            generatedMesh = null;
        }
        if (generatedMaterial != null)
        {
            if (Application.isPlaying) Destroy(generatedMaterial);
            else DestroyImmediate(generatedMaterial);
            generatedMaterial = null;
        }
    }
}
