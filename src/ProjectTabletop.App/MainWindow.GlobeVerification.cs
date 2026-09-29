#if DEBUG
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
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
        CheckClosedDrawer();
        var landscapeHandle = CheckHandleClearance(arrivedPixels, width, height, CameraPoint,
            scene.CurrentBoardButtons.Single().Bounds, CameraPoint(.5, .5),
            .335 * arrived.Zoom * height * .93 * (1 - inset));
        await Capture("controls-drawer-closed");

        now += TimeSpan.FromSeconds(20);
        byte[] spunPixels = await Capture("earth-slow-spin");
        var spun = scene.GlobeState;
        Require(Math.Abs(AngleDelta(arrived.RotationDegrees, spun.RotationDegrees) - 20) < .00001 &&
            spun.Revision == arrived.Revision && ChangedPixels(arrivedPixels, spunPixels) > 10000,
            "The Earth did not slowly spin independently of interaction revisions.");
        await CheckAcquisition();
        await CheckNativeCameraChevron();

        var beforeDrawer = scene.GlobeState;
        byte[] closedBeforeOpening = Draw();
        Act("globe-drawer-open");
        var openedAt = now;
        byte[] drawerStartPixels = await Capture("drawer-start");
        CheckDrawerProgress(0);
        Require(ChangedOutsideHandle(closedBeforeOpening, drawerStartPixels) == 0,
            "Globe's bottom drawer appeared on the board before rising from below the viewport.");
        Require(scene.CurrentBoardButtons.Single(button => button.Id == "globe-drawer-close").Enabled &&
                scene.CurrentBoardButtons.Where(button => button.Id != "globe-drawer-close").All(button => !button.Enabled) &&
                scene.GlobeState.Revision == beforeDrawer.Revision,
            "Opening Globe's drawer enabled moving controls or changed the Earth.");
        now = openedAt.AddMilliseconds(150);
        byte[] drawerHalfPixels = await Capture("drawer-halfway");
        CheckDrawerProgress(.5);
        Require(ChangedBottomOutsideEarth(drawerStartPixels, drawerHalfPixels) > 5000,
            "The half-open drawer did not visibly emerge from the bottom of the board.");
        Require(scene.GetHandAcquisitionContext(now) is
                { ObserveMotion: false, IlluminatedHint: null, ExpectedScene: null } &&
                scene.ActiveHandSpotlightCount == 0 &&
                !scene.ActivateGlobeButton("globe-exit") && !scene.ActivateGlobeButton("globe-zoom-in"),
            "Globe's moving drawer supplied an acquisition reference, light or active action.");
        now = openedAt.AddMilliseconds(299);
        Require(!scene.ActivateGlobeButton("globe-zoom-out") && !scene.ActivateGlobeButton("globe-exit"),
            "Globe's drawer actions enabled before the 300 ms opening finished.");
        now = openedAt.AddMilliseconds(300);
        scene.TickGlobe(now);
        byte[] drawerOpenPixels = await Capture("controls-drawer-open");
        CheckDrawerProgress(1);
        CheckOpenDrawer();
        // The opaque glass replaces the Earth and sky that were visible
        // before the drawer rose, apart from pixels coincidentally alike.
        foreach (var button in scene.CurrentBoardButtons.Where(button => button.Id != "globe-drawer-close"))
            Require(ChangedFraction(drawerStartPixels, drawerOpenPixels, button.Bounds) > .75,
                "Globe's settled bottom row did not visibly render the " + button.Label + " glass button.");
        await CheckAcquisition();
        CheckHoldButtonLightClip();

        var beforeZoom = scene.GlobeState;
        Act("globe-zoom-in"); now += GlobeState.ControlTransitionDuration;
        var zoomed = scene.GlobeState;
        byte[] zoomPixels = await Capture("earth-zoom-in");
        Require(zoomed.Zoom > beforeZoom.Zoom * 1.2 && ChangedPixels(spunPixels, zoomPixels) > 100000,
            "Zoom + did not smoothly enlarge the Earth.");
        Act("globe-zoom-out"); now += GlobeState.ControlTransitionDuration;
        Require(Math.Abs(scene.GlobeState.Zoom - beforeZoom.Zoom) < .000001,
            "Zoom - did not restore the previous scale.");
        var beforeRemovedActions = scene.GlobeState;
        Require(!scene.ActivateGlobeButton("globe-rotate-left") &&
                !scene.ActivateGlobeButton("globe-rotate-right") &&
                scene.GlobeState == beforeRemovedActions,
            "A removed manual Rotate target still changes the Globe.");
        // A square board's 1.5× limb reaches the tall handle's upper-right
        // corner; the measured 72 × 56 cm board and 16:9 extremes stay clear.
        var squareCircle = await CheckAlternateAspect(2160, 2160, "square", requireHandleClearance: false);
        var measuredBoardCircle = await CheckAlternateAspect(2777, 2160, "measured-board", requireHandleClearance: true);
        var portraitCircle = await CheckAlternateAspect(2160, 3840, "portrait", requireHandleClearance: true);

        Act("globe-drawer-close");
        CheckClosedDrawer();
        await Capture("controls-drawer-reclosed");
        Require(!scene.ActivateGlobeButton("globe-exit") &&
                !scene.ActivateGlobeButton("globe-zoom-in") &&
                !scene.ActivateGlobeButton("globe-zoom-out"),
            "A hidden Globe drawer control remained active after closing.");
        Act("globe-drawer-open");
        now += TimeSpan.FromMilliseconds(300);
        scene.TickGlobe(now);
        Draw();
        CheckOpenDrawer();

        var exit = scene.CurrentBoardButtons.Single(button => button.Id == "globe-exit");
        Require(scene.ActivateGlobeAt(exit.Bounds.X + exit.Bounds.Width / 2, exit.Bounds.Y + exit.Bounds.Height / 2) &&
            scene.CurrentBoardScreen == BoardScreen.Menu && scene.CurrentBoardButtons.Any(button => button.Id == "globe") &&
            scene.CurrentBoardButtons.All(button => button.Label != "Diablo"),
            "Globe's actual Exit target did not return to the updated main menu.");
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Isolated Globe verification changed the user's camera, projector, calibration or board.");
        return new { passed = true, nativeWidth = width, nativeHeight = height, directory, images,
            distantEntranceAndSlowSpin = true, zoomControlsAndRemovedManualRotation = true,
            circle, squareCircle, measuredBoardCircle, portraitCircle, texture, landscapeHandle,
            compactClosedDrawerAndOpenActions = true, drawerDurationMilliseconds = 300,
            bottomDrawerEmergesBelowViewport = true, singleRenderedBottomActionRow = true,
            defaultHandleClearOfEarthOnRectangularBoards = true,
            movingDrawerBlocksActionsAndAcquisition = true, hiddenDrawerActionsRejected = true,
            controls = tested, nativeActualCaptionMasks = true, twoFreshFramesAndSevenPercentRequired = true,
            vectorChevronTemplates = true, intactChevronRejectsLargePillObstruction = true,
            native1080pChevronAcquisitionWithBroadSkinFingers = true,
            emptyCameraOpticsAndIntactChevronRemainQuiet = true,
            rotatingEarthCannotTriggerButtonLighting = true, offButtonActivityCannotStartSpotlight = true,
            assistanceCannotExecute = true, updatedMenuAndExit = true, liveHardwareUnchanged = true };

        void Act(string id)
        {
            Require(scene.ActivateGlobeButton(id), "The native Globe view rejected " + id + ".");
        }
        void CheckClosedDrawer() => Require(!scene.GlobeDrawerOpen && scene.CurrentBoardButtons.Count == 1 &&
                scene.CurrentBoardButtons.Single() is { Id: "globe-drawer-open", Label: "^", Enabled: true } handle &&
                handle.Bounds == new BoardRect(.01, .87, .18, .12),
            "Globe's closed drawer does not contain only its enabled space-themed up arrow.");
        void CheckOpenDrawer()
        {
            string[] ids = ["globe-drawer-close", "globe-exit", "globe-zoom-out", "globe-zoom-in"];
            string[] captions = ["v", "Exit", "Zoom -", "Zoom +"];
            Require(scene.GlobeDrawerOpen && scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(ids) &&
                    scene.CurrentBoardButtons.Select(button => button.Label).SequenceEqual(captions) &&
                    scene.CurrentBoardButtons.All(button => button.Enabled) &&
                    scene.CurrentBoardButtons.Single(button => button.Id == "globe-drawer-close").Bounds == new BoardRect(.01, .87, .18, .12) &&
                    scene.CurrentBoardButtons.Single(button => button.Id == "globe-exit").Bounds == new BoardRect(.20, .87, .155, .12) &&
                    scene.CurrentBoardButtons.Single(button => button.Id == "globe-zoom-out").Bounds == new BoardRect(.365, .87, .155, .12) &&
                    scene.CurrentBoardButtons.Single(button => button.Id == "globe-zoom-in").Bounds == new BoardRect(.53, .87, .155, .12),
                "Globe's settled drawer lacks its down arrow and the three requested actions.");
        }
        void CheckDrawerProgress(double expected)
        {
            var drawer = scene.GetGlobeDrawerDiagnostics(now);
            Require(drawer.Open && drawer.DurationMilliseconds == 300 &&
                    Math.Abs(drawer.Progress - expected) < .001 && drawer.Animating == (expected < 1),
                "Globe's drawer opening has an inconsistent clock or duration.");
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
                ready.StationarySearchCenters?.Length == buttons.Length &&
                ready.ExpectedScene!.BoardSearchRegions?.Count == buttons.Length &&
                ready.ExpectedScene.BoardTriggerRegions?.Count == buttons.Length,
                "Globe acquisition did not restrict its current native drawer control/glyph regions.");
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
                if (!emptyFirst.BaselineReady || emptyFirst.Hints.Count != 0 || emptySecond.Hints.Count != 0)
                {
                    await Capture("empty-control-failure");
                    File.WriteAllBytes(Path.Combine(directory, "empty-control-native.bgra"), target.GetPixelBytes());
                    File.WriteAllBytes(Path.Combine(directory, "empty-control-expected.bgra"), expected.Bgra);
                    using var reference = CanvasBitmap.CreateFromBytes(target.Device, expected.Bgra,
                        expected.Width, expected.Height, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    await reference.SaveAsync(Path.Combine(directory, "empty-control-expected.png"), CanvasBitmapFileFormat.Png);
                    File.WriteAllText(Path.Combine(directory, "empty-control-failure.json"),
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            width, height, stride = width * 4, now, button, ready.SearchPolygon,
                            expected = new { expected.Width, expected.Height, expected.CameraToBoard,
                                expected.BoardSearchRegions, expected.BoardReferenceRegions, expected.BoardTriggerRegions },
                            emptyFirst, emptySecond
                        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    string problem = !emptyFirst.BaselineReady || !emptySecond.BaselineReady
                        ? "The empty Globe control reference was rejected for "
                        : "The animated empty Globe generated hand evidence over ";
                    throw new InvalidOperationException(problem + button.Label +
                        ". Diagnostic: " + directory + ". " + System.Text.Json.JsonSerializer.Serialize(new { emptyFirst, emptySecond }));
                }

                if (button.Id is "globe-drawer-open" or "globe-drawer-close")
                    CheckIntactChevron(button, index, trigger, empty, ready);

                byte[] outside = Draw();
                var offButton = CameraPoint(.5, .43);
                Fill(outside, (int)offButton.X - 120, (int)offButton.Y - 120, 240, 240, 15, 20, 225);
                var outsideDetector = new HandAcquisitionPresenceTracker();
                outsideDetector.Update(width, height, width * 4, outside, ready.SearchPolygon, expected, now, now);
                now += TimeSpan.FromMilliseconds(33);
                var outsideSecond = outsideDetector.Update(width, height, width * 4, outside, ready.SearchPolygon, expected, now, now);
                Require(outsideSecond.Hints.Count == 0, "Activity on the Earth bypassed Globe's button-only spotlight policy.");

                byte[] occupied = Draw();
                bool chevron = button.Id is "globe-drawer-open" or "globe-drawer-close";
                // A lone arrow has no independent second control to train camera
                // color from. Obscure its rendered ink and enough surrounding
                // glass to exceed the control-area floor, while leaving most of
                // the pill's gradient untouched for the initial appearance fit.
                double coveredHeight = chevron ? Math.Max(trigger.Height, bounds.Height * .42) : bounds.Height - .026;
                double coveredWidth = chevron ? Math.Min(Math.Max(trigger.Width + .004, bounds.Width * .40),
                    .36 * (bounds.Width - .024) * (bounds.Height - .024) / coveredHeight) : bounds.Width - .032;
                double coveredCenterX = chevron ? trigger.X + trigger.Width / 2 : bounds.X + bounds.Width / 2;
                double coveredCenterY = chevron ? trigger.Y + trigger.Height / 2 : bounds.Y + bounds.Height / 2;
                Require(!chevron || coveredWidth * coveredHeight /
                        ((bounds.Width - .024) * (bounds.Height - .024)) is > .07 and < .40,
                    "The arrow obstruction fixture lacks sufficient control area or independent untouched glass.");
                var topLeft = CameraPoint(coveredCenterX - coveredWidth / 2, coveredCenterY - coveredHeight / 2);
                var bottomRight = CameraPoint(coveredCenterX + coveredWidth / 2, coveredCenterY + coveredHeight / 2);
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
                if (button.HoldToRepeat)
                {
                    Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null && scene.GlobeState.Revision == revision,
                        "Qualified obstruction lit or immediately activated the hold-to-repeat " + button.Label + " button.");
                    CheckHoldToRepeat(button, occupied, empty);
                    // Its zooms changed the Globe; let acquisition settle again.
                    Draw(); scene.GetHandAcquisitionContext(now);
                    now += TimeSpan.FromSeconds(1);
                    tested.Add(button.Label);
                    await Task.Yield();
                    continue;
                }
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
        void CheckHoldToRepeat(BoardButton button, byte[] occupied, byte[] empty)
        {
            // Fingers held over the caption activate once after a second, then
            // each further second, through the actual detector and scene timing.
            var hold = scene.GetHoldButtonContext(now);
            Require(hold is not null && hold.ButtonIds.Contains(button.Id),
                "Globe did not provide a hold context for " + button.Label + ".");
            var tracker = new HandAcquisitionPresenceTracker();
            var started = now;
            double zoomBefore = scene.GlobeState.TargetZoom;
            double step = button.Id == "globe-zoom-in" ? GlobeState.ZoomStep : 1 / GlobeState.ZoomStep;
            var activations = new List<double>();
            for (int frame = 0; frame <= 21; frame++)
            {
                double at = (now - started).TotalMilliseconds;
                if (FeedHold(occupied).Contains(button.Id)) activations.Add(at);
            }
            Require(activations.SequenceEqual([1000d, 2000d]) &&
                    Math.Abs(scene.GlobeState.TargetZoom - zoomBefore * step * step) < 1e-9,
                $"Holding {button.Label} did not activate exactly at one and two seconds: {string.Join(", ", activations)}.");
            for (int frame = 0; frame < 4; frame++)
                Require(FeedHold(empty).Count == 0, "An uncovered hold button activated.");
            for (int frame = 0; frame < 9; frame++)
                Require(FeedHold(occupied).Count == 0, "A hold released for 400 ms kept its earlier timer.");
            Require(Math.Abs(scene.GlobeState.TargetZoom - zoomBefore * step * step) < 1e-9 &&
                    scene.CurrentBoardScreen == BoardScreen.Globe && scene.GlobeDrawerOpen,
                "Hold evidence changed the Globe beyond its two timed activations.");

            IReadOnlyList<string> FeedHold(byte[] pixels)
            {
                var held = hold!.HeldButtons(tracker.Update(width, height, width * 4, pixels,
                    hold.SearchPolygon, hold.ExpectedScene, now, now));
                var activated = scene.ObserveHoldButtons(hold, held, now);
                now += TimeSpan.FromMilliseconds(100);
                return activated;
            }
        }
        void CheckHoldButtonLightClip()
        {
            // A hand spotlight beside a hold button must not paint over it: its
            // projected caption is the camera's only evidence of a press.
            var zoom = scene.CurrentBoardButtons.Single(button => button.Id == "globe-zoom-in");
            var top = CameraPoint(zoom.Bounds.X + zoom.Bounds.Width / 2, zoom.Bounds.Y);
            byte[] before = Draw();
            PixelPoint[] shape =
            [
                new(0, .09), new(-.03, .06), new(-.055, .035), new(-.07, .015), new(-.09, 0),
                new(-.035, .015), new(-.04, -.02), new(-.045, -.05), new(-.05, -.08),
                new(0, 0), new(0, -.04), new(0, -.07), new(0, -.1),
                new(.03, .015), new(.035, -.02), new(.04, -.045), new(.045, -.07),
                new(.055, .03), new(.065, .005), new(.075, -.01), new(.08, -.025)
            ];
            scene.SetHandSpotlights([new HandDetection(shape.Select(point =>
                new PixelPoint(top.X + point.X * 1400, top.Y + point.Y * 1400)).ToArray(), .95, .5)], DateTimeOffset.UtcNow);
            byte[] after = Draw();
            var above = CameraPoint(zoom.Bounds.X + zoom.Bounds.Width / 2, zoom.Bounds.Y - .03);
            Require(scene.ActiveHandSpotlightCount == 1 && ChangedFraction(before, after, zoom.Bounds) < .02 &&
                    WhiteNear(after, above) > WhiteNear(before, above) + 200,
                "A hand spotlight covered a hold-to-repeat button, or vanished beside it.");
        }
        void CheckIntactChevron(BoardButton button, int index, HandTrackingBounds trigger, byte[] clean,
            SceneCompositor.HandAcquisitionContext context)
        {
            // The rasterized chevron must supply its actual ink/halo template;
            // obscuring an unrelated part of its pill must not illuminate it.
            var untouched = new HandAcquisitionPresenceTracker().Update(width, height, width * 4,
                clean, context.SearchPolygon, context.ExpectedScene, now, now);
            Require(untouched.TextPatterns?.SingleOrDefault(pattern => pattern.ControlRegion == index) is
                    { LabelIntact: true, Correlation: > .8 },
                "Globe's space chevron fell back to a rectangular text trigger.");
            var bounds = button.Bounds;
            byte[] away = (byte[])clean.Clone();
            var corner = CameraPoint(bounds.X + .012, bounds.Y + .008);
            var opposite = CameraPoint(Math.Min(bounds.X + bounds.Width * .35, trigger.X - .010),
                bounds.Y + bounds.Height - .008);
            int patchWidth = (int)(opposite.X - corner.X), patchHeight = (int)(opposite.Y - corner.Y);
            var controlStart = CameraPoint(bounds.X, bounds.Y);
            var controlEnd = CameraPoint(bounds.X + bounds.Width, bounds.Y + bounds.Height);
            Require(patchWidth * patchHeight / ((controlEnd.X - controlStart.X) *
                    (controlEnd.Y - controlStart.Y)) > .07,
                "The intact-chevron negative fixture did not cover 7% of its pill.");
            Fill(away, (int)corner.X, (int)corner.Y, patchWidth, patchHeight, 20, 35, 225);
            var detector = new HandAcquisitionPresenceTracker();
            for (int frame = 0; frame < 2; frame++)
            {
                var intact = detector.Update(width, height, width * 4, away,
                    context.SearchPolygon, context.ExpectedScene, now, now);
                Require(intact.Hints.Count == 0 && intact.TextPatterns?.SingleOrDefault(pattern => pattern.ControlRegion == index) is
                        { LabelIntact: true, ShapeCorrupted: false, Correlation: > .8 },
                    "A large obstruction beside Globe's untouched arrow triggered acquisition: " +
                    System.Text.Json.JsonSerializer.Serialize(intact));
                now += TimeSpan.FromMilliseconds(33);
            }
        }
        async Task CheckNativeCameraChevron()
        {
            const int cameraWidth = 1920, cameraHeight = 1080;
            const double boardLeft = 405, boardTop = 100, boardWidth = 1050, boardHeight = 820;
            var cameraNow = now;
            using var cameraScene = new SceneCompositor(blackjackClock: () => cameraNow, globeClock: () => cameraNow);
            using var source = new CanvasRenderTarget(target.Device, width, height, 96);
            using var cameraTarget = new CanvasRenderTarget(target.Device, cameraWidth, cameraHeight, 96);
            cameraScene.SetDisplayAspect(width / (double)height);
            cameraScene.SetBoardSetup(true);
            Point2[] cameraCorners = [new(boardLeft, boardTop), new(boardLeft + boardWidth, boardTop),
                new(boardLeft + boardWidth, boardTop + boardHeight), new(boardLeft, boardTop + boardHeight)];
            Point2[] outputCorners = [new(.035, .035), new(.965, .035), new(.965, .965), new(.035, .965)];
            double cameraInset = cameraScene.SetDetectedBoardGrid(outputCorners.Select(point =>
                new System.Numerics.Vector2((float)point.X, (float)point.Y)).ToArray(),
                Homography.FromFourPoints(cameraCorners, outputCorners));
            cameraScene.SetBoardSetup(false);
            cameraScene.ShowGlobe();
            await cameraScene.EnsureGlobeResourcesAsync(target.Device);
            cameraNow += TimeSpan.FromSeconds(3);

            foreach (bool open in new[] { false, true })
            {
                if (open)
                {
                    Require(cameraScene.ActivateGlobeButton("globe-drawer-open"), "The webcam arrow fixture could not open its drawer.");
                    cameraNow += BoardSession.GlobeDrawerOpeningDuration;
                    cameraScene.TickGlobe(cameraNow);
                }
                RenderSource();
                cameraScene.GetHandAcquisitionContext(cameraNow);
                cameraNow += TimeSpan.FromMilliseconds(600);
                RenderSource();
                var context = cameraScene.GetHandAcquisitionContext(cameraNow)!;
                Require(context is { ObserveMotion: true, ExpectedScene: not null },
                    "The webcam arrow fixture did not acquire a settled generated control reference.");
                var expected = context.ExpectedScene!;
                int index = Array.FindIndex(cameraScene.CurrentBoardButtons.ToArray(), button =>
                    button.Id is "globe-drawer-open" or "globe-drawer-close");
                var button = cameraScene.CurrentBoardButtons[index];
                var trigger = expected.BoardTriggerRegions![index];
                byte[] clean = RenderCamera(blur: .75f);
                byte[] opticalDrift = RenderCamera(blur: 1f, offsetX: .8, offsetY: -.6, gain: .95, background: 4);
                string cameraPrefix = "camera-1080p-" + (open ? "down" : "up");
                await SaveCamera(clean, cameraPrefix + "-empty");
                await File.WriteAllBytesAsync(Path.Combine(directory, cameraPrefix + "-expected.bgra"), expected.Bgra);
                var idle = new HandAcquisitionPresenceTracker();
                foreach (var pixels in new[] { clean, clean, opticalDrift, opticalDrift, clean })
                    Quiet(Feed(idle, pixels), "Empty generated arrow under ordinary camera exposure, blur and registration drift");
                var globallyTinted = (byte[])clean.Clone();
                for (int pixel = 0; pixel < globallyTinted.Length; pixel += 4)
                    TintPixel(globallyTinted, clean, pixel);
                var globalDetector = new HandAcquisitionPresenceTracker();
                for (int frame = 0; frame < 3; frame++)
                    Quiet(Feed(globalDetector, globallyTinted), "Global camera color response with no local finger disturbance");

                byte[] offside = Skin(clean, glyphPreserved: false, awayFromGlyph: true);
                var untouched = new HandAcquisitionPresenceTracker();
                for (int frame = 0; frame < 3; frame++)
                {
                    var result = Feed(untouched, offside);
                    Require(result.Hints.Count == 0 && result.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                        { LabelIntact: true, ShapeCorrupted: false },
                        "Skin beside an untouched native-camera chevron generated acquisition evidence. " + Describe(result));
                }

                foreach (bool glyphPreserved in new[] { false, true })
                {
                    byte[] occupied = Skin(clean, glyphPreserved, awayFromGlyph: false);
                    string occupiedName = cameraPrefix + (glyphPreserved ? "-readable-on-skin" : "-skin-occlusion");
                    // Preserve this wholly synthetic native-camera case before
                    // assertions, so a failed fit can be replayed without live
                    // hardware, user camera images or imprecise glyph substitutes.
                    await SaveCamera(occupied, occupiedName);
                    await File.WriteAllTextAsync(Path.Combine(directory, occupiedName + ".json"),
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            cameraWidth, cameraHeight, cameraStride = cameraWidth * 4,
                            capturedUtc = cameraNow, glyphPreserved, controlRegion = index, button,
                            searchPolygon = context.SearchPolygon,
                            expectedWidth = expected.Width, expectedHeight = expected.Height,
                            expectedCameraToBoard = expected.CameraToBoard,
                            boardSearchRegions = expected.BoardSearchRegions,
                            boardReferenceRegions = expected.BoardReferenceRegions,
                            boardTriggerRegions = expected.BoardTriggerRegions,
                            expected.AllowsLocalForegroundContext,
                            cleanImage = cameraPrefix + "-empty.png", occupiedImage = occupiedName + ".png",
                            expectedBgra = cameraPrefix + "-expected.bgra"
                        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    foreach (bool warm in new[] { false, true })
                    {
                        var detector = new HandAcquisitionPresenceTracker();
                        if (warm)
                            for (int frame = 0; frame < 3; frame++) Quiet(Feed(detector, clean), "Warm empty native-camera arrow");
                        var first = Feed(detector, occupied);
                        Require(first.Hints.Count == 0 && first.TextPatterns?.Single(pattern => pattern.ControlRegion == index).ConfirmationFrames == 1,
                            "Broad native-camera skin fingers missed the arrow evidence or bypassed first-frame confirmation. " + Describe(first));
                        var repeated = detector.Update(cameraWidth, cameraHeight, cameraWidth * 4, occupied,
                            context.SearchPolygon, expected, cameraNow, cameraNow);
                        Require(repeated.Hints.Count == 0,
                            "Repeating one native-camera arrow observation confirmed a second fresh frame.");
                        var second = Feed(detector, occupied);
                        Require(second.Hints.Any(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07) &&
                            second.TextPatterns?.Single(pattern => pattern.ControlRegion == index).ConfirmationFrames == 2,
                            "Two broad native-camera skin observations did not qualify the compact arrow and both 7% floors. " + Describe(second));
                        Require(Feed(detector, occupied).Hints.Count > 0,
                            "Stationary broad fingers over the compact arrow were learned as empty background.");
                        Quiet(Feed(detector, clean), "Native-camera arrow after the fingers leave");
                    }
                }

                byte[] Skin(byte[] pixels, bool glyphPreserved, bool awayFromGlyph)
                {
                    var result = (byte[])pixels.Clone();
                    var bounds = button.Bounds;
                    double firstX = awayFromGlyph ? bounds.X + .01 : bounds.X + bounds.Width * .24;
                    double lastX = awayFromGlyph ? trigger.X - .010 : bounds.X + bounds.Width * .76;
                    double firstY = bounds.Y + bounds.Height * .09, lastY = bounds.Y + bounds.Height * .91;
                    double span = lastX - firstX;
                    Require(span > .01, "The native-camera intact-chevron negative has no independent side area.");
                    for (int y = 0; y < cameraHeight; y++)
                    for (int x = 0; x < cameraWidth; x++)
                    {
                        double u = ((x - boardLeft) / boardWidth - cameraInset / 2) / (1 - cameraInset);
                        double v = ((y - boardTop) / boardHeight - cameraInset / 2) / (1 - cameraInset);
                        if (u < firstX || u > lastX || v < firstY || v > lastY) continue;
                        // Four wide, closely grouped fingers; tiny gaps preserve
                        // their ordinary projected texture instead of test-red strips.
                        double withinFinger = (u - firstX) / span * 4;
                        if (!awayFromGlyph && withinFinger % 1 > .94) continue;
                        int pixel = (y * cameraWidth + x) * 4;
                        int texture = (x * 13 + y * 7) % 7;
                        if (glyphPreserved)
                        {
                            TintPixel(result, pixels, pixel);
                        }
                        else
                        {
                            result[pixel] = (byte)(85 + texture);
                            result[pixel + 1] = (byte)(125 + texture);
                            result[pixel + 2] = (byte)(185 + texture);
                        }
                    }
                    return result;
                }
                HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker detector, byte[] pixels)
                {
                    cameraNow += TimeSpan.FromMilliseconds(100);
                    return detector.Update(cameraWidth, cameraHeight, cameraWidth * 4, pixels,
                        context.SearchPolygon, expected, cameraNow, cameraNow);
                }
            }
            void RenderSource()
            {
                using var drawing = source.CreateDrawingSession();
                cameraScene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            }
            byte[] RenderCamera(float blur, double offsetX = 0, double offsetY = 0, double gain = 1, double background = 0)
            {
                using (var drawing = cameraTarget.CreateDrawingSession())
                using (var optical = new GaussianBlurEffect { Source = source, BlurAmount = (float)(blur * width / boardWidth),
                    BorderMode = EffectBorderMode.Hard })
                {
                    drawing.Clear(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                    drawing.DrawImage(optical, new Windows.Foundation.Rect(boardLeft + offsetX, boardTop + offsetY, boardWidth, boardHeight),
                        new Windows.Foundation.Rect(width * .035, height * .035, width * .93, height * .93),
                        1, CanvasImageInterpolation.HighQualityCubic);
                }
                var pixels = cameraTarget.GetPixelBytes();
                for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                {
                    pixels[pixel] = Clamp((pixels[pixel] * .80 + 12) * gain + background);
                    pixels[pixel + 1] = Clamp((pixels[pixel + 1] * .82 + 10) * gain + background);
                    pixels[pixel + 2] = Clamp((pixels[pixel + 2] * .86 + 9) * gain + background);
                }
                return pixels;
            }
            async Task SaveCamera(byte[] pixels, string name)
            {
                using var bitmap = CanvasBitmap.CreateFromBytes(target.Device, pixels, cameraWidth, cameraHeight,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                string path = Path.Combine(directory, name + ".png");
                await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
            }
            static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
            static void TintPixel(byte[] destination, byte[] original, int pixel)
            {
                destination[pixel] = Clamp(original[pixel] * .52 + 8);
                destination[pixel + 1] = Clamp(original[pixel + 1] * .66 + 10);
                destination[pixel + 2] = Clamp(original[pixel + 2] * .87 + 24);
            }
            static string Describe(HandAcquisitionPresenceResult result) => System.Text.Json.JsonSerializer.Serialize(result);
            static void Quiet(HandAcquisitionPresenceResult result, string description) =>
                Require(result.BaselineReady && result.Hints.Count == 0, description + " generated hand evidence. " + Describe(result));
        }
        SceneCompositor.HandAcquisitionContext Ready()
        {
            Draw();
            var context = scene.GetHandAcquisitionContext(now);
            Require(context is { ObserveMotion: true, ExpectedScene: not null }, "Globe's static controls never finish acquisition settling.");
            return context!;
        }
        async Task<object> CheckAlternateAspect(int portraitWidth, int portraitHeight, string name,
            bool requireHandleClearance)
        {
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
                .335 * portrait.GlobeState.Zoom * Math.Min(portraitWidth, portraitHeight) * .93 * (1 - portraitInset));
            portraitNow += TimeSpan.FromSeconds(2.45);
            using (var drawing = portraitTarget.CreateDrawingSession())
                portrait.Draw(drawing, portraitWidth, portraitHeight, preview: false, runningSlowly: false);
            PixelPoint Map(double u, double v) => new(portraitWidth * (.035 + .93 * (portraitInset / 2 + u * (1 - portraitInset))),
                portraitHeight * (.035 + .93 * (portraitInset / 2 + v * (1 - portraitInset))));
            var handle = CheckHandleClearance(portraitTarget.GetPixelBytes(), portraitWidth, portraitHeight, Map,
                portrait.CurrentBoardButtons.Single().Bounds, Map(.5, .5),
                .335 * portrait.GlobeState.Zoom * Math.Min(portraitWidth, portraitHeight) * .93 * (1 - portraitInset),
                requireHandleClearance);
            string path = Path.Combine(directory, "earth-" + name + "-native.png");
            await portraitTarget.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            return new { nativeWidth = portraitWidth, nativeHeight = portraitHeight, limb, handle };
        }
        int ChangedOutsideHandle(byte[] closed, byte[] opening)
        {
            var handle = scene.CurrentBoardButtons.Single(button => button.Id == "globe-drawer-close").Bounds;
            var topLeft = CameraPoint(handle.X - .004, handle.Y - .004);
            var bottomRight = CameraPoint(handle.X + handle.Width + .004, handle.Y + handle.Height + .004);
            int changed = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x >= topLeft.X && x <= bottomRight.X && y >= topLeft.Y && y <= bottomRight.Y) continue;
                int offset = (y * width + x) * 4;
                if (Math.Max(Math.Abs(closed[offset] - opening[offset]),
                    Math.Max(Math.Abs(closed[offset + 1] - opening[offset + 1]),
                        Math.Abs(closed[offset + 2] - opening[offset + 2]))) > 16) changed++;
            }
            return changed;
        }
        double ChangedFraction(byte[] before, byte[] after, BoardRect bounds)
        {
            var topLeft = CameraPoint(bounds.X + .01, bounds.Y + .01);
            var bottomRight = CameraPoint(bounds.X + bounds.Width - .01, bounds.Y + bounds.Height - .01);
            int changed = 0, total = 0;
            for (int y = (int)Math.Ceiling(topLeft.Y); y < bottomRight.Y; y++)
            for (int x = (int)Math.Ceiling(topLeft.X); x < bottomRight.X; x++)
            {
                total++;
                int offset = (y * width + x) * 4;
                if (Math.Max(Math.Abs(before[offset] - after[offset]),
                    Math.Max(Math.Abs(before[offset + 1] - after[offset + 1]),
                        Math.Abs(before[offset + 2] - after[offset + 2]))) > 16) changed++;
            }
            return total == 0 ? 0 : changed / (double)total;
        }
        int ChangedBottomOutsideEarth(byte[] starting, byte[] opening)
        {
            var first = CameraPoint(.01, .86);
            var last = CameraPoint(.99, .995);
            var center = CameraPoint(.5, .5);
            double radius = .335 * GlobeState.DefaultZoom * height * .93 * (1 - inset) + 4;
            int changed = 0;
            for (int y = (int)Math.Ceiling(first.Y); y < last.Y; y++)
            for (int x = (int)Math.Ceiling(first.X); x < last.X; x++)
            {
                if (Math.Pow(x - center.X, 2) + Math.Pow(y - center.Y, 2) <= radius * radius) continue;
                int offset = (y * width + x) * 4;
                if (Math.Max(Math.Abs(starting[offset] - opening[offset]),
                    Math.Max(Math.Abs(starting[offset + 1] - opening[offset + 1]),
                        Math.Abs(starting[offset + 2] - opening[offset + 2]))) > 16) changed++;
            }
            return changed;
        }
        static object CheckHandleClearance(byte[] pixels, int physicalWidth, int physicalHeight,
            Func<double, double, PixelPoint> map, BoardRect bounds, PixelPoint center, double radius,
            bool requireClearance = true)
        {
            // Measure the actual rendered pale-glass pixels, not just a matching
            // hit rectangle: the handle must be present beyond the limb.
            var glass = MeasureGlass(pixels, physicalWidth, physicalHeight, map, bounds, center);
            Require(glass.GlassPixels > glass.Total * .65 && (!requireClearance || glass.NearestDistance > radius + 2),
                $"Globe's rendered compact handle overlaps its 1.5× Earth on {physicalWidth} × {physicalHeight}: " +
                $"glass {glass.GlassPixels}/{glass.Total}, nearest {glass.NearestDistance:F2}, radius {radius:F2}.");
            return new { glass.GlassPixels, glass.Total, clearancePixels = glass.NearestDistance - radius };
        }
        static (int GlassPixels, int Total, double NearestDistance) MeasureGlass(byte[] pixels,
            int physicalWidth, int physicalHeight, Func<double, double, PixelPoint> map, BoardRect bounds, PixelPoint center)
        {
            var topLeft = map(bounds.X, bounds.Y);
            var bottomRight = map(bounds.X + bounds.Width, bounds.Y + bounds.Height);
            int glass = 0, total = 0;
            double nearestSquared = double.PositiveInfinity;
            for (int y = Math.Max(0, (int)Math.Ceiling(topLeft.Y)); y < Math.Min(physicalHeight, bottomRight.Y); y++)
            for (int x = Math.Max(0, (int)Math.Ceiling(topLeft.X)); x < Math.Min(physicalWidth, bottomRight.X); x++)
            {
                total++;
                int offset = (y * physicalWidth + x) * 4;
                if (pixels[offset] < 155 || pixels[offset + 1] < 150 || pixels[offset + 2] < 145) continue;
                glass++;
                nearestSquared = Math.Min(nearestSquared, Math.Pow(x - center.X, 2) + Math.Pow(y - center.Y, 2));
            }
            return (glass, total, Math.Sqrt(nearestSquared));
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
