#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Runs on an isolated compositor and offscreen GPU target. Synthetic hands
    // never enter camera recognition or the live board's input/rendering state.
    private async Task<object> VerifyHandSpotlightsAsync()
    {
        const int size = 800;
        using var scene = new SceneCompositor();
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.1f, .1f), new(.9f, .1f), new(.9f, .9f), new(.1f, .9f)],
            Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        long gestureEvent = 0;
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        Draw(); // Warm cached text/surface creation before timed light observations.

        HandDetection centerHand = Hand(.45, .6);
        scene.SetHandSpotlights([centerHand], DateTimeOffset.UtcNow);
        var illuminated = Draw();
        Require(scene.ActiveHandSpotlightCount == 1, "The synthetic hand did not acquire a spotlight.");
        foreach (PixelPoint point in centerHand.Landmarks.Skip(1))
            Require(WhiteAt(illuminated, point), "The opaque white core did not cover every thumb/finger landmark.");

        var pinchingPoints = centerHand.Landmarks.ToArray();
        pinchingPoints[0] = new(.45, .79); // Wrist extension must not pull light away from the fingers.
        pinchingPoints[4] = pinchingPoints[8] = new(.4, .6);
        var pinchingHand = centerHand with { Landmarks = pinchingPoints };
        scene.SetHandSpotlights([pinchingHand], DateTimeOffset.UtcNow);
        var pinchIlluminated = Draw();
        foreach (PixelPoint point in pinchingHand.Landmarks.Skip(1))
            Require(WhiteAt(pinchIlluminated, point), "The opaque white core lost finger coverage during a pinch.");

        HandDetection left = Hand(.3, .6), right = Hand(.7, .6);
        scene.ClearHandTips(); // Begin an independent fixture rather than retaining the preceding hand's hold.
        scene.SetHandSpotlights([left, right], DateTimeOffset.UtcNow);
        var both = Draw();
        Require(scene.ActiveHandSpotlightCount == 2 && WhiteAt(both, new(.3, .6)) && WhiteAt(both, new(.7, .6)),
            "Two detected hands did not render independent white spotlights.");

        // The hand's circle crosses the left board edge; clipped output must
        // still be black even where that circle would otherwise illuminate it.
        scene.ClearHandTips();
        scene.SetHandSpotlights([Hand(.13, .6)], DateTimeOffset.UtcNow);
        var clipped = Draw();
        Require(WhiteAt(clipped, new(.16, .6)) && BlackAt(clipped, new(.08, .6)),
            "A spotlight failed to reach the board edge or spilled outside the board clip.");

        scene.ClearHandTips();
        scene.SetHandSpotlights([left], DateTimeOffset.UtcNow);
        var currentTime = DateTimeOffset.UtcNow;
        scene.SetHandSpotlights([left], currentTime);
        scene.SetHandSpotlights([right], currentTime.AddMilliseconds(-10));
        scene.SetHandSpotlights([right], DateTimeOffset.UtcNow.AddSeconds(1));
        scene.SetHandSpotlights([right], DateTimeOffset.UtcNow.AddSeconds(-1));
        var ordered = Draw();
        Require(scene.ActiveHandSpotlightCount == 1 && WhiteAt(ordered, new(.3, .6)) && !WhiteAt(ordered, new(.7, .6)),
            "An old, stale or future camera frame moved the current spotlight.");

        PixelPoint pointingTip = BoardPoint(.25, .35); // Hand-Tracking button.
        var observationTime = DateTimeOffset.UtcNow;
        scene.SetHandCursors([new(pointingTip, DateTimeOffset.MinValue)], observationTime);
        Require(scene.HoveredBoardButtons.SequenceEqual(["hand-tracking"]),
            "The isolated input fixture did not point at Hand-Tracking.");
        scene.SetHandSpotlights([left], observationTime);
        scene.SetHandSpotlights([], DateTimeOffset.UtcNow);
        scene.SetHandCursors([], DateTimeOffset.UtcNow);
        Require(scene.ActiveHandSpotlightCount == 1 && scene.HoveredBoardButtons.Count == 0 &&
            scene.CurrentBoardScreen == BoardScreen.Menu,
            "Lighting hold supplied a phantom hover/click or ended on the first missing hand frame.");
        scene.ClearHandTips(resetInput: false);
        Require(scene.ActiveHandSpotlightCount == 1, "A visual cursor clear prematurely removed the brief lighting hold.");
        scene.ClearHandTips(resetInput: true);
        Require(scene.ActiveHandSpotlightCount == 0, "An input/camera reset left a spotlight active.");
        scene.SetHandSpotlights([left], observationTime);
        Require(scene.ActiveHandSpotlightCount == 0, "An in-flight frame from before reset relit the board.");

        var expirySourceTime = DateTimeOffset.UtcNow;
        scene.SetHandSpotlights([left], expirySourceTime);
        await Task.Delay(120);
        scene.SetHandSpotlights([], DateTimeOffset.UtcNow);
        Require(scene.ActiveHandSpotlightCount == 1, "The light disappeared before its short dropout hold completed.");
        await Task.Delay(630);
        Require(scene.ActiveHandSpotlightCount == 0 && !WhiteAt(Draw(), new(.3, .6)),
            "Empty observations refreshed lighting beyond its 700 ms source-frame lifetime.");

        // Live reproduction: one hand has executed and remains suppressed,
        // while the other hand briefly drops out of inference. Seeing the
        // suppressed hand must neither erase nor indefinitely refresh the other
        // hand's cached illumination.
        scene.ShowHandTrackingTest();
        Draw();
        const long normalId = 4101, suppressedId = 4102, recoveredId = 4103;
        void Observe((HandDetection Hand, long Id)[] observations, bool executeOther = false)
        {
            var sourceTime = DateTimeOffset.UtcNow;
            long eventId = executeOther ? ++gestureEvent : 0;
            scene.SetHandCursors(observations.Select(item => new HandCursor(item.Hand.IndexTip,
                executeOther && item.Id == suppressedId ? sourceTime.AddSeconds(1) : DateTimeOffset.MinValue,
                executeOther && item.Id == suppressedId ? eventId : 0) { TrackingId = item.Id }).ToArray(), sourceTime);
            scene.SetHandSpotlights(observations.Select(item => item.Hand).ToArray(), sourceTime);
        }
        Observe([(left, normalId), (right, suppressedId)]);
        Require(scene.ActiveHandSpotlightCount == 2, "Per-hand dropout fixture did not start with two lights.");
        Observe([(left, normalId), (right, suppressedId)], executeOther: true);
        Require(scene.ActiveHandSpotlightCount == 1 &&
            scene.GetHandLightingDiagnostics().SuppressedHandIds.Contains(suppressedId),
            "The second hand's execute did not extinguish only its own light.");
        await Task.Delay(120);
        Observe([(right, suppressedId)]);
        Require(scene.ActiveHandSpotlightCount == 1 && WhiteAt(Draw(), new(.3, .6)),
            "Seeing only a suppressed hand erased another hand's 120 ms dropout hold.");
        await Task.Delay(180);
        Observe([(right, suppressedId)]);
        Require(scene.ActiveHandSpotlightCount == 1 && WhiteAt(Draw(), new(.3, .6)) &&
            scene.HoveredBoardButtons.Count == 0 && scene.CurrentBoardScreen == BoardScreen.HandTracking,
            "A second hand erased the 300 ms light hold or held illumination generated input.");
        HandDetection recovered = Hand(.305, .603);
        Observe([(recovered, recoveredId), (right, suppressedId)]);
        Require(scene.ActiveHandSpotlightCount == 1 && WhiteAt(Draw(), new(.305, .603)),
            "A returning physical hand with a new tracker ID created duplicate cached lights.");
        var recoveredSourceTime = scene.GetHandLightingDiagnostics().LightLifetimes.Single().SourceFrameTime;
        // The already-known suppressed hand now approaches the missing hand's
        // last position. Its stable identity must prevent proximity matching
        // from claiming or refreshing the other hand's cached illumination.
        HandDetection nearbySuppressed = Hand(.355, .603);
        for (int sample = 0; sample < 8; sample++)
        {
            await Task.Delay(100);
            Observe([(nearbySuppressed, suppressedId)]);
            if (sample == 1)
                Require(scene.ActiveHandSpotlightCount == 1 && WhiteAt(Draw(), new(.305, .603)) &&
                    scene.GetHandLightingDiagnostics().LightLifetimes.Single().SourceFrameTime == recoveredSourceTime,
                    "A nearby known suppressed hand claimed or refreshed another hand's retained light.");
        }
        Require(scene.ActiveHandSpotlightCount == 0 && !WhiteAt(Draw(), new(.305, .603)) &&
            scene.GetHandLightingDiagnostics().SuppressedHandIds.Contains(suppressedId),
            "A different hand refreshed expired illumination or lost its execute suppression.");
        scene.ClearHandTips();

        scene.SetHandSpotlights([left], DateTimeOffset.UtcNow);
        scene.ShowPhotoCopy();
        Draw();
        await Task.Delay(1100);
        Require(scene.TryGetPhotoCopyCaptureContext(out var photoContext), "The grey capture field never became ready.");
        const int fixtureSize = 1000;
        const double objectAngle = Math.PI / 4, objectStretch = 1.6, objectCenterX = 270, objectCenterY = 350;
        var fixture = new byte[fixtureSize * fixtureSize * 4];
        for (int y = 0; y < fixtureSize; y++)
        for (int x = 0; x < fixtureSize; x++)
        {
            int index = (y * fixtureSize + x) * 4;
            // Normalizing a non-square physical board stretches a rotated
            // rectangle into a parallelogram. This must exercise the GPU shear,
            // not just rotation, or its long edges would no longer fit the object.
            double unstretchedX = (x - objectCenterX) / objectStretch, unstretchedY = y - objectCenterY;
            double localX = unstretchedX * Math.Cos(objectAngle) + unstretchedY * Math.Sin(objectAngle);
            double localY = -unstretchedX * Math.Sin(objectAngle) + unstretchedY * Math.Cos(objectAngle);
            byte value = Math.Abs(localX) < 60 && Math.Abs(localY) < 80 ? (byte)30 : (byte)100;
            fixture[index] = fixture[index + 1] = fixture[index + 2] = value;
            fixture[index + 3] = 255;
        }
        var photoObject = PhotoObjectLocator.Locate(fixtureSize, fixtureSize, fixtureSize * 4, fixture,
            [.001, 0, 0, 0, .001, 0, 0, 0, 1], out var locateFailure);
        Require(photoObject is not null, "The spotlight object fixture failed: " + locateFailure);
        Require(photoObject!.Spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
            "A rectangular object did not acquire a fitted rectangular light.");
        Require(Math.Abs(photoObject.Spotlight.Shear) > .3,
            "The stretched, rotated object did not exercise a substantially sheared light.");
        Require(scene.SetPhotoCopyObject(photoObject!, photoContext.Revision), "The object spotlight could not lock.");
        var obsoleteSprite = new PhotoHandCutout(1, 1, [20, 40, 80, 255], new(0, 0), new(0, -1));
        Require(!scene.SetPhotoCopyCapture(obsoleteSprite, photoContext.Revision),
            "A hand-only capture entered after a different object target had locked.");
        Require(!scene.TryGetPhotoCopyCaptureContext(out _), "Capture ignored the object's illumination settling time.");
        Draw();
        await Task.Delay(450);
        Require(scene.TryGetPhotoCopyCaptureContext(out var litContext) && ReferenceEquals(litContext.Target, photoObject),
            "The settled object light did not expose its matching capture target.");
        scene.SetHandSpotlights([right], DateTimeOffset.UtcNow);
        var twoLights = Draw();
        var objectCenter = BoardPoint(photoObject!.Center.X / 1000, photoObject.Center.Y / 1000);
        Require(scene.ActiveHandSpotlightCount == 1 && WhiteAt(twoLights, objectCenter) && WhiteAt(twoLights, new(.7, .6)),
            "Photo Copy failed to draw separate object and hand lights.");
        var fitted = photoObject.Spotlight;
        PixelPoint LightPoint(double x, double y, bool applyShear = true)
        {
            if (applyShear) x += fitted.Shear * y;
            return BoardPoint((fitted.Center.X + x * Math.Cos(fitted.RotationRadians) - y * Math.Sin(fitted.RotationRadians)) / 1000,
                (fitted.Center.Y + x * Math.Sin(fitted.RotationRadians) + y * Math.Cos(fitted.RotationRadians)) / 1000);
        }
        foreach (int signX in new[] { -1, 1 })
        foreach (int signY in new[] { -1, 1 })
        {
            double x = signX * (fitted.Width / 2 - 12), y = signY * (fitted.Height / 2 - 12);
            Require(WhiteAt(twoLights, LightPoint(x, y)),
                "The rectangular light did not shear and rotate with the object's corners.");
            // These expected source corners are independent of the fitted model.
            double objectX = signX * 58, objectY = signY * 78;
            var sourceCorner = BoardPoint((objectCenterX + objectStretch *
                (objectX * Math.Cos(objectAngle) - objectY * Math.Sin(objectAngle))) / 1000,
                (objectCenterY + objectX * Math.Sin(objectAngle) + objectY * Math.Cos(objectAngle)) / 1000);
            Require(WhiteAt(twoLights, sourceCorner), "The fitted light missed a corner of the source object.");
        }
        foreach (int sign in new[] { -1, 1 })
        {
            Require(WhiteAt(twoLights, LightPoint(sign * (fitted.Width / 2 - 8), 0)) &&
                WhiteAt(twoLights, LightPoint(0, sign * (fitted.Height / 2 - 8))),
                "The rectangular light did not cover all four fitted edges.");
            Require(!WhiteAt(twoLights, LightPoint(sign * (fitted.Width / 2 + 18), 0)) &&
                !WhiteAt(twoLights, LightPoint(0, sign * (fitted.Height / 2 + 18))),
                "The rectangular light spilled beyond its fitted edges.");
            double y = sign * (fitted.Height / 2 - 12);
            double x = -Math.Sign(fitted.Shear * y) * (fitted.Width / 2 - 12);
            Require(Math.Abs(fitted.Shear * y) > 28,
                "The GPU shear fixture does not separate transformed and untransformed corners enough.");
            // Missing/reordered shear would incorrectly illuminate this point,
            // which is inside the rotation-only rectangle but outside the fit.
            Require(!WhiteAt(twoLights, LightPoint(x, y, applyShear: false)),
                "The object light rendered an unsheared rectangular corner.");
        }
        Require(!WhiteAt(twoLights, BoardPoint(.5, .9)), "Photo Copy flooded the grey field away from either subject.");
        scene.ClearHandTips(resetInput: false);
        var photoButtons = new BoardSession();
        photoButtons.ShowPhotoCopy();
        var backBounds = photoButtons.Buttons.Single(button => button.Id == "menu").Bounds;
        var backCenter = BoardPoint(backBounds.X + backBounds.Width / 2, backBounds.Y + backBounds.Height / 2);
        // This light reaches over the actual Exit button near the bottom edge,
        // so both full control coverage and outer clipping matter.
        scene.SetHandSpotlights([Hand(backCenter.X, backCenter.Y)], DateTimeOffset.UtcNow);
        var litControls = Draw();
        Require(WhiteAt(litControls, backCenter) &&
            WhiteAt(litControls, BoardPoint(backBounds.X + backBounds.Width / 2, backBounds.Y + .02)),
            "The hand spotlight was cut off over Photo Copy's Exit button.");
        Require(BlackAt(litControls, new(backCenter.X, .92)),
            "A Photo Copy hand spotlight spilled below the physical board.");
        await Task.Delay(750);
        Require(scene.ActiveHandSpotlightCount == 0 && WhiteAt(Draw(), objectCenter),
            "The stationary object light disappeared when the hand light expired.");
        Require(scene.ClearPhotoCopyObject(photoObject, photoContext.Revision), "A removed object retained its spotlight.");
        Require(!scene.TryGetPhotoCopyCaptureContext(out _) && !WhiteAt(Draw(), objectCenter),
            "Removing the target failed to restore grey or require a fresh surface settle.");
        Require(!scene.SetPhotoCopyCapture(obsoleteSprite, photoContext.Revision, photoObject),
            "A capture completed after its object lock had been removed.");
        scene.ShowBoardMenu();
        scene.SetHandSpotlights([left], DateTimeOffset.UtcNow);
        scene.ShowCalibrationTarget(0, pieceTop: false);
        scene.SetHandSpotlights([left], DateTimeOffset.UtcNow);
        Require(scene.ActiveHandSpotlightCount == 0, "A spotlight remained enabled during calibration.");
        scene.ShowCalibrationTarget(-1, pieceTop: false);
        scene.SetHandSpotlights([left], DateTimeOffset.UtcNow);
        Require(scene.ActiveHandSpotlightCount == 1, "Lighting did not resume from a fresh post-calibration hand.");
        scene.SetBlackOutput(true);
        Require(scene.ActiveHandSpotlightCount == 0 && BlackAt(Draw(), new(.3, .6)),
            "Black output retained or rendered a hand spotlight.");
        scene.SetBlackOutput(false);
        Require(scene.ActiveHandSpotlightCount == 0, "Disabling black output restored a discarded spotlight.");

        // Render confirmed pinch input into every kind of projector scene. Only
        // the gesture tester may add red pixels; menu navigation still operates.
        var probeTip = BoardPoint(.5, .65); // Clear of all menu and app controls.
        void CheckPinchRendering(bool showCircle)
        {
            Draw(); // Warm each screen before sending a time-limited observation.
            var frameTime = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(probeTip, frameTime.AddSeconds(1), ++gestureEvent)], frameTime);
            var rendered = Draw();
            var redPixels = 0;
            var ringPixels = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int index = (y * size + x) * 4;
                if (rendered[index + 2] < 160 || rendered[index + 1] > 80 || rendered[index] > 80) continue;
                redPixels++;
                if (Math.Abs(x - probeTip.X * size) < 25 && Math.Abs(y - probeTip.Y * size) < 25)
                    ringPixels++;
            }
            Require(showCircle ? ringPixels > 20 : redPixels == 0,
                showCircle ? "The Hand-Tracking projector lost its red pinch circle."
                    : $"A red pinch marker appeared on {scene.CurrentBoardScreen}.");
        }

        scene.ShowBoardMenu();
        CheckPinchRendering(showCircle: false);
        var navigateTime = DateTimeOffset.UtcNow;
        scene.SetHandCursors([new(BoardPoint(.25, .53), navigateTime.AddSeconds(1), ++gestureEvent)], navigateTime);
        Require(scene.CurrentBoardScreen == BoardScreen.Blackjack,
            "Suppressing pinch markers prevented normal board navigation.");
        CheckPinchRendering(showCircle: false);
        scene.ShowPhotoCopy();
        CheckPinchRendering(showCircle: false);
        scene.SetBackground(null);
        CheckPinchRendering(showCircle: false);
        scene.ShowHandTrackingTest();
        CheckPinchRendering(showCircle: true);

        return new { passed = true, opaqueFingerCoverage = true, boardClipping = true, twoHands = true,
            sourceFrameOrdering = true, lightingDoesNotGenerateInput = true, resetRejectsOldFrames = true,
            perHandDropoutHold = true, suppressedOtherHandDoesNotCancelHold = true,
            recoveredTrackingIdDoesNotDuplicateLight = true, independentLightExpiry = true,
            knownSuppressedHandCannotClaimOtherHold = true,
            sourceLifetimeMilliseconds = 700, photoCopyIndependentLights = true, photoCopyHandCoversBottomControls = true,
            photoCopyHandBoardClipping = true,
            photoCopyObjectLockLifecycle = true, rotatedRectangularObjectLight = true,
            shearedRectangularObjectLight = true, rectangularObjectLightBoundaryCoverage = true,
            calibrationSuppressed = true, blackOutputClears = true,
            pinchCircleOnlyOnHandTracking = true, hiddenPinchStillNavigates = true };

        PixelPoint BoardPoint(double u, double v) => new(
            .1 + .8 * (inset / 2 + u * (1 - inset)), .1 + .8 * (inset / 2 + v * (1 - inset)));

        static HandDetection Hand(double x, double y)
        {
            PixelPoint[] local =
            [
                new(0, .09), new(-.03, .06), new(-.055, .035), new(-.07, .015), new(-.09, 0),
                new(-.035, .015), new(-.04, -.02), new(-.045, -.05), new(-.05, -.08),
                new(0, 0), new(0, -.04), new(0, -.07), new(0, -.1),
                new(.03, .015), new(.035, -.02), new(.04, -.045), new(.045, -.07),
                new(.055, .03), new(.065, .005), new(.075, -.01), new(.08, -.025)
            ];
            return new(local.Select(point => new PixelPoint(x + point.X, y + point.Y)).ToArray(), .95, .5);
        }

        static int PixelIndex(PixelPoint point) =>
            ((int)Math.Round(point.Y * size) * size + (int)Math.Round(point.X * size)) * 4;
        static bool WhiteAt(byte[] pixels, PixelPoint point)
        {
            int index = PixelIndex(point);
            return pixels[index] >= 250 && pixels[index + 1] >= 250 && pixels[index + 2] >= 250 && pixels[index + 3] == 255;
        }
        static bool BlackAt(byte[] pixels, PixelPoint point)
        {
            int index = PixelIndex(point);
            return pixels[index] == 0 && pixels[index + 1] == 0 && pixels[index + 2] == 0;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
