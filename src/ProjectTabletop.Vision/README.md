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

`BoardDetector.Detect(width, height, stride, bgra)` uses camera-frame edges to
find a complete outer board quadrilateral containing the projected grid. It
returns four camera-pixel corners in top-left, top-right, bottom-right,
bottom-left order and a relative confidence value, or `null` when the evidence
is weak. The detector intentionally rejects a clipped board instead of
mistaking the inner projected grid for the board. This is a setup aid, not a
physical measurement or a substitute for projector/camera calibration. The
current geometry assumes the board is larger than the projected square, as in
the first phone-camera capture; verify it on the newly centered live view.

## Synthetic regression

Run:

```powershell
dotnet run --project src/ProjectTabletop.Vision/Regression/ProjectTabletop.Vision.Regression.csproj
```

The fixture trains on two cards with the same asymmetric outline and different
bright dot patterns at multiple rotations. It saves and reloads full captures,
then checks two pieces in one frame and an unpatterned card with the same
outline. It also checks board corners around a projected grid and rejects an
isolated projection with no outer board. This proves data flow, persistence,
classification, and pose fitting against clean
generated images. It does **not** establish accuracy under moving projected
video, hand occlusion, glare, or retroreflective bead behavior. Real camera
captures must drive the final choice of segmentation and training data.
