using UnityEngine;

[RequireComponent(typeof(Camera))]
public sealed class DashedLineClickRemover : MonoBehaviour
{
    [SerializeField] private Collider surfaceCollider;
    [SerializeField] private DashedSurfaceLine line;
    [SerializeField, Min(0f)]
    [Tooltip("Maximum world-space distance from a surface click to a visible dash.")]
    private float clickTolerance = 0.035f;

    private Camera drawingCamera;

    private void Awake()
    {
        drawingCamera = GetComponent<Camera>();
        if (surfaceCollider == null || line == null)
        {
            Debug.LogError("Assign a surface collider and dashed line to the click remover.", this);
            enabled = false;
        }
    }

    private void Update()
    {
        if (!Input.GetMouseButtonDown(0))
            return;

        TryRemoveFromRay(drawingCamera.ScreenPointToRay(Input.mousePosition));
    }

    public bool TryRemoveFromRay(Ray ray)
    {
        return surfaceCollider != null && line != null
            && surfaceCollider.Raycast(ray, out RaycastHit hit, 100f)
            && line.TryRemoveNearestDash(hit.point, clickTolerance);
    }

    private void OnGUI()
    {
        GUI.Box(new Rect(12, 12, 340, 30), "Click near a red dash to remove it");
    }
}
