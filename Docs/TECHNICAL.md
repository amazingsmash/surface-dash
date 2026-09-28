# Technical notes

## Components

- `DashedSurfaceLine` (`Assets/Scripts`) owns collider-local control points, path projection, fixed-capacity mesh generation, and dash visibility.
- `DashedSurfaceLineEditor` (`Assets/Editor`) provides Scene view point creation, selection, movement, projection previews, Undo support, and coordinate copy/paste.
- `DashedLineClickRemover` raycasts from the demo camera and asks the line to hide the nearest dash within `Click Tolerance`.
- `DashedPlanePainter` is an independent texture-based demonstration for a flat plane.

## Path construction

The line is rebuilt on enable and after authoring changes. Each segment between control points is subdivided by `Max Sample Spacing`. Samples are raycast onto the target collider according to the selected projection mode:

- **Along Direction** uses a fixed world-space `Projection Direction`, suitable for a model viewed from a chosen side.
- **Toward Collider Center** projects toward the collider bounds center, suitable for the sphere example and similar convex shapes.

`Projection Distance` bounds the raycast around each requested point. If a sample misses the collider, that portion of the path is skipped. A smaller `Max Sample Spacing` follows a curved surface more closely but needs more raycasts during a rebuild.

Visible and empty dash spans are determined by accumulated distance along the projected samples. `Dash Length` and `Gap Length` are world-space lengths and continue across control-point boundaries. Each visible dash is represented by one quad, so its geometry is a straight approximation of its sampled surface span.

## Rendering and interaction

The mesh allocates `Dash Capacity × 4` vertices and `Dash Capacity × 2` triangles. Unused slots have zero alpha. The shader uses back-face culling; the included line materials render in the overlay queue with an always-pass depth test. This keeps the dashes visible above the model without adding more geometry to conform each dash to every surface triangle.

The sample sphere material also enables a front-facing normal check. A line on the far side of a closed shape is therefore hidden even though the material draws above the model. The single-sided quads in the directional model use their fixed winding to disappear when viewed from behind.

`TryRemoveNearestDash` searches the visible dash center segments. Once it finds a dash within `Click Tolerance`, it writes transparent alpha to that dash's four vertex colors. It does not rebuild the mesh or replace its triangles. A full rebuild is needed only when the path or line settings change.

## Limits

- Increase `Dash Capacity` when the inspector reports that the route needs more dashes. Unused capacity still reserves mesh vertices and triangles.
- A dash is one flat quad. On sharp curvature it may deviate slightly from the surface. Reduce dash length or sample spacing where this matters.
- The projection ray must reach the intended collider surface. If the editor shows **Outside projection range**, adjust the point, projection mode, direction, or distance.
- The component expects to live on a separate GameObject from the surface collider so it does not replace the model's mesh.
