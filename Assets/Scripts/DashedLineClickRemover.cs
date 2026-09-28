using UnityEngine;

[RequireComponent(typeof(Camera))]
public sealed class DashedLineClickRemover : MonoBehaviour
{
    [SerializeField] private Collider surfaceCollider;
    [SerializeField] private DashedSurfaceLine line;

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
            && line.TryRemoveNearestDash(hit.point);
    }

    private void OnGUI()
    {
        GUI.Box(new Rect(12, 12, 340, 30), "Click near a red dash to remove it");
    }
}
