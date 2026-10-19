#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.GlobeRendering;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated GPU/caption/file checks; never write to Pictures or change the
    // running projector, camera, user-selected model or board controls.
    private async Task<object> VerifyGlobeSaveAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int width = 3840, height = 2160;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int assertions = 0;
        var now = MonotonicClock.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "GlobeSaveVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
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
        now += GlobeState.EntranceDuration + TimeSpan.FromMilliseconds(100);
        Check(scene.ActivateGlobeButton("globe-drawer-open"), "Cannot open SAVE fixture controls.");
        now += TimeSpan.FromMilliseconds(301);
        scene.TickGlobe(now);
        Check(!scene.CurrentBoardButtons.Single(button => button.Id == "globe-save").Enabled &&
            !scene.ActivateGlobeButton("globe-save"), "SAVE was enabled before Earth loaded.");
        await scene.EnsureGlobeResourcesAsync(target.Device);
        Draw();
        Check(scene.ActivateGlobeButton("globe-model"), "Cannot expand model controls for SAVE fixture.");
        now += TimeSpan.FromMilliseconds(500);
        Check(session.SetGlobePitchPosition(.8, now) && session.SetGlobeZoomPosition(.25, now),
            "The save fixture could not choose a non-default pitch and zoom.");
        now += TimeSpan.FromSeconds(1);
        Draw();
        var save = scene.CurrentBoardButtons.Single(button => button.Id == "globe-save");
        var model = scene.CurrentBoardButtons.Single(button => button.Id == "globe-model");
        Check(save.Enabled && save.Hold == BoardButtonHold.Once && save.Label == "SAVE" &&
            save.Bounds.Y == model.Bounds.Y && save.Bounds.Height == model.Bounds.Height &&
            save.Bounds.Width == model.Bounds.Width && save.Bounds.X > model.Bounds.X + model.Bounds.Width,
            "SAVE is not a matching long-press button beside MODEL.");
        var context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No SAVE caption context.");
        var tracker = new HandAcquisitionPresenceTracker();
        byte[] clear = Draw();
        byte[] covered = CoverCaption(clear, context, "globe-save");
        Feed(clear);
        int activations = 0;
        GlobeSnapshot? selected = null;
        for (int frame = 0; frame < 15; frame++)
        {
            int count = Feed(covered).Count(id => id == "globe-save");
            activations += count;
            if (count > 0) selected = scene.GlobeState;
        }
        Check(activations == 1 && selected is not null &&
            !scene.CurrentBoardButtons.Single(button => button.Id == "globe-save").Enabled,
            "A held SAVE caption did not queue exactly one export and become busy.");
        Check(scene.TryTakeGlobeSaveRequest(out var first) && !scene.TryTakeGlobeSaveRequest(out _) &&
            !scene.ActivateGlobeButton("globe-save"), "SAVE duplicated a pending or writing image.");
        Check(first.Width == 3840 && Math.Abs(first.Width / (double)first.Height - scene.GlobePreviewAspect) < .001,
            "Saved image is not 4K with the board's aspect.");
        Check(first.BgraPixels.SequenceEqual(RenderArtwork(selected!, first.Width, first.Height)),
            "SAVE changed the selected time/zoom/pitch or included controls, labels, rails, or feedback.");
        string firstPath = await SaveGlobeMemoryImageAsync(scene, first, directory);
        paths.Add(firstPath);
        using (var png = await CanvasBitmap.LoadAsync(target.Device, firstPath))
            Check(png.GetPixelBytes().SequenceEqual(first.BgraPixels), "Saved PNG did not preserve the clean rendered pixels.");
        Check(scene.CurrentBoardButtons.Single(button => button.Id == "globe-save").Enabled &&
            scene.GetGlobeSaveStatus(now) == "Saved to Pictures folder" &&
            scene.GetGlobeSaveStatus(now.AddSeconds(3)) is null,
            "SAVE success did not restore the button with the expected temporary message.");

        foreach (var choice in new[] { "globe-model-no-clouds", "globe-model-night" })
        {
            now += TimeSpan.FromSeconds(1);
            Check(scene.ActivateGlobeButton(choice), "Could not choose " + choice + ".");
            Check(!scene.CurrentBoardButtons.Single(button => button.Id == "globe-save").Enabled &&
                !scene.ActivateGlobeButton("globe-save"), "SAVE accepted obsolete Earth textures during model selection.");
            await scene.EnsureGlobeResourcesAsync(target.Device);
            now += TimeSpan.FromSeconds(1);
            var state = scene.GlobeState;
            Check(scene.ActivateGlobeButton("globe-save"), "Direct SAVE rejected loaded " + state.Model + ".");
            now += TimeSpan.FromSeconds(2);
            Check(scene.TryTakeGlobeSaveRequest(out var image) &&
                image.BgraPixels.SequenceEqual(RenderArtwork(state, image.Width, image.Height)),
                "Direct SAVE did not freeze the selected model and animation time.");
            paths.Add(await SaveGlobeMemoryImageAsync(scene, image, directory));
        }
        Check(paths.Distinct().Count() == 3 && paths.All(File.Exists) &&
            !Directory.EnumerateFiles(directory, "*.tmp").Any(), "Repeated exports collided or left partial image files.");
        Check(BoardImageDirectories.Globe == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Pictures", "Globe"), "Globe artwork does not target its own local Pictures folder.");

        now += TimeSpan.FromSeconds(1);
        Check(scene.ActivateGlobeButton("globe-save"), "The write-failure fixture could not request an image.");
        Check(scene.TryTakeGlobeSaveRequest(out var failure), "The write-failure fixture could not capture an image.");
        string blocked = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "write failure fixture");
        bool failed = false;
        try { await SaveGlobeMemoryImageAsync(scene, failure, blocked); }
        catch (IOException) { failed = true; }
        Check(failed && scene.GetGlobeSaveStatus(now) == "Save failed - retry" &&
            scene.CurrentBoardButtons.Single(button => button.Id == "globe-save").Enabled,
            "A file error left SAVE busy or falsely reported success.");
        now += TimeSpan.FromSeconds(1);
        Check(scene.ActivateGlobeButton("globe-save"), "SAVE could not retry after an error.");
        scene.ShowBoardMenu();
        Check(!scene.TryTakeGlobeSaveRequest(out _) && scene.GetGlobeSaveStatus(now) is null,
            "Navigation retained a pending export or leaked its status onto another board.");
        Check(liveOutput == _output && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "SAVE verification changed the live application or camera.");
        return new { passed = true, assertions, directory, paths, captionActivations = activations,
            cleanArtworkOnly = true, models = 3, frozenPresentation = true, fileFailureRetry = true,
            liveHardwareUnchanged = true, noPicturesWrites = true, saveDirectory = BoardImageDirectories.Globe };

        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        IReadOnlyList<string> Feed(byte[] pixels)
        {
            now += TimeSpan.FromMilliseconds(100);
            var result = tracker.Update(width, height, width * 4, pixels, context.SearchPolygon,
                context.ExpectedScene, now, now);
            return scene.ObserveHoldButtons(context, context.HeldButtons(result), now,
                context.ClearedButtons(result), context.BlockedButtons(result));
        }
        byte[] CoverCaption(byte[] pixels, SceneCompositor.HoldButtonContext hold, string id)
        {
            int index = hold.ButtonIds.ToList().IndexOf(id);
            var ink = hold.ExpectedScene.BoardTriggerRegions![index];
            var surface = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap", flags)!.GetValue(scene)!;
            var firstCorner = surface.Transform(new(ink.X - .004, ink.Y - .004));
            var lastCorner = surface.Transform(new(ink.X + ink.Width + .004, ink.Y + ink.Height + .004));
            byte[] result = (byte[])pixels.Clone();
            for (int y = Math.Max(0, (int)(firstCorner.Y * height)); y <= Math.Min(height - 1, lastCorner.Y * height); y++)
            for (int x = Math.Max(0, (int)(firstCorner.X * width)); x <= Math.Min(width - 1, lastCorner.X * width); x++)
            {
                int offset = (y * width + x) * 4;
                result[offset] = 95; result[offset + 1] = 135; result[offset + 2] = 185;
            }
            return result;
        }
        byte[] RenderArtwork(GlobeSnapshot state, int imageWidth, int imageHeight)
        {
            var renderer = (GlobeSurfaceRenderer)typeof(SceneCompositor).GetField("_globeRenderer", flags)!.GetValue(scene)!;
            using var artwork = new CanvasRenderTarget(target.Device, imageWidth, imageHeight, 96);
            using (var drawing = artwork.CreateDrawingSession())
            {
                drawing.Clear(Colors.Black);
                drawing.Transform = Matrix3x2.CreateScale(imageWidth / 1000f, imageHeight / 1000f);
                Check(renderer.Draw(drawing, (float)state.Zoom, (float)state.RotationDegrees, scene.GlobePreviewAspect,
                    (float)state.ViewLatitudeDegrees, state.ElapsedSeconds, state.Model), "Reference Earth rendering failed.");
            }
            return artwork.GetPixelBytes();
        }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
