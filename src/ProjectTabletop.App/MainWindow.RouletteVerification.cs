#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Offscreen production rendering and native camera caption comparison only.
    // These checks never construct or operate a camera, projector or control pipe.
    private async Task<object> VerifyRouletteAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        string directory = Path.Combine(_appDataDirectory, "RouletteVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var aspects = new List<object>();
        int exactClockChecks = 0, unchangedCaptionChecks = 0, nativeHolds = 0;
        int pocketEndpointChecks = 0, consecutiveRoundContinuityChecks = 0, boundedTrackRadiusChecks = 0;

        // Prove the absolute-time visual endpoint independently for every pocket,
        // including zero and the discontinuity across the last/first angle.
        var sample = new RouletteGame(71).Snapshot;
        var motionStart = DateTimeOffset.UtcNow;
        foreach (long roundNumber in new[] { 1L, 2L })
        foreach (int index in Enumerable.Range(0, RouletteGame.WheelOrder.Count))
        {
            var round = sample with { Phase = RoulettePhase.Spinning, RoundNumber = roundNumber,
                RoundStartedAt = motionStart, Outcome = RouletteGame.WheelOrder[index], PocketIndex = index };
            var terminal = SceneCompositor.GetRouletteMotion(round, motionStart + round.SpinDuration);
            var approaching = SceneCompositor.GetRouletteMotion(round,
                motionStart + round.SpinDuration - TimeSpan.FromMilliseconds(1));
            double resting = SceneCompositor.RouletteRestingWheelAngle(roundNumber);
            double expected = index * Math.Tau / RouletteGame.WheelOrder.Count - Math.PI / 2 + resting;
            var settled = SceneCompositor.GetRouletteMotion(round with { Phase = RoulettePhase.Betting }, motionStart + round.SpinDuration);
            Check(AngleError(terminal.WheelAngle, resting) < 1e-10 && terminal.BallRadius == 130 && terminal.Progress == 1 &&
                AngleError(terminal.BallAngle, expected) < 1e-10 &&
                AngleError(approaching.BallAngle, terminal.BallAngle) < .001 &&
                AngleError(approaching.WheelAngle, terminal.WheelAngle) < .001 &&
                Math.Abs(approaching.BallRadius - terminal.BallRadius) < .01 && terminal == settled &&
                Vector2.Distance(SceneCompositor.ProjectRouletteBall(approaching), SceneCompositor.ProjectRouletteBall(terminal)) < .15,
                "Roulette's ball/wheel endpoint does not settle continuously in pocket " + index + ".");
            pocketEndpointChecks++;
            if (index == 0)
            {
                // A ball may bounce radially on descent, but its center must
                // remain inside the physical track rather than cross the rail.
                for (int frame = 0; frame <= 420; frame++)
                {
                    var moving = SceneCompositor.GetRouletteMotion(round, motionStart.AddSeconds(frame / 60d));
                    Check(double.IsFinite(moving.BallRadius) && moving.BallRadius <= 159.000001,
                        "Roulette's descending ball crossed its fixed outer rail.");
                    boundedTrackRadiusChecks++;
                }
            }
        }

        foreach (var aspect in new[] { (Width: 1152, Height: 896, Name: "72x56"),
            (Width: 720, Height: 1280, Name: "portrait") })
        {
            var probe = new RouletteGame(71);
            Check(probe.HandleAction("roulette-number-7", motionStart) && probe.HandleAction("roulette-spin", motionStart),
                "The deterministic roulette fixture could not choose its normal random outcome.");
            int winningNumber = probe.Snapshot.Outcome!.Value;
            var game = new RouletteGame(71);
            using var fixture = new RouletteNativeFixture(aspect.Width, aspect.Height, game);
            var scene = fixture.Scene;
            scene.ShowRoulette();
            byte[] idle = await Capture("idle");
            var projection = fixture.CheckProjectedRim(idle);
            fixture.CheckRotorPocketColors();
            Check(typeof(SceneCompositor).GetField("_rouletteBackdrop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(scene) is CanvasBitmap, "Roulette rendered a fallback instead of its generated casino artwork.");
            Check(scene.CurrentBoardButtons.Where(button => button.Bounds.Y >= .855)
                .All(button => button.Hold == BoardButtonHold.Once), "A bottom roulette control is not a single-action caption hold.");
            Check(scene.ActivateRouletteButton("roulette-number-" + winningNumber), "The roulette number target did not accept its wager.");
            await Capture("bets-placed");
            fixture.CheckPartialHoldCancels("roulette-spin");
            fixture.Hold("roulette-spin");
            nativeHolds++;
            Check(scene.RouletteState.Phase == RoulettePhase.Spinning && scene.RouletteState.RoundNumber == 1,
                "Broken SPIN lettering did not begin exactly one roulette round.");
            var committed = scene.RouletteState;
            Check(committed.Outcome == winningNumber && RouletteGame.WheelOrder[committed.PocketIndex!.Value] == winningNumber,
                "The renderer changed the game's committed pocket.");

            fixture.Now = committed.RoundStartedAt.AddSeconds(1.05);
            byte[] flight = await Capture("spin-flight");
            fixture.Now = committed.RoundStartedAt.AddSeconds(3.15);
            byte[] orbit = fixture.Draw();
            var mechanism = fixture.CheckMechanismParts(committed);
            var material = fixture.CheckMaterialFinish();
            Check(ReferenceEquals(committed, scene.RouletteState) &&
                !flight.AsSpan().SequenceEqual(orbit), "Roulette motion either changed authoritative state or did not animate.");
            fixture.CheckStationaryRimAndWood(flight, orbit);
            foreach (var button in scene.CurrentBoardButtons.Where(button => button.IsHold))
            {
                Check(fixture.RegionUnchanged(flight, orbit, button.Bounds),
                    "Roulette motion changed stationary " + button.Label + " caption/surface pixels.");
                unchangedCaptionChecks++;
            }
            Check(!scene.ActivateRouletteButton("roulette-spin") && !scene.ActivateRouletteButton("roulette-number-1") &&
                ReferenceEquals(committed, scene.RouletteState), "Roulette accepted another wager/spin during its committed round.");
            fixture.Now = committed.RoundStartedAt.AddSeconds(5.85);
            await Capture("spin-descent");
            fixture.Now = committed.RoundStartedAt + committed.SpinDuration;
            byte[] landed = await Capture("landing");
            var result = scene.RouletteState;
            decimal returned = RouletteGame.Payout(new(RouletteBetKind.Straight, winningNumber), committed.TotalBet, winningNumber);
            Check(result.Phase == RoulettePhase.Betting && result.Outcome == winningNumber &&
                result.RoundNumber == committed.RoundNumber && result.LastWin == returned &&
                result.Balance == RouletteGame.StartingBalance - committed.TotalBet + returned && result.History.Count == 1,
                "Roulette's native deadline did not settle its committed winning wager exactly once.");
            fixture.CheckRenderedBall(landed, result);
            fixture.Now += TimeSpan.FromMilliseconds(650);
            await Capture("win-glints");
            Check(ReferenceEquals(result, scene.RouletteState), "Winning decorative glints changed roulette state.");

            // A second paid round must inherit the first physical resting pose,
            // rather than resetting its rotor or ball when Betting/Spinning flips.
            fixture.Now = result.RoundStartedAt + result.SpinDuration + TimeSpan.FromSeconds(4);
            Check(scene.ActivateRouletteButton("roulette-number-7"), "The second consecutive round could not place its wager.");
            var previousPose = SceneCompositor.GetRouletteMotion(scene.RouletteState, fixture.Now);
            fixture.Hold("roulette-spin");
            nativeHolds++;
            var second = scene.RouletteState;
            var secondStart = SceneCompositor.GetRouletteMotion(second, second.RoundStartedAt);
            Check(second.RoundNumber == 2 && second.Phase == RoulettePhase.Spinning &&
                AngleError(previousPose.WheelAngle, secondStart.WheelAngle) < 1e-10 &&
                AngleError(previousPose.BallAngle, secondStart.BallAngle) < 1e-10 &&
                Vector2.Distance(SceneCompositor.ProjectRouletteBall(previousPose), SceneCompositor.ProjectRouletteBall(secondStart)) < .001,
                "The next roulette round reset its inherited wheel/ball pose.");
            consecutiveRoundContinuityChecks++;
            await Capture("second-spin-start");
            fixture.Now = second.RoundStartedAt.AddSeconds(3.15);
            await Capture("second-spin-orbit");
            fixture.Now = second.RoundStartedAt + second.SpinDuration;
            var secondLanded = await Capture("second-landing");
            var final = scene.RouletteState;
            Check(final.RoundNumber == 2 && final.History.Count == 2 && final.Phase == RoulettePhase.Betting,
                "The second roulette round did not settle exactly once.");
            fixture.CheckRenderedBall(secondLanded, final);
            fixture.Hold("roulette-exit");
            nativeHolds++;
            Check(scene.CurrentBoardScreen == BoardScreen.Menu && scene.RouletteState == final,
                "Roulette's caption-held Exit lost the table state or failed to return to the menu.");
            aspects.Add(new { aspect.Name, aspect.Width, aspect.Height, projection, mechanism, material, winningNumber,
                firstPocket = result.PocketIndex, secondPocket = final.PocketIndex, final.LastWin, final.Balance });

            async Task<byte[]> Capture(string name)
            {
                byte[] pixels = fixture.Draw();
                var state = scene.RouletteState;
                Check(pixels.AsSpan().SequenceEqual(fixture.Draw()) && ReferenceEquals(state, scene.RouletteState),
                    "The same roulette clock changed pixels or game state: " + name + ".");
                exactClockChecks++;
                string path = Path.Combine(directory, aspect.Name + "-" + name + ".png");
                await fixture.Target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
                return pixels;
            }
        }
        Check(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Isolated roulette verification changed live hardware or navigation.");
        return new { passed = true, directory, images, aspects, exactClockChecks, unchangedCaptionChecks, nativeHolds,
            pocketEndpointChecks, consecutiveRoundContinuityChecks, boundedTrackRadiusChecks,
            all37PocketEndpoints = true, continuousTerminalGeometry = true, actualNativeCaptionHolds = true,
            all37RenderedPocketColors = true,
            intactCaptionCancels = true, immutableCommittedRound = true, renderedBallMatchesOutcome = true,
            fixedPerspectiveSilhouette = true, fixedBowlLighting = true, independentPocketAndSpindleMotion = true,
            persistentRestingOrientation = true, generatedArtworkLoaded = true, liveHardwareUnchanged = true };

        static double AngleError(double a, double b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }

    private async Task<object> VerifyMenuScrollAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        string directory = Path.Combine(_appDataDirectory, "MenuScrollVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        using var fixture = new RouletteNativeFixture(1000, 1000, new RouletteGame(1));
        var scene = fixture.Scene;
        var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scene)!;
        scene.ShowBoardMenu();
        byte[] initial = await Capture("menu-top");
        var initialButtons = scene.CurrentBoardButtons.Select(button => (button.Id, button.Bounds)).ToArray();
        Check(scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(
            ["slots", "photo-copy", "blackjack", "paint", "monopoly", "globe", "settings", "menu-scroll-down"]),
            "The initial menu does not show the six original cards and a separate scroll handle.");
        scene.GetHandAcquisitionContext(fixture.Now);
        fixture.Now += TimeSpan.FromMilliseconds(600);
        fixture.Draw();
        var topReference = scene.GetHandAcquisitionContext(fixture.Now);
        Check(topReference?.ExpectedScene is not null, "The initial menu did not capture its stationary control reference.");
        fixture.CheckPartialHoldCancels("menu-scroll-down");
        fixture.Hold("menu-scroll-down");
        var started = fixture.Now;
        Check(board.MenuScrolling && scene.CurrentBoardButtons.All(button => !button.Enabled),
            "A moving menu left cards or navigation controls enabled.");
        Check(scene.GetHandAcquisitionContext(fixture.Now) is null && scene.GetHoldButtonContext(fixture.Now) is null &&
            !board.ActivateButton("roulette", fixture.Now) && !board.ActivateButton("settings", fixture.Now),
            "The moving menu accepted input or a camera reference.");
        await Capture("menu-slide-start");
        fixture.Now = started + BoardSession.MenuScrollDuration / 2;
        byte[] midpoint = await Capture("menu-slide-half");
        Check(Math.Abs(board.GetMenuScrollOffset(fixture.Now) - .30) < 1e-8 &&
            fixture.RegionUnchanged(initial, midpoint, new(.06, .035, .66, .19)) &&
            fixture.RegionUnchanged(initial, midpoint, new(.06, .89, .64, .08)) &&
            scene.GetHandAcquisitionContext(fixture.Now) is null,
            "Menu motion changed the fixed heading/footer or supplied an input reference.");
        fixture.Now = started + BoardSession.MenuScrollDuration;
        await Capture("menu-scrolled");
        Check(board.MenuScrolled && !board.MenuScrolling && scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(
            ["roulette", "settings", "menu-scroll-up"]),
            "The settled second page did not show exactly Roulette and the fixed navigation controls.");
        var roulette = scene.CurrentBoardButtons.Single(button => button.Id == "roulette");
        Check(Math.Abs(board.GetMenuScrollOffset(fixture.Now) - .60) < 1e-9 &&
            Math.Abs(roulette.Bounds.Y - .25) < 1e-9 && roulette.Bounds.Height == .16 &&
            board.GetMenuCards(fixture.Now).Where(button => button.Id != "roulette")
                .All(button => button.Bounds.Y + button.Bounds.Height <= BoardSession.MenuCardViewport.Y),
            "A full-page scroll left an original card in the viewport or misplaced Roulette's visible/input bounds.");
        scene.GetHandAcquisitionContext(fixture.Now);
        fixture.Now += TimeSpan.FromMilliseconds(600);
        fixture.Draw();
        var bottomReference = scene.GetHandAcquisitionContext(fixture.Now);
        Check(bottomReference?.ExpectedScene is not null && bottomReference.Revision != topReference!.Revision &&
            !ReferenceEquals(bottomReference.ExpectedScene, topReference.ExpectedScene),
            "The scrolled cards reused a stale camera reference.");
        fixture.Hold("menu-scroll-up");
        var returning = fixture.Now;
        fixture.Now = returning + BoardSession.MenuScrollDuration;
        byte[] returned = await Capture("menu-returned");
        Check(!board.MenuScrolled && !board.MenuScrolling &&
            scene.CurrentBoardButtons.Select(button => (button.Id, button.Bounds)).SequenceEqual(initialButtons) &&
            fixture.RegionUnchanged(initial, returned, BoardSession.MenuCardViewport),
            "The up-arrow caption hold did not restore all six initial cards, their positions and their native pixels.");
        Check(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Isolated menu-scroll verification changed live hardware or navigation.");
        return new { passed = true, directory, images, nativeArrowHolds = 2, actualChevronEvidence = true,
            intactChevronCancels = true, scrollDurationMilliseconds = BoardSession.MenuScrollDuration.TotalMilliseconds,
            fixedHeadingAndFooter = true, movingInputAndAcquisitionSuppressed = true,
            seventhCardRevealed = true, fullPageScroll = true, originalCardsFullyLeaveViewport = true,
            firstPageRestoredExactly = true, sharedVisibleAndInputBounds = true, settledReferenceRefreshed = true,
            liveHardwareUnchanged = true };

        async Task<byte[]> Capture(string name)
        {
            byte[] pixels = fixture.Draw();
            Check(pixels.AsSpan().SequenceEqual(fixture.Draw()), "The same menu clock changed pixels: " + name + ".");
            string path = Path.Combine(directory, name + ".png");
            await fixture.Target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            return pixels;
        }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }

    private sealed class RouletteNativeFixture : IDisposable
    {
        public readonly SceneCompositor Scene;
        public readonly CanvasRenderTarget Target;
        public readonly int Width, Height;
        public DateTimeOffset Now;
        private readonly double _inset;
        private readonly HandAcquisitionPresenceTracker _presence = new();
        private long _referenceRevision = -1;

        public RouletteNativeFixture(int width, int height, RouletteGame game)
        {
            Width = width; Height = height; Now = DateTimeOffset.UtcNow.AddMinutes(1);
            Scene = new SceneCompositor(blackjackClock: () => Now, roulette: game);
            Target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            Scene.SetDisplayAspect(width / (double)height);
            Scene.SetBoardSetup(true);
            _inset = Scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            Scene.SetBoardSetup(false);
        }

        public byte[] Draw()
        {
            using (var ds = Target.CreateDrawingSession()) Scene.Draw(ds, Width, Height, preview: false, runningSlowly: false);
            return Target.GetPixelBytes();
        }

        public void Hold(string id)
        {
            for (int frame = 0; frame < 5; frame++)
                Check(Feed(id, false).Count == 0, "Intact " + id + " lettering started a press.");
            var first = Now;
            for (int frame = 0; frame < 18; frame++)
            {
                var activated = Feed(id, true);
                if (activated.Count == 0) continue;
                Check(activated.SequenceEqual([id]) && Now - first >= BoardSession.HoldActivationInterval,
                    "A native caption hold activated early or chose the wrong control: " + id + ".");
                return;
            }
            throw new InvalidOperationException("Native broken lettering did not complete " + id + "'s hold.");
        }

        public void CheckPartialHoldCancels(string id)
        {
            for (int frame = 0; frame < 5; frame++) Check(Feed(id, false).Count == 0, "Intact letters activated " + id + ".");
            for (int frame = 0; frame < 4; frame++) Check(Feed(id, true).Count == 0, "A short obstruction activated " + id + ".");
            Check(Scene.CurrentHoldProgress.Any(item => item.ButtonId == id), "The native partial hold never started: " + id + ".");
            Check(Feed(id, false).Count == 0 && Scene.CurrentHoldProgress.All(item => item.ButtonId != id),
                "Intact caption lettering did not immediately cancel " + id + ".");
        }

        private IReadOnlyList<string> Feed(string id, bool broken)
        {
            Now += TimeSpan.FromMilliseconds(100);
            byte[] pixels = Draw();
            var context = Scene.GetHoldButtonContext(Now) ?? throw new InvalidOperationException("Missing native hold reference for " + id + ".");
            if (context.Revision != _referenceRevision) { _presence.Reset(); _referenceRevision = context.Revision; }
            int index = context.ButtonIds.ToList().IndexOf(id);
            Check(index >= 0 && Scene.CurrentBoardButtons.Single(button => button.Id == id) is { Enabled: true, Hold: BoardButtonHold.Once },
                "The native hold target is absent or disabled: " + id + ".");
            if (broken)
            {
                var trigger = context.ExpectedScene.BoardTriggerRegions![index];
                var center = CameraPoint(trigger.X + trigger.Width / 2, trigger.Y + trigger.Height / 2);
                var bounds = Scene.CurrentBoardButtons.Single(button => button.Id == id).Bounds;
                double span = Math.Min(Height * .078, Width * .93 * (1 - _inset) * bounds.Width * .80);
                int halfHeight = (int)Math.Min(Height * .034, Height * .93 * (1 - _inset) * bounds.Height * .35);
                for (int strip = 0; strip < 4; strip++)
                {
                    int left = (int)(center.X - span / 2 + strip * span / 4);
                    int right = (int)(center.X - span / 2 + (strip + .90) * span / 4);
                    for (int y = Math.Max(0, (int)center.Y - halfHeight); y < Math.Min(Height, (int)center.Y + halfHeight); y++)
                        for (int x = Math.Max(0, left); x < Math.Min(Width, right); x++)
                        {
                            int p = (y * Width + x) * 4;
                            pixels[p] = 75; pixels[p + 1] = 95; pixels[p + 2] = 185; pixels[p + 3] = 255;
                        }
                }
            }
            var presence = _presence.Update(Width, Height, Width * 4, pixels, context.SearchPolygon, context.ExpectedScene, Now, Now);
            Check(presence.Reason != "invalid-rendered-scene", "The roulette/menu control reference was rejected by native camera validation.");
            var held = context.HeldButtons(presence);
            if (held.Contains(id)) Check(broken && presence.TextPatterns!.Single(pattern => pattern.ControlRegion == index) is
                { ShapeCorrupted: true, LabelIntact: false, ConfirmationFrames: >= 1 }, "Unbroken lettering qualified as held: " + id + ".");
            return Scene.ObserveHoldButtons(context, held, Now, context.ClearedButtons(presence));
        }

        public object CheckProjectedRim(byte[] pixels)
        {
            var rim = Enumerable.Range(0, 128).Select(i =>
            {
                double angle = i * Math.Tau / 128;
                return LocalToCamera(SceneCompositor.ProjectRouletteWheelPoint(176 * Math.Cos(angle), 176 * Math.Sin(angle), 8));
            }).ToArray();
            double expectedWidth = rim.Max(p => p.X) - rim.Min(p => p.X);
            double expectedHeight = rim.Max(p => p.Y) - rim.Min(p => p.Y);
            Check(expectedWidth > expectedHeight * 1.06,
                "Roulette's fixed perspective camera no longer foreshortens the round world-space bowl.");
            double maximumProbeDistance = 0;
            foreach (int index in new[] { 0, 32, 64, 96 })
            {
                double nearest = double.PositiveInfinity;
                var point = rim[index];
                for (int y = Math.Max(0, (int)point.Y - 7); y <= Math.Min(Height - 1, (int)point.Y + 7); y++)
                for (int x = Math.Max(0, (int)point.X - 7); x <= Math.Min(Width - 1, (int)point.X + 7); x++)
                {
                    int p = (y * Width + x) * 4;
                    if (pixels[p + 2] > 45 && pixels[p + 1] > 20 && pixels[p + 2] > pixels[p + 1] * 1.05 &&
                        pixels[p + 1] > pixels[p] * 1.12)
                        nearest = Math.Min(nearest, Math.Sqrt(Math.Pow(x - point.X, 2) + Math.Pow(y - point.Y, 2)));
                }
                Check(nearest < 5, "Roulette's native rim does not follow its projected world-space circle.");
                maximumProbeDistance = Math.Max(maximumProbeDistance, nearest);
            }
            return new { expectedWidth, expectedHeight, projectedRatio = expectedWidth / expectedHeight,
                maximumProbeDistance, intentionallyForeshortenedWorldCircle = true };
        }

        public void CheckRotorPocketColors()
        {
            byte[] pixels = DrawMechanismPart("DrawRoulettePocketRotor", 0);
            foreach (int pocket in Enumerable.Range(0, RouletteGame.WheelOrder.Count))
            {
                double angle = pocket * Math.Tau / RouletteGame.WheelOrder.Count - Math.PI / 2;
                // Inside the recessed colored pocket, clear of the number band.
                var point = SceneCompositor.ProjectRouletteWheelPoint(126 * Math.Cos(angle), 126 * Math.Sin(angle), -10) * 2;
                int number = RouletteGame.WheelOrder[pocket], matching = 0;
                for (int y = Math.Max(0, (int)point.Y - 3); y <= Math.Min(799, (int)point.Y + 3); y++)
                for (int x = Math.Max(0, (int)point.X - 3); x <= Math.Min(799, (int)point.X + 3); x++)
                {
                    int p = (y * 800 + x) * 4;
                    byte blue = pixels[p], green = pixels[p + 1], red = pixels[p + 2];
                    if (pixels[p + 3] >= 230 && (number == 0 ? green > red * 1.15 : RouletteGame.IsRed(number)
                        ? red > green * 1.5 : red < 70 && green < 70 && blue < 70)) matching++;
                }
                Check(matching >= 3,
                    "The rendered rotor's pocket color/order differs from outcome " + number + ".");
            }
        }

        public object CheckMechanismParts(RouletteSnapshot game)
        {
            var first = SceneCompositor.GetRouletteMotion(game, game.RoundStartedAt.AddSeconds(1.05));
            var next = SceneCompositor.GetRouletteMotion(game, game.RoundStartedAt.AddSeconds(3.15));
            var previousClock = Now;
            Now = game.RoundStartedAt.AddSeconds(1.05);
            byte[] bowl = DrawMechanismPart("DrawRouletteFixedBowl");
            Now = game.RoundStartedAt.AddSeconds(3.15);
            byte[] nextBowl = DrawMechanismPart("DrawRouletteFixedBowl");
            Now = previousClock;
            Check(bowl.AsSpan().SequenceEqual(nextBowl),
                "Roulette's stationary bowl or lighting changes with the rotor clock.");
            byte[] ring = DrawMechanismPart("DrawRoulettePocketRotor", first.WheelAngle);
            byte[] nextRing = DrawMechanismPart("DrawRoulettePocketRotor", next.WheelAngle);
            byte[] spindle = DrawMechanismPart("DrawRouletteSpindle", first.WheelAngle);
            byte[] nextSpindle = DrawMechanismPart("DrawRouletteSpindle", next.WheelAngle);
            int changedRing = ChangedPixels(ring, nextRing), changedSpindle = ChangedPixels(spindle, nextSpindle);
            Check(changedRing > 500 && changedSpindle > 100,
                "Roulette's recessed ring and raised spindle do not move as independent projected geometry.");

            // The axisymmetric cap keeps its camera/light anchor while the raised
            // radial arms turn. A flattened, baked highlight would orbit the hub.
            var cap = SceneCompositor.ProjectRouletteWheelPoint(0, 0, 44) * 2;
            int opaqueCapPixels = 0;
            for (int y = (int)cap.Y - 3; y <= (int)cap.Y + 3; y++)
            for (int x = (int)cap.X - 3; x <= (int)cap.X + 3; x++)
            {
                int p = (y * 800 + x) * 4;
                Check(spindle.AsSpan(p, 4).SequenceEqual(nextSpindle.AsSpan(p, 4)),
                    "Roulette's fixed cap lighting rotates like a painted record.");
                if (spindle[p + 3] > 230) opaqueCapPixels++;
            }
            Check(opaqueCapPixels >= 9, "The projected spindle's raised cap is not visible at its world-space anchor.");
            return new { stationaryBowlAndLighting = true, changedRingPixels = changedRing,
                changedSpindlePixels = changedSpindle, fixedCapPixels = opaqueCapPixels,
                stationaryNativeRimAndWoodSamples = 16 };
        }

        public void CheckStationaryRimAndWood(byte[] a, byte[] b)
        {
            // Also prove the fixed material survives the complete production
            // composition; a correct isolated bowl must not be rotated upstream.
            // The cone carries faint rotor spokes, and the near bank projects
            // into moving number-band edge pixels. Probe visible fixed wood.
            foreach (var surface in new[] { (Radius: 176d, Height: 8d), (Radius: 170d, Height: 8d) })
            foreach (int index in Enumerable.Range(0, 8))
            {
                double angle = index * Math.Tau / 8;
                var point = LocalToCamera(SceneCompositor.ProjectRouletteWheelPoint(
                    surface.Radius * Math.Cos(angle), surface.Radius * Math.Sin(angle), surface.Height));
                for (int y = (int)point.Y - 1; y <= (int)point.Y + 1; y++)
                for (int x = (int)point.X - 1; x <= (int)point.X + 1; x++)
                {
                    int p = (y * Width + x) * 4;
                    Check(a.AsSpan(p, 4).SequenceEqual(b.AsSpan(p, 4)),
                        "Roulette's stationary rim/wood lighting moved in the final composition.");
                }
            }
        }

        public object CheckMaterialFinish()
        {
            const int size = 800;
            var field = typeof(SceneCompositor).GetField("_rouletteBurlBitmap", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var shaderField = typeof(SceneCompositor).GetField("_rouletteWoodShader", BindingFlags.Instance | BindingFlags.NonPublic)!;
            byte[] first = DrawCone(0), turned = DrawCone(1.15);
            Check(first.AsSpan().SequenceEqual(DrawCone(0)), "The same cone angle changed its native wood pixels.");
            var original = field.GetValue(Scene) as CanvasBitmap;
            Check(original is not null, "The cone finish did not load its original burl artwork.");
            object? shader = shaderField.GetValue(Scene);
            int changedInterior = 0, opaqueInterior = 0;
            for (int pixel = 0; pixel < size * size; pixel++)
            {
                int p = pixel * 4;
                Check(first[p + 3] == turned[p + 3], "Turning the cone grain changed the physical surface silhouette.");
                if (first[p + 3] != 255) continue;
                opaqueInterior++;
                if (!first.AsSpan(p, 3).SequenceEqual(turned.AsSpan(p, 3))) changedInterior++;
            }
            Check(opaqueInterior > 10000 && changedInterior > 1000,
                "The cone's actual wood grain did not turn visibly inside its fixed surface.");

            // Remove the photographed albedo as an independent lighting oracle.
            // Uniform material has no grain orientation: its projected normals,
            // shadows and room reflections must stay anchored to the camera.
            byte[] uniform = new byte[16 * 16 * 4];
            for (int p = 0; p < uniform.Length; p += 4)
            {
                uniform[p] = 60; uniform[p + 1] = 90; uniform[p + 2] = 130; uniform[p + 3] = 255;
            }
            using var reference = CanvasBitmap.CreateFromBytes(Target.Device, uniform, 16, 16,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
                Microsoft.Graphics.Canvas.CanvasAlphaMode.Premultiplied);
            int maximumLightingDelta = 0, changedLightingPixels = 0;
            int minimumLight = 255, maximumLight = 0;
            try
            {
                field.SetValue(Scene, reference);
                byte[] lit = DrawCone(0), turnedLit = DrawCone(1.15);
                for (int pixel = 0; pixel < size * size; pixel++)
                {
                    int p = pixel * 4;
                    Check(lit[p + 3] == first[p + 3] && turnedLit[p + 3] == first[p + 3],
                        "The reference material changed the cone's geometry or coverage.");
                    int delta = 0;
                    for (int channel = 0; channel < 3; channel++)
                        delta = Math.Max(delta, Math.Abs(lit[p + channel] - turnedLit[p + channel]));
                    maximumLightingDelta = Math.Max(maximumLightingDelta, delta);
                    if (delta > 0) changedLightingPixels++;
                    if (lit[p + 3] == 255)
                    {
                        minimumLight = Math.Min(minimumLight, lit[p + 2]);
                        maximumLight = Math.Max(maximumLight, lit[p + 2]);
                    }
                }
                Check(maximumLightingDelta <= 1,
                    "Uniform cone material reveals lighting that rotates with the wood instead of staying world-fixed.");
                Check(maximumLight - minimumLight > 20,
                    "The uniform-material lighting oracle is flat or invisible.");
            }
            finally
            {
                field.SetValue(Scene, original);
                // Rebind the production shader before disposing our temporary
                // bitmap, without disposing or replacing the owned source art.
                byte[] restored = DrawCone(0);
                Check(restored.AsSpan().SequenceEqual(first) && ReferenceEquals(field.GetValue(Scene), original) &&
                    ReferenceEquals(shaderField.GetValue(Scene), shader),
                    "The finish fixture failed to restore the original artwork and reusable shader.");
            }
            return new { nativeWidth = size, nativeHeight = size, opaqueInteriorPixels = opaqueInterior,
                changedWoodInteriorPixels = changedInterior, silhouetteAlphaIdentical = true,
                sameAnglePixelsIdentical = true, uniformLightingMaximumChannelDelta = maximumLightingDelta,
                uniformLightingChangedPixels = changedLightingPixels, uniformLightingRange = maximumLight - minimumLight,
                originalArtworkAndShaderRestored = true };

            byte[] DrawCone(double angle)
            {
                using var target = new CanvasRenderTarget(Target.Device, size, size, 96);
                using (var ds = target.CreateDrawingSession())
                {
                    ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    ds.Transform = Matrix3x2.CreateScale(2);
                    typeof(SceneCompositor).GetMethod("DrawRouletteBurlSurface", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(Scene, [ds, true, angle, false]);
                }
                return target.GetPixelBytes();
            }
        }

        private byte[] DrawMechanismPart(string name, double? angle = null)
        {
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 800, 800, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ds.Transform = Matrix3x2.CreateScale(2);
                var method = typeof(SceneCompositor).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!;
                method.Invoke(method.IsStatic ? null : Scene, angle is double rotation ? [ds, rotation] : [ds]);
            }
            return target.GetPixelBytes();
        }

        private static int ChangedPixels(byte[] a, byte[] b)
        {
            int changed = 0;
            for (int p = 0; p < a.Length; p += 4) if (!a.AsSpan(p, 4).SequenceEqual(b.AsSpan(p, 4))) changed++;
            return changed;
        }

        public void CheckRenderedBall(byte[] pixels, RouletteSnapshot game)
        {
            var motion = SceneCompositor.GetRouletteMotion(game, Now);
            var point = LocalToCamera(SceneCompositor.ProjectRouletteBall(motion));
            int ivory = 0;
            for (int y = Math.Max(0, (int)point.Y - 3); y <= Math.Min(Height - 1, (int)point.Y + 3); y++)
                for (int x = Math.Max(0, (int)point.X - 3); x <= Math.Min(Width - 1, (int)point.X + 3); x++)
                {
                    int p = (y * Width + x) * 4;
                    if (pixels[p + 2] > 190 && pixels[p + 1] > 170 && pixels[p] > 120) ivory++;
                }
            Check(ivory >= 3, "The rendered roulette ball did not occupy its committed winning pocket.");
        }

        public bool RegionUnchanged(byte[] a, byte[] b, BoardRect bounds)
        {
            var top = CameraPoint(bounds.X, bounds.Y);
            var bottom = CameraPoint(bounds.X + bounds.Width, bounds.Y + bounds.Height);
            for (int y = Math.Max(0, (int)Math.Ceiling(top.Y)); y < Math.Min(Height, (int)Math.Floor(bottom.Y)); y++)
                for (int x = Math.Max(0, (int)Math.Ceiling(top.X)); x < Math.Min(Width, (int)Math.Floor(bottom.X)); x++)
                    if (!a.AsSpan((y * Width + x) * 4, 4).SequenceEqual(b.AsSpan((y * Width + x) * 4, 4))) return false;
            return true;
        }

        private PixelPoint CameraPoint(double u, double v) => new(Width * (.035 + .93 * (_inset / 2 + u * (1 - _inset))),
            Height * (.035 + .93 * (_inset / 2 + v * (1 - _inset))));
        private PixelPoint LocalToCamera(Vector2 point)
        {
            double aspect = Width / (double)Height, fit = Math.Min(1, aspect * 1.22);
            return CameraPoint(.274 + (point.X - 200) * fit / aspect / 1000,
                .294 + (point.Y - 200) * fit / 1000);
        }
        public void Dispose() { Scene.Dispose(); Target.Dispose(); }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
