# Short gold-caption acquisition fixture

These lossless PNGs were produced by Project Tabletop's Win2D renderer at 3840 x 2160,
with a separate 1000 x 1000 generated reference. They contain no webcam images or user
data. The occupied frame overlays four uniform red strips across the gold Start control.

They were captured on September 28, 2026 by the isolated Monopoly verification using
[the Monopoly renderer](../../../ProjectTabletop.App/Projection/SceneCompositor.Monopoly.cs).
The exact camera-to-board matrix, search polygon, control rectangles, glyph rectangles
and independent reference bands are retained in
[the regression source](../HandAcquisitionShortLabelRegression.cs).

The short caption supplies 3.87% measured glyph/halo damage, while the physical
obstruction covers most of the control. A corrupted control must be excluded from
camera-colour training before measuring foreground, even when its raw glyph area is
small. This exclusion does not invent area or change the independent 7% control and
label requirements. Two fresh frames remain necessary to illuminate the control.

The accompanying regression checks first-frame presence, an optional empty baseline,
stationary persistence and removal using the exact native camera-to-board mapping.
