#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated compositor and synthetic motion; never touches live camera/output.
    private async Task<object> VerifyControlSwipesAsync()
    {
        const int size = 700;
        var now = MonotonicClock.UtcNow.AddMinutes(1);
        DateTimeOffset? frozenWaterTime = null;
        var device = CanvasDevice.GetSharedDevice();
        using var scene = new SceneCompositor(blackjackClock: () => now, waterClock: () => frozenWaterTime ?? now,
            footballClock: () => now, strykersRunClock: () => now);
        using var target = new CanvasRenderTarget(device, size, size, 96);
        await scene.EnsureMenuPreviewResourcesAsync(device);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.StrykersRun);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.CrownDeed);
        scene.SetDisplayAspect(1);
        Calibrate();
        var session = Field<BoardSession>("_boardSession");
        foreach (double facing in new[] { 0d, 90, 180 })
        {
            scene.SetBoardFacingDegrees(facing);
            scene.ShowSettings();
            var ids = scene.CurrentBoardButtons.Select(button => button.Id).ToArray();
            var before = Cross(hide: true);
            Require(before.Top == .04 && before.Bottom == .20, "Settings did not use its top control row.");
            Require(!session.ButtonsVisible && scene.CurrentBoardButtons.Count == 0,
                $"The {facing}-degree board did not hide its controls.");
            Require(scene.GetControlSwipeContext(now) is null, "A redraw discarded the command cooldown.");
            Cross(hide: false);
            Require(session.ButtonsVisible && scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(ids),
                $"The hidden {facing}-degree board could not restore controls.");
            now += TimeSpan.FromMilliseconds(10);
            Require(!scene.CompleteControlSwipe(before, BoardControlSwipe.Hide, false, now),
                "A pre-hide completion survived an identical hide/show round trip.");
        }

        scene.ShowSettings();
        var stale = Context();
        var captured = now;
        now += TimeSpan.FromMilliseconds(400);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Hide, false, captured),
            "A stale camera result changed controls.");
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Hide, false, now.AddMilliseconds(1)),
            "A future camera result changed controls.");
        Require(!scene.CompleteControlSwipe(stale, null, false, now), "Empty motion changed visibility.");
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Hide, false, now),
            "A repeated source frame changed controls.");
        scene.ClearHandTips();
        now += TimeSpan.FromMilliseconds(10);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Hide, false, now),
            "A camera/reset barrier accepted old motion history.");
        stale = Context();
        scene.SetBlackOutput(true);
        scene.SetBlackOutput(false);
        now += TimeSpan.FromMilliseconds(10);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Hide, false, now),
            "A transient black calibration field retained an old baseline.");
        stale = Context();
        scene.ShowBlackjack();
        scene.ShowSettings();
        now += TimeSpan.FromMilliseconds(10);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Hide, false, now),
            "Leaving and returning to a board retained an old context.");
        stale = Context();
        scene.SetBoardSetup(true);
        Require(scene.GetControlSwipeContext(now) is null &&
                !scene.CompleteControlSwipe(stale, BoardControlSwipe.Hide, false, now),
            "Calibration accepted swipe input.");
        Calibrate();
        now += TimeSpan.FromMilliseconds(10);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Hide, false, now),
            "Recalibration accepted the old mapping.");
        scene.ShowBoardMenu();
        Require(scene.GetControlSwipeContext(now) is null && session.ButtonsVisible,
            "The main menu enabled swipe commands.");
        scene.ShowStrykersRun();
        Require(scene.GetControlSwipeContext(now) is null, "Stryker's Run enabled swipe detection.");

        scene.ShowCrownDeed();
        scene.ClearHandTips();
        var crownContext = Cross(hide: true);
        Require(crownContext.Top == .60 && crownContext.Bottom == .83,
            "Crown & Deed did not use its central action row.");
        Cross(hide: false);

        scene.ShowFootball();
        Cross(hide: true);
        Cross(hide: false);
        Require(session.ActivateButton("football-play", now), "Football PLAY fixture failed.");
        now += TimeSpan.FromMilliseconds(10);
        session.SetFootballInput(0, new(-.4f, 0), now);
        session.SetFootballInput(1, new(.4f, 0), now);
        session.TickFootball(now);
        Require(!session.FootballControlsVisible && scene.GetControlSwipeContext(now) is null,
            "Football's automatically hidden gameplay controls enabled swipes.");

        scene.ShowWaterGarden();
        Draw();
        Require(scene.GetControlSwipeContext(now) is null, "Water Garden intro trained a motion baseline.");
        now += TimeSpan.FromSeconds(4);
        Draw();
        var movementContext = Context();
        var holdContext = scene.GetHoldButtonContext(now);
        Require(holdContext is not null, "Water Garden did not expose its caption context.");
        string handle = scene.CurrentBoardButtons.Single(button => button.IsHold).Id;
        scene.ObserveHoldButtons(holdContext, [], now, [handle]);
        var path = new BoardControlSwipeTracker();
        double[] slow = [.90, .65, .55, .53, .51, .49, .47, .45, .43, .41, .39,
            .37, .35, .33, .31, .29, .27, .25, .23, .21, .19, .17, .15];
        bool hid = false;
        foreach (double u in slow)
        {
            now += TimeSpan.FromMilliseconds(150);
            var command = path.Update([new(7, u, .91)], now, now);
            hid |= scene.CompleteControlSwipe(movementContext, command, path.HasTraversalInProgress, now);
            var currentHold = scene.GetHoldButtonContext(now);
            Require(scene.ObserveHoldButtons(currentHold, [handle], now, []).Count == 0 &&
                    !scene.WaterGardenDrawerOpen,
                "A slow swipe completed a caption hold while crossing it.");
            Draw();
        }
        Require(hid && !session.ButtonsVisible, "The slow motion crossing did not hide controls.");
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "Hidden controls enabled hand-search illumination.");
        Cross(hide: false);
        var restoredHold = scene.GetHoldButtonContext(now);
        Require(restoredHold is not null && restoredHold.Revision != holdContext!.Revision,
            "Restored captions reused their previous evidence.");
        for (int i = 0; i < 12; i++)
        {
            now += TimeSpan.FromMilliseconds(100);
            Require(scene.ObserveHoldButtons(restoredHold, [handle], now, []).Count == 0,
                "The stationary hand left by a swipe activated a restored caption without uncovering it.");
        }
        Require(!scene.WaterGardenDrawerOpen, "The restored arrow opened under a stationary hand.");
        now += TimeSpan.FromMilliseconds(100);
        scene.ObserveHoldButtons(restoredHold, [], now, [handle]);
        bool opened = false;
        for (int i = 0; i < 12; i++)
        {
            now += TimeSpan.FromMilliseconds(100);
            opened |= scene.ObserveHoldButtons(restoredHold, [handle], now, []).Count > 0;
        }
        Require(opened && scene.WaterGardenDrawerOpen, "A freshly uncovered caption could not rearm.");
        Require(scene.GetControlSwipeContext(now) is null, "A sliding drawer trained a motion baseline.");
        now += BoardSession.WaterGardenDrawerOpeningDuration + TimeSpan.FromMilliseconds(50);
        session.TickWaterGarden(now);
        Draw();
        var waterButtons = scene.CurrentBoardButtons.ToArray();
        Require(waterButtons.Length == 4, "The Water Garden fade fixture did not open all drawer controls.");
        frozenWaterTime = now;
        double waterAspect = (double)typeof(SceneCompositor).GetMethod("PaintBoardAspect",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, null)!;
        double waterWidth = Math.Min(size, size * waterAspect), waterHeight = waterWidth / waterAspect;
        VerifyFade("Water Garden", DrawWaterPreview, waterButtons,
            bounds => new Rect((size - waterWidth) / 2 + bounds.X * waterWidth,
                (size - waterHeight) / 2 + bounds.Y * waterHeight,
                bounds.Width * waterWidth, bounds.Height * waterHeight));
        frozenWaterTime = null;
        Require(scene.WaterGardenDrawerOpen, "Fading controls closed the Water Garden drawer.");
        var fadedHold = scene.GetHoldButtonContext(now);
        Require(fadedHold is not null, "Completed Water Garden fade did not restore the caption context.");
        string duckButton = waterButtons.Single(button => button.Id == "water-garden-duck-add").Id;
        long ducksBefore = session.WaterGardenDuckAddRevision;
        for (int i = 0; i < 12; i++)
        {
            now += TimeSpan.FromMilliseconds(100);
            Require(scene.ObserveHoldButtons(fadedHold, [duckButton], now, []).Count == 0,
                "A button restored by a fade accepted coverage without fresh clear lettering.");
        }
        Require(session.WaterGardenDuckAddRevision == ducksBefore,
            "A covered DUCK+ button activated on completion of the fade.");
        now += TimeSpan.FromMilliseconds(100);
        scene.ObserveHoldButtons(fadedHold, [], now, [duckButton]);
        for (int i = 0; i < 12 && session.WaterGardenDuckAddRevision == ducksBefore; i++)
        {
            now += TimeSpan.FromMilliseconds(100);
            scene.ObserveHoldButtons(fadedHold, [duckButton], now, []);
        }
        Require(session.WaterGardenDuckAddRevision > ducksBefore,
            "A fully shown button did not rearm after fresh clear lettering.");
        scene.ShowBlackjack();
        var blackjackContext = Context();
        Require(blackjackContext.Top == .75 && blackjackContext.Bottom == .90,
            "Blackjack did not use its lower action-row band.");
        VerifyFade("Blackjack", DrawPreview, scene.CurrentBoardButtons.ToArray(),
            bounds => new Rect(bounds.X * size, bounds.Y * size, bounds.Width * size, bounds.Height * size));
        return new { passed = true, playerFacingAngles = new[] { 0, 90, 180 },
            disturbanceOnly = true, hiddenBandRetained = true, excludedMenuAndStryker = true,
            excludedFootballPlay = true, cameraNavigationCalibrationAndVisibilityBarriers = true,
            slowSwipeSuppressesHolds = true, restoredCaptionRequiresClear = true,
            introAndDrawerAnimationExcluded = true, simultaneousFadeMilliseconds = BoardSession.ControlFadeDuration.TotalMilliseconds,
            fadeBoards = new[] { "WaterGarden", "Blackjack" }, fadingControlsExcludedFromInput = true,
            worldPixelsUnaffected = true, liveHardwareUntouched = true };

        SceneCompositor.ControlSwipeContext Cross(bool hide)
        {
            var context = Context();
            var camera = Field<Homography>("_boardCameraMap");
            var surface = Field<Homography>("_boardSurfaceMap");
            var band = Homography.FromFourPoints(context.SearchPolygon.Select(point => new Point2(point.X, point.Y)).ToArray(),
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
            var tracker = new BoardControlSwipeTracker();
            bool applied = false;
            foreach (double position in new[] { .88, .66, .45, .12 })
            {
                now += TimeSpan.FromMilliseconds(110);
                double u = hide ? position : 1 - position;
                double v = (context.Top + context.Bottom) / 2;
                var pixel = camera.InverseTransform(surface.Transform(new(u, v)));
                var normalized = band.Transform(pixel);
                Require(Math.Abs(normalized.X - u) < 1e-6 && Math.Abs(normalized.Y - .5) < 1e-6,
                    "The control band did not map the player's facing correctly.");
                var command = tracker.Update([new(1, normalized.X,
                    context.Top + (context.Bottom - context.Top) * normalized.Y)], now, now);
                applied |= scene.CompleteControlSwipe(context, command, tracker.HasTraversalInProgress, now);
                Draw();
            }
            Require(applied, "The fresh cross-board motion was not published.");
            Require(session.ControlVisibilityAnimating(now), "A swipe changed controls without beginning their fade.");
            RequireFadeInputBlocked("Swipe");
            now += BoardSession.ControlFadeDuration;
            session.TickControlVisibility(now);
            Draw();
            Require(!session.ControlVisibilityAnimating(now) && session.GetButtonsOpacity(now) == (hide ? 0 : 1),
                "A swipe fade failed to reach its requested endpoint.");
            return context;
        }

        void VerifyFade(string name, Func<byte[]> capture, BoardButton[] buttons, Func<BoardRect, Rect> map)
        {
            Require(buttons.Length >= 2 && session.ButtonsVisible && !session.ControlVisibilityAnimating(now),
                name + " did not begin with multiple settled controls.");
            byte[] shown = capture();
            Require(session.SetButtonsVisible(false, now), name + " did not start fading out.");
            Require(session.GetButtonsOpacity(now) == 1 && session.GetVisibleButtons(now).Count == buttons.Length,
                name + " removed its visual controls before the hide fade.");
            RequireFadeInputBlocked(name);
            now += BoardSession.ControlFadeDuration / 2;
            byte[] hideMiddle = capture();
            Require(Math.Abs(session.GetButtonsOpacity(now) - .5) < .0001,
                name + " did not use one shared midpoint opacity.");
            RequireFadeInputBlocked(name);
            Require(hideMiddle.SequenceEqual(capture()), name + " changed its fade with no clock advancement.");
            now += BoardSession.ControlFadeDuration / 2;
            session.TickControlVisibility(now);
            byte[] hidden = capture();
            Require(session.GetButtonsOpacity(now) == 0 && session.GetVisibleButtons(now).Count == 0 &&
                    scene.CurrentBoardButtons.Count == 0 && !session.ControlVisibilityAnimating(now),
                name + " retained controls after the hide fade completed.");
            Require(session.SetButtonsVisible(true, now), name + " did not start fading in.");
            Require(session.GetButtonsOpacity(now) == 0 && session.GetVisibleButtons(now).Count == buttons.Length,
                name + " did not restore its visuals at zero opacity.");
            RequireFadeInputBlocked(name);
            now += BoardSession.ControlFadeDuration / 2;
            byte[] showMiddle = capture();
            RequireFadeInputBlocked(name);
            now += BoardSession.ControlFadeDuration / 2;
            session.TickControlVisibility(now);
            byte[] restored = capture();
            Require(session.GetButtonsOpacity(now) == 1 && !session.ControlVisibilityAnimating(now) &&
                    scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(buttons.Select(button => button.Id)),
                name + " failed to restore all controls after its show fade.");
            Require(shown.SequenceEqual(restored), name + " did not return to its original rendered scene.");
            foreach (var button in buttons)
            {
                var rectangle = map(button.Bounds);
                RequireMidpoint(name + " " + button.Id + " hide", shown, hidden, hideMiddle, rectangle);
                RequireMidpoint(name + " " + button.Id + " show", shown, hidden, showMiddle, rectangle);
            }
            // Exclude button shadows/anti-aliasing, but check every other scene pixel.
            var controls = buttons.Select(button => map(new BoardRect(button.Bounds.X - .02, button.Bounds.Y - .02,
                button.Bounds.Width + .04, button.Bounds.Height + .04))).ToArray();
            int changedWorld = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (controls.Any(rectangle => rectangle.Contains(new Point(x, y)))) continue;
                int offset = (y * size + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                    if (shown[offset + channel] != hidden[offset + channel] ||
                        shown[offset + channel] != hideMiddle[offset + channel] ||
                        shown[offset + channel] != showMiddle[offset + channel]) { changedWorld++; break; }
            }
            Require(changedWorld == 0, name + " faded or repainted " + changedWorld + " world pixels outside its controls.");
        }

        void RequireFadeInputBlocked(string name)
        {
            Require(session.ControlVisibilityAnimating(now) && scene.CurrentBoardButtons.Count == 0 &&
                    scene.GetHoldButtonContext(now) is null && scene.GetControlSwipeContext(now) is null,
                name + " accepted control input while its buttons were fading.");
            var acquisition = scene.GetHandAcquisitionContext(now);
            Require(acquisition is { ObserveMotion: false, ExpectedScene: null, IlluminatedHint: null,
                    RestrictAcquisitionToSearchRegions: true } &&
                    (acquisition.StationarySearchCenters?.Length ?? 0) == 0 &&
                    (acquisition.HoldControls?.Count ?? 0) == 0,
                name + " exposed fading captions to acquisition or enabled an unrestricted camera search.");
        }

        void RequireMidpoint(string name, byte[] shown, byte[] hidden, byte[] middle, Rect rectangle)
        {
            double squaredContrast = 0, projectedContrast = 0, error = 0;
            int samples = 0;
            for (int y = Math.Max(0, (int)Math.Ceiling(rectangle.Top)); y < Math.Min(size, rectangle.Bottom); y++)
            for (int x = Math.Max(0, (int)Math.Ceiling(rectangle.Left)); x < Math.Min(size, rectangle.Right); x++)
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = (y * size + x) * 4 + channel, contrast = shown[offset] - hidden[offset];
                if (Math.Abs(contrast) < 20) continue;
                squaredContrast += contrast * contrast;
                projectedContrast += (middle[offset] - hidden[offset]) * contrast;
                error += Math.Abs(middle[offset] - (shown[offset] + hidden[offset]) / 2d);
                samples++;
            }
            Require(samples > 150 && projectedContrast / squaredContrast is > .46 and < .54 && error / samples < 3,
                name + " did not blend simultaneously at half opacity (samples=" + samples +
                ", opacity=" + (projectedContrast / squaredContrast).ToString("F3") +
                ", pixel error=" + (error / Math.Max(1, samples)).ToString("F3") + ").");
        }
        SceneCompositor.ControlSwipeContext Context()
        {
            Draw();
            scene.GetControlSwipeContext(now);
            now += TimeSpan.FromMilliseconds(700);
            Draw();
            var context = scene.GetControlSwipeContext(now);
            Require(context is not null, "The settled eligible board has no control-band context.");
            return context!;
        }
        void Calibrate()
        {
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(.04f, .07f), new(.95f, .04f), new(.96f, .93f), new(.06f, .96f)],
                Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
        }
        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, size, size, false, false);
        }
        byte[] DrawPreview()
        {
            using (var drawing = target.CreateDrawingSession()) scene.DrawBlackjackPreview(drawing, size, size);
            return target.GetPixelBytes();
        }
        byte[] DrawWaterPreview()
        {
            using (var drawing = target.CreateDrawingSession()) scene.DrawWaterGardenPreview(drawing, size, size);
            return target.GetPixelBytes();
        }
        T Field<T>(string name) => (T)typeof(SceneCompositor).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
