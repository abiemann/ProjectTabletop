#if DEBUG
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private native render/camera fixtures preserve the user's hardware, save and game.
    private async Task<object> VerifyMonopolyDrawerAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision);
        const int width = 3840, height = 2160;
        var now = DateTimeOffset.UtcNow.AddMinutes(10);
        string directory = Path.Combine(_appDataDirectory, "MonopolyDrawerVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var tested = new HashSet<string>();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var scene = new SceneCompositor(monopoly: new MonopolyGame(seed: 97),
            blackjackClock: () => now, monopolyClock: () => now);
        scene.SetDisplayAspect(width / (double)height); scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false); scene.ShowMonopoly();
        await Capture("landing-closed");
        Require(!scene.MonopolyDrawerOpen && Button("mp-exit").Label == "^", "The landing drawer lacks its closed upward caret.");
        CheckAcquisition();
        var landing = scene.MonopolyState;
        Act("mp-exit"); var openedAt = now;
        await Capture("drawer-start");
        CheckDrawerProgress(0);
        Require(ReferenceEquals(landing, scene.MonopolyState) && !Button("mp-exit-game").Enabled,
            "Opening the landing drawer mutated rules state or enabled its moving action.");
        now = openedAt.AddMilliseconds(150); await Capture("drawer-halfway"); CheckDrawerProgress(.5);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null, ExpectedScene: null } &&
                !scene.ActivateMonopolyButton("mp-exit-game"),
            "The moving drawer supplied an acquisition reference, light or active Exit Game target.");
        now = openedAt.AddMilliseconds(299);
        Require(!scene.ActivateMonopolyButton("mp-exit-game"), "Exit Game enabled before the 300 ms slide finished.");
        now = openedAt.AddMilliseconds(300); scene.TickMonopoly(now);
        await Capture("landing-drawer-open"); CheckDrawerProgress(1); CheckOpen(active: false);
        CheckAcquisition();
        Act("mp-exit-cancel");
        Require(!scene.MonopolyDrawerOpen && ReferenceEquals(landing, scene.MonopolyState),
            "The downward caret failed to close the inactive drawer without changing the game.");
        Act("mp-start-game"); Act("mp-ai-minus"); Act("mp-human-plus"); Act("mp-start");
        var preserved = scene.MonopolyState;
        string preservedSave = scene.ExportMonopolySave();
        await Capture("active-game-closed");
        Act("mp-exit"); now += TimeSpan.FromMilliseconds(300); scene.TickMonopoly(now);
        await Capture("active-game-drawer"); CheckOpen(active: true); CheckAcquisition();
        Require(scene.ExportMonopolySave() == preservedSave, "Opening the active drawer changed the interrupted save payload.");
        Act("mp-save-exit");
        Require(scene.TryGetMonopolySaveRequest(out long first, out string json) && json == preservedSave &&
                scene.MonopolyDrawerOpen && scene.CurrentBoardButtons.All(button => !button.Enabled),
            "Save and Exit closed the drawer early, changed its payload or left busy controls enabled.");
        await Capture("saving");
        now += TimeSpan.FromMilliseconds(20);
        Require(scene.CompleteMonopolySave(first, false, "Verification write failure") && scene.MonopolyDrawerOpen &&
                scene.MonopolyState.Phase == MonopolyPhase.ExitConfirmation &&
                scene.MonopolyState.Status.Contains("Verification write failure") && !scene.CompleteMonopolySave(first, true),
            "A failed save dismissed the drawer, hid the failure or accepted a stale completion.");
        await Capture("save-failure"); CheckOpen(active: true);
        Act("mp-exit-cancel");
        Require(!scene.MonopolyDrawerOpen && scene.MonopolyState.Phase == preserved.Phase &&
                scene.MonopolyState.Players.SequenceEqual(preserved.Players), "Closing the failed-save drawer lost the interrupted turn.");
        Act("mp-exit"); now += TimeSpan.FromMilliseconds(300); scene.TickMonopoly(now);
        Act("mp-save-exit");
        Require(scene.TryGetMonopolySaveRequest(out long retry, out string retryJson) && retry > first && retryJson == preservedSave &&
                scene.CompleteMonopolySave(retry, true) && scene.CurrentBoardScreen == BoardScreen.Menu && !scene.MonopolyDrawerOpen,
            "A successful retry did not navigate after persistence completed.");
        scene.ShowMonopoly(); Act("mp-resume");
        Require(!scene.MonopolyDrawerOpen && scene.ExportMonopolySave() == preservedSave,
            "Resume lost the saved turn or replayed the old drawer.");
        Require(tested.IsSupersetOf(["^", "v", "Exit Game", "Save and Exit"]), "Caption acquisition omitted a drawer label.");
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.MonopolyState.Revision),
            "The isolated drawer check changed the user's hardware or live game.");
        return new { passed = true, nativeWidth = width, nativeHeight = height, durationMilliseconds = 300,
            slideReferenceSuppression = true, modalTargets = true, bothConditionalActionsAndCaretsAcquired = true,
            vectorChevronTemplates = true, intactChevronRejectsLargePillObstruction = true,
            twoFreshFramesAndSevenPercent = true, busySaveAndFailureRecovery = true, successfulRetryAndResume = true,
            liveHardwareUnchanged = true, tested, directory, images };

        void Act(string id)
        {
            now += TimeSpan.FromMilliseconds(20);
            Require(scene.ActivateMonopolyButton(id), $"Drawer fixture rejected {id} during {scene.MonopolyState.Phase}.");
        }
        BoardButton Button(string id) => scene.CurrentBoardButtons.Single(button => button.Id == id);
        void CheckOpen(bool active) => Require(scene.MonopolyDrawerOpen &&
            scene.CurrentBoardButtons.Count == 2 && Button("mp-exit-cancel").Label == "v" &&
            Button(active ? "mp-save-exit" : "mp-exit-game").Label == (active ? "Save and Exit" : "Exit Game") &&
            Button(active ? "mp-save-exit" : "mp-exit-game").Enabled,
            "The stationary drawer lacks its downward caret and single enabled conditional action.");
        void CheckDrawerProgress(double expected)
        {
            using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(scene.GetMonopolyDrawerDiagnostics(now)));
            var root = metadata.RootElement;
            Require(root.GetProperty("open").GetBoolean() && root.GetProperty("durationMilliseconds").GetDouble() == 300 &&
                    Math.Abs(root.GetProperty("progress").GetDouble() - expected) < .001 &&
                    root.GetProperty("animating").GetBoolean() == (expected < 1), "The drawer slide has an inconsistent duration or clock.");
        }
        void CheckAcquisition()
        {
            Draw(); scene.GetHandAcquisitionContext(now); now += TimeSpan.FromMilliseconds(600); Draw();
            var context = scene.GetHandAcquisitionContext(now);
            Require(context is { ObserveMotion: true, ExpectedScene: not null }, "The stationary drawer did not provide a clean reference.");
            var clean = Draw();
            var empty = new HandAcquisitionPresenceTracker().Update(width, height, width * 4, clean,
                context!.SearchPolygon, context.ExpectedScene, now, now);
            Require(empty.BaselineReady && empty.Hints.Count == 0, "An untouched drawer caption produced an acquisition candidate.");
            var buttons = scene.CurrentBoardButtons.ToArray();
            for (int index = 0; index < buttons.Length; index++)
            {
                var button = buttons[index]; if (!button.Enabled || button.Id is not ("mp-exit" or "mp-exit-cancel" or "mp-exit-game" or "mp-save-exit")) continue;
                var trigger = context.ExpectedScene!.BoardTriggerRegions![index];
                var control = button.Bounds;
                Require(trigger.Width > 0 && trigger.Height > 0 && trigger.X >= control.X && trigger.Y >= control.Y &&
                    trigger.X + trigger.Width <= control.X + control.Width + 1e-8 &&
                    trigger.Y + trigger.Height <= control.Y + control.Height + 1e-8, "A drawer caption has no bounded glyph trigger.");
                bool chevron = button.Id is "mp-exit" or "mp-exit-cancel";
                if (chevron)
                {
                    // A matching diagnostic exists only if Vision extracted real
                    // rendered ink/halo; a generic rectangular trigger has none.
                    Require(empty.TextPatterns?.SingleOrDefault(pattern => pattern.ControlRegion == index) is
                        { LabelIntact: true, Correlation: > .8 }, "The gold chevron used a rectangular fallback instead of an ink template.");
                    var away = (byte[])clean.Clone();
                    var corner = CameraPoint(control.X + .018, control.Y + .008);
                    var opposite = CameraPoint(Math.Min(control.X + control.Width * .35, trigger.X - .020),
                        control.Y + control.Height - .008);
                    var controlStart = CameraPoint(control.X, control.Y);
                    var controlEnd = CameraPoint(control.X + control.Width, control.Y + control.Height);
                    int changedPixels = 0;
                    for (int y = (int)corner.Y; y < opposite.Y; y++)
                    for (int x = (int)corner.X; x < opposite.X; x++)
                    { int offset = (y * width + x) * 4; away[offset] = 20; away[offset + 1] = 35; away[offset + 2] = 225; changedPixels++; }
                    Require(changedPixels / ((controlEnd.X - controlStart.X) * (controlEnd.Y - controlStart.Y)) > .07,
                        "The intact-chevron negative fixture did not obstruct more than 7% of its pill.");
                    var inkStart = CameraPoint(trigger.X, trigger.Y);
                    var inkEnd = CameraPoint(trigger.X + trigger.Width, trigger.Y + trigger.Height);
                    for (int y = (int)inkStart.Y; y <= inkEnd.Y; y++)
                    for (int x = (int)inkStart.X; x <= inkEnd.X; x++)
                    { int offset = (y * width + x) * 4; Require(away.AsSpan(offset, 4).SequenceEqual(clean.AsSpan(offset, 4)), "The negative pill patch damaged chevron pixels."); }
                    var awayDetector = new HandAcquisitionPresenceTracker();
                    for (int frame = 0; frame < 2; frame++)
                    {
                        var intact = awayDetector.Update(width, height, width * 4, away, context.SearchPolygon, context.ExpectedScene, now, now);
                        Require(intact.Hints.Count == 0 && intact.TextPatterns?.SingleOrDefault(pattern => pattern.ControlRegion == index) is
                            { LabelIntact: true, ShapeCorrupted: false, Correlation: > .8 },
                            "A large obstruction beside an intact gold chevron triggered acquisition: " + JsonSerializer.Serialize(intact));
                        now += TimeSpan.FromMilliseconds(33);
                    }
                }
                var occupied = (byte[])clean.Clone();
                double span = Math.Min(control.Width - .024, Math.Max(trigger.Width + .025, control.Width * .4));
                double center = trigger.X + trigger.Width / 2;
                var firstCorner = CameraPoint(Math.Max(control.X + .012, center - span / 2), control.Y + .008);
                var lastCorner = CameraPoint(Math.Min(control.X + control.Width - .012, center + span / 2), control.Y + control.Height - .008);
                for (int y = (int)firstCorner.Y; y < lastCorner.Y; y++)
                for (int x = (int)firstCorner.X; x < lastCorner.X; x++)
                { int offset = (y * width + x) * 4; occupied[offset] = 20; occupied[offset + 1] = 35; occupied[offset + 2] = 225; }
                var detector = new HandAcquisitionPresenceTracker();
                var first = detector.Update(width, height, width * 4, occupied, context.SearchPolygon, context.ExpectedScene, now, now);
                now += TimeSpan.FromMilliseconds(33);
                var second = detector.Update(width, height, width * 4, occupied, context.SearchPolygon, context.ExpectedScene, now, now);
                Require(first.Hints.Count == 0 && second.Hints.Any(hint => hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07),
                    $"The actual {button.Label} caption failed two-frame 7% acquisition: " + JsonSerializer.Serialize(new { first, second }));
                if (chevron)
                    Require(first.TextPatterns?.SingleOrDefault(pattern => pattern.ControlRegion == index) is
                            { ShapeCorrupted: true, ConfirmationFrames: 1 } &&
                        second.TextPatterns?.SingleOrDefault(pattern => pattern.ControlRegion == index) is
                            { ShapeCorrupted: true, ConfirmationFrames: 2 },
                        "Gold chevron obstruction qualified without two fresh rendered-shape confirmations.");
                tested.Add(button.Label); now += TimeSpan.FromMilliseconds(33);
            }
        }
        PixelPoint CameraPoint(double u, double v) => new(width * (.035 + .93 * (inset / 2 + u * (1 - inset))),
            height * (.035 + .93 * (inset / 2 + v * (1 - inset))));
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        async Task Capture(string name)
        {
            var pixels = Draw();
            foreach (int offset in new[] { 0, (width - 1) * 4, (height - 1) * width * 4, (width * height - 1) * 4 })
                Require(pixels[offset] == 0 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0, "The drawer escaped the physical board clip.");
            string path = Path.Combine(directory, name + ".png"); await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path, drawer = scene.GetMonopolyDrawerDiagnostics(now) });
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
