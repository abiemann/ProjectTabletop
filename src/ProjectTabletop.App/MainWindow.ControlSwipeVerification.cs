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
            foreach (bool rightToLeft in new[] { true, false })
            {
                var before = Cross(rightToLeft);
                Require(before.Top == .04 && before.Bottom == .20, "Settings did not use its top control row.");
                Require(!session.ButtonsVisible && scene.CurrentBoardButtons.Count == 0,
                    $"The {facing}-degree board did not hide its controls.");
                Require(scene.GetControlSwipeContext(now) is null, "A redraw discarded the command cooldown.");
                Cross(rightToLeft);
                Require(session.ButtonsVisible && scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(ids),
                    $"A second swipe in the same direction did not restore the {facing}-degree board's controls.");
                now += TimeSpan.FromMilliseconds(10);
                Require(!scene.CompleteControlSwipe(before, BoardControlSwipe.Toggle, false, now),
                    "A pre-hide completion survived an identical hide/show round trip.");
            }
        }

        scene.ShowSettings();
        var stale = Context();
        var captured = now;
        now += TimeSpan.FromMilliseconds(400);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, captured),
            "A stale camera result changed controls.");
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, now.AddMilliseconds(1)),
            "A future camera result changed controls.");
        Require(!scene.CompleteControlSwipe(stale, null, false, now), "Empty motion changed visibility.");
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, now),
            "A repeated source frame changed controls.");
        scene.ClearHandTips();
        now += TimeSpan.FromMilliseconds(10);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, now),
            "A camera/reset barrier accepted old motion history.");
        stale = Context();
        scene.SetBlackOutput(true);
        scene.SetBlackOutput(false);
        now += TimeSpan.FromMilliseconds(10);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, now),
            "A transient black calibration field retained an old baseline.");
        stale = Context();
        scene.ShowBlackjack();
        scene.ShowSettings();
        now += TimeSpan.FromMilliseconds(10);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, now),
            "Leaving and returning to a board retained an old context.");
        stale = Context();
        scene.SetBoardSetup(true);
        Require(scene.GetControlSwipeContext(now) is null &&
                !scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, now),
            "Calibration accepted swipe input.");
        Calibrate();
        now += TimeSpan.FromMilliseconds(10);
        Require(!scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, now),
            "Recalibration accepted the old mapping.");
        scene.ShowBoardMenu();
        Require(scene.GetControlSwipeContext(now) is null && session.ButtonsVisible,
            "The main menu enabled swipe commands.");
        scene.ShowStrykersRun();
        Require(scene.GetControlSwipeContext(now) is null, "Stryker's Run enabled swipe detection.");
        scene.ShowRoulette();
        Require(scene.GetControlSwipeContext(now) is null && session.ButtonsVisible &&
                scene.CurrentBoardButtons.Count == 58 && !session.SetButtonsVisible(false, now) &&
                !scene.CompleteControlSwipe(stale, BoardControlSwipe.Toggle, false, now),
            "Roulette enabled swipe detection or hid its controls.");

        scene.ShowCrownDeed();
        scene.ClearHandTips();
        var crownContext = Cross(rightToLeft: true);
        Require(crownContext.Top == .60 && crownContext.Bottom == .83,
            "Crown & Deed did not use its central action row.");
        Cross(rightToLeft: false);

        scene.ShowFootball();
        Cross(rightToLeft: true);
        Cross(rightToLeft: false);
        Require(session.ActivateButton("football-play", now), "Football PLAY fixture failed.");
        now += TimeSpan.FromMilliseconds(10);
        session.SetFootballInput(0, new(-.4f, 0), now);
        session.SetFootballInput(1, new(.4f, 0), now);
        session.TickFootball(now);
        Require(!session.FootballControlsVisible && scene.GetControlSwipeContext(now) is null,
            "Football's automatically hidden gameplay controls enabled swipes.");

        // A detected double-bar marker paints or stirs. The same crossing that
        // toggles controls by hand must not toggle them when the marker appears
        // mid-sweep, nor after it briefly drops out of detection.
        scene.ShowFootball();
        var markerBand = Context();
        var markerPath = new BoardControlSwipeTracker();
        double[] markerStroke = [.88, .66, .45, .12];
        bool markerSwipe = false, markerToggled = false;
        for (int index = 0; index < markerStroke.Length; index++)
        {
            now += TimeSpan.FromMilliseconds(110);
            // The marker detector first reports it on the second frame, then misses it.
            if (index == 1) scene.NoteMarkerOnBoard(now);
            var command = markerPath.Update([new(index, markerStroke[index], (markerBand.Top + markerBand.Bottom) / 2)], now, now);
            markerSwipe |= command == BoardControlSwipe.Toggle;
            markerToggled |= scene.CompleteControlSwipe(markerBand, command, markerPath.HasTraversalInProgress, now);
            Require(index == 0 || scene.GetControlSwipeContext(now) is null,
                "A marker on the board left the control-band context engaged.");
        }
        Require(markerSwipe, "The marker fixture did not form a complete control-row sweep.");
        Require(!markerToggled && session.ButtonsVisible, "A marker stroke along the control row toggled the controls.");
        now += SceneCompositor.ControlSwipeMarkerQuietPeriod;
        Cross(rightToLeft: true);
        Cross(rightToLeft: false);

        VerifyMarkerCompletionOrdering();

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
        double[] crossing = [.90, .65, .45, .25, .15];
        bool hid = false;
        int observation = 0;
        foreach (double u in crossing)
        {
            now += TimeSpan.FromMilliseconds(150);
            // Missing/reassigned identities do not interrupt spatial progress.
            var command = path.Update([new(observation++ % 2 == 0 ? 0 : observation, u, .91)], now, now);
            hid |= scene.CompleteControlSwipe(movementContext, command, path.HasTraversalInProgress, now);
            var currentHold = scene.GetHoldButtonContext(now);
            Require(scene.ObserveHoldButtons(currentHold, [handle], now, []).Count == 0 &&
                    !scene.WaterGardenDrawerOpen,
                "A coordinate swipe completed a caption hold while crossing it.");
            Draw();
        }
        Require(hid && !session.ButtonsVisible, "The one-second coordinate crossing did not hide controls.");
        // The shorter crossing ends during the hide fade. Settle that transition
        // before preparing the next independent swipe's camera context.
        now += BoardSession.ControlFadeDuration;
        session.TickControlVisibility(now);
        Draw();
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "Hidden controls enabled hand-search illumination.");
        Cross(rightToLeft: false);
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
            disturbanceOnly = true, eitherDirectionTogglesVisibility = true,
            hiddenBandRetained = true, excludedMenuAndStryker = true, excludedRoulette = true,
            excludedFootballPlay = true, cameraNavigationCalibrationAndVisibilityBarriers = true,
            coordinateSwipeSuppressesHolds = true, restoredCaptionRequiresClear = true,
            markerStrokesIgnored = true, markerQuietMilliseconds = SceneCompositor.ControlSwipeMarkerQuietPeriod.TotalMilliseconds,
            markerLateCompletionCancels = true, markerClearReleasesImmediately = true,
            bothFootballPlayersCheckedBeforeClear = true,
            markerPendingResetNavigationAndExpiry = true, nonMarkerBoardsDoNotWait = true,
            markerWait = scene.GetControlSwipeMarkerWaitDiagnostics(),
            introAndDrawerAnimationExcluded = true, simultaneousFadeMilliseconds = BoardSession.ControlFadeDuration.TotalMilliseconds,
            fadeBoards = new[] { "WaterGarden", "Blackjack" }, fadingControlsExcludedFromInput = true,
            worldPixelsUnaffected = true, liveHardwareUntouched = true };

        void VerifyMarkerCompletionOrdering()
        {
            var clearPlayer = new MarkerTipDetectionResult([], "not-found");
            var markerPlayer = new MarkerTipDetectionResult([], "ambiguous")
                { AmbiguousMarkerCenters = [new(20, 20)] };
            var waitingPlayer = new MarkerTipDetectionResult([], "waiting-for-projected-bat-reference");
            bool? clear = MarkerResultForControlSwipes(clearPlayer, false);
            bool? marker = MarkerResultForControlSwipes(markerPlayer, false);
            bool? waiting = MarkerResultForControlSwipes(waitingPlayer, false);
            Require(clear == false && marker == true && waiting is null &&
                    CombineControlSwipeMarkerResults(clear, marker) == true &&
                    CombineControlSwipeMarkerResults(marker, clear) == true &&
                    CombineControlSwipeMarkerResults(clear, waiting) is null &&
                    CombineControlSwipeMarkerResults(waiting, clear) is null,
                "Football did not aggregate both players before declaring a checked frame clear.");
            var context = Prepare();
            var tracker = new BoardControlSwipeTracker();
            DateTimeOffset firstFrame = default, lastFrame = default;
            bool commandProduced = false;
            for (int sample = 0; sample < markerStroke.Length; sample++)
            {
                now += TimeSpan.FromMilliseconds(sample == 0 ? 1 : 50);
                if (sample == 0) firstFrame = now;
                lastFrame = now;
                var command = tracker.Update([new(sample, markerStroke[sample], .91)], now, now);
                commandProduced |= command == BoardControlSwipe.Toggle;
                Require(!scene.CompleteControlSwipe(context, command, tracker.HasTraversalInProgress, now,
                        markerCheckRequired: true) && session.ButtonsVisible,
                    "The swipe worker outran an unfinished marker check and toggled controls.");
            }
            Require(commandProduced, "The reverse-completion fixture did not produce a swipe.");
            now += TimeSpan.FromMilliseconds(50);
            Require(!scene.CompleteControlSwipeMarkerCheck(firstFrame, markerSeen: true) && session.ButtonsVisible,
                "A marker measured during the swipe did not cancel its waiting command.");
            Require(!scene.CompleteControlSwipeMarkerCheck(lastFrame, markerSeen: false) && session.ButtonsVisible,
                "A later clear frame resurrected a marker-cancelled command.");

            // Deliberately no detector callback while a worker is busy or its
            // frame was dropped. An older clear result also cannot release it.
            context = Prepare();
            now += TimeSpan.FromMilliseconds(1);
            var waitingFrame = now;
            Require(!scene.CompleteControlSwipe(context, BoardControlSwipe.Toggle, false, waitingFrame, true),
                "An unchecked marker-board swipe did not wait.");
            now += TimeSpan.FromMilliseconds(17);
            Require(!scene.CompleteControlSwipeMarkerCheck(waitingFrame.AddTicks(-1), false) && session.ButtonsVisible,
                "A clear result from before the swipe released it prematurely.");
            now += TimeSpan.FromMilliseconds(1);
            Require(scene.CompleteControlSwipeMarkerCheck(waitingFrame, false) && !session.ButtonsVisible &&
                    session.ControlVisibilityAnimating(now),
                "A clear same-frame result did not start the fade immediately when it finished.");
            Require(!scene.CompleteControlSwipeMarkerCheck(waitingFrame, false),
                "A repeated clear completion toggled a pending swipe twice.");
            var wait = System.Text.Json.JsonSerializer.SerializeToElement(scene.GetControlSwipeMarkerWaitDiagnostics());
            Require(wait.GetProperty("lastWaitMilliseconds").GetDouble() == 18,
                "Marker wait includes an added timer instead of only the 18 ms detector lag.");

            context = Prepare();
            now += TimeSpan.FromMilliseconds(1);
            Require(!scene.CompleteControlSwipeMarkerCheck(now, false) &&
                    scene.CompleteControlSwipe(context, BoardControlSwipe.Toggle, false, now, true),
                "A marker detector already caught up with the swipe introduced a wait.");

            foreach (string invalidation in new[] { "reset", "navigation", "expiry" })
            {
                context = Prepare();
                now += TimeSpan.FromMilliseconds(1);
                Require(!scene.CompleteControlSwipe(context, BoardControlSwipe.Toggle, false, now, true),
                    "The invalidation fixture did not queue its single waiting command.");
                if (invalidation == "reset") scene.ResetControlSwipeMarkerChecks();
                else if (invalidation == "navigation") scene.ShowSettings();
                now += TimeSpan.FromMilliseconds(invalidation == "expiry" ? 251 : 1);
                Require(!scene.CompleteControlSwipeMarkerCheck(now, false) && session.ButtonsVisible,
                    "A " + invalidation + " allowed a delayed marker check to toggle controls.");
                if (invalidation == "expiry")
                {
                    now += TimeSpan.FromMilliseconds(1);
                    Require(!scene.CompleteControlSwipe(context, BoardControlSwipe.Toggle, false, now, true) &&
                            scene.CompleteControlSwipeMarkerCheck(now, false) && !session.ButtonsVisible,
                        "An expired pending command occupied the slot and blocked a fresh later swipe.");
                }
            }

            // Carry a recent marker into another board: neither its quiet window
            // nor marker-detector completion may delay non-marker controls.
            scene.NoteMarkerOnBoard(now);
            scene.ShowSettings();
            context = Context();
            now += TimeSpan.FromMilliseconds(1);
            Require(scene.CompleteControlSwipe(context, BoardControlSwipe.Toggle, false, now),
                "A non-marker board waited for a marker result or inherited its quiet period.");

            SceneCompositor.ControlSwipeContext Prepare()
            {
                now += TimeSpan.FromSeconds(2);
                scene.ShowFootball();
                scene.ClearHandTips();
                scene.ResetControlSwipeMarkerChecks();
                return Context();
            }
        }

        SceneCompositor.ControlSwipeContext Cross(bool rightToLeft)
        {
            var context = Context();
            bool expectedVisible = !session.ButtonsVisible;
            var camera = Field<Homography>("_boardCameraMap");
            var surface = Field<Homography>("_boardSurfaceMap");
            var band = Homography.FromFourPoints(context.SearchPolygon.Select(point => new Point2(point.X, point.Y)).ToArray(),
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
            var tracker = new BoardControlSwipeTracker();
            bool applied = false;
            int observation = 0;
            foreach (double position in new[] { .88, .66, .45, .12 })
            {
                now += TimeSpan.FromMilliseconds(110);
                double u = rightToLeft ? position : 1 - position;
                double v = (context.Top + context.Bottom) / 2;
                var pixel = camera.InverseTransform(surface.Transform(new(u, v)));
                var normalized = band.Transform(pixel);
                Require(Math.Abs(normalized.X - u) < 1e-6 && Math.Abs(normalized.Y - .5) < 1e-6,
                    "The control band did not map the player's facing correctly.");
                var command = tracker.Update([new(observation++, normalized.X,
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
            Require(!session.ControlVisibilityAnimating(now) && session.ButtonsVisible == expectedVisible &&
                    session.GetButtonsOpacity(now) == (expectedVisible ? 1 : 0),
                "A swipe fade failed to toggle its previous visibility.");
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
