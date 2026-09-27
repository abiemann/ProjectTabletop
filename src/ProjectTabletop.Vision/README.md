# ProjectTabletop.Vision

## Hand interaction prototype

The current app priority is hand interaction before card recognition.
`HandTrackingEngine` uses bundled OpenCV Zoo MediaPipe ONNX palm and hand models
through OpenCvSharp DNN on the CPU. Construct it with the `Models/Hands` directory
from the build output and pass top-down BGRA8 images to
`Detect(width, height, stride, bgra)`. Each returned `HandDetection` contains 21
camera-pixel landmarks, confidence, a model handedness score, and `IndexTip`
(landmark 8). Up to two hands are returned. Dispose the engine after inference
has stopped. No training samples or network service are needed.

The app schedules one inference at a time off the UI thread and maps the index
tip through the completed board scan for the projector cursor. Predicted hand
landmarks do not establish board contact. Button hit testing and press/release
gestures are not implemented. Performance under the physical projector remains
to be checked. [Model sources, checksums, and licenses](Models/Hands/README.md)
are bundled with the models.

## Card recognition baseline

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
After the coarse contour search, each physical cardboard side is measured at
the full camera resolution and fitted as a line. The intersections give
fractional-pixel corner coordinates. The projector field remains a coarse
camera-space outline because it is used only to check scan geometry.

`BoardDetector.DetectAmbientBoard(...)` offers a separate check while the
projector output is solid black. In the current room lighting, it finds the
cardboard edge by ambient reflection. Its four corners should agree with the
full-white scan. It will return `null` if the cardboard is not brighter than
the surrounding floor or its edges are obscured. When the cardboard is only
a few pixels from the projector field edge, prefer the black-output result
for exact board corners and use the white result as an independent check.

If the black-output scan loses one edge after a small move, call
`BoardDetector.RecoverAmbientBoardWithPrior(...)` with the last trusted
four-corner detection. It measures the three remaining ambient-light lines
near their previous locations and fits a fixed-size cardboard pose on the
same plane. It rejects large or geometrically inconsistent moves. Three
image lines alone cannot determine the fourth under perspective, so the
previous pose is essential; a recovered corner is less certain than a fresh
four-edge measurement. Do not use this on a white or patterned projection.

`ProjectedCornerDetector.Detect(...)` measures the orange L brackets after the
grid appears. Supply the four physical board corners from the black-output
scan as a search prior and a fresh BGRA camera frame. Its four nullable results
give the intersections of the two projected arm centerlines, arm-fit RMS, and
a relative confidence. It inspects only red/orange pixels in small regions
around the expected corners, requires two long arms, and returns `null` for a
clipped or occluded marker. Comparing an intersection to the independently
measured board corner reveals a camera-pixel projection residual. This is a
diagnostic measurement; the app does not currently use one frame to correct
projector output.

For projector mapping, retain a fresh camera frame of the white scan. Project
one dark calibration disk at a time and call
`BoardDetector.DetectDarkCalibrationSpot(width, height, stride,
whiteBgra, spotBgra)` for each fresh camera frame. It returns a camera-pixel
center and relative confidence. The app first measures four central disks to
form a provisional camera-to-projector homography. `NearEdgeRegistrationPlan`
then places four more disks safely inside the detected physical corners; those
measurements form the final homography. A ninth disk at the projector center
validates the result before the app shows the selected board app or media within
the safe board corners. The detector compensates for a global camera exposure shift, but
movement between baseline and spot frames can still invalidate a measurement.

The older `BoardDetector.Detect(...)` is kept for the projected-grid regression
and compatibility. It requires a separately visible board contour outside a
smaller grid and is not the full-white physical-cardboard scan.

## Regression and hardware checks

The default regression also runs the bundled hand models against an independently
annotated MediaPipe pointing-hand image, its rotation, an off-center landscape
placement, padded camera rows, and a two-hand original/mirrored composite. Blank
images and saved board photographs check no-hand rejection. These are offline
model and coordinate checks, not proof of live projected-hand tracking. Run only
these cases with `-- --hands`; see the [fixture provenance](Regression/Fixtures/HAND-FIXTURE.md).

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
a dark calibration disk despite a global exposure shift. Two additional
hardware frames show the board after it was moved: the black-output and
projected-white estimates agree within 3 camera pixels at all four corners.
The three-edge recovery also runs against those moved camera frames with each
side withheld in turn, then each photographed edge gradually erased in turn;
all recovered corners remain within 6 camera pixels of the complete black
scan. A blank frame and an unrelated prior pose are rejected.
This measures repeatability on those frames, not absolute geometric accuracy.
Three live nine-spot scans with the moved cardboard completed with black/white
corner disagreement of 3.9, 3.4, and 3.9 camera pixels and normalized center-check
errors of 0.0007, 0.0018, and 0.0021. A first-run optical comparison placed the four orange
bracket centerline intersections within 5.3 camera pixels of the black-frame board
corners. This estimate depends on camera image thresholding and projected line
blur. Three-edge recovery has not yet been tested with a live physical occlusion.
The orange-bracket regression measures two saved live grid frames. Three
corners fit in both, while the fourth fits in only one frame because its bottom
arm is almost clipped by the projector field. The fixture rejects an erased
arm and an ambient-only frame. The regression can also print the marker fits
for another camera capture with `--markers <absolute-png-path>`.
The older projected-grid checks remain for compatibility. The synthetic card
checks establish data flow, persistence, classification, and pose fitting
against clean generated images. They do **not** establish accuracy under moving
projected video, hand occlusion, glare, or retroreflective bead behavior. Real camera
captures must drive the final choice of segmentation and training data.
