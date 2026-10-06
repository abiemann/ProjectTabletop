#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private games, clocks and GPU targets exercise the entrance without
    // changing the user's camera, projector, save or current board.
    private async Task<object> VerifyMonopolyEntranceAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision);
        const int width = 3840, height = 2160;
        const int lead = 320, stagger = 75, tileDuration = 540, centerStart = 3925, duration = 4975;
        var now = DateTimeOffset.UtcNow.AddMinutes(10);
        string directory = Path.Combine(_appDataDirectory, "MonopolyEntranceVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var samples = new List<object>();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var preview = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 960, 960, 96);
        using var scene = await FixtureAsync();
        var started = now;
        var state = scene.MonopolyState;
        Require(scene.MonopolyEntranceActive && scene.GetMonopolyEntranceDiagnostics().DurationMilliseconds == duration,
            "Opening Monopoly did not start its declared entrance.");

        byte[] first = Draw(scene);
        var board = Field<CanvasRenderTarget>(scene, "_boardApplicationTarget");
        int rasterWidth = (int)board.SizeInPixels.Width, rasterHeight = (int)board.SizeInPixels.Height;
        var baseLayer = Field<CanvasRenderTarget>(scene, "_monopolyEntranceBaseTarget");
        // An independent verifier-only composite provides the final deed
        // pixels. Production retains only individually animated parcel images.
        using var atlas = new CanvasRenderTarget(board.Device, rasterWidth, rasterHeight, 96);
        using (var drawing = atlas.CreateDrawingSession())
        {
            drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            drawing.Transform = Matrix3x2.CreateScale(rasterWidth / 1000f, rasterHeight / 1000f);
            var drawSpace = typeof(SceneCompositor).GetMethod("DrawMonopolySpace", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var space in MonopolyGame.Spaces)
                drawSpace.Invoke(scene, [drawing, space, state, scene.MonopolyPreviewAspect, 1f]);
        }
        var lid = Field<CanvasRenderTarget>(scene, "_monopolyEntranceLidTarget");
        var basePixels = baseLayer.GetPixelBytes();
        var atlasPixels = atlas.GetPixelBytes();
        var lidPixels = lid.GetPixelBytes();
        var atlasKey = Field<object>(scene, "_monopolyEntranceTileState");
        var lidKey = Field<object>(scene, "_monopolyEntranceLidState");
        var firstBoard = board.GetPixelBytes();
        var parcelMasks = NativeParcelMasks();
        var fixedCenterMask = Enumerable.Range(0, lidPixels.Length / 4)
            .Where(pixel => lidPixels[pixel * 4 + 3] >= 254).Select(pixel => pixel * 4).ToArray();
        Require(fixedCenterMask.Length > 100, "The anchored plaza has no opaque stationary contents.");
        byte[] initialPixels;
        using (var initial = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), rasterWidth, rasterHeight, 96))
        {
            using (var drawing = initial.CreateDrawingSession())
            {
                drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = Matrix3x2.CreateScale(rasterWidth / 1000f, rasterHeight / 1000f);
                // Compare the same logical-to-native sampling path. An identity
                // bitmap blit rounds translucent cutout edges differently.
                var logical = new Rect(0, 0, 1000, 1000);
                var source = new Rect(0, 0, rasterWidth, rasterHeight);
                drawing.DrawImage(baseLayer, logical, source, 1, CanvasImageInterpolation.Linear);
                drawing.DrawImage(lid, logical, source, 1, CanvasImageInterpolation.Linear);
            }
            initialPixels = initial.GetPixelBytes();
        }
        Require(rasterWidth >= width * .8 && rasterHeight >= height * .8 &&
                new[] { baseLayer, atlas, lid }.All(layer => layer.SizeInPixels == board.SizeInPixels),
            "The entrance layers lost the projector's native sampling density.");
        Require(MeanError(firstBoard, initialPixels, rasterWidth, rasterHeight, new(0, 0, 1000, 1000)) < .1,
            "The first entrance frame differs from the stationary city and plaza contents.");
        for (int index = 0; index < 40; index++)
            Require(ParcelError(firstBoard, initialPixels, index) < .1,
                $"Parcel {index} is visible before the opening delivery.");
        Require(AlphaFraction(atlasPixels, rasterWidth, rasterHeight, new(400, 400, 200, 200)) == 0 &&
                AlphaFraction(lidPixels, rasterWidth, rasterHeight, new(60, 60, 90, 90)) == 0,
            "The native atlas or center lid includes opaque pixels outside its own pieces.");
        await Capture("00-stationary-city-and-plaza");

        now = started.AddMilliseconds(-200);
        Require(Frame(scene) is { ElapsedMilliseconds: 0, LandedTiles: 0, CenterProgress: 0 } &&
                first.SequenceEqual(Draw(scene)), "A future entrance start produced negative progress or premature pieces.");

        now = started.AddMilliseconds(lead - 1);
        Draw(scene);
        Require(firstBoard.SequenceEqual(board.GetPixelBytes()) && Frame(scene).LandedTiles == 0,
            "A parcel moved during the initial city pause.");
        now = started.AddMilliseconds(lead + 50);
        var goFrame = Frame(scene);
        var goPose = SceneCompositor.MonopolyEntranceTilePose(goFrame, 0);
        var goPixels = Draw(scene);
        Require(goPose.Visible && !goPose.Landed && goPose.Elevation > 100 && goPose.Scale > 1 &&
                Enumerable.Range(1, 39).All(index => !SceneCompositor.MonopolyEntranceTilePose(goFrame, index).Visible) &&
                Differences(first, goPixels) > 1000,
            "GO did not arrive alone from above before the other perimeter pieces.");
        await Capture("01-go-first");

        // Samples at every settlement boundary establish the 40-piece order.
        // Airborne tiles may cross future destinations, so pixel equality is
        // checked on unobscured delivered pieces and again on the full perimeter.
        for (int index = 0; index < 40; index++)
        {
            int milliseconds = lead + index * stagger + tileDuration;
            now = started.AddMilliseconds(milliseconds);
            var frame = Frame(scene);
            Require(frame.Active && frame.LandedTiles == index + 1 && frame.CenterProgress == 0 &&
                    Enumerable.Range(0, 40).All(tile => SceneCompositor.MonopolyEntranceTilePose(frame, tile).Landed == (tile <= index)),
                $"The clockwise delivery skipped, reordered or prematurely completed tile {index}.");
            var settled = SceneCompositor.MonopolyEntranceTilePose(frame, index);
            Require(settled is { Landed: true, Progress: 1, Elevation: 0, Scale: 1, RotationRadians: 0 },
                "A landed tile retains elevation, scaling or tilt.");
            var pixels = Draw(scene);
            Require(OutsideCornersBlack(pixels, width, height), "A falling Monopoly piece escaped the physical board clip.");
            Require(CenterError(board.GetPixelBytes()) < .1,
                "An airborne parcel changed the anchored center lettering or controls.");
            if (index is 0 or 9 or 19 or 29 or 39)
            {
                double error = ParcelError(board.GetPixelBytes(), atlasPixels, index);
                Require(error < 3, $"Delivered tile {index} does not show its native final artwork (RGB error {error:F3}).");
                await Capture($"tile-{index:00}-landed");
            }
            CheckSuppression(scene);
            CheckCaches();
            samples.Add(new { milliseconds, frame.LandedTiles, frame.CenterProgress, frame.Active });
        }

        now = started.AddMilliseconds(centerStart - 1);
        Draw(scene);
        var perimeter = board.GetPixelBytes();
        Require(Frame(scene) is { LandedTiles: 40, CenterProgress: 0 } &&
                SceneCompositor.MonopolyEntranceCenterPose(Frame(scene)) is
                    { Visible: true, Landed: true, Elevation: 0, Scale: 1, RotationRadians: 0 } &&
                CenterError(perimeter) < .1,
            "The plaza contents moved while the oval parcels assembled.");
        for (int index = 0; index < 40; index++)
            Require(ParcelError(perimeter, atlasPixels, index) < 3 &&
                    ParcelError(perimeter, basePixels, index) > 1,
                $"The complete clockwise perimeter is missing its real artwork at tile {index}.");
        await Capture("02-oval-parcels-complete");
        CheckPreview(scene);

        now = started.AddMilliseconds(centerStart + 525);
        var middle = Frame(scene);
        var center = SceneCompositor.MonopolyEntranceCenterPose(middle);
        Require(middle.LandedTiles == 40 && middle.CenterProgress is > .49 and < .51 &&
                center is { Visible: true, Landed: true, Elevation: 0, Scale: 1, RotationRadians: 0 },
            "The fixed plaza became a falling rectangular panel.");
        Draw(scene);
        Require(perimeter.SequenceEqual(board.GetPixelBytes()),
            "The finished parcel assembly changed during its input-settling interval.");
        CheckSuppression(scene);
        CheckPreview(scene);
        CheckCaches();
        await Capture("03-stationary-settling-interval");

        now = started.AddMilliseconds(duration - 1);
        Draw(scene);
        Require(scene.MonopolyEntranceActive && !scene.ActivateMonopolyButton("mp-start-game") &&
                !scene.ActivateMonopolyButton("mp-exit"), "The final moving contact frame accepted input early.");
        now = started.AddMilliseconds(duration);
        Draw(scene);
        Require(!scene.MonopolyEntranceActive && ReferenceEquals(state, scene.MonopolyState),
            "Completing the entrance altered rules state or missed its exact deadline.");
        using (var baseline = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), rasterWidth, rasterHeight, 96))
        {
            using (var drawing = baseline.CreateDrawingSession())
            {
                drawing.Transform = Matrix3x2.CreateScale(rasterWidth / 1000f, rasterHeight / 1000f);
                typeof(SceneCompositor).GetMethod("DrawMonopolyBoard", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(scene, [drawing, scene.MonopolyState, scene.CurrentBoardButtons, Array.Empty<string>(),
                        Array.Empty<BoardFingerSelectionFeedback>(), scene.MonopolyPreviewAspect, false, false, false, 1f, null]);
            }
            Require(board.GetPixelBytes().SequenceEqual(baseline.GetPixelBytes()),
                "The final entrance frame differs from the independent unanimated native board.");
        }
        CheckPreview(scene);
        var completedForeground = board.GetPixelBytes();
        now = started.AddMilliseconds(duration + 700);
        Draw(scene);
        Require(completedForeground.SequenceEqual(board.GetPixelBytes()),
            "The finished entrance replayed or changed its landed foreground.");
        await Capture("04-exact-complete-board");
        Require(scene.ActivateMonopolyButton("mp-start-game"), "The completed entrance did not release fresh pointer input.");

        await CheckAiHold();
        await CheckCancellation();
        await CheckRescan();
        await CheckArtworkDelay();
        await CheckMenuGesture();
        await CheckInputBarrier();
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision),
            "The isolated entrance verification changed the user's live hardware or game.");
        string samplePath = Path.Combine(directory, "entrance-samples.json");
        await File.WriteAllTextAsync(samplePath, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        return new { passed = true, durationMilliseconds = duration, nativeWidth = rasterWidth, nativeHeight = rasterHeight,
            cityAndPlazaStationaryInitially = true, goThenFortyClockwiseParcels = true, centerContentsRemainAnchored = true,
            exactStaticFinalPixels = true, projectorPreviewSameClockAndPixels = true, nativeLayersReused = true,
            inputAiAndLightingBlocked = true, staleInputConsumed = true, navigationAndRescanCancellation = true,
            liveHardwareUnchanged = true, directory, samplePath, images };

        async Task<SceneCompositor> FixtureAsync(MonopolyGame? game = null, bool showMonopoly = true)
        {
            var fixture = new SceneCompositor(monopoly: game ?? new MonopolyGame(seed: 113),
                blackjackClock: () => now, monopolyClock: () => now, boardRevealClock: () => now);
            await fixture.EnsureCrownDeedResourcesAsync(CanvasDevice.GetSharedDevice());
            if (!showMonopoly)
                await fixture.EnsureMenuPreviewResourcesAsync(CanvasDevice.GetSharedDevice());
            fixture.SetDisplayAspect(width / (double)height);
            fixture.SetBoardSetup(true);
            fixture.SetDetectedBoardGrid(Corners(), CameraMap());
            fixture.SetBoardSetup(false);
            if (showMonopoly) fixture.ShowMonopoly();
            return fixture;
        }
        static Vector2[] Corners() => [new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)];
        static Homography CameraMap() => Homography.FromFourPoints(
            [new(0, 0), new(width, 0), new(width, height), new(0, height)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        byte[] Draw(SceneCompositor fixture)
        {
            using (var drawing = target.CreateDrawingSession()) fixture.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        void CheckCaches()
        {
            Require(ReferenceEquals(baseLayer, Field<CanvasRenderTarget>(scene, "_monopolyEntranceBaseTarget")) &&
                    ReferenceEquals(lid, Field<CanvasRenderTarget>(scene, "_monopolyEntranceLidTarget")) &&
                    Equals(atlasKey, Field<object>(scene, "_monopolyEntranceTileState")) &&
                    Equals(lidKey, Field<object>(scene, "_monopolyEntranceLidState")),
                "Clock advancement recreated or repainted static native entrance layers.");
        }
        void CheckPreview(SceneCompositor fixture)
        {
            var before = Field<CanvasRenderTarget>(fixture, "_boardApplicationTarget").GetPixelBytes();
            var frame = FrameOrCompleted(fixture);
            using (var drawing = preview.CreateDrawingSession()) fixture.DrawMonopolyPreview(drawing, 960, 960);
            var previewLayer = Field<CanvasRenderTarget>(fixture, "_monopolyPreviewTarget");
            Require(before.SequenceEqual(previewLayer.GetPixelBytes()) && frame == FrameOrCompleted(fixture),
                "Projector and laptop preview sampled different entrance times or artwork.");
            var firstPreview = preview.GetPixelBytes();
            using (var drawing = preview.CreateDrawingSession()) fixture.DrawMonopolyPreview(drawing, 960, 960);
            Require(firstPreview.SequenceEqual(preview.GetPixelBytes()) && ReferenceEquals(previewLayer,
                    Field<CanvasRenderTarget>(fixture, "_monopolyPreviewTarget")) &&
                    BlackRect(firstPreview, 960, 0, 0, 960, 200) && BlackRect(firstPreview, 960, 0, 760, 960, 960),
                "A repeated preview draw advanced the entrance, lowered its resolution or leaked into letterboxing.");
            Require(before.SequenceEqual(Field<CanvasRenderTarget>(fixture, "_boardApplicationTarget").GetPixelBytes()),
                "Drawing a smaller laptop preview changed the projector's native cache.");
        }
        void CheckSuppression(SceneCompositor fixture)
        {
            Require(!fixture.ActivateMonopolyButton("mp-start-game") && !fixture.ActivateMonopolyButton("mp-exit") &&
                    !fixture.TickMonopoly(now), "The entrance allowed gameplay, navigation input or AI progress.");
            var context = fixture.GetHandAcquisitionContext(now);
            Require(context is { ObserveMotion: false, IlluminatedHint: null, ExpectedScene: null },
                "The moving board supplied acquisition evidence or a white search light.");
            fixture.CompleteHandAcquisition(context, [new(new(50, 50, 100, 100), new(100, 100), 40,
                now, .10, .10, .10)], [], now);
            fixture.SetHandSpotlights([Hand()], now);
            Require(fixture.GetHandAcquisitionContext(now)?.IlluminatedHint is null && fixture.ActiveHandSpotlightCount == 0,
                "A qualified asynchronous acquisition or tracked hand illuminated the falling board.");
        }
        async Task CheckAiHold()
        {
            var game = new MonopolyGame(seed: 117, initialRolls: [new(1, 2), new(2, 3)]);
            var gameTime = now.AddSeconds(-10);
            foreach (string id in new[] { "mp-start-game", "mp-start", "mp-roll", "mp-buy", "mp-end-turn" })
                Require(game.HandleAction(id, gameTime += TimeSpan.FromMilliseconds(20)), "AI entrance setup rejected " + id + ".");
            Require(game.Snapshot.ActivePlayer is { IsAi: true }, "AI entrance fixture did not reach the AI turn.");
            using var ai = await FixtureAsync(game);
            var aiStarted = now;
            long revision = game.Revision;
            now = aiStarted.AddMilliseconds(duration - 1);
            Draw(ai);
            Require(!ai.TickMonopoly(now) && game.Revision == revision && ai.GetMonopolyDiceFrames(now).Count == 0,
                "An AI turn progressed or rolled while the board was still assembling.");
            now = aiStarted.AddMilliseconds(duration);
            Require(ai.TickMonopoly(now) && game.Revision == revision + 1 && ai.GetMonopolyDiceFrames(now).Count == 2,
                "Completing the entrance did not release the already-due AI roll exactly once.");
        }
        async Task CheckCancellation()
        {
            foreach (string cause in new[] { "menu", "paint", "black", "camera", "clip" })
            {
                using var canceled = await FixtureAsync();
                now += TimeSpan.FromMilliseconds(600); Draw(canceled);
                Require(canceled.MonopolyEntranceActive, "Cancellation fixture did not have an active entrance.");
                switch (cause)
                {
                    case "menu": canceled.ShowBoardMenu(); break;
                    case "paint": canceled.ShowPaint(); break;
                    case "black": canceled.SetBlackOutput(true); break;
                    case "camera": canceled.ClearHandTips(); break;
                    case "clip": canceled.ClearBoardMediaClip(); break;
                }
                Require(!canceled.MonopolyEntranceActive, cause + " did not cancel Monopoly delivery.");
                now += TimeSpan.FromMilliseconds(duration + 100); Draw(canceled);
                Require(!canceled.MonopolyEntranceActive, cause + " revived an interrupted entrance later.");
                if (cause is "black" or "clip")
                    Require(BlackRect(target.GetPixelBytes(), width, 0, 0, width, height), cause + " left nonblack projector output.");
            }
            using var reopened = await FixtureAsync();
            var before = reopened.GetMonopolyEntranceDiagnostics().Revision;
            now += TimeSpan.FromMilliseconds(600); reopened.ShowBoardMenu(); now += TimeSpan.FromMilliseconds(20);
            reopened.ShowMonopoly(); Draw(reopened);
            Require(reopened.MonopolyEntranceActive && Frame(reopened).ElapsedMilliseconds == 0 &&
                    reopened.GetMonopolyEntranceDiagnostics().Revision > before,
                "Explicitly opening Monopoly again reused the interrupted delivery clock.");
        }
        async Task CheckRescan()
        {
            using var scanned = await FixtureAsync();
            now += TimeSpan.FromMilliseconds(600); scanned.SetBoardSetup(true);
            Require(!scanned.MonopolyEntranceActive, "Rescanning retained the old Monopoly entrance.");
            scanned.ShowBoardCalibrationSpot(SceneCompositor.BoardCalibrationSpotCount - 1);
            scanned.CompleteBoardSetup(Corners(), CameraMap());
            var revealStarted = now;
            // The corner flair plays before the reveal proper.
            var reveal = scanned.GetBoardRevealDiagnostics();
            double revealTotal = reveal.FlairMilliseconds + reveal.DurationMilliseconds;
            now = revealStarted.AddMilliseconds(revealTotal - 1); Draw(scanned);
            Require(scanned.BoardRevealActive && Frame(scanned) is { ElapsedMilliseconds: 0, LandedTiles: 0, CenterProgress: 0 } &&
                    scanned.GetMonopolyEntranceDiagnostics().StartedAt == revealStarted.AddMilliseconds(revealTotal),
                "Monopoly pieces progressed before the calibration reveal finished.");
            now = revealStarted.AddMilliseconds(revealTotal); Draw(scanned);
            Require(!scanned.BoardRevealActive && scanned.MonopolyEntranceActive && Frame(scanned).ElapsedMilliseconds == 0,
                "Completing the calibration reveal failed to begin a fresh Monopoly entrance.");
        }
        async Task CheckArtworkDelay()
        {
            using var pending = new SceneCompositor(monopoly: new MonopolyGame(319),
                blackjackClock: () => now, monopolyClock: () => now, boardRevealClock: () => now);
            pending.ShowMonopoly();
            Require(pending.GetMonopolyEntranceDiagnostics() is { Active: true, StartedAt: null, LandedTiles: 0 },
                "An entrance began before its artwork was ready.");
            now += TimeSpan.FromSeconds(12);
            Require(pending.GetMonopolyEntranceFrame(now) is { ElapsedMilliseconds: 0, LandedTiles: 0, Active: true },
                "Time spent awaiting artwork advanced the pending entrance.");
            await pending.EnsureCrownDeedResourcesAsync(CanvasDevice.GetSharedDevice());
            var readyAt = now;
            Require(pending.GetMonopolyEntranceFrame(now) is { ElapsedMilliseconds: 0, LandedTiles: 0, Active: true } &&
                pending.GetMonopolyEntranceDiagnostics().StartedAt == readyAt,
                "Loaded artwork skipped the entrance's initial city pause.");
            now += TimeSpan.FromMilliseconds(duration);
            Require(!pending.MonopolyEntranceActive,
                "An artwork-delayed entrance did not retain its declared animation duration.");
        }
        async Task CheckMenuGesture()
        {
            now = MonotonicClock.UtcNow;
            using var menu = await FixtureAsync(showMonopoly: false);
            var button = menu.CurrentBoardButtons.Single(item => item.Id == "monopoly");
            var map = Field<Homography>(menu, "_boardSurfaceMap");
            var camera = Field<Homography>(menu, "_boardCameraMap");
            var aim = camera.InverseTransform(map.Transform(new(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2)));
            var grouped = new HandCursor(new(aim.X - 25, aim.Y), DateTimeOffset.MinValue)
            {
                TrackingId = 83001, HasFourExtendedFingers = true, FingersTogether = true,
                FingerTips = [new(aim.X - 25, aim.Y), new(aim.X, aim.Y), new(aim.X + 25, aim.Y), new(aim.X + 50, aim.Y)]
            };
            var separated = grouped with { FingersTogether = false, IndexFingerSeparated = true };
            foreach (var (hand, delay) in new[] { (grouped, 5), (grouped, 110), (separated, 20), (separated, 90) })
            {
                await Task.Delay(delay);
                now = MonotonicClock.UtcNow;
                menu.SetHandCursors([hand], now);
            }
            Require(menu.CurrentBoardScreen == BoardScreen.Monopoly && menu.MonopolyEntranceActive &&
                    Frame(menu).ElapsedMilliseconds == 0 && Frame(menu).LandedTiles == 0,
                "The projected menu's sideways-index selection bypassed Monopoly entrance startup.");
            await Task.Delay(5); now = MonotonicClock.UtcNow;
            menu.SetHandCursors([separated], now);
            Require(menu.MonopolyState.Phase == MonopolyPhase.Landing && menu.HoveredBoardButtons.Count == 0,
                "The menu selection carried its held separated pose into the moving Monopoly board.");
        }
        async Task CheckInputBarrier()
        {
            // The camera-facing compositor uses monotonic freshness. Prove its
            // active guard with a real timestamp, then use the private session's
            // coherent synthetic clock to check exact post-deadline source times.
            now = DateTimeOffset.UtcNow.AddMinutes(10);
            using var input = await FixtureAsync();
            var inputStarted = now;
            await Task.Delay(5);
            var button = input.CurrentBoardButtons.Single(item => item.Id == "mp-start-game");
            var map = Field<Homography>(input, "_boardSurfaceMap");
            var camera = Field<Homography>(input, "_boardCameraMap");
            var point = camera.InverseTransform(map.Transform(new(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2)));
            var frame = MonotonicClock.UtcNow;
            var held = new HandCursor(new(point.X, point.Y), frame.AddSeconds(1), 81001) { TrackingId = 81001 };
            input.SetHandCursors([held], frame);
            Require(input.MonopolyState.Phase == MonopolyPhase.Landing && input.HoveredBoardButtons.Count == 0,
                "A camera pulse selected or hovered during the entrance.");
            now = inputStarted.AddMilliseconds(duration); Draw(input);
            var session = Field<BoardSession>(input, "_boardSession");
            double u = button.Bounds.X + button.Bounds.Width / 2, v = button.Bounds.Y + button.Bounds.Height / 2;
            var delayed = new BoardHandSample(u, v, DateTimeOffset.MinValue, 0);
            now = inputStarted.AddMilliseconds(duration + 1);
            Require(session.Update([delayed], inputStarted.AddMilliseconds(duration - 10), now) is null &&
                    session.HoveredButtonIds.Count == 0,
                "A delayed camera frame from the falling board armed or hovered a landed control.");
            // This pulse is still alive for another300ms, so rejection must be
            // caused by its transition origin, not its expiry or clock mismatch.
            var pulse = new BoardHandSample(u, v, inputStarted.AddMilliseconds(duration + 300), 81002);
            now += TimeSpan.FromMilliseconds(1);
            Require(session.Update([pulse], now, now) is null, "A still-live transition pulse selected after landing.");
            now += TimeSpan.FromMilliseconds(1);
            var anchored = new BoardHandSample(u, v, now.AddSeconds(1), 81003, inputStarted.AddMilliseconds(duration - 20));
            Require(session.Update([anchored], now, now) is null, "A transition pointing anchor survived the final landing barrier.");
            var grouped = new BoardHandSample(u, v, DateTimeOffset.MinValue, 0)
            {
                TrackingId = 84001, FingerAim = new(u, v), FourFingersExtended = true, FingersTogether = true
            };
            var separated = grouped with { FingersTogether = false, IndexFingerSeparated = true };
            now += TimeSpan.FromMilliseconds(20); session.Update([separated], now, now);
            now += TimeSpan.FromMilliseconds(100);
            Require(session.Update([separated], now, now) is null && session.FingerSelectionFeedback.Count == 0,
                "A separated pose inherited arming from the moving board.");
            Require(input.MonopolyState.Phase == MonopolyPhase.Landing,
                "An observation originating during delivery executed after landing.");
            now += TimeSpan.FromMilliseconds(20); session.Update([grouped], now, now);
            now += TimeSpan.FromMilliseconds(110); session.Update([grouped], now, now);
            now += TimeSpan.FromMilliseconds(20); session.Update([separated], now, now);
            now += TimeSpan.FromMilliseconds(90);
            Require(session.Update([separated], now, now) is { ButtonId: "mp-start-game", Gesture: BoardSelectionGesture.IndexSeparation } &&
                    input.MonopolyState.Phase == MonopolyPhase.Setup,
                "The landing barrier permanently blocked a fresh grouped-to-separated selection.");
        }
        async Task Capture(string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        SceneCompositor.MonopolyEntranceFrame Frame(SceneCompositor fixture) => fixture.GetMonopolyEntranceFrame(now) is { } frame
            ? frame : throw new InvalidOperationException("Missing active entrance frame.");
        SceneCompositor.MonopolyEntranceFrame? FrameOrCompleted(SceneCompositor fixture) => fixture.GetMonopolyEntranceFrame(now);
        static T Field<T>(SceneCompositor fixture, string name) =>
            typeof(SceneCompositor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture) is T value
                ? value : throw new InvalidOperationException("Missing entrance verification field " + name + ".");
        int[][] NativeParcelMasks()
        {
            // Real opaque parcel interiors remain valid for rounded, rotated or
            // curved lots. Neighbouring parcels must not enter a rectangular
            // probe merely because their axis-aligned bounding boxes overlap.
            var masks = new int[40][];
            using var isolated = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), rasterWidth, rasterHeight, 96);
            var method = typeof(SceneCompositor).GetMethod("DrawMonopolySpace",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;
            for (int index = 0; index < masks.Length; index++)
            {
                using (var drawing = isolated.CreateDrawingSession())
                {
                    drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    drawing.Transform = Matrix3x2.CreateScale(rasterWidth / 1000f, rasterHeight / 1000f);
                    method.Invoke(method.IsStatic ? null : scene,
                        [drawing, MonopolyGame.Spaces[index], state, scene.MonopolyPreviewAspect, 1f]);
                }
                var pixels = isolated.GetPixelBytes();
                var offsets = new List<int>();
                for (int y = 0; y < rasterHeight; y += 3)
                for (int x = 0; x < rasterWidth; x += 3)
                {
                    int offset = (y * rasterWidth + x) * 4;
                    if (pixels[offset + 3] >= 250) offsets.Add(offset);
                }
                Require(offsets.Count >= 32, "Parcel " + index + " has no visible native opaque artwork.");
                masks[index] = offsets.ToArray();
            }
            return masks;
        }
        double ParcelError(byte[] actual, byte[] expected, int index)
        {
            Require(actual.Length == expected.Length, "Parcel comparison changed native dimensions.");
            double error = 0;
            foreach (int offset in parcelMasks[index])
                for (int channel = 0; channel < 3; channel++)
                    error += Math.Abs(actual[offset + channel] - expected[offset + channel]);
            return error / (parcelMasks[index].Length * 3d);
        }
        double CenterError(byte[] actual)
        {
            double error = 0;
            foreach (int offset in fixedCenterMask)
                for (int channel = 0; channel < 3; channel++)
                    error += Math.Abs(actual[offset + channel] - firstBoard[offset + channel]);
            return error / (fixedCenterMask.Length * 3d);
        }
        static double MeanError(byte[] actual, byte[] expected, int imageWidth, int imageHeight, Rect bounds)
        {
            Require(actual.Length == expected.Length, "Entrance comparison changed native dimensions.");
            double error = 0; int count = 0;
            ForPixels(imageWidth, imageHeight, bounds, offset =>
            {
                for (int channel = 0; channel < 3; channel++) error += Math.Abs(actual[offset + channel] - expected[offset + channel]);
                count += 3;
            });
            return error / Math.Max(1, count);
        }
        static double AlphaFraction(byte[] pixels, int imageWidth, int imageHeight, Rect bounds)
        {
            int opaque = 0, count = 0;
            ForPixels(imageWidth, imageHeight, bounds, offset => { if (pixels[offset + 3] > 0) opaque++; count++; });
            return opaque / (double)Math.Max(1, count);
        }
        static void ForPixels(int imageWidth, int imageHeight, Rect bounds, Action<int> visit)
        {
            int left = Math.Clamp((int)Math.Ceiling(bounds.X * imageWidth / 1000), 0, imageWidth);
            int top = Math.Clamp((int)Math.Ceiling(bounds.Y * imageHeight / 1000), 0, imageHeight);
            int right = Math.Clamp((int)Math.Floor(bounds.Right * imageWidth / 1000), left, imageWidth);
            int bottom = Math.Clamp((int)Math.Floor(bounds.Bottom * imageHeight / 1000), top, imageHeight);
            for (int y = top; y < bottom; y += 3)
            for (int x = left; x < right; x += 3) visit((y * imageWidth + x) * 4);
        }
        static int Differences(byte[] firstPixels, byte[] secondPixels)
        {
            Require(firstPixels.Length == secondPixels.Length, "Entrance output dimensions changed.");
            int count = 0;
            for (int offset = 0; offset < firstPixels.Length; offset += 4)
                if (firstPixels[offset] != secondPixels[offset] || firstPixels[offset + 1] != secondPixels[offset + 1] ||
                    firstPixels[offset + 2] != secondPixels[offset + 2]) count++;
            return count;
        }
        static bool OutsideCornersBlack(byte[] pixels, int imageWidth, int imageHeight) =>
            BlackRect(pixels, imageWidth, 0, 0, 20, 20) && BlackRect(pixels, imageWidth, imageWidth - 20, 0, imageWidth, 20) &&
            BlackRect(pixels, imageWidth, 0, imageHeight - 20, 20, imageHeight) &&
            BlackRect(pixels, imageWidth, imageWidth - 20, imageHeight - 20, imageWidth, imageHeight);
        static bool BlackRect(byte[] pixels, int imageWidth, int left, int top, int right, int bottom)
        {
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int offset = (y * imageWidth + x) * 4;
                if (pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0) return false;
            }
            return true;
        }
        static HandDetection Hand()
        {
            PixelPoint[] points = [new(1500, 1250), new(1475, 1215), new(1445, 1190), new(1410, 1165), new(1390, 1145),
                new(1465, 1160), new(1460, 1120), new(1455, 1090), new(1450, 1060),
                new(1500, 1150), new(1500, 1105), new(1500, 1070), new(1500, 1035),
                new(1530, 1160), new(1530, 1120), new(1530, 1090), new(1530, 1060),
                new(1555, 1180), new(1555, 1150), new(1555, 1125), new(1555, 1100)];
            return new(points, .99, .5);
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
