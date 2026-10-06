#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        await scene.EnsureCrownDeedResourcesAsync(CanvasDevice.GetSharedDevice());
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f),
                new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowMonopoly();
        now += TimeSpan.FromMilliseconds(4975);
        scene.TickMonopoly(now);
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
        now += TimeSpan.FromMilliseconds(320);
        scene.TickMonopoly(now);
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
        Act("mp-exit");
        now += TimeSpan.FromMilliseconds(320);
        scene.TickMonopoly(now);
        Act("mp-save-exit");
        Require(scene.TryGetMonopolySaveRequest(out long retryId, out string retryJson) && retryId > requestId,
            "A save failure blocked a fresh retry.");
        await MonopolySaveStore.SaveAsync(savePath, retryJson);
        Require(scene.CompleteMonopolySave(retryId, success: true) && scene.CurrentBoardScreen == BoardScreen.Menu,
            "A completed disk save did not exit to the menu.");
        using var resumed = new SceneCompositor(monopolyClock: () => now);
        await resumed.EnsureCrownDeedResourcesAsync(CanvasDevice.GetSharedDevice());
        Require(resumed.LoadMonopolySave((await MonopolySaveStore.LoadAsync(savePath))!), "A persisted Monopoly game could not be loaded.");
        resumed.ShowMonopoly();
        now += TimeSpan.FromMilliseconds(4975);
        resumed.TickMonopoly(now);
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
            int visible = 0, gold = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                byte b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                if (r + g + b > 60) visible++;
                if (r > 145 && g > 95 && b < g * .90) gold++;
            }
            Require(visible > width * height / 3 && gold > 10000 &&
                typeof(SceneCompositor).GetField("_crownDeedCityBitmap", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(scene) is CanvasBitmap,
                $"The {name} frame is missing its fictional city artwork, visible board surface or gold details.");
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
                // Stationary four-finger strips cover the actual label. They
                // are present at startup, with no motion history supplied.
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
                Require(second.TextPatterns!.Where(pattern => pattern.ControlRegion != index)
                    .All(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted),
                    "Obstructing one caption corrupted an untouched neighboring control.");
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
                var pose = SceneCompositor.CrownDeedParcelPose(space);
                Require(cell.X >= 0 && cell.Y >= 0 && cell.Right <= 1000 && cell.Bottom <= 1000 &&
                    pose.Width == 60 && pose.Depth == 90 && Matrix3x2.Invert(pose.Transform, out _),
                    "An oval deed leaves the playable board or changes the common parcel footprint.");
                double expectedAngle = Math.PI / 2 + space * Math.Tau / 40;
                Require(Math.Abs(pose.Center.X - (500 + 434 * Math.Cos(expectedAngle))) < .001 &&
                    Math.Abs(pose.Center.Y - (500 + 408 * Math.Sin(expectedAngle))) < .001,
                    "The equal-angle parcels do not follow the clockwise oval boulevard.");
                var fixtures = game with { Players = Array.AsReadOnly(game.Players.Select(player => player with { Position = space }).ToArray()) };
                foreach (var player in fixtures.Players)
                {
                    var center = SceneCompositor.MonopolyTokenCenter(fixtures, player.Id);
                    Matrix3x2.Invert(pose.Transform, out var inverse);
                    var local = Vector2.Transform(center, inverse);
                    Require(local.X > 6 && local.X < pose.Width - 6 && local.Y > 6 && local.Y < pose.Depth - 6,
                        $"Player {player.Id}'s token is outside its own rotated deed {space}.");
                    var camera = CameraPoint(center.X / 1000, center.Y / 1000);
                    double u = (camera.X / width - .035) / .93;
                    double v = (camera.Y / height - .035) / .93;
                    u = (u - inset / 2) / (1 - inset); v = (v - inset / 2) / (1 - inset);
                    Require(Math.Abs(u * 1000 - center.X) < .001 && Math.Abs(v * 1000 - center.Y) < .001,
                        "Native projector aspect changes a token's board coordinates.");
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
    // Real committed human and AI developments feed the same native renderer
    // used by the projector and laptop. No clock, event or image is substituted.
    private async Task<object> VerifyMonopolyDevelopmentAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision);
        var now = DateTimeOffset.UtcNow.AddMinutes(20);
        string directory = Path.Combine(_appDataDirectory, "CrownDeedDevelopmentVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var images = new List<object>();
        var samples = new List<object>();
        int humanBuilds = 0, exactClockChecks = 0, immutableChecks = 0, captionChecks = 0, confinedMotionChecks = 0;
        foreach (var viewport in new[] { (Name: "measured", Width: 1152, Height: 896),
            (Name: "landscape", Width: 1920, Height: 1080), (Name: "square", Width: 1000, Height: 1000),
            (Name: "portrait", Width: 720, Height: 1280) })
        {
            var game = GameFixture();
            var developments = new List<MonopolyDevelopment>();
            game.DevelopmentOccurred += value => developments.Add(value);
            using var scene = Fixture(game, viewport.Width, viewport.Height);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), viewport.Width, viewport.Height, 96);
            Require(Math.Abs(scene.MonopolyPreviewAspect - viewport.Width / (double)viewport.Height) < .001,
                "The development fixture lost its physical viewport aspect.");
            await Capture(scene, target, viewport.Name + "-owned-stage-0");
            Act(scene, "mp-manage");
            for (int stage = 1; stage <= 5; stage++)
            {
                if (stage > 1) Act(scene, "mp-property-next");
                Require(scene.MonopolyState.SelectedPropertyIndex == 1, "The human rise selected the wrong deed.");
                var previous = scene.MonopolyState;
                int oldCount = developments.Count;
                Act(scene, "mp-build");
                humanBuilds++;
                Require(developments.Count == oldCount + 1, "A human development did not publish exactly once.");
                var development = developments[^1];
                var started = now;
                var committed = scene.MonopolyState;
                string frozen = scene.ExportMonopolySave();
                Require(development.StartedAt == started && development.PlayerId == committed.ActivePlayer!.Id &&
                    !committed.ActivePlayer.IsAi && ReferenceEquals(development.Previous, previous) &&
                    ReferenceEquals(development.Current, committed) && Level(previous, 1) == stage - 1 &&
                    Level(committed, 1) == stage && previous.Players[0].Money - committed.Players[0].Money == 50,
                    "The native rise does not describe the actual committed human purchase.");
                byte[]? initial = null;
                byte[]? initialForeground = null;
                var controls = scene.CurrentBoardButtons.ToArray();
                foreach (double milliseconds in viewport.Name == "measured" ? new[] { 0d, 337.5, 675, 1012.5, 1349, 1350 }
                    : new[] { 0d, 675, 1350 })
                {
                    now = started.AddMilliseconds(milliseconds);
                    var frame = scene.GetCrownDeedDevelopmentFrame(now) ??
                        throw new InvalidOperationException("The committed purchase has no development presentation.");
                    Require(frame.Sequence == development.Sequence && frame.SpaceIndex == 1 &&
                        frame.PlayerId == development.PlayerId && frame.Active == (milliseconds < 1350) &&
                        frame.Progress >= 0 && frame.Progress <= 1 &&
                        (milliseconds != 0 || frame.Progress == 0) && (milliseconds != 675 || frame.Progress == .5f) &&
                        (milliseconds != 1350 || frame.Progress == 1), "The rise lost its injected source clock or exact deadline.");
                    var pixels = Draw(scene, target);
                    Require(pixels.SequenceEqual(Draw(scene, target)), "Repeated same-clock building draws changed native pixels.");
                    exactClockChecks++;
                    Require(scene.ExportMonopolySave() == frozen && ReferenceEquals(committed, scene.MonopolyState) &&
                        developments.Count == oldCount + 1, "Drawing a building mutated rules, cash, state or event identity.");
                    immutableChecks++;
                    initial ??= pixels;
                    if (milliseconds < 1350)
                    {
                        Require(!scene.ActivateMonopolyButton("mp-build") && !scene.TickMonopoly(now),
                            "A development presentation allowed another action before its deadline.");
                        Require(ButtonPixelsEqual(initial, pixels, controls, viewport.Width, viewport.Height),
                            "Building motion changed stationary control captions or their surfaces.");
                        captionChecks++;
                        // Construction must leave the original city artwork,
                        // neighbouring deeds and fixed UI unchanged.
                        var foreground = (CanvasRenderTarget)typeof(SceneCompositor)
                            .GetField("_monopolyPreviewTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
                        var foregroundPixels = foreground.GetPixelBytes();
                        initialForeground ??= foregroundPixels;
                        var nativeSize = foreground.SizeInPixels;
                        var residual = OutsideGrowthDifference(initialForeground, foregroundPixels,
                            (int)nativeSize.Width, (int)nativeSize.Height, 1);
                        Require(residual.AlphaStable && residual.MaxChannelDelta <= 12 &&
                            residual.ChangedPixels <= Math.Ceiling(nativeSize.Width * (double)nativeSize.Height * .00002) &&
                            residual.TotalChannelError <= Math.Ceiling(nativeSize.Width * (double)nativeSize.Height * .0003),
                            "Building motion changed another deed, token, city, plaza or fixed UI.");
                        samples.Add(new { viewport = viewport.Name, stage, milliseconds, outsideGrowthResidual = new
                            { residual.ChangedPixels, residual.MaxChannelDelta, residual.TotalChannelError, residual.AlphaStable } });
                        confinedMotionChecks++;
                    }
                    if (viewport.Name == "measured" && milliseconds is 0 or 337.5 or 675 or 1012.5 or 1350)
                        await Capture(scene, target, $"measured-rise-stage-{stage}-{milliseconds:0000.0}ms");
                    samples.Add(new { viewport.Name, stage, milliseconds, frame.Progress, frame.Active, frame.Sequence });
                }
                Require(!initial!.SequenceEqual(Draw(scene, target)), "The accepted building has no visible native rise.");
                Act(scene, "mp-property-next");
                Require(scene.MonopolyState.SelectedPropertyIndex == 3, "The even-development partner is not selected.");
                Act(scene, "mp-build"); humanBuilds++;
                now += SceneCompositor.CrownDeedDevelopmentDuration;
                Draw(scene, target);
                Require(Level(scene.MonopolyState, 1) == stage && Level(scene.MonopolyState, 3) == stage,
                    "The final human stage disagrees with the committed legal development levels.");
                await Capture(scene, target, viewport.Name + "-owned-stage-" + stage);
            }
        }
        CheckEarlierShops();
        int aiBuilds = await CheckAi();
        CheckStaleRollRetirement();
        CheckCancellation();
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision),
            "Native development verification changed the user's live hardware or game.");
        string samplePath = Path.Combine(directory, "development-samples.json");
        await File.WriteAllTextAsync(samplePath, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        return new { passed = true, durationMilliseconds = 1350, humanBuilds, aiBuilds, exactClockChecks, immutableChecks,
            captionChecks, confinedMotionChecks, allFiveCommittedStages = true, previousShopsStayFullHeight = true,
            fourPhysicalAspects = true, humanAndAiEvents = true, exactEndpoint = true, navigationAndProjectionCancel = true,
            stalePriorRollRetirementPreservesDevelopment = true,
            liveHardwareUnchanged = true, directory, samplePath, images };

        MonopolyGame GameFixture(bool ai = false)
        {
            var game = new MonopolyGame(seed: 151, initialRolls: [new(1, 2)]);
            Require(game.HandleAction("mp-start-game", now) && game.HandleAction("mp-ai-minus", now.AddMilliseconds(1)) &&
                game.HandleAction("mp-human-plus", now.AddMilliseconds(2)) && game.HandleAction("mp-start", now.AddMilliseconds(3)),
                "Development game fixture failed to start.");
            now += TimeSpan.FromMilliseconds(4);
            var data = JsonSerializer.Deserialize<MonopolySaveData>(game.ExportSave(), json)!;
            foreach (var property in data.Properties.Where(property => property.SpaceIndex is 1 or 3)) property.OwnerId = 1;
            if (ai)
            {
                data.Humans = 1; data.Ais = 1; data.Players[0].IsAi = true;
                data.Phase = MonopolyPhase.AwaitingEndTurn;
            }
            game.LoadSave(JsonSerializer.Serialize(data, json), now);
            return game;
        }
        SceneCompositor Fixture(MonopolyGame game, int width, int height)
        {
            var fixture = new SceneCompositor(monopoly: game, blackjackClock: () => now, monopolyClock: () => now,
                boardRevealClock: () => now);
            fixture.SetDisplayAspect(width / (double)height);
            fixture.SetBoardSetup(true);
            fixture.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            fixture.SetBoardSetup(false); fixture.ShowMonopoly();
            Require(fixture.LoadMonopolySave(game.ExportSave(), resume: true), "An isolated owned city could not load.");
            return fixture;
        }
        void Act(SceneCompositor scene, string id)
        {
            now += TimeSpan.FromMilliseconds(20);
            Require(scene.ActivateMonopolyButton(id), "Development fixture rejected " + id + " during " + scene.MonopolyState.Phase + ".");
        }
        static int Level(MonopolySnapshot game, int space) => game.Properties.Single(property => property.SpaceIndex == space).Houses;
        static byte[] Draw(SceneCompositor scene, CanvasRenderTarget target)
        {
            using (var drawing = target.CreateDrawingSession())
                scene.DrawMonopolyPreview(drawing, (float)target.Size.Width, (float)target.Size.Height);
            return target.GetPixelBytes();
        }
        async Task Capture(SceneCompositor scene, CanvasRenderTarget target, string name)
        {
            Draw(scene, target);
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        static bool ButtonPixelsEqual(byte[] first, byte[] current, IReadOnlyList<BoardButton> buttons, int width, int height)
        {
            foreach (var button in buttons)
            {
                var b = button.Bounds;
                for (int y = (int)Math.Ceiling(b.Y * height); y < (b.Y + b.Height) * height; y += 2)
                for (int x = (int)Math.Ceiling(b.X * width); x < (b.X + b.Width) * width; x += 2)
                {
                    int offset = (y * width + x) * 4;
                    for (int channel = 0; channel < 4; channel++) if (first[offset + channel] != current[offset + channel]) return false;
                }
            }
            return true;
        }
        static (int ChangedPixels, int MaxChannelDelta, long TotalChannelError, bool AlphaStable) OutsideGrowthDifference(
            byte[] first, byte[] current, int width, int height, int space)
        {
            var b = SceneCompositor.MonopolySpaceRectangle(space);
            int changed = 0, maxDelta = 0; long total = 0; bool alphaStable = true;
            for (int y = 0; y < height; y += 2)
            for (int x = 0; x < width; x += 2)
            {
                double u = x * 1000d / width, v = y * 1000d / height;
                if (u >= b.X - 48 && u <= b.Right + 48 && v >= b.Y - 48 && v <= b.Bottom + 48) continue;
                // Estate management intentionally displays the same rising
                // architectural mesh at a larger scale, above its controls.
                if (u >= 417 && u <= 581 && v >= 345 && v <= 470) continue;
                int offset = (y * width + x) * 4;
                int pixelError = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int delta = Math.Abs(first[offset + channel] - current[offset + channel]);
                    pixelError += delta; maxDelta = Math.Max(maxDelta, delta);
                }
                if (pixelError > 0) changed++;
                total += pixelError;
                alphaStable &= first[offset + 3] == current[offset + 3];
            }
            // Rebuilding the native command stream changes a few antialias
            // boundary samples by <5% brightness. Same-clock cache redraws
            // remain byte exact; this tightly bounded residual is recorded.
            return (changed, maxDelta, total, alphaStable);
        }
        void CheckEarlierShops()
        {
            using var isolated = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 256, 256, 96);
            var method = typeof(SceneCompositor).GetMethod("DrawCrownDeedBuildings", BindingFlags.Static | BindingFlags.NonPublic)!;
            byte[] Buildings(int level, float progress)
            {
                using (var drawing = isolated.CreateDrawingSession())
                {
                    drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    drawing.Transform = Matrix3x2.CreateTranslation(10, 45) * Matrix3x2.CreateScale(3);
                    method.Invoke(null, [drawing, 60f, level, progress, Windows.UI.Color.FromArgb(255, 143, 78, 51), 1d]);
                }
                return isolated.GetPixelBytes();
            }
            for (int level = 2; level <= 4; level++)
            {
                var previous = Buildings(level - 1, 1);
                foreach (float progress in new[] { 0f, .25f, .5f, .75f, 1f })
                {
                    var current = Buildings(level, progress);
                    for (int shop = 0; shop < level - 1; shop++)
                    for (int y = 159; y < 180; y++)
                    for (int x = (int)((10 + 10.5 + shop * 12.5 - 2) * 3); x < (10 + 10.5 + shop * 12.5 + 2) * 3; x++)
                    {
                        int offset = (y * 256 + x) * 4;
                        Require(previous.AsSpan(offset, 4).SequenceEqual(current.AsSpan(offset, 4)),
                            "An earlier shop shrank or regrew while the new shop rose.");
                    }
                }
            }
        }
        async Task<int> CheckAi()
        {
            var game = GameFixture(ai: true);
            var developments = new List<MonopolyDevelopment>();
            game.DevelopmentOccurred += development => developments.Add(development);
            using var scene = Fixture(game, 1152, 896);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1152, 896, 96);
            now += TimeSpan.FromMilliseconds(1000);
            Require(scene.TickMonopoly(now) && developments.Count == 1 && scene.MonopolyState.ActivePlayer!.IsAi &&
                Level(scene.MonopolyState, 1) == 1, "The real AI did not develop its owned district.");
            var started = now;
            string frozen = scene.ExportMonopolySave();
            foreach (int milliseconds in new[] { 0, 675, 1349 })
            {
                now = started.AddMilliseconds(milliseconds);
                Require(!scene.TickMonopoly(now) && scene.GetCrownDeedDevelopmentFrame(now) is { Active: true } &&
                    scene.ExportMonopolySave() == frozen && developments.Count == 1,
                    "The AI made another decision before its building settled.");
                await Capture(scene, target, "ai-rise-" + milliseconds + "ms");
            }
            now = started.AddMilliseconds(1350);
            Require(scene.GetCrownDeedDevelopmentFrame(now) is { Active: false, Progress: 1 } &&
                scene.TickMonopoly(now) && developments.Count == 2 && Level(scene.MonopolyState, 3) == 1 &&
                developments[^1].Sequence > developments[0].Sequence,
                "The actual AI did not resume one visible even development after the deadline.");
            await Capture(scene, target, "ai-second-development");
            return developments.Count;
        }
        void CheckStaleRollRetirement()
        {
            var game = GameFixture();
            var data = JsonSerializer.Deserialize<MonopolySaveData>(game.ExportSave(), json)!;
            foreach (var property in data.Properties.Where(property => property.SpaceIndex is 1 or 3)) property.OwnerId = 2;
            game.LoadSave(JsonSerializer.Serialize(data, json), now);
            using var scene = Fixture(game, 1152, 896);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1152, 896, 96);
            Act(scene, "mp-roll");
            now += TimeSpan.FromMilliseconds(3600); Draw(scene, target);
            Require(scene.GetMonopolyDiceFrames(now).Count == 2 && scene.MonopolyState.Phase == MonopolyPhase.AwaitingEndTurn,
                "The stale-dice fixture has no settled real prior roll.");
            // No draw between turn change and construction: the compositor must
            // retire old dice during its first building frame without resetting
            // the newer presentation or its shared input deadline.
            Act(scene, "mp-end-turn"); Act(scene, "mp-manage"); Act(scene, "mp-build");
            var started = now;
            var committed = scene.MonopolyState;
            foreach (int milliseconds in new[] { 0, 675, 1349 })
            {
                now = started.AddMilliseconds(milliseconds); Draw(scene, target);
                Require(scene.GetCrownDeedDevelopmentFrame(now) is { Active: true } &&
                    ReferenceEquals(committed, scene.MonopolyState) && scene.GetMonopolyDiceFrames(now).Count == 0 &&
                    !scene.CurrentBoardButtons.Single(button => button.Id == "mp-property-next").Enabled &&
                    !scene.ActivateMonopolyButton("mp-property-next"),
                    "Retiring a stale prior roll cancelled a newer building rise or released its controls.");
            }
            now = started.AddMilliseconds(1350); Draw(scene, target);
            Require(scene.GetCrownDeedDevelopmentFrame(now) is { Active: false, Progress: 1 } &&
                scene.ActivateMonopolyButton("mp-property-next"),
                "The newer building failed to release controls at its own exact deadline.");
        }
        void CheckCancellation()
        {
            foreach (string cause in new[] { "drawer", "menu", "paint", "same-board", "camera", "clip", "black", "load" })
            {
                using var scene = Fixture(GameFixture(), 1000, 1000);
                Act(scene, "mp-manage"); Act(scene, "mp-build");
                Require(scene.GetCrownDeedDevelopmentFrame(now) is { Active: true }, "The cancellation fixture has no rise.");
                switch (cause)
                {
                    case "drawer": Act(scene, "mp-exit"); break;
                    case "menu": scene.ShowBoardMenu(); break;
                    case "paint": scene.ShowPaint(); break;
                    case "same-board": scene.ShowMonopoly(); break;
                    case "camera": scene.ClearHandTips(resetInput: true); break;
                    case "clip": scene.ClearBoardMediaClip(); break;
                    case "black": scene.SetBlackOutput(true); break;
                    case "load": scene.LoadMonopolySave(scene.ExportMonopolySave(), resume: true); break;
                }
                Require(scene.GetCrownDeedDevelopmentFrame(now) is null,
                    "A cancelled " + cause + " presentation retained stale development pixels.");
            }
        }
        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
#endif
