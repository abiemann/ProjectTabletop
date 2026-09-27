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
        scene.SetHandSpotlights([left, right], DateTimeOffset.UtcNow);
        var both = Draw();
        Require(scene.ActiveHandSpotlightCount == 2 && WhiteAt(both, new(.3, .6)) && WhiteAt(both, new(.7, .6)),
            "Two detected hands did not render independent white spotlights.");

        // The hand's circle crosses the left board edge; clipped output must
        // still be black even where that circle would otherwise illuminate it.
        scene.SetHandSpotlights([Hand(.13, .6)], DateTimeOffset.UtcNow);
        var clipped = Draw();
        Require(WhiteAt(clipped, new(.16, .6)) && BlackAt(clipped, new(.08, .6)),
            "A spotlight failed to reach the board edge or spilled outside the board clip.");

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

        scene.SetHandSpotlights([left], DateTimeOffset.UtcNow);
        scene.ShowPhotoCopy();
        Draw();
        await Task.Delay(1100);
        Require(scene.TryGetPhotoCopyCaptureContext(out var photoContext), "The grey capture field never became ready.");
        const int fixtureSize = 1000;
        var fixture = new byte[fixtureSize * fixtureSize * 4];
        for (int y = 0; y < fixtureSize; y++)
        for (int x = 0; x < fixtureSize; x++)
        {
            int index = (y * fixtureSize + x) * 4;
            double localX = (x - 270) * Math.Cos(.5) + (y - 600) * Math.Sin(.5);
            double localY = -(x - 270) * Math.Sin(.5) + (y - 600) * Math.Cos(.5);
            byte value = Math.Abs(localX) < 50 && Math.Abs(localY) < 70 ? (byte)30 : (byte)100;
            fixture[index] = fixture[index + 1] = fixture[index + 2] = value;
            fixture[index + 3] = 255;
        }
        var photoObject = PhotoObjectLocator.Locate(fixtureSize, fixtureSize, fixtureSize * 4, fixture,
            [.001, 0, 0, 0, .001, 0, 0, 0, 1], out var locateFailure);
        Require(photoObject is not null, "The spotlight object fixture failed: " + locateFailure);
        Require(photoObject!.Spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
            "A rectangular object did not acquire a fitted rectangular light.");
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
        foreach (int signX in new[] { -1, 1 })
        foreach (int signY in new[] { -1, 1 })
        {
            double x = signX * (fitted.Width / 2 - 12), y = signY * (fitted.Height / 2 - 12);
            var corner = BoardPoint((fitted.Center.X + x * Math.Cos(fitted.RotationRadians) - y * Math.Sin(fitted.RotationRadians)) / 1000,
                (fitted.Center.Y + x * Math.Sin(fitted.RotationRadians) + y * Math.Cos(fitted.RotationRadians)) / 1000);
            Require(WhiteAt(twoLights, corner), "The rectangular light did not rotate with the object's corners.");
        }
        Require(!WhiteAt(twoLights, BoardPoint(.5, .9)), "Photo Copy flooded the grey field away from either subject.");
        scene.ClearHandTips(resetInput: false);
        scene.SetHandSpotlights([Hand(.7, .25)], DateTimeOffset.UtcNow);
        var clippedControls = Draw();
        Require(!WhiteAt(clippedControls, BoardPoint(.75, .15)), "The hand spotlight covered Photo Copy controls.");
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
        long gestureEvent = 0;
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
            sourceLifetimeMilliseconds = 700, photoCopyIndependentLights = true, photoCopyControlsProtected = true,
            photoCopyObjectLockLifecycle = true, rotatedRectangularObjectLight = true,
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
