using UnityEngine;

[RequireComponent(typeof(MeshRenderer), typeof(MeshCollider))]
public sealed class DashedPlanePainter : MonoBehaviour
{
    [SerializeField] private Camera drawingCamera;
    [SerializeField] private MeshCollider drawingCollider;
    [SerializeField, Range(128, 2048)] private int textureSize = 512;
    [SerializeField, Min(1)] private int brushRadius = 3;
    [SerializeField, Min(1)] private int dashLength = 18;
    [SerializeField, Min(1)] private int gapLength = 16;
    [SerializeField] private Color brushColor = Color.red;

    private MeshRenderer planeRenderer;
    private Material originalMaterial;
    private Material paintingMaterial;
    private Texture2D canvas;
    private Color32[] blankPixels;
    private Vector2 previousPixel;
    private float strokeDistance;
    private bool hasPreviousPixel;

    private void Awake()
    {
        planeRenderer = GetComponent<MeshRenderer>();
        if (drawingCamera == null || drawingCollider == null)
        {
            Debug.LogError("The drawing plane requires a camera and a MeshCollider.", this);
            enabled = false;
            return;
        }

        textureSize = Mathf.Clamp(textureSize, 128, 2048);
        brushRadius = Mathf.Max(1, brushRadius);
        dashLength = Mathf.Max(1, dashLength);
        gapLength = Mathf.Max(1, gapLength);

        canvas = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
        {
            name = "Dashed Painting",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        blankPixels = new Color32[textureSize * textureSize];
        for (int i = 0; i < blankPixels.Length; i++)
            blankPixels[i] = new Color32(255, 255, 255, 255);
        ClearCanvas();

        originalMaterial = planeRenderer.sharedMaterial;
        paintingMaterial = new Material(originalMaterial) { name = "Drawing Plane (Runtime)" };
        paintingMaterial.mainTexture = canvas;
        planeRenderer.sharedMaterial = paintingMaterial;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.C))
        {
            ClearCanvas();
            hasPreviousPixel = false;
            strokeDistance = 0f;
        }

        if (Input.GetMouseButtonUp(0))
            hasPreviousPixel = false;

        if (!Input.GetMouseButton(0))
            return;

        Ray ray = drawingCamera.ScreenPointToRay(Input.mousePosition);
        if (!drawingCollider.Raycast(ray, out RaycastHit hit, 100f))
        {
            hasPreviousPixel = false;
            return;
        }

        Vector2 pixel = new Vector2(
            hit.textureCoord.x * (textureSize - 1),
            hit.textureCoord.y * (textureSize - 1));

        if (!hasPreviousPixel)
        {
            PaintCircle(Mathf.RoundToInt(pixel.x), Mathf.RoundToInt(pixel.y));
            canvas.Apply(false);
            previousPixel = pixel;
            strokeDistance = 0f;
            hasPreviousPixel = true;
            return;
        }

        float distance = Vector2.Distance(previousPixel, pixel);
        if (distance < 0.01f)
            return;

        float period = dashLength + gapLength;
        int steps = Mathf.CeilToInt(distance * 2f);
        for (int step = 1; step <= steps; step++)
        {
            float t = step / (float)steps;
            if ((strokeDistance + distance * t) % period < dashLength)
            {
                Vector2 point = Vector2.Lerp(previousPixel, pixel, t);
                PaintCircle(Mathf.RoundToInt(point.x), Mathf.RoundToInt(point.y));
            }
        }

        strokeDistance += distance;
        previousPixel = pixel;
        canvas.Apply(false);
    }

    private void PaintCircle(int centerX, int centerY)
    {
        int radiusSquared = brushRadius * brushRadius;
        for (int y = -brushRadius; y <= brushRadius; y++)
        {
            int pixelY = centerY + y;
            if (pixelY < 0 || pixelY >= textureSize)
                continue;

            for (int x = -brushRadius; x <= brushRadius; x++)
            {
                int pixelX = centerX + x;
                if (x * x + y * y <= radiusSquared && pixelX >= 0 && pixelX < textureSize)
                    canvas.SetPixel(pixelX, pixelY, brushColor);
            }
        }
    }

    private void ClearCanvas()
    {
        canvas.SetPixels32(blankPixels);
        canvas.Apply(false);
    }

    private void OnGUI()
    {
        GUI.Box(new Rect(12, 4, 355, 34), "Drag on the plane to draw\nPress C to clear");
    }

    private void OnDestroy()
    {
        if (planeRenderer != null && planeRenderer.sharedMaterial == paintingMaterial)
            planeRenderer.sharedMaterial = originalMaterial;
        if (paintingMaterial != null)
            Destroy(paintingMaterial);
        if (canvas != null)
            Destroy(canvas);
    }
}
