# Technical notes

## Components

- `DashedSurfaceLine` (`Assets/Scripts`) owns collider-local control points, path projection, fixed-capacity mesh generation, and dash visibility.
- `DashedSurfaceLineEditor` (`Assets/Editor`) provides Scene view point creation, selection, movement, projection previews, Undo support, and coordinate copy/paste.
- `DashedLineClickRemover` on `Main Camera` owns mouse input, the surface raycast, and `Click Tolerance`. It asks the line to hide the nearest dash.

## Path construction

The line is rebuilt on enable and after authoring changes. Each segment between control points is subdivided by `Max Sample Spacing`. Samples are raycast onto the target collider according to the selected projection mode:

- **Along Direction** uses a fixed world-space `Projection Direction`, suitable for a model viewed from a chosen side.
- **Toward Collider Center** projects toward the collider bounds center, suitable for convex shapes.

`Projection Distance` bounds the raycast around each requested point. If a sample misses the collider, that portion of the path is skipped. A smaller `Max Sample Spacing` follows a curved surface more closely but needs more raycasts during a rebuild.

Visible and empty dash spans are determined by accumulated distance along the projected samples. `Dash Length` and `Gap Length` are world-space lengths and continue across control-point boundaries. Each visible dash is represented by one quad, so its geometry is a straight approximation of its sampled surface span.

## Rendering and interaction

The mesh allocates `Dash Capacity × 4` vertices and `Dash Capacity × 2` triangles. Unused slots have zero alpha. The shader uses back-face culling; the line material renders in the overlay queue with an always-pass depth test. This keeps the dashes visible above the model without adding more geometry to conform each dash to every surface triangle.

The horse scene uses a front-facing normal check. A line on the far side of the model is therefore hidden even though the material draws above it. The single-sided quads use their fixed winding to disappear when viewed from behind.

`TryRemoveNearestDash` searches the visible dash center segments. Once it finds a dash within the distance passed by the caller, it writes transparent alpha to that dash's four vertex colors. It does not rebuild the mesh or replace its triangles. A full rebuild is needed only when the path or line settings change.

## Click-to-line call

On a left mouse click, `DashedLineClickRemover.Update` creates a camera ray with `ScreenPointToRay(Input.mousePosition)`. `TryRemoveFromRay(Ray ray)` raycasts that ray against `surfaceCollider`. When the ray hits the model, it calls:

```csharp
bool removed = line.TryRemoveNearestDash(hit.point, clickTolerance);
```

- `line` is the referenced `DashedSurfaceLine` component.
- `hit.point` (`Vector3`) is the surface hit position in **world coordinates**.
- `clickTolerance` (`float`) is the maximum **world-space distance** from that position to a visible dash. It is serialized on `DashedLineClickRemover` (0.055 in the horse scene), not on `DashedSurfaceLine`.
- The returned `bool` is `true` only when a visible dash was found within that distance and hidden.

`DashedSurfaceLine.TryRemoveNearestDash(Vector3 worldPoint, float maxDistance)` can also be called by another input component without changing the line generator. It clamps a negative distance to zero and updates only the four vertex colors of the selected dash.

## Limits

- Increase `Dash Capacity` when the inspector reports that the route needs more dashes. Unused capacity still reserves mesh vertices and triangles.
- A dash is one flat quad. On sharp curvature it may deviate slightly from the surface. Reduce dash length or sample spacing where this matters.
- The projection ray must reach the intended collider surface. If the editor shows **Outside projection range**, adjust the point, projection mode, direction, or distance.
- The component expects to live on a separate GameObject from the surface collider so it does not replace the model's mesh.
