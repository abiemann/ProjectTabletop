#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Render and camera-comparison fixtures are isolated from the user's game,
    // camera, projection window and persistent Monopoly save.
    private async Task<object> VerifyMonopolyAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision);
        const int width = 3840, height = 2160;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "MonopolyVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var scene = new SceneCompositor(monopoly: new MonopolyGame(seed: 27, initialRolls: [new(1, 2)]),
            blackjackClock: () => now, monopolyClock: () => now);
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f),
                new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowMonopoly();
        await Capture("landing");
        Require(scene.CurrentBoardButtons.Any(button => button.Label == "Start Game"), "The landing board lacks Start Game.");
        await CheckAcquisition("landing");
        Act("mp-start-game");
        await Capture("setup");
        Require(scene.MonopolyState.Phase == MonopolyPhase.Setup && scene.MonopolyState.HumanPlayers == 1 &&
            scene.MonopolyState.AiPlayers == 1, "The setup view does not show one human and one AI.");
        await CheckAcquisition("setup");
        Act("mp-ai-minus"); Act("mp-human-plus"); Act("mp-start");
        await Capture("game");
        Require(scene.MonopolyState.Players.Count == 2 && scene.MonopolyState.Players.All(player => !player.IsAi),
            "The actual game did not use the configured two human players.");
        CheckTokenGeometry(scene.MonopolyState);
        var roll = scene.CurrentBoardButtons.Single(button => button.Id == "mp-roll");
        Require(roll.Label == "Roll" && roll.Enabled && roll.Bounds.Width > roll.Bounds.Height,
            "The dice pill lacks its exact Roll caption or enabled target.");
        await CheckAcquisition("game");
        Act("mp-roll");
        now += TimeSpan.FromSeconds(3.6);
        scene.TickMonopoly(now);
        Act("mp-buy");
        await Capture("property-purchased");
        Require(scene.MonopolyState.Players[0].Position == 3 &&
            scene.MonopolyState.Properties.Single(property => property.SpaceIndex == 3).OwnerId == scene.MonopolyState.Players[0].Id,
            "A native rendered move and purchase disagree with the game state.");
        Act("mp-exit");
        await Capture("exit-confirmation");
        await CheckAcquisition("exit-confirmation");
        Require(scene.CurrentBoardScreen == BoardScreen.Monopoly && scene.MonopolyState.Phase == MonopolyPhase.ExitConfirmation,
            "Mid-game Exit closed the game before save confirmation.");

        Act("mp-save-exit");
        Require(scene.TryGetMonopolySaveRequest(out long requestId, out string json) && scene.MonopolyState.Phase == MonopolyPhase.Saving,
            "Save and exit did not produce a save request.");
        string savePath = Path.Combine(directory, "saved-game.json");
        await MonopolySaveStore.SaveAsync(savePath, json);
        Require(await MonopolySaveStore.LoadAsync(savePath) == json, "Saving and reading Monopoly changed its JSON payload.");
        byte[] previousSave = await File.ReadAllBytesAsync(savePath);
        // Holding the destination without delete-sharing rejects atomic replacement
        // after the temporary write. The earlier game must survive that failure.
        bool failed = false;
        await using (var heldSave = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await MonopolySaveStore.SaveAsync(savePath, json + " "); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        }
        byte[] saveAfterFailure = await File.ReadAllBytesAsync(savePath);
        Require(failed && previousSave.SequenceEqual(saveAfterFailure) &&
            Directory.GetFiles(directory, "*.tmp").Length == 0,
            "A failed Monopoly write damaged the prior save or left an unpublished temporary file.");
        Require(scene.CompleteMonopolySave(requestId, success: false, "Verification write failure") &&
            scene.CurrentBoardScreen == BoardScreen.Monopoly && scene.MonopolyState.Phase == MonopolyPhase.ExitConfirmation &&
            !scene.CompleteMonopolySave(requestId, success: true),
            "A failed save navigated away or accepted a later stale success.");
        Act("mp-exit-cancel");
        Require(scene.MonopolyState.Phase == MonopolyPhase.AwaitingEndTurn && scene.MonopolyState.Players[0].Money == 1440,
            "Cancel after a failed save lost the interrupted game.");
        Act("mp-exit"); Act("mp-save-exit");
        Require(scene.TryGetMonopolySaveRequest(out long retryId, out string retryJson) && retryId > requestId,
            "A save failure blocked a fresh retry.");
        await MonopolySaveStore.SaveAsync(savePath, retryJson);
        Require(scene.CompleteMonopolySave(retryId, success: true) && scene.CurrentBoardScreen == BoardScreen.Menu,
            "A completed disk save did not exit to the menu.");
        using var resumed = new SceneCompositor(monopolyClock: () => now);
        Require(resumed.LoadMonopolySave((await MonopolySaveStore.LoadAsync(savePath))!), "A persisted Monopoly game could not be loaded.");
        resumed.ShowMonopoly();
        Require(resumed.MonopolyState.Phase == MonopolyPhase.Landing && resumed.MonopolyState.CanResume &&
            resumed.ActivateMonopolyButton("mp-resume") && resumed.MonopolyState.Phase == MonopolyPhase.AwaitingEndTurn &&
            resumed.MonopolyState.Players[0].Money == 1440 && resumed.MonopolyState.Players[0].Position == 3,
            "Resume did not restore the player's cash, location and interrupted turn.");
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision),
            "The isolated Monopoly check changed the user's hardware or live game.");
        return new { passed = true, nativeWidth = width, nativeHeight = height, images, directory,
            landingSetupGameplayAndExitRendered = true, actualTextTargets = true, stationaryTextCorruptionNeedsTwoFrames = true,
            sevenPercentThreshold = true, preliminaryLightCannotExecute = true, fortySpacesAndTokenLocations = true, saveRoundTrip = true,
            failedWriteKeepsGameAndPriorSave = true, resumePreservesTurn = true, liveHardwareUnchanged = true };

        void Act(string id)
        {
            now += TimeSpan.FromMilliseconds(20);
            Require(scene.ActivateMonopolyButton(id), $"Monopoly view rejected {id} during {scene.MonopolyState.Phase}.");
        }
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        async Task Capture(string name)
        {
            var pixels = Draw();
            Require(pixels.Take(4).SequenceEqual(new byte[] { 0, 0, 0, 255 }), "The Monopoly frame spills outside its physical board clip.");
            int visible = 0, gold = 0, ivory = 0, emerald = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                byte b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                if (r + g + b > 60) visible++;
                if (r > 145 && g > 95 && b < g * .90) gold++;
                if (r > 200 && g > 195 && b > 150) ivory++;
                if (g > r * 1.3 && g > b * 1.2 && g > 25) emerald++;
            }
            Require(visible > width * height / 3 && gold > 10000 && ivory > 10000 && emerald > 20000,
                $"The regal {name} frame is missing its board surface, gold, ivory or green material.");
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png); images.Add(path);
        }
        async Task CheckAcquisition(string state)
        {
            Draw(); scene.GetHandAcquisitionContext(now);
            now += TimeSpan.FromMilliseconds(600);
            var ready = Ready();
            var buttons = scene.CurrentBoardButtons.ToArray();
            var expected = ready.ExpectedScene!;
            Require(expected.BoardSearchRegions?.Count == buttons.Length && expected.BoardTriggerRegions?.Count == buttons.Length &&
                ready.StationarySearchCenters?.Length == buttons.Length && ready.RestrictAcquisitionToSearchRegions,
                $"The {state} acquisition reference does not include every actual control.");
            var unlit = Draw();
            var emptyDetector = new HandAcquisitionPresenceTracker();
            var empty = emptyDetector.Update(width, height, width * 4, unlit, ready.SearchPolygon, expected, now, now);
            Require(empty.BaselineReady && empty.Hints.Count == 0, $"The empty {state} board triggered a hand search: {empty.Reason}.");
            for (int index = 0; index < buttons.Length; index++)
            {
                var button = buttons[index];
                var bounds = button.Bounds;
                var actual = ready.StationarySearchCenters![index];
                var center = CameraPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                Require(Math.Abs(actual.X - center.X) < .1 && Math.Abs(actual.Y - center.Y) < .1,
                    $"The {button.Label} stationary crop misses its actual target.");
                var region = expected.BoardTriggerRegions![index];
                Require(region.Width > 0 && region.Height > 0 && region.X >= bounds.X && region.Y >= bounds.Y &&
                    region.X + region.Width <= bounds.X + bounds.Width && region.Y + region.Height <= bounds.Y + bounds.Height,
                    $"The {button.Label} glyph region is empty or outside its control.");
                if (!button.Enabled) continue;
                ready = Ready();
                if (button.Id == "mp-start")
                {
                    // The gold Start panel must remain anchored by independent
                    // perimeter colours even when a flat obstruction is present
                    // in the very first camera frame.
                    var uniform = (byte[])unlit.Clone();
                    var uniformTop = CameraPoint(bounds.X + .016, bounds.Y + .013);
                    var uniformBottom = CameraPoint(bounds.X + bounds.Width - .016, bounds.Y + bounds.Height - .013);
                    int uniformSpan = (int)(uniformBottom.X - uniformTop.X);
                    for (int finger = 0; finger < 4; finger++)
                        Fill(uniform, (int)uniformTop.X + finger * uniformSpan / 4, (int)uniformTop.Y,
                            uniformSpan / 4 - 2, (int)(uniformBottom.Y - uniformTop.Y), 15, 20, 225);
                    var uniformDetector = new HandAcquisitionPresenceTracker();
                    var uniformFirst = uniformDetector.Update(width, height, width * 4, uniform, ready.SearchPolygon,
                        ready.ExpectedScene, now, now);
                    now += TimeSpan.FromMilliseconds(33);
                    var uniformSecond = uniformDetector.Update(width, height, width * 4, uniform, ready.SearchPolygon,
                        ready.ExpectedScene, now, now);
                    bool validUniform = uniformFirst.Hints.Count == 0 &&
                        uniformFirst.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                            { ShapeCorrupted: true, ConfirmationFrames: 1 } &&
                        uniformSecond.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                            { ShapeCorrupted: true, ConfirmationFrames: 2 } &&
                        uniformSecond.Hints.Any(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07);
                    if (!validUniform) SaveAcquisitionFailure("uniform-gold-start", button, ready, unlit, uniform, uniformSecond);
                    Require(validUniform, "Independent perimeter references did not preserve first-arrival foreground on the gold Start panel. " +
                        System.Text.Json.JsonSerializer.Serialize(new { uniformFirst, uniformSecond }));
                    now += TimeSpan.FromMilliseconds(33);
                    ready = Ready();
                }
                var occupied = (byte[])unlit.Clone();
                // Stationary four-finger strips cover the actual label. They are
                // present on the first frame, with no motion history supplied.
                double centerU = region.X + region.Width / 2;
                double fingerSpan = Math.Min(bounds.Width - .032, Math.Max(region.Width + .018, bounds.Width * .30));
                var topLeft = CameraPoint(Math.Max(bounds.X + .016, centerU - fingerSpan / 2), bounds.Y + .013);
                var bottomRight = CameraPoint(Math.Min(bounds.X + bounds.Width - .016, centerU + fingerSpan / 2),
                    bounds.Y + bounds.Height - .013);
                int span = Math.Max(24, (int)(bottomRight.X - topLeft.X));
                for (int finger = 0; finger < 4; finger++)
                    Fill(occupied, (int)topLeft.X + finger * span / 4, (int)topLeft.Y,
                        Math.Max(5, span / 4 - 2), Math.Max(8, (int)(bottomRight.Y - topLeft.Y)),
                        (byte)(15 + finger * 3), (byte)(20 + finger * 5), (byte)(225 - finger * 7));
                var detector = new HandAcquisitionPresenceTracker();
                var first = detector.Update(width, height, width * 4, occupied, ready.SearchPolygon, ready.ExpectedScene, now, now);
                if (!(first.Hints.Count == 0 && first.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                    { ShapeCorrupted: true, ConfirmationFrames: 1 }))
                    SaveAcquisitionFailure(state, button, ready, unlit, occupied, first);
                Require(first.Hints.Count == 0 && first.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                    { ShapeCorrupted: true, ConfirmationFrames: 1 },
                    $"The first stationary corruption over {state}/{button.Label} bypassed confirmation or missed the actual letters: {first.Reason}. " +
                    System.Text.Json.JsonSerializer.Serialize(first.TextPatterns));
                scene.CompleteHandAcquisition(ready, first.Hints, [], now);
                Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null, "One damaged text frame started search illumination.");
                now += TimeSpan.FromMilliseconds(33);
                var second = detector.Update(width, height, width * 4, occupied, ready.SearchPolygon, ready.ExpectedScene, now, now);
                if (!(second.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                    { ShapeCorrupted: true, ConfirmationFrames: 2 } &&
                    second.Hints.Any(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07)))
                    SaveAcquisitionFailure(state, button, ready, unlit, occupied, second);
                Require(second.TextPatterns?.Single(pattern => pattern.ControlRegion == index) is
                    { ShapeCorrupted: true, ConfirmationFrames: 2 } &&
                    second.Hints.Any(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07),
                    $"Two stationary label occlusions did not produce measured 7% foreground over {state}/{button.Label}: {second.Reason}. " +
                    System.Text.Json.JsonSerializer.Serialize(new { second.TextPatterns, second.Hints }));
                var measured = second.Hints.First(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07);
                foreach (var belowThreshold in new[] { measured with { ControlCoverage = .069999 },
                    measured with { ControlTriggerCoverage = .069999 } })
                {
                    scene.CompleteHandAcquisition(ready, [belowThreshold], [], now);
                    Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
                        "Sub-7% control or lettering evidence started Monopoly search illumination.");
                }
                long revision = scene.MonopolyState.Revision;
                scene.CompleteHandAcquisition(ready, [measured with { ControlCoverage = .07, ControlTriggerCoverage = .07 }], [], now);
                var illuminated = scene.GetHandAcquisitionContext(now)!;
                Require(illuminated.IlluminatedHint is not null && scene.MonopolyState.Revision == revision &&
                    scene.ActiveHandSpotlightCount == 0 && scene.HoveredBoardButtons.Count == 0 &&
                    ReferenceEquals(ready.ExpectedScene, illuminated.ExpectedScene),
                    $"Search assistance over {button.Label} executed an action or contaminated its unlit reference.");
                Require(WhiteNear(Draw(), illuminated.IlluminatedHint!.Center) > WhiteNear(unlit, illuminated.IlluminatedHint.Center) + 200,
                    "The search light does not illuminate the obstructed Monopoly text.");
                scene.CompleteHandAcquisition(illuminated, second.Hints, [], now, illuminatedPresence: false);
                now += TimeSpan.FromMilliseconds(1000);
                await Task.Yield();
            }
        }
        SceneCompositor.HandAcquisitionContext Ready()
        {
            Draw(); var context = scene.GetHandAcquisitionContext(now);
            Require(context is { ObserveMotion: true, ExpectedScene: not null }, "Monopoly text acquisition did not finish settling.");
            return context!;
        }
        void SaveAcquisitionFailure(string state, BoardButton button, SceneCompositor.HandAcquisitionContext context,
            byte[] empty, byte[] occupied, HandAcquisitionPresenceResult presence)
        {
            string failureDirectory = Path.Combine(directory, "acquisition-failure");
            Directory.CreateDirectory(failureDirectory);
            File.WriteAllBytes(Path.Combine(failureDirectory, "empty.bgra"), empty);
            File.WriteAllBytes(Path.Combine(failureDirectory, "occupied.bgra"), occupied);
            File.WriteAllBytes(Path.Combine(failureDirectory, "expected.bgra"), context.ExpectedScene!.Bgra);
            File.WriteAllText(Path.Combine(failureDirectory, "failure.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                width, height, stride = width * 4, state, button, now, context.SearchPolygon,
                expected = new { context.ExpectedScene.Width, context.ExpectedScene.Height, context.ExpectedScene.CameraToBoard,
                    context.ExpectedScene.BoardSearchRegions, context.ExpectedScene.BoardReferenceRegions, context.ExpectedScene.BoardTriggerRegions },
                presence
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        void CheckTokenGeometry(MonopolySnapshot game)
        {
            for (int space = 0; space < 40; space++)
            {
                var cell = SceneCompositor.MonopolySpaceRectangle(space);
                Require(cell.X >= 49 && cell.Y >= 49 && cell.Right <= 951 && cell.Bottom <= 951,
                    "A Monopoly square leaves the playable perimeter.");
                var fixtures = game with { Players = Array.AsReadOnly(game.Players.Select(player => player with { Position = space }).ToArray()) };
                foreach (var player in fixtures.Players)
                {
                    var center = SceneCompositor.MonopolyTokenCenter(fixtures, player.Id);
                    Require(center.X > cell.X + 6 && center.X < cell.Right - 6 && center.Y > cell.Y + 6 && center.Y < cell.Bottom - 6,
                        $"Player {player.Id}'s token is outside its correct Monopoly square {space}.");
                    var camera = CameraPoint(center.X / 1000, center.Y / 1000);
                    double u = (camera.X / width - .035) / .93;
                    double v = (camera.Y / height - .035) / .93;
                    u = (u - inset / 2) / (1 - inset); v = (v - inset / 2) / (1 - inset);
                    Require(Math.Abs(u * 1000 - center.X) < .001 && Math.Abs(v * 1000 - center.Y) < .001,
                        "Native projector aspect changes a token's board coordinates.");
                }
                if (space is not (9 or 19 or 29 or 39))
                {
                    var next = SceneCompositor.MonopolySpaceRectangle(space + 1);
                    bool correctlyOrdered = space < 10 ? next.X < cell.X : space < 20 ? next.Y < cell.Y
                        : space < 30 ? next.X > cell.X : next.Y > cell.Y;
                    Require(correctlyOrdered, "The Monopoly perimeter squares run in the wrong order.");
                }
            }
        }
        PixelPoint CameraPoint(double u, double v) => new(width * (.035 + .93 * (inset / 2 + u * (1 - inset))),
            height * (.035 + .93 * (inset / 2 + v * (1 - inset))));
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
