# ProjectTabletop.Vision

This .NET 10 library is the first local vision baseline for several white cards with
camera-visible patterns. It uses OpenCvSharp 4.13.0.20260627 to segment candidate
outlines, trains a multiclass SVM on rotation-invariant shape and brightness-pattern
descriptors, rejects unfamiliar candidates with an exemplar-distance check, and
fits each recognized card's outline and front direction. It does not use a cloud
service.

## App integration

1. Create one `VisionEngine` and adjust its `VisionSettings` for the actual camera
   frame and lighting. `Brightness`, `OtsuBright`, and `BackgroundDifference`
   segmentation are available. The latter needs `SetEmptyBoardReference(...)`.
2. Let the user draw each card's top outline and click its front direction on the
   live camera preview. For each labeled piece in a frame, call
   `AddLabeledCapture(pieceId, width, height, stride, bgra, outline, front)`.
   `PixelPoint` coordinates are in the original camera image. A single frame may
   contain several labeled pieces; call the method once per piece.
3. Capture several views of every piece at different positions and angles, then
   call `Train()`.
4. For every camera frame, call `Detect(width, height, stride, bgra)`. Each
   `PieceDetection` has a stable `PieceId`, camera-pixel center, front angle,
   fitted `Outline`, raw `ObservedOutline`, and a relative confidence score.
   Use the fitted outline for the boat mask after applying the calibrated mapping
   for the raised piece top.
5. Call `Save(directory)` to write `vision-profile.json`, `vision-svm.yml`, and
   **full original camera snapshots** under `captures/<capture-id>.png`. Load a
   session with `VisionEngine.Load(directory)`.
6. Run `Evaluate(annotatedFrames)` to get per-piece identity, center, angle,
   and outline IoU errors. Keep held-out frames from the real projection setup
   for meaningful regression testing.

The engine expects BGRA8 top-down image data. `stride` may exceed `width * 4`.
Its public coordinate values are camera pixels; display/projector calibration is
outside this library. Engine operations are synchronized internally, so training
will briefly pause detection. Change `VisionSettings` only while detection is idle;
train again after changing settings that affect feature extraction.

## Board setup detection

For a new scan, first display a **full-frame uniform white image** on the
projector. Call `BoardDetector.DetectProjectedField(...)` for the outer light
field and `BoardDetector.DetectUniformIllumination(...)` for a separate physical
cardboard contour within it. Both return camera-pixel corners in top-left,
top-right, bottom-right, bottom-left order, or `null` when all four edges are
not distinguishable. The cardboard detector accepts a wide or narrow camera
quadrilateral because the oblique camera view changes the apparent aspect
ratio. It requires consistent brightness contrast across all four edges and
rejects a field with no inner board contour. Only call it while the projector
is actually showing the uniform scan image; a single camera frame cannot
always distinguish an unpatterned projected rectangle from real cardboard.

`BoardDetector.DetectAmbientBoard(...)` offers a separate check while the
projector output is solid black. In the current room lighting, it finds the
cardboard edge by ambient reflection. Its four corners should agree with the
full-white scan. It will return `null` if the cardboard is not brighter than
the surrounding floor or its edges are obscured.

For projector mapping, retain a fresh camera frame of the white scan. Project
one dark calibration disk at a time and call
`BoardDetector.DetectDarkCalibrationSpot(width, height, stride,
whiteBgra, spotBgra)` for each fresh camera frame. It returns a camera-pixel
center and relative confidence. Four measured disk correspondences can define
the camera-to-projector homography; validate it with a fifth point before
placing a grid on the physical cardboard. The detector compensates for a
global camera exposure shift, but movement between baseline and spot frames
can still invalidate a measurement.

The older `BoardDetector.Detect(...)` is kept for the projected-grid regression
and compatibility. It requires a separately visible board contour outside a
smaller grid and is not the full-white physical-cardboard scan.

## Synthetic regression

Run:

```powershell
dotnet run --project src/ProjectTabletop.Vision/Regression/ProjectTabletop.Vision.Regression.csproj
```

The fixture trains on two cards with the same asymmetric outline and different
bright dot patterns at multiple rotations. It saves and reloads full captures,
then checks two pieces in one frame and an unpatterned card with the same
outline. It also checks independent bright and dark cardboard contours in a
uniform light field, rejects a light field with no cardboard, checks clean
hardware frames under both full-white and black projector output, and locates
a dark calibration disk despite a global exposure shift. The older projected-grid
checks remain for compatibility. This proves data flow, persistence,
classification, and pose fitting against clean
generated images. It does **not** establish accuracy under moving projected
video, hand occlusion, glare, or retroreflective bead behavior. Real camera
captures must drive the final choice of segmentation and training data.
