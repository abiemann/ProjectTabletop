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

The engine uses the previous hand's palm landmarks as crop hints for fresh
landmark inference, avoiding full-image palm searches on every tracked frame.
A lost fit searches immediately, and periodic searches acquire arriving hands.
Call `ResetTracking()` after a camera/scene interruption; changing dimensions
also invalidates crop hints. Old landmarks are never returned as current results.

`Detect` also accepts optional `HandTrackingBounds` camera search regions. On
acquisition and periodic-search frames it tries at most two valid, deduplicated,
in-frame square crops, then preserves the ordinary full-frame and tiled searches.
Palm proposals are translated to camera coordinates and hand landmarks are
inferred from the original frame. These hints do not bypass either model threshold
or turn movement into a hand detection. Diagnostics label these attempts `motion-roi`
(the hints can come from foreground, motion, or scheduled control-area searches).
If acquisition fails, the engine retries each supplied hint with more palm context:
same-center squares 1.8 and 2.0 times its side, capped at 60% of the camera's short
side and shifted inside the frame at an edge. This recovers text-centered searches that
see fingertips but omit the palm. Diagnostics label the retry `motion-roi-context`.
It runs only after ordinary fits fail; successful acquisition and tracked-only
frames incur no extra context searches. Restricted boards still wait for a valid
control hint and never use whole-camera or tiled fallback.
If the raw fits still fail, the focused palm thumbnails get a bounded
colour-cast correction estimated from bright, modest-chroma pixels. Correction
needs sufficient reference pixels and a material colour imbalance; its channel
gains stay within .75-1.35. Diagnostics label these attempts
`motion-roi-normalized` or `motion-roi-context-normalized` and report the gains
and reference-pixel count. Only the
palm-search thumbnail changes: landmark inference, previews, captures and copied
images use the original camera pixels. Existing model confidence cutoffs remain.
Run `-- --hand-color-correction` for recovery, raw-tracking continuity, neutral
input and empty-frame checks.

On button boards, a fresh confirmed disturbance schedules its native crop before
optional search lighting. If the current image already gives valid landmarks,
the normal fitted hand spotlight takes over; if recognition fails, the search
light assists later frames. Illumination never supplies a hand or gesture by itself.

`HandAcquisitionMotionTracker` finds local luminance changes inside a supplied
camera polygon. It uses equal sampling scale on both axes, compensates for global
brightness shifts, rejects noise/broad changes, and returns at most two bounded
square search crops plus suggested light geometry. A hint expires after 450 ms
without new movement. Call `Reset` after changes to the expected scene or lighting;
the app does so around Blackjack redraws and exploratory illumination. These hints
are acquisition aids only; they never supply gesture observations.

