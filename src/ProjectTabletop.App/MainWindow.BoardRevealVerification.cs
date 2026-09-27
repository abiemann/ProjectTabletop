#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private compositors and an injected animation clock exercise projected
    // pixels without rescanning, moving hardware, or changing the user's board.
    private async Task<object> VerifyBoardRevealAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.BlackjackState.Revision);
        const int width = 960, height = 540;
        const double duration = 1300;
        var now = DateTimeOffset.UtcNow;
        var start = now;
        Vector2[] corners = [new(.12f, .14f), new(.88f, .12f), new(.9f, .85f), new(.11f, .88f)];
        Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var cameraMap = Homography.FromFourPoints(unit, unit);
        var boardMap = Homography.FromFourPoints(unit, corners.Select(p => new Point2(p.X, p.Y)).ToArray());
        string directory = Path.Combine(_appDataDirectory, "BoardRevealSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var preview = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 800, 800, 96);
        using var scene = NewScene();
        byte[] finalDot = Draw(scene);
        Require(White(finalDot, 0, 0) && Black(finalDot, width / 2, height / 2),
            "The reveal fixture did not begin on the last white-field center dot.");
        await Save(target, "00-final-calibration-dot");
        scene.CompleteBoardSetup(corners, cameraMap);
        Require(scene.BoardRevealActive && scene.GetBoardRevealDiagnostics().DurationMilliseconds == duration,
            "Successful setup did not start the expected reveal.");
        byte[] first = Draw(scene);
        Require(first.SequenceEqual(finalDot), "The transition jumped away from the final center calibration dot.");
        now = start.AddMilliseconds(260);
        byte[] blackGrowing = Draw(scene);
        Require(Count(blackGrowing, black: true) > Count(first, black: true) + 1000 &&
            Count(blackGrowing, black: false) < Count(first, black: false),
            "The black calibration circle did not expand over the white projector field.");
        await Save(target, "01-black-circle-growing");
        now = start.AddMilliseconds(800);
        byte[] menuSmall = Draw(scene);
        int smallArea = ColoredArea(menuSmall);
        Require(smallArea > 200 && smallArea < width * height * .8,
            "The board menu did not emerge from the center after the black expansion.");
        await Save(target, "02-menu-growing");
        now = start.AddMilliseconds(1100);
        byte[] menuLarge = Draw(scene);
        Require(ColoredArea(menuLarge) > smallArea + 1000,
            "The menu did not grow toward its calibrated final size.");
        await Save(target, "03-menu-nearly-full");
        Draw(scene, preview, 800, 800, isPreview: true);
        byte[] previewPixels = preview.GetPixelBytes();
        Require(BlackRect(previewPixels, 800, 0, 0, 800, 175) &&
            BlackRect(previewPixels, 800, 0, 625, 800, 800),
            "The expanding reveal leaked into the preview's letterboxing.");
        await Save(preview, "04-letterboxed-preview");
        now = start.AddMilliseconds(duration);
        byte[] final = Draw(scene);
        Require(!scene.BoardRevealActive, "The reveal did not complete at its declared duration.");
        using (var baseline = new SceneCompositor())
        {
            baseline.SetDisplayAspect(16.0 / 9);
            baseline.SetBoardSetup(true);
            baseline.SetDetectedBoardGrid(corners, cameraMap);
            baseline.SetBoardSetup(false);
            Require(final.SequenceEqual(Draw(baseline)),
                "The reveal's final frame changed the normal calibrated board geometry or content.");
        }
        Draw(scene);
        await Save(target, "05-complete-menu");

        // Actual source timestamps remain wall-clock based even while animation
        // frames advance deterministically. A held pulse must not replay when
        // the reveal ends, and a deliberate new pulse must remain usable.
        now = DateTimeOffset.UtcNow;
        start = now;
        using (var inputScene = NewScene())
        {
            inputScene.CompleteBoardSetup(corners, cameraMap);
            await Task.Delay(5);
            Point2 aim = boardMap.Transform(new(.28, .33));
            var frame = DateTimeOffset.UtcNow;
            var held = new HandCursor(new(aim.X, aim.Y), frame.AddSeconds(1), 91001) { TrackingId = 82001 };
            inputScene.SetHandCursors([held], frame);
            Require(inputScene.CurrentBoardScreen == BoardScreen.Menu && inputScene.HoveredBoardButtons.Count == 0,
                "Input navigated or hovered through the opening animation.");
            now = start.AddMilliseconds(duration + 1);
            Draw(inputScene);
            await Task.Delay(5);
            inputScene.SetHandCursors([held], DateTimeOffset.UtcNow);
            Require(inputScene.CurrentBoardScreen == BoardScreen.Menu,
                "A held transition pinch activated a button after the reveal.");
            await Task.Delay(5);
            frame = DateTimeOffset.UtcNow;
            inputScene.SetHandCursors([new(new(aim.X, aim.Y), frame.AddSeconds(1), 91002)
                { TrackingId = 82001 }], frame);
            Require(inputScene.CurrentBoardScreen == BoardScreen.HandTracking,
                "The reveal permanently blocked a fresh deliberate selection.");
        }

        foreach (string cancellation in new[] { "black", "clear-clip", "new-scan", "menu" })
        {
            now = DateTimeOffset.UtcNow;
            using var canceled = NewScene();
            canceled.CompleteBoardSetup(corners, cameraMap);
            now = now.AddMilliseconds(250);
            Draw(canceled);
            switch (cancellation)
            {
                case "black": canceled.SetBlackOutput(true); break;
                case "clear-clip": canceled.ClearBoardMediaClip(); break;
                case "new-scan": canceled.SetBoardSetup(true); break;
                default: canceled.ShowBoardMenu(); break;
            }
            Require(!canceled.BoardRevealActive, cancellation + " did not cancel the pending reveal.");
            byte[] immediate = Draw(canceled);
            now = now.AddSeconds(2);
            byte[] later = Draw(canceled);
            Require(immediate.SequenceEqual(later), "A canceled reveal resumed after " + cancellation + ".");
            if (cancellation is "black" or "clear-clip")
                Require(Count(later, black: true) == width * height,
                    cancellation + " did not keep all projector output black.");
        }

        now = DateTimeOffset.UtcNow;
        using (var restored = NewScene(BoardScreen.HandTracking))
        {
            restored.CompleteBoardSetup(corners, cameraMap);
            Require(restored.CurrentBoardScreen == BoardScreen.HandTracking,
                "Rescanning replaced the user's selected non-menu board.");
            now = now.AddMilliseconds(duration + 1);
            Draw(restored);
            Require(restored.CurrentBoardScreen == BoardScreen.HandTracking,
                "The reveal completion navigated away from the restored board.");
        }
        now = DateTimeOffset.UtcNow;
        using (var photo = NewScene(BoardScreen.PhotoCopy))
        {
            photo.CompleteBoardSetup(corners, cameraMap);
            now = now.AddMilliseconds(1100);
            Draw(photo);
            // Keep the reveal clock fixed while the real capture-settling timer
            // elapses: merely checking immediately after a draw would also pass
            // without an animation guard because Photo Copy waits one second.
            await Task.Delay(1100);
            Draw(photo);
            Require(!photo.TryGetPhotoCopyCaptureContext(out _),
                "Photo Copy treated its expanding scene as a settled capture field.");
        }

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.BlackjackState.Revision),
            "The isolated reveal verification changed live hardware or gameplay.");
        return new { passed = true, startsAtLastCenterDot = true, expandingBlackCircle = true,
            growingMenu = true, exactFinalCalibratedFrame = true, previewLetterbox = true,
            inputBlockedAndHeldPulseConsumed = true, cancellations = true, selectedBoardPreserved = true,
            photoCopyCaptureBlocked = true, liveHardwareUnchanged = true, durationMilliseconds = duration,
            directory, images };

        SceneCompositor NewScene(BoardScreen board = BoardScreen.Menu)
        {
            var result = new SceneCompositor(boardRevealClock: () => now);
            result.SetDisplayAspect(16.0 / 9);
            if (board == BoardScreen.HandTracking) result.ShowHandTrackingTest();
            else if (board == BoardScreen.PhotoCopy) result.ShowPhotoCopy();
            result.SetBoardSetup(true);
            result.ShowBoardCalibrationSpot(4);
            return result;
        }
        byte[] Draw(SceneCompositor drawingScene, CanvasRenderTarget? destination = null,
            int outputWidth = width, int outputHeight = height, bool isPreview = false)
        {
            destination ??= target;
            using (var drawing = destination.CreateDrawingSession())
                drawingScene.Draw(drawing, outputWidth, outputHeight, preview: isPreview, runningSlowly: false);
            return destination.GetPixelBytes();
        }
        async Task Save(CanvasRenderTarget image, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        static bool Black(byte[] pixels, int x, int y)
        {
            int i = (y * width + x) * 4;
            return pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0;
        }
        static bool White(byte[] pixels, int x, int y)
        {
            int i = (y * width + x) * 4;
            return pixels[i] > 250 && pixels[i + 1] > 250 && pixels[i + 2] > 250;
        }
        static int Count(byte[] pixels, bool black)
        {
            int result = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (black ? pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0
                    : pixels[i] > 250 && pixels[i + 1] > 250 && pixels[i + 2] > 250) result++;
            return result;
        }
        static int ColoredArea(byte[] pixels)
        {
            int result = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) -
                    Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) > 8) result++;
            return result;
        }
        static bool BlackRect(byte[] pixels, int pixelWidth, int left, int top, int right, int bottom)
        {
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int i = (y * pixelWidth + x) * 4;
                    if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) return false;
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
