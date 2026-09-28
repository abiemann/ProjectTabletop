#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // All clocks, games, render targets and input are private to this fixture.
    // Reading its PNG gallery must never alter the user's camera or current game.
    private async Task<object> VerifyMonopolyDiceAnimationAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision);
        const int width = 3840, height = 2160;
        var now = DateTimeOffset.UtcNow.AddMinutes(10);
        string directory = Path.Combine(_appDataDirectory, "MonopolyDiceVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var samples = new List<object>();
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var scene = Fixture(width, height, humansOnly: true);
        Draw(scene, target, width, height);
        scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        Draw(scene, target, width, height);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: true, ExpectedScene: not null },
            "The unanimated Monopoly fixture did not provide its clean control reference.");

        var previous = scene.MonopolyState;
        Act(scene, "mp-roll");
        var started = now;
        long gameRevision = scene.MonopolyState.Revision;
        Require(scene.MonopolyState.Dice == new MonopolyDice(1, 2) && scene.MonopolyState.Players[0].Position == 3,
            "The animation fixture did not commit its deterministic dice and actual move.");
        using (var diagnostic = JsonDocument.Parse(JsonSerializer.Serialize(scene.GetMonopolyDiceAnimationDiagnostics(now))))
            Require(diagnostic.RootElement.GetProperty("durationMilliseconds").GetDouble() == 3600,
                "The dice presentation does not last exactly 3.6 seconds.");
        var initialFrames = scene.GetMonopolyDiceFrames(now).ToArray();
        CheckFrames(initialFrames);
        Require(initialFrames.All(frame => frame.Progress == 0 && !frame.Settled &&
            frame.VisibleFaces.SelectMany(face => face.Corners).All(point => frame.Edge switch
            {
                0 => point.X < 0, 1 => point.X > 1000, 2 => point.Y < 0, _ => point.Y > 1000
            })) && initialFrames[0].Edge != initialFrames[1].Edge,
            "The two cubes did not start completely outside two captured random board edges.");

        CanvasRenderTarget? boardCache = null;
        object? boardCacheKey = null;
        byte[]? boardPixels = null;
        byte[]? earlierOverlay = null;
        byte[]? earlierOutput = null;
        object? earlierLayerKey = null;
        long presentationRevision = 0;
        foreach (int milliseconds in new[] { 0, 450, 900, 1182, 1464, 1746, 2310, 3000, 3599, 3600 })
        {
            now = started.AddMilliseconds(milliseconds);
            var frames = scene.GetMonopolyDiceFrames(now).ToArray();
            CheckFrames(frames);
            Require(frames.Zip(initialFrames).All(pair => pair.First.Sequence == pair.Second.Sequence &&
                    pair.First.Edge == pair.Second.Edge && pair.First.Seed == pair.Second.Seed &&
                    pair.First.StartCenter == pair.Second.StartCenter && pair.First.Destination == pair.Second.Destination),
                "Sampling an animation recaptured randomness, changed its sequence or moved its destinations.");
            string signature = JsonSerializer.Serialize(frames, jsonOptions);
            Require(signature == JsonSerializer.Serialize(scene.GetMonopolyDiceFrames(now), jsonOptions),
                "Two same-time samples changed a cube's captured pose.");
            var pixels = Draw(scene, target, width, height);
            Require(OutsideCornersBlack(pixels, width, height), "A flying die escaped the physical board clip.");
            var board = Field<CanvasRenderTarget>(scene, "_boardApplicationTarget");
            var overlay = Field<CanvasRenderTarget>(scene, "_monopolyDiceTarget");
            var overlayPixels = overlay.GetPixelBytes();
            var layerKey = Field<object>(scene, "_monopolyDiceLayerState");
            var currentCacheKey = Field<object>(scene, "_renderedBoardState");
            Require(overlay.SizeInPixels.Width == board.SizeInPixels.Width &&
                    overlay.SizeInPixels.Height == board.SizeInPixels.Height && board.SizeInPixels.Width >= width * .8 &&
                    board.SizeInPixels.Height >= height * .8,
                "Dice use a low-resolution overlay or a raster separate from the native board allocation.");
            var presented = Presented(scene);
            if (milliseconds < 3600)
            {
                Require(presented.Players.Select(player => player.Position).SequenceEqual(previous.Players.Select(player => player.Position)) &&
                        presented.Dice == default && presented.Status == "Rolling the dice…" &&
                        scene.MonopolyState.Revision == gameRevision,
                    "The token, result or gameplay revision advanced visually before the dice settled.");
                Require(scene.CurrentBoardButtons.Single(button => button.Id == "mp-exit").Enabled &&
                        scene.CurrentBoardButtons.Where(button => button.Id != "mp-exit").All(button => !button.Enabled) &&
                        !scene.ActivateMonopolyButton("mp-buy") && !scene.ActivateMonopolyButton("mp-roll") &&
                        !scene.TickMonopoly(now),
                    "The presentation accepted game input or AI progress while its cubes were moving.");
                var context = scene.GetHandAcquisitionContext(now);
                Require(context is { ObserveMotion: false, IlluminatedHint: null, ExpectedScene: null },
                    "Dice activity contaminated the clean reference or triggered a search light.");
                scene.CompleteHandAcquisition(context, [new HandAcquisitionHint(new(50, 50, 100, 100),
                    new(100, 100), 40, now, .10, .10, .10)], [], now);
                Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                    "A moving-dice frame accepted an acquisition completion and illuminated the table.");
                if (boardCache is null)
                {
                    boardCache = board; boardCacheKey = currentCacheKey; boardPixels = board.GetPixelBytes();
                    presentationRevision = scene.MonopolyDicePresentationRevision;
                }
                else
                    Require(ReferenceEquals(boardCache, board) && Equals(boardCacheKey, currentCacheKey) &&
                            boardPixels!.SequenceEqual(board.GetPixelBytes()) && scene.MonopolyDicePresentationRevision == presentationRevision,
                        "An intermediate dice pose invalidated or repainted the cached Monopoly board.");
                if (milliseconds > 0)
                    Require(earlierOverlay is not null && Differences(earlierOverlay, overlayPixels) > 1000 &&
                            !Equals(earlierLayerKey, layerKey) && Differences(earlierOutput!, pixels) > 1000,
                        "The independent native overlay failed to move while the board cache stayed fixed.");
            }
            else
            {
                Require(frames.All(frame => frame.Settled && frame.Progress == 1 && frame.Height == 0 &&
                        frame.Size == 30 && frame.Center == frame.Destination && frame.FrontFace == frame.Result) &&
                        frames.Select(frame => frame.Result).SequenceEqual(new[] { 1, 2 }) &&
                        presented.Players[0].Position == 3 && presented.Dice == new MonopolyDice(1, 2) &&
                        scene.CurrentBoardButtons.Single(button => button.Id == "mp-buy").Enabled &&
                        scene.MonopolyDicePresentationRevision == presentationRevision + 1,
                    "At 3.6 seconds the exact result faces, token move and newly enabled purchase did not settle together.");
            }
            if (milliseconds is 450 or 1182 or 1746 or 2310)
                Require(frames.All(frame => frame.Height > 10 && frame.Center.Y < frame.GroundCenter.Y),
                    "The flight or one of the three diminishing bounce arcs has no physical height.");
            if (milliseconds is 900 or 1464)
                Require(frames.All(frame => frame.Height < .02f), "The die failed to touch the board at an impact.");
            if (milliseconds == 3000)
                Require(frames.All(frame => frame.Height is >= 0 and < 3 && frame.Size < 34),
                    "The final roll did not reduce its bounce and cube size toward the resting result.");
            Require(layerKey.Equals(Field<object>(scene, "_monopolyDiceLayerState")) &&
                    pixels.SequenceEqual(Draw(scene, target, width, height)),
                "A same-time projector redraw consumed randomness or changed its cached dice layer.");
            await Capture(target, $"dice-{milliseconds:0000}ms", width, height);
            samples.Add(new { milliseconds, frames = scene.GetMonopolyDiceAnimationDiagnostics(now),
                presentedPosition = presented.Players[0].Position, actualPosition = scene.MonopolyState.Players[0].Position });
            earlierOverlay = overlayPixels; earlierOutput = pixels; earlierLayerKey = layerKey;
        }
        var settledPixels = earlierOutput!;
        var settledFrames = JsonSerializer.Serialize(scene.GetMonopolyDiceFrames(now), jsonOptions);
        now = started.AddSeconds(5);
        Require(settledFrames == JsonSerializer.Serialize(scene.GetMonopolyDiceFrames(now), jsonOptions) &&
                settledPixels.SequenceEqual(Draw(scene, target, width, height)),
            "The settled cubes disappeared, moved or replayed after their animation deadline.");
        Act(scene, "mp-buy");
        Require(scene.GetMonopolyDiceFrames(now).All(frame => frame.Settled && frame.FrontFace == frame.Result) &&
                scene.MonopolyState.Properties.Single(property => property.SpaceIndex == 3).OwnerId == 1,
            "Purchasing a property discarded the persistent settled cubes.");
        await CheckAiHold();
        await CheckPortrait();
        CheckCancellation();
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision),
            "The private dice verification changed the user's camera, projector or game.");
        string samplePath = Path.Combine(directory, "dice-samples.json");
        await File.WriteAllTextAsync(samplePath, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        return new { passed = true, durationMilliseconds = 3600, nativeWidth = width, nativeHeight = height,
            twoRandomOutsideEdges = true, samplingDoesNotConsumeRandomness = true, threeDiminishingBounces = true,
            exactSettledFaces = true, tokenMoveWaitsForSettle = true, gameplayAndAiHeld = true,
            cachedBoardStableDuringFlight = true, independentNativeOverlay = true, diceCannotTriggerSearchLight = true,
            settledCubesPersist = true, navigationCameraAndProjectionCancel = true, nativePortraitAspect = true,
            liveHardwareUnchanged = true, directory, samplePath, images };

        SceneCompositor Fixture(int outputWidth, int outputHeight, bool humansOnly,
            MonopolyGame? game = null)
        {
            var fixture = new SceneCompositor(monopoly: game ?? new MonopolyGame(seed: 73,
                initialRolls: [new(1, 2), new(1, 2)]), blackjackClock: () => now, monopolyClock: () => now);
            fixture.SetDisplayAspect(outputWidth / (double)outputHeight);
            fixture.SetBoardSetup(true);
            fixture.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(outputWidth, 0), new(outputWidth, outputHeight), new(0, outputHeight)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            fixture.SetBoardSetup(false); fixture.ShowMonopoly();
            now += TimeSpan.FromMilliseconds(4975); fixture.TickMonopoly(now);
            Act(fixture, "mp-start-game");
            if (humansOnly) { Act(fixture, "mp-ai-minus"); Act(fixture, "mp-human-plus"); }
            Act(fixture, "mp-start");
            return fixture;
        }
        void Act(SceneCompositor fixture, string id)
        {
            now += TimeSpan.FromMilliseconds(20);
            Require(fixture.ActivateMonopolyButton(id), $"Dice fixture rejected {id} during {fixture.MonopolyState.Phase}.");
        }
        MonopolySnapshot Presented(SceneCompositor fixture) =>
            (MonopolySnapshot)typeof(SceneCompositor).GetMethod("MonopolyPresentedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture, [now])!;
        async Task CheckAiHold()
        {
            using var ai = Fixture(width, height, humansOnly: false);
            Act(ai, "mp-roll"); now += TimeSpan.FromMilliseconds(3600);
            Draw(ai, target, width, height);
            Require(ai.GetMonopolyDiceFrames(now).All(frame => frame.Settled), "The human dice never settled in the AI fixture.");
            Act(ai, "mp-buy"); Act(ai, "mp-end-turn");
            now += TimeSpan.FromMilliseconds(1100);
            Require(ai.TickMonopoly(now) && ai.MonopolyState.ActivePlayer is { IsAi: true, Position: 3, Money: 1496 },
                "The AI did not commit its own deterministic roll and rent.");
            var aiStarted = now;
            long revision = ai.MonopolyState.Revision;
            now = aiStarted.AddMilliseconds(3000);
            Require(!ai.TickMonopoly(now) && ai.MonopolyState.Revision == revision &&
                    Presented(ai).ActivePlayer is { IsAi: true, Position: 0 } &&
                    ai.GetMonopolyDiceFrames(now).All(frame => !frame.Settled),
                "The AI advanced another decision or revealed its move during its own dice presentation.");
            Draw(ai, target, width, height); await Capture(target, "ai-rolling", width, height);
            now = aiStarted.AddMilliseconds(3599);
            Require(!ai.TickMonopoly(now) && ai.MonopolyState.Revision == revision,
                "The AI's turn resumed one millisecond before the animation deadline.");
            now = aiStarted.AddMilliseconds(3600);
            Require(ai.GetMonopolyDiceFrames(now).All(frame => frame.Settled && frame.FrontFace == frame.Result) &&
                    Presented(ai).ActivePlayer is { IsAi: true, Position: 3 },
                "The AI's displayed token/result did not settle on its exact deadline.");
            Require(ai.TickMonopoly(now) && ai.MonopolyState.ActivePlayer is { IsAi: false },
                "AI gameplay did not resume after the settled presentation released its hold.");
        }
        async Task CheckPortrait()
        {
            const int portraitWidth = 2160, portraitHeight = 3840;
            using var portrait = Fixture(portraitWidth, portraitHeight, humansOnly: true);
            using var portraitTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), portraitWidth, portraitHeight, 96);
            Act(portrait, "mp-roll"); now += TimeSpan.FromMilliseconds(3600);
            var pixels = Draw(portrait, portraitTarget, portraitWidth, portraitHeight);
            var overlay = Field<CanvasRenderTarget>(portrait, "_monopolyDiceTarget");
            double aspect = portrait.MonopolyPreviewAspect;
            double xScale = overlay.SizeInPixels.Width / 1000.0;
            double yScale = overlay.SizeInPixels.Height / 1000.0 * aspect;
            Require(aspect < 1 && Math.Abs(xScale / yScale - 1) < .01 &&
                    overlay.SizeInPixels.Width > portraitWidth * .8 && overlay.SizeInPixels.Height > portraitHeight * .8 &&
                    OutsideCornersBlack(pixels, portraitWidth, portraitHeight) &&
                    portrait.GetMonopolyDiceFrames(now).All(frame => frame.Settled && frame.FrontFace == frame.Result),
                "The portrait projector stretched the cube geometry, lost native detail or escaped its clip.");
            await Capture(portraitTarget, "portrait-settled", portraitWidth, portraitHeight);
        }
        void CheckCancellation()
        {
            foreach (string cause in new[] { "exit", "menu", "same-board", "camera", "clip", "setup", "black", "load" })
            {
                using var cancelled = Fixture(width, height, humansOnly: true);
                string save = cancelled.ExportMonopolySave();
                Act(cancelled, "mp-roll"); now += TimeSpan.FromMilliseconds(450);
                Require(cancelled.GetMonopolyDiceFrames(now).Count == 2, "Cancellation fixture lacks its active dice.");
                switch (cause)
                {
                    case "exit": Act(cancelled, "mp-exit"); break;
                    case "menu": cancelled.ShowBoardMenu(); break;
                    case "same-board": cancelled.ShowMonopoly(); break;
                    case "camera": cancelled.ClearHandTips(resetInput: true); break;
                    case "clip": cancelled.ClearBoardMediaClip(); break;
                    case "setup": cancelled.SetBoardSetup(true); break;
                    case "black": cancelled.SetBlackOutput(true); break;
                    case "load": Require(cancelled.LoadMonopolySave(save, resume: true), "A reset save could not load."); break;
                }
                Require(cancelled.GetMonopolyDiceFrames(now).Count == 0,
                    $"{cause} retained moving dice from an interrupted presentation.");
                if (cause == "exit") Act(cancelled, "mp-exit-cancel");
                else if (cause == "menu") cancelled.ShowMonopoly();
                else if (cause == "setup") cancelled.SetBoardSetup(false);
                else if (cause == "black") cancelled.SetBlackOutput(false);
                // Relaunching the board first completes its entrance; the old
                // dice must remain cancelled once game controls return.
                now += TimeSpan.FromMilliseconds(cause is "menu" or "same-board" ? 4975 : 3700);
                cancelled.TickMonopoly(now);
                Require(cancelled.GetMonopolyDiceFrames(now).Count == 0,
                    $"Returning after {cause} replayed a stale roll.");
                if (cause is "camera" or "exit" or "black")
                    Require(cancelled.ActivateMonopolyButton("mp-buy"), $"{cause} left game input held after cancelling dice.");
            }
        }
        async Task Capture(CanvasRenderTarget image, string name, int imageWidth, int imageHeight)
        {
            string path = Path.Combine(directory, name + ".png");
            await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path, width = imageWidth, height = imageHeight });
        }
        static T Field<T>(SceneCompositor fixture, string name) =>
            typeof(SceneCompositor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture) is T value
                ? value : throw new InvalidOperationException("Missing dice verification field " + name + ".");
        static byte[] Draw(SceneCompositor fixture, CanvasRenderTarget image, int imageWidth, int imageHeight)
        {
            using (var drawing = image.CreateDrawingSession())
                fixture.Draw(drawing, imageWidth, imageHeight, preview: false, runningSlowly: false);
            return image.GetPixelBytes();
        }
        static void CheckFrames(IReadOnlyList<SceneCompositor.MonopolyDiceFrame> frames)
        {
            Require(frames.Count == 2 && frames.Select(frame => frame.DieIndex).SequenceEqual(new[] { 0, 1 }),
                "A dice sample lost or duplicated one of its two cubes.");
            foreach (var frame in frames)
            {
                Require(frame.Sequence > 0 && frame.Edge is >= 0 and <= 3 && frame.Result is >= 1 and <= 6 &&
                        frame.Progress is >= 0 and <= 1 && float.IsFinite(frame.Height) && frame.Height >= 0 &&
                        float.IsFinite(frame.Size) && frame.Size is >= 30 and <= 64 && Finite(frame.Center) &&
                        Finite(frame.GroundCenter) && Finite(frame.StartCenter) && Finite(frame.Destination) &&
                        float.IsFinite(frame.Rotation.X) && float.IsFinite(frame.Rotation.Y) &&
                        float.IsFinite(frame.Rotation.Z) && float.IsFinite(frame.Rotation.W) &&
                        Math.Abs(frame.Rotation.LengthSquared() - 1) < .002 && frame.VisibleFaces.Count is >= 1 and <= 3,
                    "A cube produced a nonfinite, degenerate or invalid three-dimensional pose.");
                foreach (var face in frame.VisibleFaces)
                    Require(face.Value is >= 1 and <= 6 && face.Corners.Count == 4 && face.Corners.All(Finite) &&
                            float.IsFinite(face.Normal.Z) && face.Normal.Z > .015f,
                        "A visible dice face has invalid corners, value or camera-facing normal.");
            }
        }
        static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
        static int Differences(byte[] first, byte[] second)
        {
            Require(first.Length == second.Length, "An animation comparison changed raster dimensions.");
            int count = 0;
            for (int offset = 0; offset < first.Length; offset += 4)
                if (first[offset] != second[offset] || first[offset + 1] != second[offset + 1] ||
                    first[offset + 2] != second[offset + 2] || first[offset + 3] != second[offset + 3]) count++;
            return count;
        }
        static bool OutsideCornersBlack(byte[] pixels, int imageWidth, int imageHeight)
        {
            foreach (var (left, top) in new[] { (0, 0), (imageWidth - 20, 0), (0, imageHeight - 20),
                (imageWidth - 20, imageHeight - 20) })
                for (int y = top; y < top + 20; y++)
                for (int x = left; x < left + 20; x++)
                {
                    int offset = (y * imageWidth + x) * 4;
                    if (pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0) return false;
                }
            return true;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
