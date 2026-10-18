#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task<object> VerifyRouletteFooterVisibilityAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        string directory = Path.Combine(_appDataDirectory, "RouletteFooterVisibilityVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var aspects = new List<object>();
        int checks = 0, unchangedTargetChecks = 0;

        foreach (var aspect in new[] { (Width: 1152, Height: 896, Name: "landscape"),
            (Width: 720, Height: 1280, Name: "portrait") })
        {
            using var fixture = new RouletteNativeFixture(aspect.Width, aspect.Height, new RouletteGame(81));
            var scene = fixture.Scene;
            var session = (BoardSession)typeof(SceneCompositor).GetField("_boardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(scene)!;
            scene.ShowRoulette();
            await scene.EnsureBoardArtworkResourcesAsync(fixture.Target.Device, BoardScreen.Roulette);
            Check(scene.ActivateRouletteButton("roulette-number-7"), "Could not prepare the visible footer fixture.");
            byte[] shown = await Capture("shown");
            var original = scene.CurrentBoardButtons.ToArray();
            var persistent = original.Where(button => !button.IsHold).ToArray();
            var footer = original.Where(button => button.IsHold).ToArray();
            Check(persistent.Length == 53 && footer.Length == 5, "Unexpected Roulette playing-surface/footer control counts.");
            var oldHold = scene.GetHoldButtonContext(fixture.Now)!;
            var unchangedGame = scene.RouletteState;

            Toggle(expectedVisible: false);
            CheckPersistentPixels(await Capture("hide-start"));
            fixture.Now += TimeSpan.FromMilliseconds(150);
            byte[] hiding = await Capture("hide-half");
            CheckPersistentPixels(hiding);
            fixture.Now += TimeSpan.FromMilliseconds(200);
            byte[] hidden = await Capture("hidden");
            CheckPersistentPixels(hidden);
            CheckHiddenTargets();
            foreach (var button in footer)
                Check(!fixture.RegionUnchanged(shown, hidden, button.Bounds),
                    "The hidden footer still rendered " + button.Id + ".");
            CheckHalfFade(shown, hiding, hidden, "Hiding");
            Check(scene.RouletteState == unchangedGame, "Hiding the footer modified Roulette's wager or phase.");
            Check(scene.GetHoldButtonContext(fixture.Now) is null &&
                    scene.ObserveHoldButtons(oldHold, ["roulette-clear"], fixture.Now, []).Count == 0,
                "Hidden footer captions still accepted a hold reference.");
            foreach (var button in footer)
                Check(!scene.ActivateRouletteButton(button.Id), "A hidden footer action remained directly selectable: " + button.Id + ".");

            scene.GetHandAcquisitionContext(fixture.Now);
            fixture.Now += TimeSpan.FromMilliseconds(600);
            fixture.Draw();
            var acquisition = scene.GetHandAcquisitionContext(fixture.Now)!;
            Check(acquisition.ObserveMotion && acquisition.ExpectedScene is not null &&
                    acquisition.ExpectedScene.BoardSearchRegions?.Count == 53 &&
                    acquisition.ExpectedScene.BoardTriggerRegions?.Count == 53 &&
                    acquisition.HoldControls?.Count == 0,
                "Hiding the footer removed the chip/table acquisition regions or retained hidden hold regions.");
            var detector = new HandAcquisitionPresenceTracker();
            for (int frame = 0; frame < 2; frame++)
            {
                fixture.Now += TimeSpan.FromMilliseconds(100);
                var result = detector.Update(aspect.Width, aspect.Height, aspect.Width * 4, fixture.Draw(),
                    acquisition.SearchPolygon, acquisition.ExpectedScene, fixture.Now, fixture.Now);
                Check(result.BaselineReady && result.Reason != "invalid-rendered-scene" && result.Hints.Count == 0,
                    "The hidden-footer reference was invalid or treated the clear betting surface as an obstruction.");
            }

            Toggle(expectedVisible: true);
            CheckPersistentPixels(await Capture("show-start"));
            fixture.Now += TimeSpan.FromMilliseconds(150);
            byte[] showing = await Capture("show-half");
            CheckPersistentPixels(showing);
            CheckHalfFade(shown, showing, hidden, "Showing");
            fixture.Now += TimeSpan.FromMilliseconds(200);
            byte[] restored = await Capture("restored");
            CheckPersistentPixels(restored);
            Check(scene.CurrentBoardButtons.SequenceEqual(original), "Restoring the footer did not restore its five original controls.");
            foreach (var button in footer)
                Check(fixture.RegionUnchanged(shown, restored, button.Bounds), "Restored footer appearance changed: " + button.Id + ".");

            // Exercise real gesture selection against the same BoardSession
            // used by the compositor, after the footer's fade has settled.
            Toggle(expectedVisible: false);
            fixture.Now += TimeSpan.FromMilliseconds(350);
            fixture.Draw();
            CheckHiddenTargets();
            Gesture("roulette-chip-5", 1001);
            Check(scene.RouletteState.Chip == 5 && scene.RouletteState.TotalBet == 25 && scene.RouletteState.Balance == 975,
                "A hidden footer prevented chip selection or altered the existing bet.");
            Gesture("roulette-red", 1002);
            Check(scene.RouletteState.Chip == 5 && scene.RouletteState.TotalBet == 30 && scene.RouletteState.Balance == 970,
                "The persistent betting surface did not accept a gesture with the selected five-credit chip.");
            Check(scene.CurrentBoardScreen == BoardScreen.Roulette && !scene.BoardButtonsVisible,
                "Selecting a persistent Roulette control restored the footer or left the board.");
            fixture.Draw();
            scene.GetHandAcquisitionContext(fixture.Now);
            fixture.Now += TimeSpan.FromMilliseconds(600);
            fixture.Draw();
            var updated = scene.GetHandAcquisitionContext(fixture.Now);
            Check(updated?.ExpectedScene?.BoardSearchRegions?.Count == 53,
                "A bet placed with the footer hidden lost its current acquisition reference.");
            Toggle(expectedVisible: true);
            fixture.Now += TimeSpan.FromMilliseconds(350);
            fixture.Draw();
            Check(scene.CurrentBoardButtons.Count == 58 && scene.RouletteState.TotalBet == 30,
                "Restoring the footer after hidden betting lost controls or changed the wager.");
            aspects.Add(new { aspect.Name, aspect.Width, aspect.Height, persistentControls = persistent.Length,
                footerControls = footer.Length, hiddenChip = 5, hiddenTotalBet = 30 });

            void Toggle(bool expectedVisible)
            {
                fixture.Draw();
                scene.GetControlSwipeContext(fixture.Now);
                fixture.Now += TimeSpan.FromMilliseconds(700);
                fixture.Draw();
                var context = scene.GetControlSwipeContext(fixture.Now) ??
                    throw new InvalidOperationException("Roulette did not rearm its bottom swipe band.");
                fixture.Now += TimeSpan.FromMilliseconds(20);
                Check(scene.CompleteControlSwipe(context, BoardControlSwipe.Toggle, false, fixture.Now) &&
                        scene.BoardButtonsVisible == expectedVisible,
                    "An actual bottom-swipe completion did not toggle Roulette's footer.");
            }

            void CheckHiddenTargets() => Check(scene.CurrentBoardButtons.Count == 53 &&
                    scene.CurrentBoardButtons.All(button => !button.IsHold) &&
                    scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(persistent.Select(button => button.Id)),
                "The hidden-footer input target set did not preserve all 53 betting controls.");

            void CheckPersistentPixels(byte[] pixels)
            {
                foreach (var button in persistent)
                {
                    Check(fixture.RegionUnchanged(shown, pixels, button.Bounds),
                        "A footer fade changed persistent chip/grid pixels: " + aspect.Name + "/" + button.Id + ".");
                    unchangedTargetChecks++;
                }
            }

            void CheckHalfFade(byte[] visible, byte[] middle, byte[] absent, string phase)
            {
                double squaredContrast = 0, projectedContrast = 0, error = 0;
                int samples = 0;
                foreach (var button in footer)
                {
                    var first = fixture.CameraPoint(button.Bounds.X, button.Bounds.Y);
                    var last = fixture.CameraPoint(button.Bounds.X + button.Bounds.Width, button.Bounds.Y + button.Bounds.Height);
                    for (int y = Math.Max(0, (int)Math.Ceiling(first.Y)); y < Math.Min(aspect.Height, (int)Math.Floor(last.Y)); y++)
                    for (int x = Math.Max(0, (int)Math.Ceiling(first.X)); x < Math.Min(aspect.Width, (int)Math.Floor(last.X)); x++)
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int offset = (y * aspect.Width + x) * 4 + channel;
                        double contrast = visible[offset] - absent[offset];
                        if (Math.Abs(contrast) < 20) continue;
                        squaredContrast += contrast * contrast;
                        projectedContrast += (middle[offset] - absent[offset]) * contrast;
                        error += Math.Abs(middle[offset] - (visible[offset] + absent[offset]) / 2d);
                        samples++;
                    }
                }
                Check(samples > 150 && projectedContrast / squaredContrast is > .46 and < .54 && error / samples < 3,
                    phase + " Roulette footer did not blend at half opacity: " + aspect.Name + ".");
            }

            void Gesture(string id, long eventId)
            {
                fixture.Now += TimeSpan.FromMilliseconds(100);
                var button = session.Buttons.Single(button => button.Id == id);
                var gesture = new BoardHandSample(button.Bounds.X + button.Bounds.Width / 2,
                    button.Bounds.Y + button.Bounds.Height / 2, fixture.Now.AddSeconds(1), eventId);
                Check(session.Update([gesture], fixture.Now, fixture.Now)?.ButtonId == id,
                    "A hidden-footer gesture did not select " + id + ".");
            }

            async Task<byte[]> Capture(string name)
            {
                byte[] pixels = fixture.Draw();
                string path = Path.Combine(directory, aspect.Name + "-" + name + ".png");
                await fixture.Target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
                return pixels;
            }
        }
        Check(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The isolated footer visibility checks changed live hardware or navigation.");
        return new { passed = true, directory, images, aspects, checks, unchangedTargetChecks,
            footerOnlyFades = true, chipAndBettingPixelsUnchanged = true, hiddenBettingGesturesWork = true,
            hiddenFooterCannotActivate = true, nativeAcquisitionReferenceRetained = true, liveHardwareUnchanged = true };

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }
}
#endif