`HandAcquisitionPresenceTracker` accepts an immutable `HandAcquisitionSceneImage`
containing the generated BGRA board surface and native-camera-to-board-UV mapping.
Optional normalized `BoardSearchRegions` restrict foreground candidates to button
interiors on every interactive board. Separate `BoardReferenceRegions` can include
stable interface areas for photometric fitting without letting them trigger a
spotlight. This preserves a lighting reference when a hand covers most of a lone
Back button. Photo Copy excludes swirl animation, status text and object lighting
from both masks while looking for still fingers covering its three controls.
It samples expected colours without deforming camera images, fits a robust camera
RGB mixing response and illumination gradient, retains dark controls while trimming
outliers, and uses distributed matching colours to correct remaining camera response.
For button masks, it compares dense edge pixels with nearby expected colours and
antialiased mixtures, while excluding edges from photometric fitting. A connected
candidate must cover at least 7% of one control's eligible interior, independently
of how many other controls are visible. Occupied cell extents include the full
last sample cell. Unmasked and illuminated-core checks keep their existing rules.
Readable projected letters can also land on fingers. A reliable compensated RGB
fit therefore measures localized chroma changes on the registered generated ink
and halo. This alternative requires an independent clean caption/reference,
changes on at least half the sampled ink, the same independent 7% control and
caption area floors, and two fresh observations. Panel-only tint, global colour
or exposure drift, and illumination itself cannot qualify. Diagnostics retain
the structural text result separately from `CaptionReflectanceChanged` and its
measured coverage, so readable letters are not reported as missing glyphs.
It returns up to two stationary foreground regions. It can start with a hand
already present and does not absorb that hand into its background. A fixed camera
reference is available only as a fallback without a usable rendered scene and
cannot identify objects already present in that reference. Reset on scene,
calibration or camera changes, not on search-light toggles. During exploratory
illumination, the tracker excludes that light from template fitting and checks
its white core separately after settling. Fresh foreground can retain the light;
an empty core or lack of fresh evidence cannot. These regions are acquisition
hints, never semantic hand recognition. A uniform occluder matching the exact footprint
of a uniquely coloured control can be ambiguous; the app also searches the control
area directly with the hand model. The app waits 900 ms after turning a
search light off before permitting another foreground-triggered light, because
camera timestamps describe CPU arrival rather than physical exposure.
Run `-- --hand-acquisition` for motion, stationary foreground, own-light feedback,
focused-search and colour-correction regressions.

DEBUG local control `capture_hand_acquisition` saves the immutable native camera
and rendered-reference BGRA buffers with dimensions, mapping, source UTC and
diagnostics under the app's `HandAcquisitionSnapshots` directory. These private
captures support exact replay and are not repository fixtures.

`PaintDisturbanceTracker` compares native camera samples with a bounded history of
generated Paint frames through the camera-to-board homography. It estimates one
global render delay from unobstructed regions, compares that historical frame and
its immediate neighbors, and tolerates interpolated exposure colours. Two fresh
observations and a minimum board-UV residual area (7% of the measured Photo Copy
control interior) are required before emitting a drop. The header is excluded;
scene/calibration changes, stale frames and missing current history clear pending
evidence. Held and moving obstructions have separate bounded emission cadences.
This detects physical interference, not hand identity or a gesture. Run `--paint`
for animation-history, noise, generic-object, perspective, cadence and reset checks.

During duplicate suppression, a fresh tracked fit with confidence at least .90
and previous-bounds IoU at least .65 takes precedence over an overlapping search
fit with at most .01 greater confidence. The original confidence and landmarks
remain unchanged. Search results keep normal priority for separate hands,
clearly stronger fits, or failed/weak/low-continuity tracking. Diagnostics report
`tracked-continuity` when this preference suppresses an alternate search fit.
Run `-- --hand-candidates` for the eight recorded near-tie cases and recovery,
confidence-boundary, and independent-hand counterexamples. These fixtures contain
numeric landmark data only. `-- --hands` includes these checks and model inference;
`-- --hand-diagnostics` checks that diagnostics do not change detection results.

The app schedules one inference at a time off the UI thread and maps the index
tip through the completed board scan for the pinch cursor and board buttons.
`HandGestureTracker` confirms a thumb/index pinch using observed dwell, tolerates
bounded landmark noise and brief missed detections, and debounces release before
allowing another execute event. Each event produces a one-second red pulse.
The gesture tracker's `HandCursor.Position` carries the actual fingertip for selection and
Photo Copy hand matching. Optional `SelectionPosition` and `SelectionFrameTime`
retain the same hand's recent open pointing pose while the fingers close. An
expired or moved anchor is non-finite until release; consumers must reject it,
and must reject anchors from before a screen change or camera reset.
These image landmarks do not establish physical board contact. See the root
README for verification status. [Model sources, checksums, and licenses](Models/Hands/README.md)
are bundled with the models.

