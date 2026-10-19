#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.GlobeRendering;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated GPU/model lifecycle and real caption evidence. This fixture
    // never opens a window, starts a camera, or changes the live board.
    private async Task<object> VerifyGlobeModelsAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int width = 3840, height = 2160;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int assertions = 0;
        var now = MonotonicClock.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "GlobeModelVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var modelChecks = new List<object>();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var scene = new SceneCompositor(blackjackClock: () => now, globeClock: () => now);
        var session = (BoardSession)typeof(SceneCompositor).GetField("_boardSession", flags)!.GetValue(scene)!;
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowGlobe();
        var renderer = (GlobeSurfaceRenderer)typeof(SceneCompositor).GetMethod("GetGlobeRenderer", flags)!
            .Invoke(scene, [target.Device])!;
        Check(renderer.ModelTextureLoadCount == 0 && renderer.ResidentModelCount == 0,
            "Creating Globe eagerly loaded Earth model textures.");
        await scene.EnsureGlobeResourcesAsync(target.Device);
        Check(renderer.SelectedModel == GlobeModel.Clouds && renderer.LoadedModel == GlobeModel.Clouds &&
                renderer.ModelTextureLoadCount == 2 && renderer.ResidentModelCount == 1 &&
                renderer.ResidentModelTextureCount == 2 && renderer.SkyTextureLoadCount == 1,
            "Initial CLOUDS did not load only its day/cloud textures and one shared sky.");
        now += GlobeState.EntranceDuration + TimeSpan.FromMilliseconds(100);
        Draw();
        Check(scene.ActivateGlobeButton("globe-drawer-open"), "Cannot open model fixture controls.");
        now += TimeSpan.FromMilliseconds(301);
        scene.TickGlobe(now);
        Draw();
        Check(scene.ActivateGlobeButton("globe-model"), "MODEL did not expand its choices.");
        now += TimeSpan.FromMilliseconds(500);
        Draw();
        string[] choiceIds = ["globe-model-clouds", "globe-model-no-clouds", "globe-model-night"];
        var choices = scene.CurrentBoardButtons.Where(button => choiceIds.Contains(button.Id)).ToArray();
        Check(choices.Length == 3 && choices.Select(button => button.Label).SequenceEqual(["CLOUDS", "NO CLOUDS", "NIGHT"]) &&
                choices.All(button => button.Hold == BoardButtonHold.Once && button.Bounds.Height == .12 &&
                    Math.Abs(button.Bounds.Y - .735) < .000001),
            "Expanded MODEL choices do not use the requested uppercase hold-button row.");
        CheckCaptionFit();
        CheckInputRevisionPreservesCaptionOptics();
        byte[]? clouds = null, noClouds = null;
        foreach (var choice in new[] { (Id: choiceIds[0], Model: GlobeModel.Clouds, Textures: 2, Loads: 2),
            (Id: choiceIds[1], Model: GlobeModel.NoClouds, Textures: 1, Loads: 3),
            (Id: choiceIds[2], Model: GlobeModel.Night, Textures: 1, Loads: 4) })
        {
            int activations = await SelectByCaption(choice.Id, choice.Model);
            Check(renderer.SelectedModel == choice.Model && renderer.LoadedModel == choice.Model && renderer.IsReady &&
                    !renderer.IsLoading && renderer.ResidentModelCount == 1 &&
                    renderer.ResidentModelTextureCount == choice.Textures && renderer.ModelTextureLoadCount == choice.Loads &&
                    renderer.SkyTextureLoadCount == 1,
                choice.Model + " retained obsolete model textures or reloaded the sky.");
            byte[] pixels = RenderModel(choice.Model);
            string path = Path.Combine(directory, choice.Model.ToString().ToLowerInvariant() + "-4k.png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
            if (choice.Model == GlobeModel.Clouds) clouds = pixels;
            else
            {
                var changes = CompareEarth(clouds!, pixels);
                Check(changes.Inside > 1000 && changes.Outside == 0,
                    choice.Model + " failed to change Earth, or changed the same-clock sky/stars outside its atmosphere.");
                if (choice.Model == GlobeModel.NoClouds) noClouds = pixels;
                else Check(CompareEarth(noClouds!, pixels).Inside > 10000, "NIGHT looks indistinguishable from NO CLOUDS.");
            }
            modelChecks.Add(new { choice.Model, activations, renderer.ModelTextureLoadCount,
                renderer.ResidentModelTextureCount, renderer.ResidentModelCount });
        }

        // Returning to a used choice reloads it instead of retaining every model.
        CheckPartialSwipeCaptionScopes();
        int repeatedLoads = renderer.ModelTextureLoadCount;
        await renderer.EnsureReadyAsync(GlobeModel.Night);
        Check(renderer.ModelTextureLoadCount == repeatedLoads, "Selecting the resident model repeated disk/GPU loading.");
        await SelectByCaption("globe-model", GlobeModel.Night);
        Check(!session.GlobeModelExpanded && scene.CurrentBoardButtons.All(button => !choiceIds.Contains(button.Id)),
            "A second MODEL hold did not close the choices.");
        await SelectByCaption("globe-model", GlobeModel.Night);
        Check(session.GlobeModelExpanded && renderer.ModelTextureLoadCount == repeatedLoads,
            "Reopening MODEL lost the selected view or loaded textures again.");
        now += TimeSpan.FromMilliseconds(100);
        Check(scene.ActivateGlobeButton("globe-drawer-close") && session.GlobeModelExpanded &&
                scene.GlobeState.Model == GlobeModel.Night && scene.CurrentBoardButtons.Count == 1,
            "Closing the drawer lost the selected model or left its options active.");
        now += TimeSpan.FromMilliseconds(100);
        Check(scene.ActivateGlobeButton("globe-drawer-open"), "Cannot reopen model controls.");
        now += TimeSpan.FromMilliseconds(301);
        scene.TickGlobe(now);
        Draw();
        Check(scene.CurrentBoardButtons.Any(button => button.Id == "globe-model-night") && session.GlobeModelExpanded,
            "Reopening the drawer did not restore its expanded MODEL choices.");
        Check(session.SetButtonsVisible(false, now), "Cannot hide model controls.");
        now += TimeSpan.FromMilliseconds(301);
        Draw();
        Check(scene.CurrentBoardButtons.Count == 0 && session.GlobeModelExpanded && scene.GlobeState.Model == GlobeModel.Night &&
                !scene.ActivateGlobeButton("globe-model-clouds"),
            "Hidden model controls remained interactive or reset the selected Earth view.");
        Check(session.SetButtonsVisible(true, now), "Cannot restore model controls.");
        now += TimeSpan.FromMilliseconds(301);
        Draw();
        Check(scene.CurrentBoardButtons.Any(button => button.Id == "globe-model-night") &&
                renderer.ModelTextureLoadCount == repeatedLoads,
            "Control visibility did not preserve the model choices and resident texture.");
        scene.ShowBoardMenu();
        scene.ShowGlobe();
        Check(scene.GlobeState.Model == GlobeModel.Clouds && !session.GlobeModelExpanded,
            "A new Globe visit retained the previous model selection or expanded menu.");
        await scene.EnsureGlobeResourcesAsync(target.Device);
        Check(renderer.ModelTextureLoadCount == repeatedLoads + 2 && renderer.ResidentModelTextureCount == 2 &&
                renderer.ResidentModelCount == 1 && renderer.SkyTextureLoadCount == 1,
            "Returning to CLOUDS did not reload only its two textures while preserving the sky.");

        Task older = renderer.EnsureReadyAsync(GlobeModel.Night);
        Task latest = renderer.EnsureReadyAsync(GlobeModel.NoClouds);
        await Task.WhenAll(older, latest);
        Check(renderer.SelectedModel == GlobeModel.NoClouds && renderer.LoadedModel == GlobeModel.NoClouds &&
                renderer.ResidentModelCount == 1 && renderer.ResidentModelTextureCount == 1 && !renderer.IsLoading,
            "An older asynchronous model request replaced the latest choice or left multiple models resident.");
        RenderModel(GlobeModel.NoClouds);
        Task interrupted = renderer.EnsureReadyAsync(GlobeModel.Night);
        renderer.Dispose();
        try { await interrupted; }
        catch (ObjectDisposedException) { }
        Check(renderer.LoadedModel is null && renderer.ResidentModelCount == 0 &&
                renderer.ResidentModelTextureCount == 0 && !renderer.IsReady,
            "Disposal during model loading published or retained an Earth texture.");
        Check(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Model verification changed the user's camera, projector or board.");
        return new { passed = true, directory, images, assertions, modelChecks,
            realCaptionHolds = true, singleResidentModel = true, lazyModelTextures = true,
            unchangedSharedSky = true, latestRequestWins = true, disposeDuringLoading = true,
            partialSwipePreservesUpperCaptions = true, inputRevisionPreservesCaptionOptics = true,
            drawerAndSwipeRestoreChoices = true, newVisitRestoresClouds = true, liveHardwareUnchanged = true };

        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        byte[] RenderModel(GlobeModel model)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Colors.Black);
                drawing.Transform = Matrix3x2.CreateScale(width / 1000f, height / 1000f);
                Check(renderer.DrawFrame(drawing, .8f, 58.2972f, width / (double)height, 34.0522f, 0, 0, model),
                    "The ready " + model + " GPU frame could not render.");
            }
            return target.GetPixelBytes();
        }
        void CheckInputRevisionPreservesCaptionOptics()
        {
            byte[] clear = Draw();
            var context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No optical reset fixture context.");
            // Exercise the production caption entry point without constructing
            // a window or borrowing the live camera's optical history.
            var owner = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            GC.SuppressFinalize(owner);
            var tracker = new HandAcquisitionPresenceTracker();
            Set("_holdPresence", tracker);
            Set("_holdDetectionGate", new object());
            Set("_handGate", new object());
            Set("_closing", true);
            var opticsField = typeof(HandAcquisitionPresenceTracker).GetField("_textPatterns", flags)!;
            var first = Observe(context, 0);
            object? optics = opticsField.GetValue(tracker);
            Check(optics is not null && first.Cleared.Contains("globe-model-night"),
                "The optical fixture did not first verify uncovered NIGHT lettering.");
            var inputOnly = context with { Revision = context.Revision + 1 };
            Check(Observe(inputOnly, 0).Cleared.Contains("globe-model-night") &&
                ReferenceEquals(optics, opticsField.GetValue(tracker)),
                "An input-only revision discarded the verified caption optics.");
            Observe(inputOnly, 1);
            object? afterGeneration = opticsField.GetValue(tracker);
            Check(afterGeneration is not null && !ReferenceEquals(optics, afterGeneration),
                "A camera generation change retained old optical calibration.");
            Observe(inputOnly with { ExpectedScene = context.ExpectedScene with { } }, 1);
            Check(!ReferenceEquals(afterGeneration, opticsField.GetValue(tracker)),
                "Changed rendered lettering retained the previous optical template.");

            HoldButtonQuery Observe(SceneCompositor.HoldButtonContext captured, long generation) =>
                owner.FindHeldButtons(new CameraFrame(width, height, width * 4, clear, MonotonicClock.UtcNow), captured, generation);
            void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(owner, value);
        }
        void CheckPartialSwipeCaptionScopes()
        {
            now += TimeSpan.FromMilliseconds(500);
            Draw();
            var context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No partial-swipe hold context.");
            scene.ObserveHoldButtons(context, [], now, context.EnabledHoldIds);
            for (int frame = 0; frame < 4; frame++)
            {
                now += TimeSpan.FromMilliseconds(100);
                Check(scene.ObserveHoldButtons(context, ["globe-model"], now, []).Count == 0,
                    "MODEL acted before the partial-swipe fixture.");
            }
            Check(scene.CurrentHoldProgress.Any(item => item.ButtonId == "globe-model"),
                "The footer did not have partial hold progress before the swipe.");
            scene.GetControlSwipeContext(now);
            now += TimeSpan.FromMilliseconds(100);
            var band = scene.GetControlSwipeContext(now) ?? throw new InvalidOperationException("No partial-swipe band.");
            var nightInk = context.ExpectedScene.BoardTriggerRegions![context.ButtonIds.ToList().IndexOf("globe-model-night")];
            var modelInk = context.ExpectedScene.BoardTriggerRegions![context.ButtonIds.ToList().IndexOf("globe-model")];
            Check(nightInk.Y + nightInk.Height / 2 < band.Top && modelInk.Y + modelInk.Height / 2 >= band.Top,
                "The native caption centers do not separate the model row from the footer swipe band.");
            Check(!scene.CompleteControlSwipe(band, null, traversalInProgress: true, now) &&
                scene.CurrentHoldProgress.All(item => item.ButtonId != "globe-model"),
                "A partial footer sweep did not cancel its MODEL hold.");
            now += TimeSpan.FromMilliseconds(100);
            Check(!scene.CompleteControlSwipe(band, null, traversalInProgress: false, now),
                "A partial sweep toggled the controls.");
            context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No post-swipe caption context.");
            for (int frame = 0; frame < 12; frame++)
            {
                now += TimeSpan.FromMilliseconds(100);
                Check(scene.ObserveHoldButtons(context, ["globe-model"], now, []).Count == 0 && session.GlobeModelExpanded,
                    "The footer MODEL rearmed after a sweep without fresh uncovered lettering.");
            }
            int activations = 0;
            for (int frame = 0; frame < 12; frame++)
            {
                now += TimeSpan.FromMilliseconds(100);
                activations += scene.ObserveHoldButtons(context, ["globe-model-night"], now, [],
                    ["globe-model", "globe-save"]).Count(id => id == "globe-model-night");
            }
            Check(activations == 1 && session.GlobeModelExpanded && scene.GlobeState.Model == GlobeModel.Night,
                "A partial footer sweep erased the upper NIGHT caption's previously verified clear readiness.");
        }
        async Task<int> SelectByCaption(string id, GlobeModel expectedModel)
        {
            now += TimeSpan.FromMilliseconds(500);
            byte[] clear = Draw();
            var context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No model hold context.");
            int index = context.ButtonIds.ToList().IndexOf(id);
            var surface = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap", flags)!.GetValue(scene)!;
            byte[] covered = (byte[])clear.Clone();
            foreach (string coveredId in choiceIds.Contains(id) ? new[] { id, "globe-model", "globe-save" } : new[] { id })
            {
                var ink = context.ExpectedScene.BoardTriggerRegions![context.ButtonIds.ToList().IndexOf(coveredId)];
                var first = surface.Transform(new(ink.X - .004, ink.Y - .004));
                var last = surface.Transform(new(ink.X + ink.Width + .004, ink.Y + ink.Height + .004));
                for (int y = Math.Max(0, (int)(first.Y * height)); y <= Math.Min(height - 1, last.Y * height); y++)
                for (int x = Math.Max(0, (int)(first.X * width)); x <= Math.Min(width - 1, last.X * width); x++)
                {
                    int offset = (y * width + x) * 4;
                    covered[offset] = 95; covered[offset + 1] = 135; covered[offset + 2] = 185;
                }
            }
            var tracker = new HandAcquisitionPresenceTracker();
            int acts = 0;
            for (int frame = 0; frame < 5; frame++) Feed(clear);
            if (choiceIds.Contains(id))
            {
                for (int frame = 0; frame < 10; frame++)
                {
                    now += TimeSpan.FromMilliseconds(100);
                    Check(scene.ObserveHoldButtons(context, ["globe-model"], now, []).Count == 0,
                        "MODEL completed before the option veto fixture began.");
                }
                Check(scene.CurrentHoldProgress.Any(progress => progress.ButtonId == "globe-model" && progress.Progress >= .89),
                    "The option veto fixture failed to start a nearly complete MODEL hold.");
            }
            for (int frame = 0; frame < 15; frame++) acts += Feed(covered).Count(action => action == id);
            Check(acts == 1 && scene.GlobeState.Model == expectedModel && scene.ActiveHandSpotlightCount == 0,
                id + " did not act exactly once from actual covered lettering without a spotlight.");
            await scene.EnsureGlobeResourcesAsync(target.Device);
            clear = Draw();
            context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No refreshed model hold context.");
            for (int frame = 0; frame < 5; frame++)
                Check(Feed(clear).Count == 0, "Lifting from a model caption activated another control.");
            return acts;

            IReadOnlyList<string> Feed(byte[] pixels)
            {
                now += TimeSpan.FromMilliseconds(100);
                var result = tracker.Update(width, height, width * 4, pixels, context.SearchPolygon,
                    context.ExpectedScene, now, now);
                var blocked = context.BlockedButtons(result);
                if (choiceIds.Contains(id) && result.TextPatterns?.Any(pattern =>
                        pattern.ControlRegion == index && pattern.CaptionHoldEvidence && pattern.ConfirmationFrames == 1) == true)
                {
                    Check(blocked.SequenceEqual(["globe-model", "globe-save"]), "The first damaged option frame failed to veto MODEL and SAVE.");
                    Check(context.HeldButtons(result).Contains("globe-model") && context.HeldButtons(result).Contains("globe-save"),
                        "The reach fixture did not cover MODEL, SAVE and the option together.");
                    var intact = result with { TextPatterns = result.TextPatterns!.Select(pattern => pattern with
                        { ShapeCorrupted = false, LabelIntact = true, CaptionReflectanceChanged = true }).ToArray() };
                    Check(context.BlockedButtons(intact).Count == 0, "Readable option lettering changing brightness vetoed MODEL.");
                }
                var actions = scene.ObserveHoldButtons(context, context.HeldButtons(result), now, context.ClearedButtons(result), blocked);
                if (blocked.Contains("globe-model"))
                    Check(session.GlobeModelExpanded && !scene.CurrentHoldProgress.Any(progress => progress.ButtonId == "globe-model"),
                        "Disturbing a model choice closed the menu or kept partial MODEL progress.");
                return actions;
            }
        }
        void CheckCaptionFit()
        {
            var makeFormat = typeof(SceneCompositor).GetMethod("HoldButtonCaptionFormat", BindingFlags.Static | BindingFlags.NonPublic)!;
            var makeRectangle = typeof(SceneCompositor).GetMethod("DrawerButtonTextRectangle", BindingFlags.Static | BindingFlags.NonPublic)!;
            var makeCaption = typeof(SceneCompositor).GetMethod("DrawerButtonCaption", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (double aspect in new[] { .78, 2777d / 2160, 16d / 9 })
            foreach (var button in choices)
            {
                var rect = (Rect)makeRectangle.Invoke(null, [button])!;
                using var format = (CanvasTextFormat)makeFormat.Invoke(null, [])!;
                format.FontSize *= (float)Math.Min(1, aspect);
                string caption = (string)makeCaption.Invoke(null, [button])!;
                using var layout = new CanvasTextLayout(target.Device, caption, format,
                    (float)(rect.Width * aspect), (float)rect.Height);
                var ink = layout.DrawBounds;
                double left = rect.X + ink.X / aspect, right = left + ink.Width / aspect;
                Check(left >= button.Bounds.X * 1000 + 4 && right <= (button.Bounds.X + button.Bounds.Width) * 1000 - 4 &&
                        ink.Height <= rect.Height,
                    $"{button.Label} caption overflows its physical button at aspect {aspect:F3}: [{left:F2},{right:F2}].");
            }
        }
        static (int Inside, int Outside) CompareEarth(byte[] first, byte[] second)
        {
            int inside = 0, outside = 0;
            double radius = .335 * .8 * height;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                if (Math.Max(Math.Abs(first[index] - second[index]), Math.Max(Math.Abs(first[index + 1] - second[index + 1]),
                    Math.Abs(first[index + 2] - second[index + 2]))) <= 2) continue;
                double distance = Math.Pow(x - width / 2d, 2) + Math.Pow(y - height / 2d, 2);
                if (distance < radius * radius) inside++;
                else if (distance > Math.Pow(radius * 1.2, 2)) outside++;
            }
            return (inside, outside);
        }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
