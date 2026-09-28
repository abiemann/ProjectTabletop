#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // This renderer/camera fixture owns its scene and buffers. It cannot start
    // the phone camera, change the projector, or navigate the user's live board.
    private async Task<object> VerifyGlobeAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int width = 3840, height = 2160;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "GlobeVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var tested = new List<string>();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var scene = new SceneCompositor(blackjackClock: () => now, globeClock: () => now);
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f),
                new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowGlobe();
        await scene.EnsureGlobeResourcesAsync(target.Device);
        var texture = scene.GetGlobeRenderDiagnostics(width / (double)height, scene.GlobeState);
        Require(texture is { Ready: true, SurfaceWidth: 8192, SurfaceHeight: 4096,
            CloudWidth: 2048, CloudHeight: 1024, Error: null },
            "Globe did not load its full-resolution Earth surface and separate cloud textures.");

        var distant = scene.GlobeState;
        Require(distant.IntroProgress == 0 && distant.Zoom < GlobeState.DefaultZoom * .05,
            "Globe did not begin with the Earth far away.");
        byte[] farPixels = await Capture("entrance-far-away");
        now += TimeSpan.FromSeconds(.65);
        var approaching = scene.GlobeState;
        byte[] approachingPixels = await Capture("entrance-approaching");
        Require(approaching.IntroProgress is > 0 and < 1 && approaching.Zoom > distant.Zoom * 10 &&
            ChangedPixels(farPixels, approachingPixels) > 100000,
            "The Earth did not visibly approach during its entrance.");
        // Measure the limb during approach while it is wholly visible and
        // clear of the controls. The larger default fills the short board axis.
        var circle = CheckCircularLimb(approachingPixels, width, height, CameraPoint(.5, .5),
            335 * approaching.Zoom / 1000 * height * .93 * (1 - inset));
        now += TimeSpan.FromSeconds(2.45);
        byte[] arrivedPixels = await Capture("earth-native-4k");
        var arrived = scene.GlobeState;
        Require(arrived.IntroProgress == 1 && Math.Abs(arrived.Zoom - GlobeState.DefaultZoom) < .000001,
            "The Earth entrance did not finish at its initial viewing scale.");
        var initialButtons = scene.CurrentBoardButtons.ToArray();
        string[] captions = ["Exit", "Zoom -", "Zoom +", "< Rotate", "Rotate >"];
        Require(initialButtons.Count() == 5 && initialButtons.Select(button => button.Label).SequenceEqual(captions) &&
            initialButtons.All(button => button.Enabled), "Globe's five requested captions are missing or disabled.");

        now += TimeSpan.FromSeconds(20);
        byte[] spunPixels = await Capture("earth-slow-spin");
        var spun = scene.GlobeState;
        Require(Math.Abs(AngleDelta(arrived.RotationDegrees, spun.RotationDegrees) - 20) < .00001 &&
            spun.Revision == arrived.Revision && ChangedPixels(arrivedPixels, spunPixels) > 10000,
            "The Earth did not slowly spin independently of interaction revisions.");
        await CheckAcquisition();

        var beforeZoom = scene.GlobeState;
        Act("globe-zoom-in"); now += GlobeState.ControlTransitionDuration;
        var zoomed = scene.GlobeState;
        byte[] zoomPixels = await Capture("earth-zoom-in");
        Require(zoomed.Zoom > beforeZoom.Zoom * 1.2 && ChangedPixels(spunPixels, zoomPixels) > 100000,
            "Zoom + did not smoothly enlarge the Earth.");
        Act("globe-zoom-out"); now += GlobeState.ControlTransitionDuration;
        Require(Math.Abs(scene.GlobeState.Zoom - beforeZoom.Zoom) < .000001,
            "Zoom - did not restore the previous scale.");
        var beforeLeft = scene.GlobeState;
        Act("globe-rotate-left"); now += GlobeState.ControlTransitionDuration;
        var turnedLeft = scene.GlobeState;
        await Capture("earth-rotate-left");
        Require(Math.Abs(AngleDelta(beforeLeft.RotationDegrees, turnedLeft.RotationDegrees) -
                (-GlobeState.RotationStepDegrees + GlobeState.ControlTransitionDuration.TotalSeconds)) < .00001,
            "< Rotate moved the globe in the wrong direction.");
        Act("globe-rotate-right"); now += GlobeState.ControlTransitionDuration;
        Require(Math.Abs(AngleDelta(turnedLeft.RotationDegrees, scene.GlobeState.RotationDegrees) -
                (GlobeState.RotationStepDegrees + GlobeState.ControlTransitionDuration.TotalSeconds)) < .00001,
            "Rotate > did not reverse the left rotation.");
        var portraitCircle = await CheckPortrait();

        var exit = scene.CurrentBoardButtons.Single(button => button.Id == "globe-exit");
        Require(scene.ActivateGlobeAt(exit.Bounds.X + exit.Bounds.Width / 2, exit.Bounds.Y + exit.Bounds.Height / 2) &&
            scene.CurrentBoardScreen == BoardScreen.Menu && scene.CurrentBoardButtons.Any(button => button.Id == "globe") &&
            scene.CurrentBoardButtons.All(button => button.Label != "Diablo"),
            "Globe's actual Exit target did not return to the updated main menu.");
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Isolated Globe verification changed the user's camera, projector, calibration or board.");
        return new { passed = true, nativeWidth = width, nativeHeight = height, directory, images,
            distantEntranceAndSlowSpin = true, zoomAndBidirectionalRotation = true, circle, portraitCircle, texture,
            controls = tested, nativeActualCaptionMasks = true, twoFreshFramesAndSevenPercentRequired = true,
            rotatingEarthCannotTriggerButtonLighting = true, offButtonActivityCannotStartSpotlight = true,
            assistanceCannotExecute = true, updatedMenuAndExit = true, liveHardwareUnchanged = true };

        void Act(string id)
        {
            Require(scene.ActivateGlobeButton(id), "The native Globe view rejected " + id + ".");
        }
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        async Task<byte[]> Capture(string name)
        {
            byte[] pixels = Draw();
            Require(pixels.Take(4).SequenceEqual(new byte[] { 0, 0, 0, 255 }),
                "The Globe frame spills outside the physical board clip.");
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            return pixels;
        }
        async Task CheckAcquisition()
        {
            Draw(); scene.GetHandAcquisitionContext(now);
            now += TimeSpan.FromMilliseconds(600);
            var ready = Ready();
            var buttons = scene.CurrentBoardButtons.ToArray();
            Require(ready.RestrictAcquisitionToSearchRegions && ready.ContinuousSearchPolygon is null &&
                ready.StationarySearchCenters?.Length == 5 && ready.ExpectedScene!.BoardSearchRegions?.Count == 5 &&
                ready.ExpectedScene.BoardTriggerRegions?.Count == 5,
                "Globe acquisition did not restrict its five native control/text regions.");
            for (int index = 0; index < buttons.Length; index++)
            {
                var button = buttons[index];
                var bounds = button.Bounds;
                ready = Ready();
                var expected = ready.ExpectedScene!;
                var trigger = expected.BoardTriggerRegions![index];
                var buttonCenter = CameraPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                var crop = ready.StationarySearchCenters![index];
                Require(Math.Abs(crop.X - buttonCenter.X) < .1 && Math.Abs(crop.Y - buttonCenter.Y) < .1 &&
                    trigger.Width > 0 && trigger.Height > 0 && trigger.X >= bounds.X && trigger.Y >= bounds.Y &&
                    trigger.X + trigger.Width <= bounds.X + bounds.Width && trigger.Y + trigger.Height <= bounds.Y + bounds.Height,
                    "The actual " + button.Label + " letters or focused camera center leave their button.");
                byte[] empty = Draw();
                var emptyDetector = new HandAcquisitionPresenceTracker();
                var emptyFirst = emptyDetector.Update(width, height, width * 4, empty, ready.SearchPolygon, expected, now, now);
                now += TimeSpan.FromMilliseconds(125);
                var emptySecond = emptyDetector.Update(width, height, width * 4, Draw(), ready.SearchPolygon, expected, now, now);
                Require(emptyFirst.BaselineReady && emptyFirst.Hints.Count == 0 && emptySecond.Hints.Count == 0,
                    "The animated empty Globe generated hand evidence over " + button.Label + ".");

                byte[] outside = Draw();
                var offButton = CameraPoint(.5, .43);
                Fill(outside, (int)offButton.X - 120, (int)offButton.Y - 120, 240, 240, 15, 20, 225);
                var outsideDetector = new HandAcquisitionPresenceTracker();
                outsideDetector.Update(width, height, width * 4, outside, ready.SearchPolygon, expected, now, now);
                now += TimeSpan.FromMilliseconds(33);
                var outsideSecond = outsideDetector.Update(width, height, width * 4, outside, ready.SearchPolygon, expected, now, now);
                Require(outsideSecond.Hints.Count == 0, "Activity on the Earth bypassed Globe's button-only spotlight policy.");

                byte[] occupied = Draw();
                var topLeft = CameraPoint(bounds.X + .016, bounds.Y + .013);
                var bottomRight = CameraPoint(bounds.X + bounds.Width - .016, bounds.Y + bounds.Height - .013);
                int span = Math.Max(24, (int)(bottomRight.X - topLeft.X));
                for (int finger = 0; finger < 4; finger++)
                    Fill(occupied, (int)topLeft.X + finger * span / 4, (int)topLeft.Y,
                        Math.Max(5, span / 4 - 3), Math.Max(8, (int)(bottomRight.Y - topLeft.Y)),
                        (byte)(15 + finger * 3), (byte)(20 + finger * 5), (byte)(225 - finger * 7));
                var detector = new HandAcquisitionPresenceTracker();
                var first = detector.Update(width, height, width * 4, occupied, ready.SearchPolygon, expected, now, now);
                Require(first.Hints.Count == 0 && first.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                    { ShapeCorrupted: true, ConfirmationFrames: 1 },
                    "The first " + button.Label + " obstruction missed its letters or bypassed two-frame confirmation. " +
                    System.Text.Json.JsonSerializer.Serialize(first));
                scene.CompleteHandAcquisition(ready, first.Hints, [], now);
                Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                    "One damaged Globe caption frame started a spotlight.");
                now += TimeSpan.FromMilliseconds(33);
                var second = detector.Update(width, height, width * 4, occupied, ready.SearchPolygon, expected, now, now);
                Require(second.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                    { ShapeCorrupted: true, ConfirmationFrames: 2 } &&
                    second.Hints.Any(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07),
                    "Two stationary obstructions failed to measure 7% of " + button.Label + " and its letters. " +
                    System.Text.Json.JsonSerializer.Serialize(second));
                var measured = second.Hints.First(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07);
                foreach (var subthreshold in new[] { measured with { ControlCoverage = .069999 },
                    measured with { ControlTriggerCoverage = .069999 } })
                {
                    scene.CompleteHandAcquisition(ready, [subthreshold], [], now);
                    Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                        "Sub-7% Globe text/control coverage started a spotlight.");
                }
                long revision = scene.GlobeState.Revision;
                scene.CompleteHandAcquisition(ready, [measured with { ControlCoverage = .07, ControlTriggerCoverage = .07 }], [], now);
                var lit = scene.GetHandAcquisitionContext(now)!;
                Require(lit.IlluminatedHint is not null && scene.GlobeState.Revision == revision &&
                    scene.HoveredBoardButtons.Count == 0 && scene.ActiveHandSpotlightCount == 0 &&
                    ReferenceEquals(expected, lit.ExpectedScene),
                    "Globe's acquisition light executed a control or replaced its static unlit reference.");
                Require(WhiteNear(Draw(), lit.IlluminatedHint!.Center) > WhiteNear(empty, lit.IlluminatedHint.Center) + 200,
                    "The native Globe search light did not shine over its obstructed caption.");
                scene.CompleteHandAcquisition(lit, second.Hints, [], now, illuminatedPresence: false);
                now += TimeSpan.FromSeconds(1);
                tested.Add(button.Label);
                await Task.Yield();
            }
        }
        SceneCompositor.HandAcquisitionContext Ready()
        {
            Draw();
            var context = scene.GetHandAcquisitionContext(now);
            Require(context is { ObserveMotion: true, ExpectedScene: not null }, "Globe's static controls never finish acquisition settling.");
            return context!;
        }
        async Task<object> CheckPortrait()
        {
            const int portraitWidth = 2160, portraitHeight = 3840;
            var portraitNow = now;
            using var portrait = new SceneCompositor(blackjackClock: () => portraitNow, globeClock: () => portraitNow);
            using var portraitTarget = new CanvasRenderTarget(target.Device, portraitWidth, portraitHeight, 96);
            portrait.SetDisplayAspect(portraitWidth / (double)portraitHeight);
            portrait.SetBoardSetup(true);
            double portraitInset = portrait.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f),
                    new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(portraitWidth, 0), new(portraitWidth, portraitHeight), new(0, portraitHeight)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            portrait.SetBoardSetup(false);
            portrait.ShowGlobe();
            await portrait.EnsureGlobeResourcesAsync(target.Device);
            portraitNow += TimeSpan.FromSeconds(.65);
            using (var drawing = portraitTarget.CreateDrawingSession())
                portrait.Draw(drawing, portraitWidth, portraitHeight, preview: false, runningSlowly: false);
            byte[] pixels = portraitTarget.GetPixelBytes();
            Require(pixels.Take(4).SequenceEqual(new byte[] { 0, 0, 0, 255 }),
                "Portrait Globe drawing escaped the physical board clip.");
            var center = new PixelPoint(portraitWidth * (.035 + .93 * (portraitInset / 2 + .5 * (1 - portraitInset))),
                portraitHeight * (.035 + .93 * (portraitInset / 2 + .5 * (1 - portraitInset))));
            object limb = CheckCircularLimb(pixels, portraitWidth, portraitHeight, center,
                .335 * portrait.GlobeState.Zoom * portraitWidth * .93 * (1 - portraitInset));
            portraitNow += TimeSpan.FromSeconds(2.45);
            using (var drawing = portraitTarget.CreateDrawingSession())
                portrait.Draw(drawing, portraitWidth, portraitHeight, preview: false, runningSlowly: false);
            string path = Path.Combine(directory, "earth-portrait-native-4k.png");
            await portraitTarget.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            return new { nativeWidth = portraitWidth, nativeHeight = portraitHeight, limb };
        }
        static object CheckCircularLimb(byte[] pixels, int physicalWidth, int physicalHeight,
            PixelPoint center, double expectedRadius)
        {
            // Native atmosphere pixels supply an independent circular-limb
            // measurement, beyond checking the renderer's scale formula.
            int halfSearch = (int)(expectedRadius * 1.12);
            int left = FindLimb(true, -1), right = FindLimb(true, 1);
            int top = FindLimb(false, -1), bottom = FindLimb(false, 1);
            double diameterX = right - left, diameterY = bottom - top;
            Require(diameterX > expectedRadius * 1.7 && diameterY > expectedRadius * 1.7 &&
                Math.Abs(diameterX / diameterY - 1) < .045,
                $"Earth is stretched in the native projector pixels: horizontal {diameterX}, vertical {diameterY}, expected radius {expectedRadius:F2}.");
            return new { diameterX, diameterY, aspectError = Math.Abs(diameterX / diameterY - 1) };

            int FindLimb(bool horizontal, int direction)
            {
                for (int distance = halfSearch; distance >= expectedRadius * .78; distance--)
                {
                    int blue = 0;
                    for (int cross = -4; cross <= 4; cross++)
                    {
                        int x = (int)center.X + (horizontal ? direction * distance : cross);
                        int y = (int)center.Y + (horizontal ? cross : direction * distance);
                        if (x < 0 || y < 0 || x >= physicalWidth || y >= physicalHeight) continue;
                        int offset = (y * physicalWidth + x) * 4;
                        if (pixels[offset] > 18 && pixels[offset + 1] > 10 && pixels[offset] > pixels[offset + 2] * 1.25) blue++;
                    }
                    // A point star cannot pass this contiguous limb support.
                    if (blue >= 5) return (int)(horizontal ? center.X : center.Y) + direction * distance;
                }
                throw new InvalidOperationException("A native Earth atmospheric limb is missing.");
            }
        }
        PixelPoint CameraPoint(double u, double v) => new(width * (.035 + .93 * (inset / 2 + u * (1 - inset))),
            height * (.035 + .93 * (inset / 2 + v * (1 - inset))));
        static int ChangedPixels(byte[] first, byte[] second)
        {
            int changed = 0;
            for (int offset = 0; offset < first.Length; offset += 4)
                if (Math.Max(Math.Abs(first[offset] - second[offset]), Math.Max(Math.Abs(first[offset + 1] - second[offset + 1]),
                    Math.Abs(first[offset + 2] - second[offset + 2]))) > 16) changed++;
            return changed;
        }
        static double AngleDelta(double first, double second) => (second - first + 540) % 360 - 180;
        static void Fill(byte[] pixels, int left, int top, int rectangleWidth, int rectangleHeight, byte b, byte g, byte r)
        {
            for (int y = Math.Max(0, top); y < Math.Min(height, top + rectangleHeight); y++)
            for (int x = Math.Max(0, left); x < Math.Min(width, left + rectangleWidth); x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255;
            }
        }
        static int WhiteNear(byte[] pixels, PixelPoint center)
        {
            int count = 0;
            for (int y = Math.Max(0, (int)center.Y - 15); y <= Math.Min(height - 1, (int)center.Y + 15); y++)
            for (int x = Math.Max(0, (int)center.X - 15); x <= Math.Min(width - 1, (int)center.X + 15); x++)
            {
                int offset = (y * width + x) * 4;
                if (pixels[offset] > 245 && pixels[offset + 1] > 245 && pixels[offset + 2] > 245) count++;
            }
            return count;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