`HandVisualSmoother` creates separate display-only cursor copies for the camera
preview and projector on every board. It filters index and four-finger markers
by tracking identity and source-camera time, with stronger damping for small
palm-relative changes and faster following during deliberate motion. Missing
hands are not drawn; new identities, large jumps, long gaps, and camera/setup
resets discard history. Gesture classification, selection anchors, button hit
testing, and Photo Copy hand matching continue to use the original observations.
`HandSpotlightSmoother` filters each matched light independently, bounds center
lag to 4% of the current radius, expands immediately to cover the complete fresh
hand fit, and smooths contraction. It does not change the existing dropout hold
or execute suppression. Tester logs retain raw landmarks/cursors and add
`visualCursors`; lighting diagnostics pair rendered `lights` with `rawLights`.
Run `-- --hand-visuals` for the focused smoothing regressions.

`HandPoseClassifier.AreFourFingersExtended` recognizes an extended index,
middle, ring and little finger without requiring a spread thumb.
`HandCursor` publishes `HasFourExtendedFingers`, an immutable four-point
`FingerTips` snapshot (landmarks 8, 12, 16 and 20), and a matched `TrackingId`
that is not reused after reset. `HandPoseClassifier.DescribeFingerSelection`
reports `Together`, `IndexSeparated`, `OtherFingersGrouped` and nullable
palm-normalized gaps between adjacent fingers. Invalid or non-extended poses
have null gaps. Cursor flags `FingersTogether` and `IndexFingerSeparated` carry
the accepted current-frame geometry; the other three fingers must remain
extended and grouped while the index moves sideways. The app maps the
**middle fingertip** to a board button; all four tips do
not need to fit inside it. `BoardSession` first confirms fingers together over
the target, then requires a fresh index-separation transition to select. Holding
a static grouped or separated pose does not activate a button. Bringing the
fingers together rearms the gesture; stale results and wall time cannot complete
it. The camera and projector show four fingertip markers with the middle aim
marker in gold. Button feedback advances through **Bring fingers together**,
**Ready · separate index**, **Selecting**, and **Selected · bring fingers
together**. Thumb/index pinch selection remains available. Photo Copy's shutter,
menu and reset buttons also accept index separation. The shutter matches the
confirmed selecting cursor to the same frame's actual hand landmarks.

`HandPoseClassifier.IsSpreadOut` classifies the current 21 landmarks: all four
fingers must be extended and laterally separated, with an extended, open thumb.
Its palm-normalized geometry is invariant to two-dimensional scaling, mirroring
and rotation; severe foreshortening, overlap and three-dimensional pose are not
resolved. Invalid or degenerate landmarks are rejected.
`HandGestureTracker` requires 120 ms of source-frame observations before setting
`HandCursor.IsSpreadOut`. A folded pose or missing hand clears that evidence;
an observation gap over 350 ms restarts its dwell, and stale results older than
350 ms or resets clear it. It never creates a pinch execute event. The separate
button gesture requires fingers together followed by lateral index separation;
a static spread hand does not select a button.
In Hand-Tracking,
the confirmed pose displays **Spread out hand** on the board and laptop; it takes
caption priority over a lingering pinch pulse without altering that pulse.
Diagnostic records retain raw `spreadOutPose` and confirmed cursor `isSpreadOut`
values, while local status exposes `spreadOutHandCount` and `handTestStatus`.
Records also include raw `fourFingersExtended`, cursor `trackingId`, `fingerTips`
and `hasFourExtendedFingers`, grouping/separation flags and button selection
feedback. Local status adds `fourFingerHandCount` and `fingerSelectionFeedback`;
`lastHandDetection` contains the current four-tip geometry and pose result.
The user confirmed reliable recognition in the live tester on the current board;
broader pose and lighting accuracy remains unmeasured.

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
center and relative confidence. The app measures four ordered disks to form
the camera-to-projector homography. A fifth disk at the projector center
independently validates the result, using the unchanged 0.015 normalized error
limit, before the app reveals the selected board app or media within the safe
board corners. The five-disk sequence omits the former four-disk edge refinement;
edge accuracy with camera lens distortion may be lower. The detector compensates for a global camera exposure shift, but
movement between baseline and spot frames can still invalidate a measurement.

