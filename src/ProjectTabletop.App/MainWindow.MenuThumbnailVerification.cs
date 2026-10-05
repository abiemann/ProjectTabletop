#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private native scenes and render targets only: no camera, live board,
    // projector window, control pipe, or game state is operated by this check.
    private async Task<object> VerifyMenuThumbnailsAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        string directory = Path.Combine(_appDataDirectory, "MenuThumbnailVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var device = CanvasDevice.GetSharedDevice();
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var images = new List<string>();
        var timings = new List<object>();
        var pagingComparisons = new List<object>();
        int retainedCacheChecks = 0, uninitializedGameResourceChecks = 0;
        string[] boardResources = ["_paintFluid", "_paintFluidDevice", "_globeRenderer",
            "_slotArtwork", "_slotBackdrop", "_slotMenuDragonArtwork", "_slotArtworkDevice",
            "_rouletteBackdrop", "_rouletteBurlBitmap", "_rouletteWoodShader", "_rouletteMaterialDevice",
            "_rouletteMotionTarget", "_rouletteFixedBowl", "_rouletteForegroundRim"];
        var atlasField = typeof(SceneCompositor).GetField("_menuPreviewImages", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SceneCompositor).FullName, "_menuPreviewImages");

        foreach (var dimensions in new[] { (Width: 1920, Height: 1080, Name: "16x9"),
            (Width: 1400, Height: 1000, Name: "1x4") })
        {
            using var scene = new SceneCompositor(blackjackClock: () => now);
            var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(scene)!;
            Configure(dimensions.Width, dimensions.Height);
            await scene.EnsureMenuPreviewResourcesAsync(device);
            using var target = new CanvasRenderTarget(device, dimensions.Width, dimensions.Height, 96);
            byte[] initial = await Capture(target, dimensions.Width, dimensions.Height, dimensions.Name + "-cold-first-page");
            var ready = scene.GetMenuPreviewDiagnostics();
            Require(ready.Ready && ready.LoadedCount == 8 && ready.Error is null,
                "The menu did not load all seven board thumbnails and the Settings hand preview: " + ready.Error);
            object atlas = atlasField.GetValue(scene) ?? throw new InvalidOperationException("The ready menu has no image atlas.");
            CheckResourcesAndCache();
            Require(initial.AsSpan().SequenceEqual(Draw(target, dimensions.Width, dimensions.Height)),
                "A stationary menu changed after its first draw.");

            // Acquisition captures the exact stationary image. Repeated draws
            // and a redundant preload must retain the same camera reference.
            scene.GetHandAcquisitionContext(now);
            now += TimeSpan.FromMilliseconds(600);
            Draw(target, dimensions.Width, dimensions.Height);
            var reference = scene.GetHandAcquisitionContext(now);
            Require(reference?.ExpectedScene is not null, "The loaded menu did not establish its camera reference.");
            await scene.EnsureMenuPreviewResourcesAsync(device);
            Draw(target, dimensions.Width, dimensions.Height);
            var repeatedReference = scene.GetHandAcquisitionContext(now);
            Require(ReferenceEquals(reference!.ExpectedScene, repeatedReference?.ExpectedScene),
                "A redundant thumbnail preload replaced the stationary menu's camera reference.");
            CheckResourcesAndCache();

            Require(board.ActivateButton("menu-scroll-down", now), "The private menu could not begin paging down.");
            now += BoardSession.MenuScrollDuration / 2;
            await Capture(target, dimensions.Width, dimensions.Height, dimensions.Name + "-page-slide");
            Require(scene.GetHandAcquisitionContext(now) is null, "The moving menu exposed a camera reference.");
            now += BoardSession.MenuScrollDuration / 2;
            await Capture(target, dimensions.Width, dimensions.Height, dimensions.Name + "-roulette-page");
            Require(board.MenuScrolled && !board.MenuScrolling && scene.CurrentBoardButtons.Any(button => button.Id == "roulette"),
                "The second menu page did not reveal Roulette.");
            CheckResourcesAndCache();
            now += TimeSpan.FromMilliseconds(1);
            Require(board.ActivateButton("menu-scroll-up", now), "The private menu could not page back up.");
            now += BoardSession.MenuScrollDuration;
            byte[] returned = Draw(target, dimensions.Width, dimensions.Height);
            await Capture(target, dimensions.Width, dimensions.Height, dimensions.Name + "-returned-first-page");
            int left = dimensions.Width, top = dimensions.Height, right = -1, bottom = -1, changed = 0, maximum = 0;
            for (int pixel = 0; pixel < initial.Length; pixel += 4)
            {
                int difference = 0;
                for (int channel = 0; channel < 4; channel++)
                    difference = Math.Max(difference, Math.Abs(initial[pixel + channel] - returned[pixel + channel]));
                if (difference == 0) continue;
                int x = pixel / 4 % dimensions.Width, y = pixel / 4 / dimensions.Width;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                changed++; maximum = Math.Max(maximum, difference);
            }
            // Repainting the same GPU gradient can round a few edge channels
            // by one unit. Permit only that measured subpixel rounding, never
            // a changed glyph, image, crop, or navigation-control silhouette.
            int roundingPixelLimit = dimensions.Width * dimensions.Height / 10000;
            pagingComparisons.Add(new { dimensions.Name, changedPixels = changed, maximumChannelDifference = maximum,
                roundingPixelLimit, changedBounds = changed == 0 ? null : new[] { left, top, right, bottom } });
            Require(maximum <= 1 && changed <= roundingPixelLimit,
                $"Paging back changed the precomputed menu's pixels: {changed} pixels, maximum channel difference {maximum}, " +
                $"bounds ({left},{top})–({right},{bottom}). Captures: {directory}");
            CheckResourcesAndCache();

            // Settings' hand illustration uses the same atlas as the main menu.
            board.ShowSettings(now);
            await Capture(target, dimensions.Width, dimensions.Height, dimensions.Name + "-settings-hand-preview");
            CheckResourcesAndCache();
            scene.ShowHandTrackingTest();
            Draw(target, dimensions.Width, dimensions.Height);
            scene.ShowBoardMenu();
            Draw(target, dimensions.Width, dimensions.Height);
            CheckResourcesAndCache();

            // A full calibration reset/aspect change, dense projector output,
            // Windows DPI scaling and a small preview must not rebuild artwork.
            Configure(dimensions.Height, dimensions.Width);
            using var portrait = new CanvasRenderTarget(device, dimensions.Height, dimensions.Width, 96);
            await Capture(portrait, dimensions.Height, dimensions.Width, dimensions.Name + "-portrait");
            CheckResourcesAndCache();
            Configure(3840, 2160);
            using var native = new CanvasRenderTarget(device, 3840, 2160, 96);
            byte[] nativePixels = Draw(native, 3840, 2160);
            using var dpi = new CanvasRenderTarget(device, 1280, 720, 288);
            byte[] dpiPixels = Draw(dpi, 1280, 720);
            long channelError = 0;
            int substantialPixelDifferences = 0;
            for (int pixel = 0; pixel < nativePixels.Length; pixel += 4)
            {
                int maximumError = 0;
                for (int channel = 0; channel < 4; channel++)
                {
                    int error = Math.Abs(nativePixels[pixel + channel] - dpiPixels[pixel + channel]);
                    channelError += error;
                    maximumError = Math.Max(maximumError, error);
                }
                if (maximumError > 16) substantialPixelDifferences++;
            }
            Require(channelError / (double)nativePixels.Length < 1 && substantialPixelDifferences < 3840 * 2160 * .005,
                "Windows scaling changed the shipped thumbnails' native projector geometry or detail.");
            using var preview = new CanvasRenderTarget(device, 400, 300, 96);
            Draw(preview, 400, 300, preview: true);
            Require(nativePixels.AsSpan().SequenceEqual(Draw(native, 3840, 2160)),
                "A laptop preview changed the following projector image.");
            await Capture(native, 3840, 2160, dimensions.Name + "-native-4k");
            CheckResourcesAndCache();

            void Configure(int width, int height)
            {
                scene.SetDisplayAspect(width / (double)height);
                scene.SetBoardSetup(true);
                scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                    Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                        [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
                scene.SetBoardSetup(false);
                scene.ShowBoardMenu();
            }

            void CheckResourcesAndCache()
            {
                foreach (string name in boardResources)
                {
                    var field = typeof(SceneCompositor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingFieldException(typeof(SceneCompositor).FullName, name);
                    Require(field.GetValue(scene) is null, "Drawing a menu thumbnail initialized destination resources: " + name);
                    uninitializedGameResourceChecks++;
                }
                Require(typeof(SceneCompositor).GetField("_slotArtworkAttempted", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(scene) is false, "Menu rendering attempted to load the slot game's artwork.");
                Require(ReferenceEquals(atlas, atlasField.GetValue(scene)) && scene.GetMenuPreviewDiagnostics() is
                    { Ready: true, LoadedCount: 8, Error: null }, "Menu paging, navigation or resizing replaced its image atlas.");
                retainedCacheChecks++;
            }

            byte[] Draw(CanvasRenderTarget destination, int width, int height, bool preview = false)
            {
                long began = Stopwatch.GetTimestamp();
                using (var drawing = destination.CreateDrawingSession())
                    scene.Draw(drawing, width, height, preview, runningSlowly: false);
                double drawMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                long readback = Stopwatch.GetTimestamp();
                byte[] pixels = destination.GetPixelBytes();
                timings.Add(new { dimensions.Name, width, height, preview, drawMilliseconds,
                    readbackMilliseconds = Stopwatch.GetElapsedTime(readback).TotalMilliseconds });
                return pixels;
            }

            async Task<byte[]> Capture(CanvasRenderTarget destination, int width, int height, string name)
            {
                byte[] pixels = Draw(destination, width, height);
                string path = Path.Combine(directory, name + ".png");
                await destination.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
                return pixels;
            }
        }

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Isolated thumbnail verification changed live hardware or navigation.");
        return new { passed = true, directory, images, timings, pagingComparisons, loadedThumbnails = 8,
            retainedCacheChecks, uninitializedGameResourceChecks, stableStationaryCameraReference = true,
            denseDpiGeometryPreserved = true, liveHardwareUnchanged = true };

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
