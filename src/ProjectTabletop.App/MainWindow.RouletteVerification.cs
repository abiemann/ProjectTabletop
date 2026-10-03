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

        // Prove the absolute-time visual endpoint independently for every pocket,
        // including zero and the discontinuity across the last/first angle.
        var sample = new RouletteGame(71).Snapshot;
        var motionStart = DateTimeOffset.UtcNow;
        foreach (int index in Enumerable.Range(0, RouletteGame.WheelOrder.Count))
        {
            var round = sample with { Phase = RoulettePhase.Spinning, RoundNumber = 1,
                RoundStartedAt = motionStart, Outcome = RouletteGame.WheelOrder[index], PocketIndex = index };
            var terminal = SceneCompositor.GetRouletteMotion(round, motionStart + round.SpinDuration);
            var approaching = SceneCompositor.GetRouletteMotion(round,
                motionStart + round.SpinDuration - TimeSpan.FromMilliseconds(1));
            double expected = index * Math.Tau / RouletteGame.WheelOrder.Count - Math.PI / 2;
            Check(Math.Abs(terminal.WheelAngle) < 1e-10 && terminal.BallRadius == 130 && terminal.Progress == 1 &&
                AngleError(terminal.BallAngle, expected) < 1e-10 &&
                AngleError(approaching.BallAngle, terminal.BallAngle) < .001 &&
                AngleError(approaching.WheelAngle, terminal.WheelAngle) < .001 &&
                Math.Abs(approaching.BallRadius - terminal.BallRadius) < .01,
                "Roulette's ball/wheel endpoint does not settle continuously in pocket " + index + ".");
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
            var circle = fixture.CheckCircularRim(idle);
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
            Check(ReferenceEquals(committed, scene.RouletteState) &&
                !flight.AsSpan().SequenceEqual(orbit), "Roulette motion either changed authoritative state or did not animate.");
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
            fixture.Hold("roulette-exit");
            nativeHolds++;
            Check(scene.CurrentBoardScreen == BoardScreen.Menu && scene.RouletteState == result,
                "Roulette's caption-held Exit lost the table state or failed to return to the menu.");
            aspects.Add(new { aspect.Name, aspect.Width, aspect.Height, circle, winningNumber,
                pocket = result.PocketIndex, result.LastWin, result.Balance });

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
            all37PocketEndpoints = true, continuousTerminalGeometry = true, actualNativeCaptionHolds = true,
            all37RenderedPocketColors = true,
            intactCaptionCancels = true, immutableCommittedRound = true, renderedBallMatchesOutcome = true,
            physicalWheelCircles = true, generatedArtworkLoaded = true, liveHardwareUnchanged = true };

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
        Check(Math.Abs(board.GetMenuScrollOffset(fixture.Now) - .10) < 1e-8 &&
            fixture.RegionUnchanged(initial, midpoint, new(.06, .035, .66, .19)) &&
            fixture.RegionUnchanged(initial, midpoint, new(.06, .89, .64, .08)) &&
            scene.GetHandAcquisitionContext(fixture.Now) is null,
            "Menu motion changed the fixed heading/footer or supplied an input reference.");
        fixture.Now = started + BoardSession.MenuScrollDuration;
        await Capture("menu-scrolled");
        Check(board.MenuScrolled && !board.MenuScrolling && scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(
            ["blackjack", "paint", "monopoly", "globe", "roulette", "settings", "menu-scroll-up"]),
            "The settled menu did not reveal Roulette and its up arrow.");
        var roulette = scene.CurrentBoardButtons.Single(button => button.Id == "roulette");
        Check(Math.Abs(roulette.Bounds.Y - .65) < 1e-9 && roulette.Bounds.Height == .16,
            "Roulette's visible card and input bounds do not share the scrolled position.");
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
        await Capture("menu-returned");
        Check(!board.MenuScrolled && !board.MenuScrolling &&
            scene.CurrentBoardButtons.Any(button => button.Id == "menu-scroll-down") &&
            scene.CurrentBoardButtons.All(button => button.Id != "roulette"),
            "The up-arrow caption hold did not restore the initial menu cards.");
        Check(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Isolated menu-scroll verification changed live hardware or navigation.");
        return new { passed = true, directory, images, nativeArrowHolds = 2, actualChevronEvidence = true,
            intactChevronCancels = true, scrollDurationMilliseconds = BoardSession.MenuScrollDuration.TotalMilliseconds,
            fixedHeadingAndFooter = true, movingInputAndAcquisitionSuppressed = true,
            seventhCardRevealed = true, sharedVisibleAndInputBounds = true, settledReferenceRefreshed = true,
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

        public object CheckCircularRim(byte[] pixels)
        {
            var center = CameraPoint(.274, .294);
            double fit = Math.Min(1, Width / (double)Height * 1.22);
            double expected = 176 * fit * Height / 1000 * .93 * (1 - _inset);
            int Find(int dx, int dy)
            {
                for (int distance = (int)Math.Ceiling(expected + 7); distance >= (int)Math.Floor(expected - 7); distance--)
                {
                    int x = (int)Math.Round(center.X) + dx * distance, y = (int)Math.Round(center.Y) + dy * distance;
                    if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                    int p = (y * Width + x) * 4;
                    if (pixels[p + 2] > 70 && pixels[p + 1] > 35 && pixels[p + 2] > pixels[p + 1] * 1.05 &&
                        pixels[p + 1] > pixels[p] * 1.15) return distance;
                }
                throw new InvalidOperationException("Roulette's actual gold rim was not found at a cardinal edge.");
            }
            int diameterX = Find(-1, 0) + Find(1, 0), diameterY = Find(0, -1) + Find(0, 1);
            Check(Math.Abs(diameterX - diameterY) <= Math.Max(3, expected * .025),
                "Roulette's wheel is oval on the physical board.");
            return new { diameterX, diameterY, expectedDiameter = 2 * expected, ratio = diameterX / (double)diameterY };
        }

        public void CheckRotorPocketColors()
        {
            var rotor = (CanvasRenderTarget?)typeof(SceneCompositor).GetField("_rouletteRotor",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Scene);
            Check(rotor is not null, "Roulette's authored native rotor did not render.");
            var pixels = rotor!.GetPixelBytes();
            int width = (int)rotor.SizeInPixels.Width;
            foreach (int pocket in Enumerable.Range(0, RouletteGame.WheelOrder.Count))
            {
                double angle = pocket * Math.Tau / RouletteGame.WheelOrder.Count - Math.PI / 2;
                // Inside each colored pocket, clear of numbers and separator pins.
                int x = (int)Math.Round(400 + Math.Cos(angle) * 244);
                int y = (int)Math.Round(400 + Math.Sin(angle) * 244);
                int p = (y * width + x) * 4, number = RouletteGame.WheelOrder[pocket];
                byte blue = pixels[p], green = pixels[p + 1], red = pixels[p + 2];
                Check(pixels[p + 3] >= 250 && (number == 0 ? green > red * 1.3 : RouletteGame.IsRed(number)
                    ? red > green * 1.8 : red < 50 && green < 50 && blue < 50),
                    "The rendered rotor's pocket color/order differs from outcome " + number + ".");
            }
        }

        public void CheckRenderedBall(byte[] pixels, RouletteSnapshot game)
        {
            var motion = SceneCompositor.GetRouletteMotion(game, Now);
            double aspect = Width / (double)Height, fit = Math.Min(1, aspect * 1.22);
            var point = CameraPoint(.274 + Math.Cos(motion.BallAngle) * motion.BallRadius * fit / aspect / 1000,
                .294 + Math.Sin(motion.BallAngle) * motion.BallRadius * fit / 1000);
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
        public void Dispose() { Scene.Dispose(); Target.Dispose(); }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
