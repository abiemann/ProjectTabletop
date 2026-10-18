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
    // Reuse the real rendered-caption fixture and the shared swipe tracker in
    // an isolated scene. No camera, projector, saved painting or profile changes.
    private async Task<object> VerifyPaintDrawerAsync()
    {
        const int size = 1000;
        var now = MonotonicClock.UtcNow;
        DateTimeOffset? frozenPaintTime = null;
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        string directory = Path.Combine(_appDataDirectory, "PaintDrawerVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        using var scene = NewScene(out double inset);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        scene.DisablePaintIdleStreaksForVerification();
        scene.ResetPaint();
        var blank = Draw();
        var session = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        Require(!scene.PaintDrawerOpen && scene.CurrentBoardButtons.Single() is
                { Id: "paint-drawer-open", Label: "^", Enabled: true, Hold: BoardButtonHold.Once } handle &&
                handle.Bounds == new BoardRect(.01, .87, .18, .12) &&
                !scene.ActivatePaintButton("menu", out _) && !scene.ActivatePaintButton("paint-reset", out _) &&
                !scene.ActivatePaintButton("paint-save", out _),
            "Paint must initially expose only its bottom-left long-press arrow.");
        await Capture("closed-arrow");

        var holds = new PhotoCopyCaptionHoldFixture(scene, Draw, size, time => now = time);
        await holds.CheckReleaseCancelsAsync("paint-drawer-open");
        await holds.HoldAsync("paint-drawer-open");
        Require(scene.PaintDrawerOpen && scene.CurrentBoardButtons.Where(button => button.Id != "paint-drawer-close")
                .All(button => !button.Enabled) && !scene.ActivatePaintButton("menu", out _) &&
                scene.GetHoldButtonContext(now) is null && scene.GetControlSwipeContext(now) is null,
            "Paint accepted an action or camera caption while its drawer was opening.");
        await Task.Delay((int)BoardSession.PaintDrawerOpeningDuration.TotalMilliseconds + 50);
        now = MonotonicClock.UtcNow;
        scene.TickPaint(now);
        Draw();
        Require(scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(["paint-drawer-close", "menu", "paint-reset", "paint-save"]) &&
                scene.CurrentBoardButtons.Select(button => button.Label).SequenceEqual(["v", "EXIT", "RESET", "SAVE"]) &&
                scene.CurrentBoardButtons.All(button => button.Hold == BoardButtonHold.Once) &&
                !scene.CurrentBoardButtons.Single(button => button.Id == "paint-save").Enabled,
            "Paint's settled drawer did not expose its down arrow, EXIT, RESET and unavailable empty-canvas SAVE.");
        CheckSharedControls();
        Require(scene.AddPaintDrop(new(.55, .45), .07, now), "The drawer fixture could not paint its canvas.");
        Draw();
        Require(scene.CurrentBoardButtons.Single(button => button.Id == "paint-save").Enabled,
            "Paint did not enable SAVE after painting.");
        long drops = scene.GetPaintDiagnostics().DropCount;
        long painting = scene.GetPaintDiagnostics().SessionRevision;
        await Capture("open-actions");
        await holds.CheckReleaseCancelsAsync("paint-save");
        var saveFrame = await holds.HoldAsync("paint-save");
        Require(scene.TryTakePaintSaveRequest(saveFrame, out var image) &&
                !scene.TryTakePaintSaveRequest(saveFrame, out _) && scene.CompletePaintSave(image),
            "The broken SAVE caption did not queue exactly one consumable in-memory save.");
        var continued = await holds.ContinueHeldAsync("paint-save");
        Require(!scene.TryTakePaintSaveRequest(continued, out _) && scene.GetPaintDiagnostics().DropCount == drops,
            "A held SAVE repeated or painting was deposited by its control input.");

        long resetRevision = session.PaintResetRevision;
        await holds.CheckReleaseCancelsAsync("paint-reset");
        Require(session.PaintResetRevision == resetRevision && scene.GetPaintDiagnostics().DropCount == drops,
            "A canceled partial RESET hold cleared the painting.");
        long markerRevision = scene.PaintMarkerInputRevision;
        now += TimeSpan.FromMilliseconds(20);
        Require(!scene.SetPaintMarkerTip(Camera(new(.25, .45)), .025, now, markerRevision),
            "Preparing the pre-reset marker path unexpectedly deposited paint.");
        var pendingSaveFrame = now;
        scene.QueuePaintSaveRequest(pendingSaveFrame);
        Require(scene.TryBeginPaintSave(out var inFlightSave),
            "The RESET fixture could not prepare an in-flight in-memory save.");
        await holds.HoldAsync("paint-reset");
        var resetPixels = Draw();
        var resetState = scene.GetPaintDiagnostics();
        var resetFields = scene.CapturePaintFieldStatisticsForVerification();
        Require(session.PaintResetRevision == resetRevision + 1 && scene.PaintDrawerOpen &&
                scene.CurrentBoardScreen == BoardScreen.Paint && resetState.SessionRevision == painting &&
                resetState.DropCount == 0 && resetState.PendingIntroductionDrops == 0 &&
                resetState.PendingIdleStreakDrops == 0 && resetFields.HeightMass == 0 &&
                resetFields.SurfaceColorSum == 0 && SameCanvas(blank, resetPixels) &&
                scene.GetPendingPaintMarkerDropsForVerification().Length == 0 &&
                !scene.CurrentBoardButtons.Single(button => button.Id == "paint-save").Enabled,
            "The broken RESET caption did not clear only the artwork while preserving the open drawer.");
        Require(scene.PaintMarkerInputRevision != markerRevision &&
                !scene.SetPaintMarkerTip(Camera(new(.29, .45)), .025, now, markerRevision) &&
                !scene.TryTakePaintSaveRequest(pendingSaveFrame, out _) &&
                !scene.CompletePaintSave(inFlightSave) && scene.GetPaintSaveStatus(now) is null,
            "RESET accepted a stale marker result, queued save or save completion from the previous painting.");
        await holds.ContinueHeldAsync("paint-reset");
        Require(session.PaintResetRevision == resetRevision + 1 &&
                scene.GetPaintDiagnostics() is { DropCount: 0, PendingIntroductionDrops: 0 } &&
                SameCanvas(blank, Draw()),
            "Holding RESET repeated the action or replayed the Paint introduction onto the cleared canvas.");
        await Capture("reset-cleared-canvas");
        markerRevision = scene.PaintMarkerInputRevision;
        now += TimeSpan.FromMilliseconds(20);
        Require(!scene.SetPaintMarkerTip(Camera(new(.30, .45)), .025, now, markerRevision),
            "The first marker position after RESET replayed the former stroke.");
        foreach (double x in new[] { .34, .38 })
        {
            now += TimeSpan.FromMilliseconds(20);
            scene.SetPaintMarkerTip(Camera(new(x, .45)), .025, now, markerRevision);
        }
        var repainted = Draw();
        Require(scene.GetPaintDiagnostics().DropCount > 0 && !SameCanvas(blank, repainted) &&
                scene.CurrentBoardButtons.Single(button => button.Id == "paint-save").Enabled && scene.PaintDrawerOpen,
            "Fresh double bars could not paint or re-enable SAVE after RESET.");
        drops = scene.GetPaintDiagnostics().DropCount;
        painting = scene.GetPaintDiagnostics().SessionRevision;
        await Capture("reset-fresh-marker-paint");

        // Freezing only the fluid makes exact output comparisons possible while
        // the shared control clock still advances every fade and swipe.
        frozenPaintTime = now;
        var buttons = scene.CurrentBoardButtons.ToArray();
        var shown = Draw();
        var hide = CheckSwipeRoundTrip(rightToLeft: true);
        CheckSwipeRoundTrip(rightToLeft: false);
        await Capture("swipe-restored");
        frozenPaintTime = null;
        Require(scene.ActivatePaintButton("paint-drawer-close", out _) && !scene.PaintDrawerOpen &&
                scene.CurrentBoardButtons.Single().Id == "paint-drawer-open" &&
                scene.GetPaintDiagnostics().DropCount == drops && scene.GetPaintDiagnostics().SessionRevision == painting,
            "Closing Paint's drawer discarded its painting or left the actions exposed.");
        scene.ShowBoardMenu();
        scene.ShowPaint();
        Require(!scene.PaintDrawerOpen && scene.CurrentBoardButtons.Single().Id == "paint-drawer-open",
            "A new Paint visit retained an open drawer.");
        CheckPartialSwipeDefersIdlePaint();
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The drawer verification changed live hardware or the user's board.");
        return new { passed = true, arrowOnlyAtEntry = true, openingActionsDisabled = true,
            sharedCaptionAndArrowPixels = true, realCaptionHoldAndRelease = true,
            resetReleaseCancels = true, resetActivatesOnce = true, resetPreservesDrawerAndNavigation = true,
            resetClearsArtworkWithoutIntroduction = true, resetRejectsStaleMarkerAndSave = true,
            freshMarkerPaintsAfterReset = true,
            saveQueuedOnce = true, bottomBand = new { hide.Top, hide.Bottom }, eitherDirectionTogglesVisibility = true,
            simultaneousFadesPreservePaintingAndDrawer = true, hiddenActionsDisabled = true,
            partialSwipeCancelsIdleWithoutTogglingOrPainting = true,
            holds.SuccessfulHolds, holds.BrokenCaptionFrames, directory, images };

        SceneCompositor NewScene(out double safetyInset)
        {
            var result = new SceneCompositor(blackjackClock: () => now, paintClock: () => frozenPaintTime ?? now);
            result.SetDisplayAspect(1);
            result.SetBoardFacingDegrees(0);
            result.SetBoardSetup(true);
            safetyInset = result.SetDetectedBoardGrid([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            result.SetBoardSetup(false);
            result.ShowPaint();
            return result;
        }
        void CheckPartialSwipeDefersIdlePaint()
        {
            using var idle = NewScene(out _);
            idle.ResetPaint();
            DrawIdle();
            var deadline = idle.GetPaintDiagnostics().NextIdleStreakAt ??
                throw new InvalidOperationException("The partial-swipe fixture has no idle paint deadline.");
            while (now < deadline)
            {
                now += TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1).Ticks, (deadline - now).Ticks));
                DrawIdle();
            }
            Require(idle.GetPaintDiagnostics() is { IdleStreakCount: 1, PendingIdleStreakDrops: > 0 },
                "The partial-swipe fixture did not start an idle streak.");
            idle.GetControlSwipeContext(now);
            now += TimeSpan.FromMilliseconds(800);
            DrawIdle();
            var context = idle.GetControlSwipeContext(now) ??
                throw new InvalidOperationException("The partial-swipe fixture has no settled bottom swipe band.");
            var before = idle.GetPaintDiagnostics();
            Require(before.PendingIdleStreakDrops > 0 && idle.BoardButtonsVisible,
                "The partial-swipe fixture needs pending decoration and visible controls.");
            var tracker = new BoardControlSwipeTracker();
            int observation = 0;
            foreach (double x in new[] { .9, .65, .4 })
            {
                now += TimeSpan.FromMilliseconds(110);
                var command = tracker.Update([new(observation++, x, (context.Top + context.Bottom) / 2)], now, now);
                Require(command is null && !idle.CompleteControlSwipe(context, command, tracker.HasTraversalInProgress, now),
                    "An unfinished bottom-row sweep toggled Paint controls.");
                if (!tracker.HasTraversalInProgress)
                    Require(idle.GetPaintDiagnostics().LastUserActivityAt == before.LastUserActivityAt &&
                            idle.GetPaintDiagnostics().PendingIdleStreakDrops == before.PendingIdleStreakDrops,
                        "An unestablished swipe candidate canceled idle paint.");
            }
            var after = idle.GetPaintDiagnostics();
            Require(tracker.HasTraversalInProgress && idle.BoardButtonsVisible && !idle.PaintDrawerOpen &&
                    idle.CurrentBoardButtons.Single().Id == "paint-drawer-open" &&
                    after.LastUserActivityAt == now && after.NextIdleStreakAt == now.AddSeconds(SceneCompositor.PaintIdleSeconds) &&
                    after.PendingIdleStreakDrops == 0 && after.DropCount == before.DropCount &&
                    after.SessionRevision == before.SessionRevision,
                "An established partial swipe must cancel idle paint without toggling controls or depositing paint.");
            now += TimeSpan.FromMilliseconds(500);
            DrawIdle();
            Require(idle.GetPaintDiagnostics().DropCount == before.DropCount &&
                    idle.GetPaintDiagnostics().IdleStreakCount == 1,
                "Canceled idle paint continued beneath the unfinished swipe.");

            void DrawIdle()
            {
                using var drawing = target.CreateDrawingSession();
                idle.Draw(drawing, size, size, false, false);
            }
        }
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, size, size, false, false);
            return target.GetPixelBytes();
        }
        Point2 Camera(Point2 board) => new(size * (inset / 2 + board.X * (1 - inset)),
            size * (inset / 2 + board.Y * (1 - inset)));
        async Task Capture(string name)
        {
            Draw();
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
        }
        void CheckSharedControls()
        {
            using var paint = new CanvasRenderTarget(target.Device, size, size, 96);
            using var globe = new CanvasRenderTarget(target.Device, size, size, 96);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var controls = scene.CurrentBoardButtons.ToArray();
            using (var drawing = paint.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                typeof(SceneCompositor).GetMethod("DrawPaintControls", flags)!.Invoke(scene,
                    [drawing, controls, Array.Empty<string>(), Array.Empty<BoardFingerSelectionFeedback>(), now]);
            }
            using (var drawing = globe.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                typeof(SceneCompositor).GetMethod("DrawGlobeControls", flags)!.Invoke(scene,
                    [drawing, controls, Array.Empty<string>(), Array.Empty<BoardFingerSelectionFeedback>(), true, 1f, 1d, 1d, 0d]);
            }
            byte[] actual = paint.GetPixelBytes(), reference = globe.GetPixelBytes();
            // Globe also draws its board title. Compare the complete row,
            // including control rims, instead of unrelated board decoration.
            for (int y = 866; y < 995; y++)
                Require(actual.AsSpan(y * size * 4, size * 4).SequenceEqual(reference.AsSpan(y * size * 4, size * 4)),
                    "Paint's actual drawer surfaces, shared 28px Semibold capitals or vector arrows differ from the shared controls.");
        }
        SceneCompositor.ControlSwipeContext CheckSwipeRoundTrip(bool rightToLeft)
        {
            var context = Swipe(rightToLeft);
            Require(!session.ButtonsVisible && scene.PaintDrawerOpen && scene.CurrentBoardButtons.Count == 0 &&
                    !scene.ActivatePaintButton("menu", out _) && !scene.ActivatePaintButton("paint-reset", out _) &&
                    !scene.ActivatePaintButton("paint-save", out _) &&
                    scene.GetHoldButtonContext(now) is null,
                "The bottom swipe left hidden Paint actions active or closed the drawer.");
            now += BoardSession.ControlFadeDuration / 2;
            var hideMiddle = Draw();
            Require(Math.Abs(session.GetButtonsOpacity(now) - .5) < .001,
                "Paint's controls did not share one half-faded opacity.");
            now += BoardSession.ControlFadeDuration / 2;
            session.TickControlVisibility(now);
            var hidden = Draw();
            Require(scene.GetPaintDiagnostics().SessionRevision == painting && scene.GetPaintDiagnostics().DropCount == drops,
                "Hiding Paint controls cleared or restarted the painting.");
            Swipe(rightToLeft);
            now += BoardSession.ControlFadeDuration / 2;
            var showMiddle = Draw();
            now += BoardSession.ControlFadeDuration / 2;
            session.TickControlVisibility(now);
            var restored = Draw();
            Require(session.ButtonsVisible && scene.PaintDrawerOpen && shown.SequenceEqual(restored) &&
                    scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(buttons.Select(button => button.Id)),
                "A second swipe in the same direction did not restore the same drawer and unchanged painting.");
            foreach (var button in buttons)
            {
                CheckMidpoint(button, shown, hidden, hideMiddle);
                CheckMidpoint(button, shown, hidden, showMiddle);
            }
            Require(SameCanvas(shown, hidden), "Fading Paint controls also faded the artwork.");
            return context;
        }
        SceneCompositor.ControlSwipeContext Swipe(bool rightToLeft)
        {
            Draw();
            scene.GetControlSwipeContext(now);
            now += TimeSpan.FromMilliseconds(800);
            Draw();
            var context = scene.GetControlSwipeContext(now) ?? throw new InvalidOperationException("Paint has no settled bottom swipe band.");
            bool expectedVisible = !session.ButtonsVisible;
            Require(context.Top >= .80 && context.Bottom <= 1 && context.Bottom > context.Top,
                "Paint watches the canvas rather than the bottom control row for its swipe.");
            var tracker = new BoardControlSwipeTracker();
            bool accepted = false;
            int observation = 0;
            foreach (double x in new[] { .9, .65, .4, .1 })
            {
                now += TimeSpan.FromMilliseconds(110);
                var command = tracker.Update([new(observation++, rightToLeft ? x : 1 - x, (context.Top + context.Bottom) / 2)], now, now);
                accepted |= scene.CompleteControlSwipe(context, command, tracker.HasTraversalInProgress, now);
            }
            Require(accepted && session.ButtonsVisible == expectedVisible && session.ControlVisibilityAnimating(now),
                "Paint did not toggle visibility after a full bottom-row swipe.");
            return context;
        }
        void CheckMidpoint(BoardButton button, byte[] shown, byte[] hidden, byte[] middle)
        {
            int samples = 0;
            double sum = 0, contrastSquared = 0;
            int left = (int)(size * (inset / 2 + button.Bounds.X * (1 - inset)));
            int top = (int)(size * (inset / 2 + button.Bounds.Y * (1 - inset)));
            int right = left + (int)(size * button.Bounds.Width * (1 - inset));
            int bottom = top + (int)(size * button.Bounds.Height * (1 - inset));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            for (int c = 0; c < 3; c++)
            {
                int offset = (y * size + x) * 4 + c;
                int contrast = shown[offset] - hidden[offset];
                if (Math.Abs(contrast) < 20) continue;
                contrastSquared += contrast * contrast;
                sum += contrast * (middle[offset] - hidden[offset]);
                samples++;
            }
            Require(samples > 150 && sum / contrastSquared is > .46 and < .54,
                button.Id + " did not fade at the same half-opacity as its siblings.");
        }
        static bool SameCanvas(byte[] a, byte[] b) => a.AsSpan(200 * size * 4, 500 * size * 4)
            .SequenceEqual(b.AsSpan(200 * size * 4, 500 * size * 4));
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
