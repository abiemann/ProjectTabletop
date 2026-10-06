# Short gold-caption acquisition fixture

These lossless PNGs were regenerated on October 5, 2026 with the isolated
[short-caption fixture generator](../../../../tools/ShortCaptionFixtureGenerator/README.md).
They contain an original neutral optical calibration scene and only the production
Crown & Deed control plates, without game names, named properties, camera images
or user data. Ordinary application builds and startup never generate them.

The independent reference is 1000 x 1000; the camera scenes are rendered natively
at 3840 x 2160. The historical camera-to-board matrix, search polygon, seven
control rectangles and glyph rectangles remain unchanged in
[the regression source](../HandAcquisitionShortLabelRegression.cs). Two fixed
colour-reference bands replace the former property artwork. The occupied frame
retains four uniform red strips over the short gold Start control.

The short caption still supplies less than 7% raw measured glyph/halo damage,
while the physical obstruction covers most of the control. A corrupted control
must be excluded from camera-colour training before measuring foreground, even
when its raw glyph area is small. This exclusion does not invent area or change
the independent 7% control and caption requirements. Two fresh frames remain
necessary to illuminate the control.

The regression checks first-frame presence, an optional empty baseline,
stationary persistence and removal with the exact native camera-to-board mapping.
The generator verifies every saved PNG against its render-target pixels and
records dimensions, geometry and hashes in [the manifest](short-gold-caption.json).
