#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated compositor and synthetic motion; never touches live camera/output.
    private async Task<object> VerifyControlSwipesAsync()
    {
        const int size = 700;
        var now = MonotonicClock.UtcNow.AddMinutes(1);
        var device = CanvasDevice.GetSharedDevice();
        using var scene = new SceneCompositor(blackjackClock: () => now, waterClock: () => now,
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

        scene.ShowBlackjack();
        var blackjackContext = Context();
        Require(blackjackContext.Top == .75 && blackjackContext.Bottom == .90,
            "Blackjack did not use its lower action-row band.");
        byte[] shown = DrawPreview();
        session.SetButtonsVisible(false, now);
        byte[] hidden = DrawPreview();
        session.SetButtonsVisible(true, now);
        Require(!shown.SequenceEqual(hidden) && shown.SequenceEqual(DrawPreview()),
            "The dedicated Blackjack preview retained hidden controls or failed to restore them.");
        return new { passed = true, playerFacingAngles = new[] { 0, 90, 180 },
            disturbanceOnly = true, hiddenBandRetained = true, excludedMenuAndStryker = true,
            excludedFootballPlay = true, cameraNavigationCalibrationAndVisibilityBarriers = true,
            slowSwipeSuppressesHolds = true, restoredCaptionRequiresClear = true,
            introAndDrawerAnimationExcluded = true, liveHardwareUntouched = true };

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
            return context;
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
        T Field<T>(string name) => (T)typeof(SceneCompositor).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