The older `BoardDetector.Detect(...)` is kept for the projected-grid regression
and compatibility. It requires a separately visible board contour outside a
smaller grid and is not the full-white physical-cardboard scan.

## Regression and hardware checks

`HandTrackingEngine.CaptureDiagnostics` optionally records a read-only
`LastDiagnostics` snapshot for each inference: tracked-region attempts and overlap,
full/tile palm-search views, landmark confidence/rejection, candidate landmarks,
and suppression/selection decisions. It defaults to off and does not change model
thresholds or selection. The app enables it in the Hand-Tracking tester and writes
the snapshot with pinch and spotlight geometry to local JSONL logs. Run
`-- --hand-diagnostics` to compare instrumented and ordinary inference results.

Run `dotnet run --project src/ProjectTabletop.Vision/Regression/ProjectTabletop.Vision.Regression.csproj -- --hand-poses`
for focused spread-hand pose and gesture-state regressions.

Run the same command with `-- --finger-selection` (also accepted as
`-- --four-fingers`) for four-finger grouping and index-separation geometry and
cursor contract checks, alongside spread and existing pinch regressions.
The interaction verification project's `BoardFingerSelectionRegression` checks
the grouped-to-separated sequence, rearming and rejection of a static pose. The app's isolated DEBUG
`--once verify_finger_selection` check covers selection and visual feedback.
The full solution's Debug x64 build passes with zero warnings/errors. `--finger-selection`
passes the new geometry plus existing pinch/spread checks. Full interaction
verification passes the selection suite, 400 deterministic Blackjack rounds and
the split-hand bust-then-repeat-Hit case. Isolated app checks
`verify_finger_selection`, `verify_blackjack`, `verify_hand_spotlights` and
`verify_hand_pose_feedback` pass; menu and casino Ready-state screenshots have
been visually inspected. The first live trial reached **Ready** but did not
select. Its normalized index/middle gap measured about 0.15–0.27 together and
0.45–0.55 apart, below the original 0.56 separation cutoff. The classifier now
uses a together cutoff of 0.35 and a separated cutoff of 0.44, retaining a 0.09
neutral band and removing knuckle-spacing bias. Added 0.20-together,
0.39-neutral and 0.48-separated fixtures pass, including transformed versions
and existing pinch/spread regressions. After alignment was restored, a live
trace showed Ready at a 0.235 gap, a 50-credit wager selected at 0.483, no repeat
while the index stayed apart, and rearming after rejoining at 0.267. The user
confirmed that selection now works. Smaller openings around 0.40–0.43 remained
neutral; broader lighting, positions, orientations and motion ranges remain
unmeasured.

Photo Copy uses `PhotoCopyHandSelector.TrySelectGestureShutter` or
`TrySelectShutter` to match fresh index-separation or pinch input to the hand
making the command. One or two hands may be visible; one hand issues the shutter
command. The current app acquires an object **before** illuminating it:
`PhotoObjectLocator.Locate` rectifies the camera frame to the board plane,
estimates the plain grey background above the bottom controls, and selects one distinct
foreground component. A smooth local illumination correction is fitted from
background-like pixels, accounting for gradual projector glare without blurring
contrasting object colors into that background estimate. The shared capture
rectangle runs from 6% to 72% of board height, reserving the title above and
controls/status below, with a 1% guard at the sides. Call it only after hands and their projected lights have
left the view and exposure has settled. The returned `PhotoObjectTarget` owns
an immutable alpha silhouette, including holes, plus board-space geometry for
the object's spotlight. The app requires three stable candidates spanning at
least 600 ms, including matching position, bounds, area, mask and light shape,
before locking that light, while hand spotlights continue to move independently.

