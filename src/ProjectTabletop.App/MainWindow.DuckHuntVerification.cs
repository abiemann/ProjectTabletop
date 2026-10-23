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
    // Isolated projected scenes only. This never opens the camera, microphone,
    // output window or a second app instance, and never changes the live board.
    private async Task<object> VerifyDuckHuntAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var results = new List<object>();
        int assertions = 0;
        foreach (var dimensions in new[]
        {
            (Name: "landscape", Width: 1920, Height: 1080),
            (Name: "square", Width: 1000, Height: 1000),
            (Name: "portrait", Width: 1080, Height: 1920)
        })
        {
            var now = MonotonicClock.UtcNow;
            using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), dimensions.Width, dimensions.Height, 96);
            var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
            var game = (DuckHuntGame)typeof(BoardSession).GetField("_duckHunt",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
            scene.SetDisplayAspect(dimensions.Width / (double)dimensions.Height);
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(dimensions.Width, 0), new(dimensions.Width, dimensions.Height), new(0, dimensions.Height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            // Calibration resets input with the real clock. Begin the isolated
            // navigation timeline after it, rather than replaying its older start.
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.ShowBoardMenu();
            Check(board.ActivateButton("menu-scroll-down", now), "Could not open the second menu page.");
            now += BoardSession.MenuScrollDuration;
            Check(board.TickMenu(now), "The second menu page did not settle.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt", now), "The menu could not open Duck Hunt.");
            Draw();
            await Save("ready");
            Check(scene.CurrentBoardScreen == BoardScreen.DuckHunt && !scene.HandDetectionRequired,
                "Duck Hunt is not active or still requests the hand model.");
            Check(!board.ControlSwipesEnabled && board.Buttons.Count == 1 && board.Buttons[0] is
                { Id: "duck-hunt-drawer-open", Hold: BoardButtonHold.Once },
                "Duck Hunt did not start with only its shared drawer arrow.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-drawer-open", now), "The Duck Hunt drawer did not open.");
            now += TimeSpan.FromMilliseconds(300);
            board.TickDuckHunt(now);
            Draw();
            Check(board.Buttons.Select(button => button.Id).SequenceEqual(["duck-hunt-drawer-close", "duck-hunt-exit"]) &&
                board.Buttons.All(button => button.Enabled),
                "The settled drawer did not contain only its arrow and EXIT.");
            now += TimeSpan.FromMilliseconds(1);
            Check(!board.ActivateButton("duck-hunt-mic", now) && !board.ActivateButton("duck-hunt-microphone-0", now),
                "A removed projected microphone control could still be activated.");
            scene.SetDuckHuntAudioStatus(true, "Microphone ready");
            Draw();
            Check(scene.DuckHuntState.Phase == DuckHuntPhase.Walking && !board.DuckHuntDrawerOpen &&
                board.Buttons.Select(button => button.Id).SequenceEqual(["duck-hunt-drawer-open"]),
                "The first round did not close an already open drawer when its input became ready.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-drawer-open", now), "The active-round drawer did not reopen.");
            now += TimeSpan.FromMilliseconds(300);
            board.TickDuckHunt(now);
            byte[] initial = Draw();
            await Save("controls");
            var context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No Duck Hunt hold reference.");
            Check(context.ButtonIds.SequenceEqual(["duck-hunt-drawer-close", "duck-hunt-exit"]) &&
                context.EnabledHoldIds.Contains("duck-hunt-exit"), "The hold reference does not contain the enabled drawer controls.");
            var caption = context.ExpectedScene.BoardTriggerRegions![context.ButtonIds.ToList().IndexOf("duck-hunt-exit")];
            var exit = board.Buttons.Single(button => button.Id == "duck-hunt-exit").Bounds;
            Check(exit.Contains(caption.X, caption.Y) && exit.Contains(caption.X + caption.Width, caption.Y + caption.Height) &&
                caption.Width < exit.Width * .8 && caption.Height < exit.Height * .8,
                "EXIT hold evidence uses the button rectangle instead of its letter shapes.");
            var acquisition = scene.GetHandAcquisitionContext(now);
            Check(acquisition is { AllowsSearchIllumination: false } &&
                scene.ActiveHandSpotlightCount == 0, "Duck Hunt exposes hand search lighting.");
            Check(scene.DuckHuntArtworkReady, "The photographic duck artwork was not loaded.");
            var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No calibrated measurement input.");
            var oldAim = new BoardAim(.2, .43);
            var newAim = new BoardAim(.32, .50);
            VerifyLiveDotDoesNotDraw("before the first click");
            var oldTime = MonotonicClock.UtcNow;
            scene.PublishDuckHuntDot(oldAim, oldTime, input.Epoch);
            Check(scene.DuckHuntMeasurement.LiveDetectedXY == oldAim, "Continuous red-dot coordinates were not published.");
            var onset = MonotonicClock.UtcNow;
            while (onset <= oldTime) onset = MonotonicClock.UtcNow;
            var frameTime = MonotonicClock.UtcNow;
            while (frameTime <= onset) frameTime = MonotonicClock.UtcNow;
            Check(scene.SubmitDuckHuntSnapshot(newAim, frameTime, onset, MonotonicClock.UtcNow, input.Epoch),
                "The click's current coordinates were rejected.");
            var measurement = scene.DuckHuntMeasurement;
            Check(measurement.HasCompletedPair && measurement.OldDetectedXY == oldAim && measurement.NewDetectedXY == newAim,
                "The completed measurement did not preserve the before/after positions.");
            byte[] measured = Draw();
            await Save("measurement");
            Check(!initial.AsSpan().SequenceEqual(measured), "The measurement overlay did not appear.");
            Check(scene.DuckHuntState is { Phase: DuckHuntPhase.Walking, Hits: 0 },
                "The ready walking round did not start, or an off-target measurement scored a hit.");
            VerifyMeasurementPixels();
            var measurementContext = scene.GetHoldButtonContext(now)!;
            Check(ReferenceEquals(context.ExpectedScene, measurementContext.ExpectedScene) && context.Revision == measurementContext.Revision,
                "The dot/circle rebuilt the stationary EXIT caption reference.");
            scene.PublishDuckHuntDot(new(.8, .65), MonotonicClock.UtcNow, input.Epoch);
            Check(scene.DuckHuntMeasurement.OldDetectedXY == oldAim && scene.DuckHuntMeasurement.NewDetectedXY == newAim,
                "Live movement moved the completed circle.");
            var walkingBefore = scene.DuckHuntState;
            now += TimeSpan.FromSeconds(2);
            board.TickDuckHunt(now);
            Draw();
            Check(scene.DuckHuntState.Phase == DuckHuntPhase.Walking &&
                scene.DuckHuntState.U != walkingBefore.U && scene.DuckHuntState.V == walkingBefore.V &&
                scene.DuckHuntMeasurement.OldDetectedXY == oldAim && scene.DuckHuntMeasurement.NewDetectedXY == newAim,
                "Walking changed the duck's height or moved the completed shot mark.");
            Check(scene.GetHandAcquisitionContext(now) is { AllowsSearchIllumination: false } && scene.ActiveHandSpotlightCount == 0,
                "Coordinate measurement enabled a hand spotlight.");
            scene.ClearDuckHuntInput();
            Check(!scene.DuckHuntMeasurement.HasCompletedPair && scene.DuckHuntMeasurement.LiveDetectedXY is null,
                "Reset retained a measurement or stale live dot.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-drawer-close", now) && !board.DuckHuntDrawerOpen,
                "The winning-round fixture could not close its drawer before the result.");

            // A deterministic round exercises the actual renderer at several gait
            // and turning phases, plus both result messages and the replay hold.
            game.Reset(now - TimeSpan.FromSeconds(11));
            Check(game.Start(now - TimeSpan.FromSeconds(11), scene.GetBoardResolutionDiagnostics().BoardPixelWidth /
                (double)scene.GetBoardResolutionDiagnostics().BoardPixelHeight, enterFromLeft: true),
                "The isolated walking round did not start.");
            Draw();
            await VerifyFootAnimation();
            Draw();
            await Save("walk-right");
            CheckClearOptics("right-facing walk");
            now += TimeSpan.FromMilliseconds(180);
            Draw();
            await Save("walk-right-next-step");
            CheckClearOptics("next foot phase");
            var hitAt = now + TimeSpan.FromMilliseconds(1);
            board.TickDuckHunt(hitAt);
            var hitPoint = new BoardAim(board.DuckHuntState.U, board.DuckHuntState.V);
            Check(board.TryDuckHuntShot(hitPoint, hitAt, dimensions.Width / (double)dimensions.Height),
                "The visible duck's body did not accept a hit.");
            int feathersPerHit = board.DuckHuntFeathers.Count;
            Check(feathersPerHit > 0 && board.DuckHuntFeathers.IsAnimating,
                "A hit did not start a visible feather burst.");
            var firstFeather = board.DuckHuntFeathers.GetPose(0);
            now = hitAt + TimeSpan.FromMilliseconds(90);
            Draw();
            await Save("turn-start");
            CheckClearOptics("early turn");
            now = hitAt + TimeSpan.FromMilliseconds(190);
            Draw();
            await Save("turn-end");
            CheckClearOptics("late turn");
            now = hitAt + DuckHuntGame.TurnDuration + TimeSpan.FromMilliseconds(100);
            Draw();
            await Save("walk-left");
            Check(scene.DuckHuntState is { Phase: DuckHuntPhase.Walking, Direction: -1, Hits: 1 },
                "A hit did not reverse the duck after its turn.");
            CheckClearOptics("left-facing walk");
            Check(board.DuckHuntFeathers.Count == feathersPerHit && board.DuckHuntFeathers.IsAnimating &&
                board.DuckHuntFeathers.GetPose(0) != firstFeather,
                "The first feather burst did not keep moving after the duck turned.");
            CheckFeathersVisible();
            await Save("feathers-early-fall");
            CheckClearOptics("airborne feathers");
            for (int hit = 2; hit <= 10; hit++)
            {
                now += DuckHuntGame.TurnDuration + TimeSpan.FromMilliseconds(100);
                board.TickDuckHunt(now);
                Check(board.TryDuckHuntShot(new(board.DuckHuntState.U, board.DuckHuntState.V), now,
                    dimensions.Width / (double)dimensions.Height), "A valid follow-up hit failed.");
            }
            byte[] winningPixels = Draw();
            await Save("player-wins");
            Check(scene.DuckHuntState is { Phase: DuckHuntPhase.PlayerWon, Hits: 10 } && board.DuckHuntDrawerOpen &&
                board.Buttons.Any(button => button.Id == "duck-hunt-replay" && button.Hold == BoardButtonHold.Once &&
                    !button.Enabled),
                "The tenth hit did not automatically open the drawer with a replay hold waiting for its caption to settle.");
            CheckClearOptics("player win");
            Check(board.DuckHuntFeathers.Count == feathersPerHit * 10 && board.DuckHuntFeathers.IsAnimating,
                "The winning hit lost a feather burst or stopped its flight.");
            long winningRevision = scene.DuckHuntState.Revision;
            now += TimeSpan.FromMilliseconds(500);
            byte[] fallingPixels = Draw();
            await Save("feathers-after-player-win");
            Check(scene.DuckHuntState is { Phase: DuckHuntPhase.PlayerWon, Hits: 10 } &&
                scene.DuckHuntState.Revision > winningRevision && !winningPixels.AsSpan().SequenceEqual(fallingPixels),
                "The winning scene cache froze the falling feathers.");
            VerifyReplayCaption();
            CheckClearOptics("feathers after player win");
            now += TimeSpan.FromSeconds(4);
            Draw();
            await Save("feathers-settled-player-win");
            Check(board.DuckHuntFeathers.Count == feathersPerHit * 10 && !board.DuckHuntFeathers.IsAnimating &&
                Enumerable.Range(0, board.DuckHuntFeathers.Count).All(index => board.DuckHuntFeathers.GetPose(index).Settled),
                "Settled feathers disappeared or kept animating after the player won.");
            CheckFeathersVisible();
            CheckClearOptics("settled player-win feathers");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-drawer-close", now), "The result drawer could not be closed manually.");
            now += TimeSpan.FromMilliseconds(500);
            Draw();
            await Save("player-win-controls-hidden");
            Check(!board.DuckHuntDrawerOpen &&
                board.Buttons.Select(button => button.Id).SequenceEqual(["duck-hunt-drawer-open"]) &&
                scene.GetHoldButtonContext(now) is { } closedResultContext &&
                !closedResultContext.ButtonIds.Contains("duck-hunt-replay") &&
                !board.ActivateButton("duck-hunt-replay", now),
                "A manually closed result drawer reopened itself or retained an exposed PLAY AGAIN control.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-drawer-open", now), "The result drawer could not be reopened.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            VerifyReplayCaption();
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-replay", now), "PLAY AGAIN did not start a fresh round.");
            Draw();
            Check(scene.DuckHuntState is { Hits: 0, Shots: 0, Phase: DuckHuntPhase.Walking } &&
                !scene.DuckHuntMeasurement.HasCompletedPair && board.DuckHuntFeathers.Count == 0 &&
                !board.DuckHuntFeathers.IsAnimating && !board.DuckHuntDrawerOpen &&
                board.Buttons.Select(button => button.Id).SequenceEqual(["duck-hunt-drawer-open"]),
                "Replay retained an old score, shot mark, feather or open drawer.");
            VerifyLiveDotDoesNotDraw("after replay");
            now += TimeSpan.FromSeconds(11);
            Draw();
            Check(board.TryDuckHuntShot(new(board.DuckHuntState.U, board.DuckHuntState.V), now,
                dimensions.Width / (double)dimensions.Height), "The escape fixture could not create a feather burst.");
            now += TimeSpan.FromSeconds(24);
            Draw();
            await Save("duck-wins");
            Check(scene.DuckHuntState is { Phase: DuckHuntPhase.DuckWon, DuckVisible: false } && board.DuckHuntDrawerOpen &&
                board.Buttons.Any(button => button.Id == "duck-hunt-replay"),
                "A complete crossing did not automatically open the duck winner's replay drawer.");
            CheckClearOptics("duck win");
            Check(board.DuckHuntFeathers.Count == feathersPerHit && !board.DuckHuntFeathers.IsAnimating &&
                Enumerable.Range(0, board.DuckHuntFeathers.Count).All(index => board.DuckHuntFeathers.GetPose(index).Settled),
                "The duck's escape removed its settled feathers.");
            CheckFeathersVisible();
            await Save("feathers-settled-duck-win");
            CheckClearOptics("settled duck-win feathers");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            VerifyReplayCaption();
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-replay", now), "The duck-win replay did not respond.");
            Draw();
            Check(board.DuckHuntFeathers.Count == 0 && !board.DuckHuntDrawerOpen &&
                board.Buttons.Select(button => button.Id).SequenceEqual(["duck-hunt-drawer-open"]),
                "Replay after a duck win retained feathers or left its drawer open.");
            now += TimeSpan.FromMilliseconds(1);
            Check(board.ActivateButton("duck-hunt-drawer-open", now), "The EXIT fixture could not reopen the round's drawer.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();

            // Feed the actual rendered pixels through native caption recognition.
            // A clean caption must never press; separated occlusion strips break
            // the lettering and must complete the ordinary shared one-second hold.
            var presence = new HandAcquisitionPresenceTracker();
            DateTimeOffset firstHeld = default;
            for (int frame = 0; frame < 6; frame++)
                Check(Feed(false).Count == 0, "The intact EXIT caption activated.");
            bool exited = false;
            for (int frame = 0; frame < 20; frame++)
            {
                var activated = Feed(true);
                if (activated.Count == 0) continue;
                Check(activated.SequenceEqual(["duck-hunt-exit"]) && firstHeld != default &&
                    now - firstHeld >= BoardSession.HoldActivationInterval, "EXIT activated without a full caption hold.");
                exited = true;
                break;
            }
            Check(exited && scene.CurrentBoardScreen == BoardScreen.Menu && board.MenuScrolled,
                "Native EXIT lettering did not restore the second menu page.");
            results.Add(new { dimensions.Name, dimensions.Width, dimensions.Height, Caption = caption,
                Hits = scene.DuckHuntState.Hits, NativeExit = exited, ExitHoldMilliseconds = (now - firstHeld).TotalMilliseconds });

            void VerifyReplayCaption()
            {
                var replayContext = scene.GetHoldButtonContext(now) ??
                    throw new InvalidOperationException("The settled replay drawer has no caption reference.");
                Check(replayContext.ButtonIds.SequenceEqual(["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-replay"]) &&
                    replayContext.EnabledHoldIds.Contains("duck-hunt-replay"),
                    "The settled result drawer does not expose its arrow, EXIT and PLAY AGAIN holds together.");
                var replayCaption = replayContext.ExpectedScene.BoardTriggerRegions![
                    replayContext.ButtonIds.ToList().IndexOf("duck-hunt-replay")];
                var replay = board.Buttons.Single(button => button.Id == "duck-hunt-replay").Bounds;
                Check(replay.Y == exit.Y && replay.Height == exit.Height &&
                    replay.Contains(replayCaption.X, replayCaption.Y) &&
                    replay.Contains(replayCaption.X + replayCaption.Width, replayCaption.Y + replayCaption.Height) &&
                    replayCaption.Width < replay.Width * .9 && replayCaption.Height < replay.Height * .8,
                    "PLAY AGAIN's caption is outside the drawer row or uses the whole button as hold evidence.");
            }

            void CheckClearOptics(string phase)
            {
                var inputContext = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No optical input mapping.");
                var detected = new RedDotDetector().Detect(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                    target.GetPixelBytes(), inputContext.CameraToBoard);
                Check(!detected.Detected && detected.CandidateCount == 0,
                    $"The {dimensions.Name} {phase} artwork generated a false laser candidate.");
            }

            void CheckFeathersVisible()
            {
                using var layer = new CanvasRenderTarget(target.Device, dimensions.Width, dimensions.Height, 96);
                using (var drawing = layer.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.White);
                    drawing.Transform = Matrix3x2.CreateScale(dimensions.Width / 1000f, dimensions.Height / 1000f);
                    scene.DrawDuckHuntFeathers(drawing, dimensions.Width / (double)dimensions.Height);
                }
                byte[] pixels = layer.GetPixelBytes();
                int visible = 0;
                for (int offset = 0; offset < pixels.Length; offset += 4)
                    if (pixels[offset] < 245 || pixels[offset + 1] < 245 || pixels[offset + 2] < 245) visible++;
                Check(visible > 20, "The feather state has particles but its GPU layer is blank.");
            }

            async Task VerifyFootAnimation()
            {
                using var feet = new CanvasRenderTarget(target.Device, dimensions.Width, dimensions.Height, 96);
                var pose = scene.DuckHuntState with { U = .5, V = .48, Direction = -1, Facing = -1, WalkPhase = 0 };
                byte[]? first = null;
                foreach (double phase in new[] { 0d, Math.PI / 2, Math.PI, 3 * Math.PI / 2 })
                {
                    using (var drawing = feet.CreateDrawingSession())
                    {
                        drawing.Clear(Microsoft.UI.Colors.White);
                        drawing.Transform = Matrix3x2.CreateScale(dimensions.Width / 1000f, dimensions.Height / 1000f);
                        scene.DrawDuckHuntWalkingMallard(drawing, pose with { WalkPhase = phase },
                            dimensions.Width / (double)dimensions.Height);
                    }
                    byte[] pixels = feet.GetPixelBytes();
                    string path = Path.Combine(directory, dimensions.Name + "-feet-" + (int)Math.Round(phase * 100) + ".png");
                    await feet.SaveAsync(path, CanvasBitmapFileFormat.Png);
                    images.Add(path);
                    if (first is null) { first = pixels; continue; }
                    var bounds = DuckHuntLayout.PhotoBounds(pose, dimensions.Width / (double)dimensions.Height);
                    int bodyRows = Math.Clamp((int)((bounds.Y + bounds.Height * .66) * dimensions.Height), 0, dimensions.Height);
                    Check(first.AsSpan(0, bodyRows * dimensions.Width * 4).SequenceEqual(
                        pixels.AsSpan(0, bodyRows * dimensions.Width * 4)), "A foot phase moved the duck's upper body.");
                    Check(!first.AsSpan().SequenceEqual(pixels), "The foot cycle did not change the lower-leg pixels.");
                }
            }

            void VerifyLiveDotDoesNotDraw(string stage)
            {
                var liveInput = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No live-dot input context.");
                var resolution = scene.GetBoardResolutionDiagnostics();
                using var overlay = new CanvasRenderTarget(target.Device, resolution.BoardPixelWidth, resolution.BoardPixelHeight, 96);
                using (var drawing = overlay.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.White);
                    drawing.Transform = Matrix3x2.CreateScale(resolution.BoardPixelWidth / 1000f, resolution.BoardPixelHeight / 1000f);
                    scene.PublishDuckHuntDot(oldAim, MonotonicClock.UtcNow, liveInput.Epoch);
                    var live = scene.DuckHuntMeasurement;
                    Check(live.LiveDetectedXY == oldAim && live.OldDetectedXY is null && live.NewDetectedXY is null,
                        $"The {stage} fixture did not retain a live aim without a shot.");
                    scene.DrawDuckHuntMeasurement(drawing);
                }
                Check(overlay.GetPixelBytes().AsSpan().IndexOfAnyExcept((byte)255) < 0,
                    $"Live aim drew a projected mark {stage}, before a click supplied a valid shot.");
            }

            void VerifyMeasurementPixels()
            {
                // Use the board's GPU overlay method without artwork underneath,
                // so a duck feather cannot masquerade as circle coverage.
                var resolution = scene.GetBoardResolutionDiagnostics();
                int pixelWidth = resolution.BoardPixelWidth, pixelHeight = resolution.BoardPixelHeight;
                using var overlay = new CanvasRenderTarget(target.Device, pixelWidth, pixelHeight, 96);
                using (var drawing = overlay.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.White);
                    drawing.Transform = Matrix3x2.CreateScale(pixelWidth / 1000f, pixelHeight / 1000f);
                    scene.DrawDuckHuntMeasurement(drawing);
                }
                byte[] pixels = overlay.GetPixelBytes();
                double cx = oldAim.U * pixelWidth, cy = oldAim.V * pixelHeight;
                double radius = Math.Sqrt(Math.Pow((newAim.U - oldAim.U) * pixelWidth, 2) +
                    Math.Pow((newAim.V - oldAim.V) * pixelHeight, 2));
                Check(BlackAt(cx, cy) && BlackAt(cx + 8, cy) && !BlackAt(cx + 12, cy),
                    "The old-position dot is not approximately 20 projected pixels across.");
                Check(BlackAt(newAim.U * pixelWidth, newAim.V * pixelHeight),
                    "The circle does not pass through the new position.");
                foreach (var direction in new[] { new Vector2(1, 0), new Vector2(0, 1), new Vector2(-1, 0), new Vector2(0, -1) })
                {
                    double x = cx + direction.X * radius, y = cy + direction.Y * radius;
                    if (x < 5 || x >= pixelWidth - 5 || y < 5 || y >= pixelHeight - 5) continue;
                    Check(BlackAt(x, y) && !BlackAt(x + direction.X * 5, y + direction.Y * 5),
                        "Circle radius or five-pixel stroke changed with board orientation.");
                }
                bool BlackAt(double x, double y)
                {
                    int px = Math.Clamp((int)Math.Round(x), 0, pixelWidth - 1);
                    int py = Math.Clamp((int)Math.Round(y), 0, pixelHeight - 1);
                    int offset = (py * pixelWidth + px) * 4;
                    return pixels[offset] < 90 && pixels[offset + 1] < 90 && pixels[offset + 2] < 90;
                }
            }
            byte[] Draw()
            {
                using (var drawing = target.CreateDrawingSession())
                    scene.Draw(drawing, dimensions.Width, dimensions.Height, preview: false, runningSlowly: false);
                return target.GetPixelBytes();
            }
            async Task Save(string name)
            {
                string path = Path.Combine(directory, dimensions.Name + "-" + name + ".png");
                await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
            }
            IReadOnlyList<string> Feed(bool broken)
            {
                now += TimeSpan.FromMilliseconds(100);
                byte[] pixels = Draw();
                var reference = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("EXIT reference disappeared during its hold.");
                if (broken) BreakCaption(pixels, reference.ExpectedScene.CameraToBoard, caption);
                var observed = presence.Update(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                    pixels, reference.SearchPolygon, reference.ExpectedScene, now, now);
                Check(observed.Reason != "invalid-rendered-scene", "The native caption detector rejected Duck Hunt's reference.");
                var held = reference.HeldButtons(observed);
                Check(broken || held.Count == 0, "Intact EXIT lettering supplied hold evidence.");
                // The engine starts on this first evidence frame with progress 0.
                // HoldProgress intentionally hides zero, so its first visible rim
                // would start the fixture's clock one camera frame too late.
                if (held.Contains("duck-hunt-exit") && firstHeld == default) firstHeld = now;
                return scene.ObserveHoldButtons(reference, held, now, reference.ClearedButtons(observed));
            }
            void BreakCaption(byte[] pixels, IReadOnlyList<double> matrix, HandTrackingBounds bounds)
            {
                // Camera-space pixels are mapped through the exact fixture homography,
                // so the same four narrow finger bands work in every board orientation.
                for (int y = 0; y < dimensions.Height; y++)
                for (int x = 0; x < dimensions.Width; x++)
                {
                    double d = matrix[6] * x + matrix[7] * y + matrix[8];
                    double u = (matrix[0] * x + matrix[1] * y + matrix[2]) / d;
                    double v = (matrix[3] * x + matrix[4] * y + matrix[5]) / d;
                    double across = (u - bounds.X) / bounds.Width;
                    if (across is < 0 or > 1 || v < bounds.Y - .004 || v > bounds.Y + bounds.Height + .004 ||
                        across * 4 - Math.Floor(across * 4) > .76) continue;
                    int p = (y * dimensions.Width + x) * 4;
                    pixels[p] = 95; pixels[p + 1] = 135; pixels[p + 2] = 175; pixels[p + 3] = 255;
                }
            }
        }
        return new { Ok = true, Assertions = assertions, Results = results, Images = images, Directory = directory };

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private async Task<object> VerifyDuckHuntInputAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntInputVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const int width = 1280, height = 960;
        int assertions = 0;
        var uiNow = MonotonicClock.UtcNow;
        using var scene = new SceneCompositor(blackjackClock: () => uiNow);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.06f, .05f), new(.96f, .09f), new(.91f, .95f), new(.09f, .90f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHunt();
        var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        var game = (DuckHuntGame)typeof(BoardSession).GetField("_duckHunt",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
        scene.SetDuckHuntAudioStatus(true, "Microphone ready");
        game.Reset(uiNow - TimeSpan.FromSeconds(11));
        Check(game.Start(uiNow - TimeSpan.FromSeconds(11), width / (double)height, enterFromLeft: false),
            "The optical fixture could not place a walking duck in the centre.");
        byte[] clear;
        using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, width, height, false, false);
        clear = target.GetPixelBytes();
        string clearPath = Path.Combine(directory, "clear-board.png");
        await target.SaveAsync(clearPath, CanvasBitmapFileFormat.Png);
        var context = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Calibrated Duck Hunt has no input context.");
        var surface = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        var projected = surface.Transform(new Point2(.5, .48));
        double cameraX = projected.X * width, cameraY = projected.Y * height;
        var m = context.CameraToBoard;
        double divisor = m[6] * cameraX + m[7] * cameraY + m[8];
        Check(Math.Abs((m[0] * cameraX + m[1] * cameraY + m[2]) / divisor - .5) < .000001 &&
            Math.Abs((m[3] * cameraX + m[4] * cameraY + m[5]) / divisor - .48) < .000001,
            "The input context does not invert the rendered duck's calibrated position.");
        var detector = new RedDotDetector();
        var empty = detector.Detect(width, height, width * 4, clear, context.CameraToBoard);
        Check(!empty.Detected && empty.CandidateCount == 0,
            "The rendered duck, title or intact EXIT control was mistaken for a red dot.");
        byte[] dotted = (byte[])clear.Clone();
        for (int y = Math.Max(0, (int)cameraY - 5); y <= Math.Min(height - 1, (int)cameraY + 5); y++)
        for (int x = Math.Max(0, (int)cameraX - 5); x <= Math.Min(width - 1, (int)cameraX + 5); x++)
        {
            if ((x - cameraX) * (x - cameraX) + (y - cameraY) * (y - cameraY) > 16) continue;
            int p = (y * width + x) * 4;
            dotted[p] = 20; dotted[p + 1] = 25; dotted[p + 2] = 255; dotted[p + 3] = 255;
        }
        var detection = detector.Detect(width, height, width * 4, dotted, context.CameraToBoard);
        Check(detection.Detected && Math.Abs(detection.BoardX!.Value - .5) < .002 &&
            Math.Abs(detection.BoardY!.Value - .48) < .002, "The synthetic camera dot missed the calibrated duck centre.");
        var aim = new BoardAim(detection.BoardX!.Value, detection.BoardY!.Value);
        scene.ShowDuckHunt();
        scene.SetDuckHuntAudioStatus(true, "Microphone ready");
        DateTimeOffset latest = MonotonicClock.UtcNow;

        context = Reset();
        Check(!Snapshot(aim, context.Epoch - 1) && scene.DuckHuntState.Shots == 0,
            "An obsolete input epoch supplied a shot.");

        context = Reset();
        Check(!scene.SubmitDuckHuntSnapshot(aim, context.NotBefore.AddTicks(-1), Fresh(), Fresh(), context.Epoch),
            "Pre-reset camera pixels supplied a shot.");

        context = Reset();
        var onset = Fresh();
        Check(!scene.SubmitDuckHuntSnapshot(aim, Fresh().AddSeconds(1), onset, Fresh(), context.Epoch),
            "A future camera observation supplied a shot.");

        context = Reset();
        var precedingFrame = Fresh();
        Check(!scene.SubmitDuckHuntSnapshot(aim, precedingFrame, Fresh(), Fresh(), context.Epoch),
            "A snapshot from before the click onset supplied a shot.");

        context = Reset();
        onset = Fresh();
        Check(!scene.SubmitDuckHuntSnapshot(null, Fresh(), onset, Fresh(), context.Epoch),
            "A snapshot without an unambiguous dot supplied a shot.");
        Check(!scene.SubmitDuckHuntSnapshot(aim, Fresh(), onset, Fresh(), context.Epoch),
            "A missing-dot decision was retried using a later dot.");

        context = Reset();
        scene.SetDuckHuntAudioStatus(false, "Microphone unavailable");
        Check(!Snapshot(aim, context.Epoch) && scene.DuckHuntState.Shots == 0,
            "A disabled microphone supplied a shot.");
        scene.SetDuckHuntAudioStatus(true, "Microphone ready");

        context = Reset();
        scene.PublishDuckHuntDot(aim, Fresh(), context.Epoch);
        long visibleRevision = scene.DuckHuntInputVisualRevision;
        var expiredOnset = Fresh();
        await Task.Delay(DuckHuntAimGate.Freshness + TimeSpan.FromMilliseconds(35));
        Check(scene.DuckHuntInputVisualRevision > visibleRevision,
            "The dot-visible hint stayed cached after its frame expired.");
        Check(!scene.SubmitDuckHuntSnapshot(aim, Fresh(), expiredOnset, Fresh(), context.Epoch) &&
            scene.DuckHuntState.Shots == 0, "An expired click used a newly visible dot.");

        context = Reset();
        var beforeLearning = context;
        var beforeLearningClick = Fresh();
        long beforeLearningHint = scene.DuckHuntInputVisualRevision;
        scene.SetDuckHuntLearningActive(true);
        Check(scene.DuckHuntBoardReady && !scene.DuckHuntInputReady && scene.GetDuckHuntInputContext() is null,
            "Click learning did not pause shooting while keeping the calibrated board available.");
        Check(scene.DuckHuntInputVisualRevision > beforeLearningHint,
            "Starting click learning did not refresh its input state.");
        Check(!Snapshot(aim, beforeLearning.Epoch) && scene.DuckHuntState.Shots == 0,
            "Click learning accepted a gameplay snapshot or a new training sound.");
        scene.SetDuckHuntLearningActive(false);
        context = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Input did not resume after click learning.");
        Check(scene.DuckHuntBoardReady && context.Epoch != beforeLearning.Epoch,
            "Ending click learning reused its previous camera interval.");
        Check(!Snapshot(aim, beforeLearning.Epoch) &&
            !scene.SubmitDuckHuntSnapshot(aim, Fresh(), beforeLearningClick, Fresh(), context.Epoch) &&
            scene.DuckHuntState.Shots == 0, "Ending click learning reused old snapshot work or audio.");

        context = Reset();
        var oldContext = context;
        var beforeResetClick = Fresh();
        context = Reset();
        Check(!Snapshot(aim, oldContext.Epoch) &&
            !scene.SubmitDuckHuntSnapshot(aim, Fresh(), beforeResetClick, Fresh(), context.Epoch) &&
            scene.DuckHuntState.Shots == 0 && context.Epoch != oldContext.Epoch,
            "Resetting camera input retained in-flight snapshot work or audio.");

        scene.ShowBoardMenu();
        Check(scene.GetDuckHuntInputContext() is null, "Duck Hunt input stayed active on the menu.");
        scene.ShowDuckHunt();
        context = scene.GetDuckHuntInputContext()!;
        Check(!Snapshot(aim, oldContext.Epoch) && scene.DuckHuntState.Shots == 0,
            "A previous visit's snapshot activated the reopened board.");
        scene.SetDuckHuntAudioStatus(true, "Microphone ready");
        context = Reset();
        // A prior continuous hint can be absent or wrong without vetoing the current image.
        scene.PublishDuckHuntDot(null, Fresh(), context.Epoch);
        onset = Fresh();
        await Task.Delay(60);
        var confirmedAt = Fresh();
        Check(scene.SubmitDuckHuntSnapshot(aim, confirmedAt.AddMilliseconds(-10), onset, confirmedAt, context.Epoch) &&
            scene.DuckHuntState is { Hits: 0, Shots: 1, Phase: DuckHuntPhase.Walking } &&
            scene.DuckHuntMeasurement.NewDetectedXY == aim && !scene.DuckHuntMeasurement.HasCompletedPair,
            "A snapshot without prior coordinates failed to count its miss or invented a circle.");
        Check(!scene.SubmitDuckHuntSnapshot(aim, Fresh(), onset, Fresh(), context.Epoch),
            "The same click was accepted again with newer pixels.");
        Snapshot(aim, context.Epoch);
        Check(scene.DuckHuntState is { Hits: 0, Shots: 2, Phase: DuckHuntPhase.Walking },
            "A fresh off-target click did not count once in the walking round.");
        var replacementOld = new BoardAim(.62, .40);
        var replacementNew = new BoardAim(.72, .45);
        scene.PublishDuckHuntDot(replacementOld, Fresh(), context.Epoch);
        Check(Snapshot(replacementNew, context.Epoch) && scene.DuckHuntMeasurement is
            { HasCompletedPair: true } replacement && replacement.OldDetectedXY == replacementOld &&
            replacement.NewDetectedXY == replacementNew,
            "A later valid shot did not replace the previous coordinate display.");
        var retainedPair = scene.DuckHuntMeasurement;
        Check(!Snapshot(null, context.Epoch) && scene.DuckHuntMeasurement.OldDetectedXY == retainedPair.OldDetectedXY &&
            scene.DuckHuntMeasurement.NewDetectedXY == retainedPair.NewDetectedXY,
            "A no-dot click changed the latest valid shot mark.");

        // Opening the EXIT drawer must not change the camera/audio epoch or
        // pause the round now that microphone selection lives in the main app.
        uiNow = Fresh();
        scene.ShowDuckHunt();
        context = Reset();
        oldContext = context;
        uiNow = Fresh();
        Check(scene.ActivateDuckHuntAt(.10, .93), "The input fixture could not open the drawer.");
        uiNow += TimeSpan.FromMilliseconds(300);
        board.TickDuckHunt(uiNow);
        uiNow += TimeSpan.FromMilliseconds(1);
        Check(!scene.ActivateDuckHuntAt(.44, .93), "The removed MIC button still handled a board press.");
        Check(scene.DuckHuntBoardReady && scene.DuckHuntInputReady &&
            scene.GetDuckHuntInputContext()?.Epoch == oldContext.Epoch,
            "Opening the EXIT drawer interrupted the calibrated shooting interval.");
        // The drawer fixture advances a presentation clock. Let actual camera
        // time catch up before checking snapshot acceptance.
        if (uiNow > MonotonicClock.UtcNow)
            await Task.Delay(uiNow - MonotonicClock.UtcNow + TimeSpan.FromMilliseconds(2));
        uiNow = Fresh();
        Check(Snapshot(aim, context.Epoch) && scene.DuckHuntState is { Hits: 0, Shots: 1, Phase: DuckHuntPhase.Walking },
            "A fresh snapshot was blocked while the EXIT drawer was open.");
        Check(scene.ActivateDuckHuntAt(.10, .93) && scene.GetDuckHuntInputContext()?.Epoch == context.Epoch,
            "Closing the EXIT drawer interrupted shooting input.");

        // A laptop microphone change clears old work before capture restarts.
        var beforeMicrophoneChange = context;
        var beforeMicrophoneClick = Fresh();
        scene.SetDuckHuntAudioStatus(false, "Starting microphone…");
        scene.ClearDuckHuntInput();
        Check(scene.DuckHuntBoardReady && !Snapshot(aim, beforeMicrophoneChange.Epoch),
            "Changing the laptop microphone accepted a shot before capture restarted.");
        scene.SetDuckHuntAudioStatus(true, "Microphone ready");
        context = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Microphone restart did not restore input.");
        Check(context.Epoch != beforeMicrophoneChange.Epoch &&
            !Snapshot(aim, beforeMicrophoneChange.Epoch) &&
            !scene.SubmitDuckHuntSnapshot(aim, Fresh(), beforeMicrophoneClick, Fresh(), context.Epoch),
            "A microphone change reused a snapshot or click from the old input.");
        uiNow = Fresh();
        Check(Snapshot(aim, context.Epoch) && scene.DuckHuntState.Shots == 2,
            "A fresh shot did not recover after the laptop microphone changed.");
        return new { Ok = true, Assertions = assertions, Detection = detection, ClickLearningPauseVerified = true,
            ClickSnapshotVerified = true, WalkingMeasurementVerified = true, NoDotCandidates = empty.CandidateCount,
            Images = new[] { clearPath }, Directory = directory };

        bool Snapshot(BoardAim? point, long epoch)
        {
            var click = Fresh();
            var frame = Fresh();
            return scene.SubmitDuckHuntSnapshot(point, frame, click, Fresh(), epoch);
        }
        SceneCompositor.DuckHuntInputContext Reset()
        {
            scene.ClearDuckHuntInput();
            var reset = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Input context did not return after reset.");
            if (reset.NotBefore > latest) latest = reset.NotBefore;
            return reset;
        }
        DateTimeOffset Fresh()
        {
            DateTimeOffset time;
            do { time = MonotonicClock.UtcNow; } while (time <= latest);
            latest = time;
            return time;
        }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
