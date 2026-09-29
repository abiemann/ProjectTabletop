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
        const double duration = 1300, flair = 1400;
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
        var physicalCorners = corners.Select(point => new Point2(point.X, point.Y)).ToArray();
        Require(scene.GetDetectedBoardCorners() is { } measuredCorners && measuredCorners.SequenceEqual(physicalCorners),
            "Completing setup lost the physical board edges or replaced them with the safety inset.");
        var copiedCorners = scene.GetDetectedBoardCorners()!;
        copiedCorners[0] = new(0, 0);
        Require(scene.GetDetectedBoardCorners()!.SequenceEqual(physicalCorners),
            "A measurement caller changed the saved physical board edges.");
        Require(scene.BoardRevealActive && scene.GetBoardRevealDiagnostics().DurationMilliseconds == duration,
            "Successful setup did not start the expected reveal.");
        byte[] first = Draw(scene);
        Require(first.SequenceEqual(finalDot), "The transition jumped away from the final center calibration dot.");
        // Corner flair: four orange arrow tips leave the final dot and land flush in
        // the measured corners as brackets; test-card strips run along the edges.
        Require(scene.GetBoardRevealDiagnostics().FlairMilliseconds == flair, "The corner flair did not precede the reveal.");
        var middle = new Vector2(width / 2f, height / 2f);
        Vector2[] cornerPixels = corners.Select(point => new Vector2(point.X * width, point.Y * height)).ToArray();
        now = start.AddMilliseconds(250);
        byte[] flying = Draw(scene);
        Require(OrangeNear(flying, middle, width) > 100 && cornerPixels.All(corner => OrangeNear(flying, corner, 14) == 0) &&
            Black(flying, width / 2, height / 2) && flying.Take(4).SequenceEqual(finalDot.Take(4)),
            "The orange arrow tips did not leave the center dot before reaching the corners, or escaped the board.");
        await Save(target, "00b-corner-arrows-flying");
        now = start.AddMilliseconds(flair - 50);
        byte[] landed = Draw(scene);
        Require(cornerPixels.All(corner => OrangeNear(landed, Vector2.Lerp(corner, middle, .04f), 10) > 20) &&
            StripPixels(landed) > 1500 && Black(landed, width / 2, height / 2) && landed.Take(4).SequenceEqual(finalDot.Take(4)),
            "The arrows did not land as corner brackets, or the test strips did not run along the edges.");
        await Save(target, "00c-corner-brackets-and-test-strips");
        now = start.AddMilliseconds(flair + 260);
        byte[] blackGrowing = Draw(scene);
        Require(Count(blackGrowing, black: true) > Count(first, black: true) + 1000 &&
            Count(blackGrowing, black: false) < Count(first, black: false),
            "The black calibration circle did not expand over the white projector field.");
        await Save(target, "01-black-circle-growing");
        now = start.AddMilliseconds(flair + 800);
        byte[] menuSmall = Draw(scene);
        int smallArea = ColoredArea(menuSmall);
        Require(smallArea > 200 && smallArea < width * height * .8,
            "The board menu did not emerge from the center after the black expansion.");
        await Save(target, "02-menu-growing");
        now = start.AddMilliseconds(flair + 1100);
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
        now = start.AddMilliseconds(flair + duration);
        byte[] final = Draw(scene);
        Require(!scene.BoardRevealActive, "The reveal did not complete at its declared duration.");
        using (var baseline = new SceneCompositor())
        {
            baseline.SetDisplayAspect(16.0 / 9);
            baseline.SetBoardSetup(true);
            baseline.SetDetectedBoardGrid(corners, cameraMap);
            baseline.SetBoardSetup(false);
            // The menu's Globe tile appears once Earth's textures load in the
            // background; load both before comparing so neither is mid-load.
            await Task.WhenAll(scene.EnsureGlobeResourcesAsync(target.Device), baseline.EnsureGlobeResourcesAsync(target.Device));
            Draw(scene); Draw(baseline);
            final = Draw(scene);
            Require(final.SequenceEqual(Draw(baseline)),
                "The reveal's final frame changed the normal calibrated board geometry or content.");
        }
        Draw(scene);
        await Save(target, "05-complete-menu");

        // Before the dots: the white field's fresh camera outline and a previous
        // registration's orientation place the corners. This camera is turned 180
        // degrees and has moved slightly since that registration.
        PixelPoint[] field = [new(262, 38), new(1800, 34), new(1790, 907), new(265, 881)];
        PixelPoint[] cardboard = [new(475, 48), new(1550, 48), new(1530, 897), new(475, 875)];
        Point2[] canvas = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var turned = Homography.FromFourPoints(field.Select(point => new Point2(point.X, point.Y)).ToArray(),
            [canvas[2], canvas[3], canvas[0], canvas[1]]);
        var earlier = Homography.FromFourPoints(field.Select(point => new Point2(point.X + 15, point.Y - 12)).ToArray(),
            [canvas[2], canvas[3], canvas[0], canvas[1]]);
        var predicted = PredictSetupCorners(new("camera", 1920, 1080, earlier.ToMatrix()), field, cardboard);
        Require(predicted is { Length: 4 } && cardboard.Select((point, index) =>
                Vector2.Distance(predicted[index], ToVector(turned.Transform(new(point.X, point.Y))))).Max() < .002,
            "The white field and previous orientation did not place the cardboard corners before the dots.");
        var collapsed = new double[] { 0, 0, .5, 0, 0, .5, 0, 0, 1 };
        Require(PredictSetupCorners(new("camera", 1920, 1080, collapsed), field, cardboard) is null,
            "An alignment that cannot tell the field corners apart still placed setup corners.");

        // Solid orange corners and test strips on the setup white field, under the dots.
        Vector2[] setupCorners = [new(.13f, .15f), new(.87f, .13f), new(.89f, .84f), new(.12f, .87f)];
        now = DateTimeOffset.UtcNow;
        var setupStart = now;
        using (var setup = NewScene())
        {
            setup.ShowSetupCorners(setupCorners);
            now = setupStart.AddMilliseconds(1300);
            byte[] marked = Draw(setup);
            Vector2[] setupPixels = setupCorners.Select(point => new Vector2(point.X * width, point.Y * height)).ToArray();
            Require(setupPixels.All(corner => OrangeNear(marked, Vector2.Lerp(corner, middle, .04f), 10) > 20) &&
                StripPixels(marked) > 1500 && Black(marked, width / 2, height / 2) && White(marked, 0, 0),
                "Setup did not show solid orange corners and test strips around the registration dot.");
            await Save(target, "06-setup-corners-before-dots");
            setup.CompleteBoardSetup(corners, cameraMap);
            var settled = setup.GetBoardRevealDiagnostics();
            byte[] measured = Draw(setup);
            Require(settled.FlairMilliseconds == 400 && setup.BoardRevealActive &&
                cornerPixels.All(corner => OrangeNear(measured, Vector2.Lerp(corner, middle, .04f), 10) > 20),
                "Corners shown before the dots flew in again instead of settling onto the measured edges.");
            await Save(target, "07-setup-corners-settled");
        }

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
            now = start.AddMilliseconds(flair + duration + 1);
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
            if (cancellation is "clear-clip" or "new-scan")
                Require(canceled.GetDetectedBoardCorners() is null,
                    cancellation + " retained stale board-size measurements.");
            else if (cancellation == "menu")
                Require(canceled.GetDetectedBoardCorners()!.SequenceEqual(physicalCorners),
                    "Navigating to the menu discarded the physical board measurement.");
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
            now = now.AddMilliseconds(flair + duration + 1);
            Draw(restored);
            Require(restored.CurrentBoardScreen == BoardScreen.HandTracking,
                "The reveal completion navigated away from the restored board.");
        }
        now = DateTimeOffset.UtcNow;
        using (var photo = NewScene(BoardScreen.PhotoCopy))
        {
            photo.CompleteBoardSetup(corners, cameraMap);
            now = now.AddMilliseconds(flair + 1100);
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
        static Vector2 ToVector(Point2 point) => new((float)point.X, (float)point.Y);
        static bool Orange(byte[] pixels, int i) =>
            pixels[i + 2] > 200 && pixels[i + 1] is > 90 and < 175 && pixels[i] < 90;
        static int OrangeNear(byte[] pixels, Vector2 point, int radius)
        {
            int result = 0;
            for (int y = Math.Max(0, (int)point.Y - radius); y <= Math.Min(height - 1, (int)point.Y + radius); y++)
                for (int x = Math.Max(0, (int)point.X - radius); x <= Math.Min(width - 1, (int)point.X + radius); x++)
                    if (Orange(pixels, (y * width + x) * 4)) result++;
            return result;
        }
        // Saturated test-card colour bars, excluding the orange brackets.
        static int StripPixels(byte[] pixels)
        {
            int result = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (!Orange(pixels, i) && Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) -
                    Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) > 90) result++;
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