`PhotoObjectSpotlight` recognizes supported pairs of opposite parallel edges,
including the oblique coordinates produced by mapping a non-square board to a
square detection mask. The light retains both edge directions through an affine
transform, so it follows a rotated physical rectangle. Long rounded remotes can
also qualify through their straight sides. Round and irregular subjects fall
back to a circle. The light covers every pixel of the original alpha plus its
presence-sampling margin, preserving narrow loops or bookmarks ignored only
during classification. Rendering keeps a solid white core and soft outer edge.
Hand illumination remains a separate moving circle.

`PhotoObjectLocator.ObserveTarget` checks the locked silhouette against its
illuminated surroundings. Both the interior and boundary must still match;
mapped hands and forearms are excluded from the comparison. It distinguishes
`Present`, `MissingOrMoved`, `Occluded`, and `Unavailable` so a brief obstruction
does not immediately erase a lock. The app clears a repeatedly moved or missing
target and waits for a settled grey field before acquiring again.
The app's local-control `photoCopyObservation` exposes the last accepted source
frame's UTC, revision, state, failure detail and camera-to-board transform, plus
the candidate center/area during acquisition, so observations can be matched to
camera evidence.
`PhotoObjectExtractor.ExtractTarget` repeats the presence/occlusion check on the
confirmed-command frame and copies **that frame's RGB pixels** through the stored
alpha. It never resegments the projected white halo or reuses the acquisition
photograph's colors. An overlapping shutter hand or forearm prevents capture.

The app then calls `PhotoCopyCameraImage.Capture` to map only this alpha mask back
to the original camera frame. RGB pixels are copied directly without resizing;
the native crop retains its camera-space anchor and inward axis. Its immutable
`PhotoCopyCameraGeometry` carries the camera-to-board map and frame dimensions.
The compositor rotates and scales copies uniformly on a camera-sized layer,
then maps that layer to the board for alignment. Thus copied photos preserve
their photographed proportions at every rotation instead of rotating a stretched
square-board bitmap. This preserves the camera photograph's perspective; it
does not infer physical object dimensions from an uncalibrated camera.

Generic objects need visible contrast on grey and under white illumination,
not a trained class or hand landmarks. Multiple subjects, clipped objects and
insufficient clear margin are rejected with retry guidance. Transparent,
low-contrast and reflective materials remain limited by visible contrast;
shadows can affect the acquired outline. If no object is locked and the other
hand is visible, `PhotoHandExtractor` still provides landmark-seeded GrabCut,
wrist trimming and its extended middle-finger direction. Generic subjects use
a center anchor and upward direction. Both paths retain current camera pixels
with straight-alpha BGRA; no generated image or semantic object model is involved.
`PhotoHandCutout` retains its historical field names for both subject types.
No empty-board reference step is required.

Run `-- --photo-objects` for shutter selection, the earlier white-background
extractor and the new grey target checks, or `-- --photo-copy` to include hand
color, alpha, finger-gap, shadow, wrist, stride, perspective and rejection checks.
Shape tests render the returned illumination geometry and cover rotated rectangles,
bookmark/loop coverage, circular and irregular fallbacks, presence and current-pixel
extraction under the tighter light. The final live book acquired a rounded rectangle
and remained `Present`; the app's GPU check also verifies rotated corner coverage.
The complete `--photo-copy` suite passes, including immutable target masks,
current illuminated pixels, stationary/moved/removed/occluded observations,
dark/light/colored subjects, gradients, padded rows, local glare compensation
and objects inside the 1% boundary guard. The correction also recovered a saved
real book frame that previously yielded two false glare components; regressions
still reject a small real second object rather than filtering it away.
On the physical board, a book near the lower edge was acquired and captured
through the live camera pipeline as a 242 × 205 cutout with a fully transparent
exterior border. The renderer completed all 576 copies with controls visible.
After reset, sampled observations over more than a minute kept the same lock
and reported `Present` without hands or new copies. Broader gesture feel and
moved-object reacquisition remain to be tested live.

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
