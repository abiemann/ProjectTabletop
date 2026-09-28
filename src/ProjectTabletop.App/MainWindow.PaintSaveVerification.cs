#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Uses isolated GPU scenes and an app-data output directory. The real
    // camera, projector, user's painting and Pictures folder are untouched.
    private async Task<object> VerifyPaintSaveAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int cameraSize = 1000, outputWidth = 3840, outputHeight = 2160;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "PaintSaveVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, outputWidth, outputHeight, 96);
        using var preview = new CanvasRenderTarget(device, 480, 270, 96);
        using var scene = new SceneCompositor(paintClock: () => now);
        scene.SetDisplayAspect(outputWidth / (double)outputHeight);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(cameraSize, 0), new(cameraSize, cameraSize), new(0, cameraSize)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowPaint();
        Draw();
        var clearFilm = scene.CapturePaintArtworkForVerification();
        Require(scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(["menu", "paint-save"]) &&
                scene.CurrentBoardButtons[0].Label == "Exit" && scene.CurrentBoardButtons[1].Label == "Save" &&
                !scene.CanSavePaint && !scene.TryBeginPaintSave(out _),
            "A blank Paint board did not expose Exit and an unavailable Save.");

        // One drop lies in the old reserved header. The body receives a second
        // coat, so exports must preserve piled paint and its newest pigment.
        Require(scene.AddPaintDrop(new(.50, .085), .10, now),
            "The former Paint header cannot retain paint beneath its floating overlays.");
        now += TimeSpan.FromMilliseconds(250);
        Require(scene.AddPaintDrop(new(.50, .55), .09, now), "The active artwork fixture rejected its body drop.");
        Advance(2);
        var previousBodyCoat = scene.CapturePaintFieldProbeForVerification(new(.50, .55));
        Require(scene.AddPaintDrop(new(.50, .55), .075, now), "The save fixture rejected a new coat over existing paint.");
        Draw();
        var stackedBodyCoat = scene.CapturePaintFieldProbeForVerification(new(.50, .55));
        Require(stackedBodyCoat.Height > previousBodyCoat.Height + .8 &&
                Math.Abs(stackedBodyCoat.SurfaceColor.X - .95) < .02 &&
                Math.Abs(stackedBodyCoat.SurfaceColor.Y - .60) < .02 &&
                Math.Abs(stackedBodyCoat.SurfaceColor.Z - .035) < .02,
            "The saved-artwork fixture averaged its newest color or failed to retain layered thickness.");
        Advance(.5);
        var beforeSnapshot = scene.GetPaintDiagnostics();
        Require(beforeSnapshot.ActiveDrops > 0 && scene.CanSavePaint && scene.CurrentBoardButtons[1].Enabled,
            "The evolving wet field did not enable Save.");

        long eventId = 120000;
        var firstFrame = await Pinch();
        Require(scene.TryTakePaintSaveRequest(firstFrame, out var firstImage),
            "Selecting the projected Save button did not snapshot the in-memory painting.");
        byte[] frozenPixels = firstImage.BgraPixels.ToArray();
        Require(!scene.TryTakePaintSaveRequest(firstFrame, out _) && !scene.TryBeginPaintSave(out _) &&
                !scene.CanSavePaint && scene.GetPaintSaveStatus(now) == "Saving image…",
            "A consumed or busy Save request could be started again.");
        AssertArtwork(firstImage, clearFilm);
        Require(Math.Abs(firstImage.Width - outputWidth * .93 * (1 - inset)) <= 3 &&
                Math.Abs(firstImage.Height - outputHeight * .93 * (1 - inset)) <= 3 &&
                Math.Abs(firstImage.Width / (double)firstImage.Height - 16d / 9) < .003 &&
                firstImage.Width > 3000 && firstImage.Height > 1800,
            "Paint Save resized or stretched the physical board instead of using its native projected pixels.");
        var afterSnapshot = scene.GetPaintDiagnostics();
        Require(afterSnapshot.ActiveDrops == beforeSnapshot.ActiveDrops &&
                afterSnapshot.DropCount == beforeSnapshot.DropCount &&
                afterSnapshot.Fluid!.SimulationSteps == beforeSnapshot.Fluid!.SimulationSteps,
            "Taking a Paint snapshot forced the live field to finish or changed its deposited drops.");

        var busyFrame = await Pinch();
        Require(!scene.TryTakePaintSaveRequest(busyFrame, out _), "A second selection queued a Save while one was busy.");
        Advance(2);
        Require(scene.AddPaintDrop(new(.73, .62), .06, now), "Saving blocked new paint from reaching the canvas.");
        Draw();
        Require(frozenPixels.SequenceEqual(firstImage.BgraPixels) && scene.IsPaintMemoryImageCurrent(firstImage),
            "Continuing to paint altered or invalidated the already captured artwork snapshot.");
        string? first = await SavePaintMemoryImageAsync(scene, firstImage, directory);
        Require(first is not null && Path.GetFileName(first).StartsWith("paint-", StringComparison.Ordinal) &&
                scene.GetPaintSaveStatus(now) == "Image Saved" && scene.CanSavePaint,
            "A successful Paint PNG did not report success and re-enable Save.");
        await AssertPng(first!, firstImage);
        Require(scene.GetPaintSaveStatus(now.AddSeconds(3).AddTicks(-1)) == "Image Saved" &&
                scene.GetPaintSaveStatus(now.AddSeconds(3)) != "Image Saved",
            "The Image Saved message did not expire after three seconds.");
        string staleDirectory = Path.Combine(directory, "already-completed");
        Require(await SavePaintMemoryImageAsync(scene, firstImage, staleDirectory) is null &&
                !Directory.Exists(staleDirectory), "Replaying a completed snapshot published another file.");

        var heldFrame = await Pinch(repeat: true);
        Require(!scene.TryTakePaintSaveRequest(heldFrame, out _), "Holding an already consumed gesture repeated Save.");
        var secondFrame = await Pinch();
        Require(scene.TryTakePaintSaveRequest(secondFrame, out var secondImage) &&
                !secondImage.BgraPixels.SequenceEqual(frozenPixels),
            "A later Save did not capture the painting's newer active state.");
        byte[] originalFile = await File.ReadAllBytesAsync(first!);
        string? second = await SavePaintMemoryImageAsync(scene, secondImage, directory);
        byte[] firstFileAfterSave = await File.ReadAllBytesAsync(first!);
        Require(second is not null && !string.Equals(first, second, StringComparison.OrdinalIgnoreCase) &&
                originalFile.SequenceEqual(firstFileAfterSave),
            "Saving Paint twice reused a filename or overwrote the previous image.");
        await AssertPng(second!, secondImage);

        // A small laptop preview must not determine export resolution or alter
        // the artwork. Keep the paint clock fixed for an exact pixel comparison.
        using (var drawing = preview.CreateDrawingSession())
            scene.Draw(drawing, 480, 270, preview: true, runningSlowly: false);
        Require(scene.TryBeginPaintSave(out var afterPreview) && afterPreview.Width == secondImage.Width &&
                afterPreview.Height == secondImage.Height && afterPreview.BgraPixels.SequenceEqual(secondImage.BgraPixels),
            "A small preview resized, stretched or changed the saved painting.");
        scene.SetBlackOutput(true);
        Require(scene.IsPaintMemoryImageCurrent(afterPreview) && scene.CompletePaintSave(afterPreview),
            "Temporarily blanking the output left a valid save unable to complete.");
        scene.SetBlackOutput(false);
        Require(scene.CanSavePaint, "Returning from black output left Paint Save busy.");
        Require(scene.TryBeginPaintSave(out var blankedFailure), "The temporary-output failure fixture could not begin Save.");
        scene.SetBlackOutput(true);
        scene.FailPaintSave(blankedFailure);
        scene.SetBlackOutput(false);
        Require(scene.CanSavePaint && scene.GetPaintSaveStatus(now) == "Save failed - retry",
            "A write failure during temporary black output left Save busy or reported success.");

        var mismatched = await Pinch();
        Require(!scene.TryTakePaintSaveRequest(mismatched.AddTicks(1), out _) &&
                !scene.TryTakePaintSaveRequest(mismatched, out _),
            "A wrong-frame Paint request survived for replay.");

        // A deterministic filesystem failure must preserve the previous files,
        // display a retry message, and release the busy state.
        string blocked = Path.Combine(directory, "not-a-directory");
        byte[] marker = [7, 11, 19, 23];
        await File.WriteAllBytesAsync(blocked, marker);
        var failureFrame = await Pinch();
        Require(scene.TryTakePaintSaveRequest(failureFrame, out var failureImage), "The failure fixture did not acquire Save.");
        bool failed = false;
        try { await SavePaintMemoryImageAsync(scene, failureImage, blocked); }
        catch (IOException) { failed = true; }
        byte[] markerAfterFailure = await File.ReadAllBytesAsync(blocked);
        Require(failed && scene.CanSavePaint && scene.GetPaintSaveStatus(now) is { } failureStatus &&
                failureStatus != "Image Saved" && failureStatus != "Saving image…" &&
                marker.SequenceEqual(markerAfterFailure),
            "A failed Paint write reported success, damaged an existing file, or left Save busy.");
        var retryFrame = await Pinch();
        Require(scene.TryTakePaintSaveRequest(retryFrame, out var retryImage), "Save could not retry after a write failure.");
        string? third = await SavePaintMemoryImageAsync(scene, retryImage, directory);
        Require(third is not null && scene.GetPaintSaveStatus(now) == "Image Saved", "The Paint write retry failed.");
        await AssertPng(third!, retryImage);
        Require(Directory.GetFiles(directory, "*.png").Length == 3 && Directory.GetFiles(directory, "*.tmp").Length == 0,
            "A failed or replayed Paint save published another PNG or left a temporary file.");

        var pendingBeforeExit = await Pinch();
        scene.ShowBoardMenu();
        Require(!scene.TryTakePaintSaveRequest(pendingBeforeExit, out _) && !scene.CanSavePaint,
            "Navigation left an unconsumed Paint Save request available.");
        scene.ShowPaint();
        Require(!scene.CanSavePaint && scene.GetPaintDiagnostics().DropCount == 0,
            "A fresh Paint session retained the old painting or Save state.");
        now += TimeSpan.FromSeconds(1);
        Require(scene.AddPaintDrop(new(.5, .5), .08, now), "The reset fixture rejected its new drop.");
        Draw();
        var resetFrame = await Pinch();
        Require(scene.TryTakePaintSaveRequest(resetFrame, out var beforeReset), "The reset fixture could not snapshot paint.");
        scene.ResetPaint();
        Require(!scene.IsPaintMemoryImageCurrent(beforeReset) && !scene.CompletePaintSave(beforeReset) &&
                !scene.CanSavePaint && scene.GetPaintSaveStatus(now) is null,
            "Reset accepted a stale save completion or retained its status.");
        string obsoleteDirectory = Path.Combine(directory, "obsolete");
        Require(await SavePaintMemoryImageAsync(scene, beforeReset, obsoleteDirectory) is null &&
                !Directory.Exists(obsoleteDirectory), "A reset Paint snapshot was written to disk.");

        now += TimeSpan.FromSeconds(1);
        Require(scene.AddPaintDrop(new(.5, .5), .08, now), "The navigation fixture rejected a new drop.");
        Draw();
        var leaveFrame = await Pinch();
        Require(scene.TryTakePaintSaveRequest(leaveFrame, out var beforeNavigation), "Navigation fixture did not begin Save.");
        scene.ShowBoardMenu();
        Require(!scene.IsPaintMemoryImageCurrent(beforeNavigation) && !scene.CompletePaintSave(beforeNavigation) &&
                await SavePaintMemoryImageAsync(scene, beforeNavigation, obsoleteDirectory) is null &&
                !Directory.Exists(obsoleteDirectory), "Leaving Paint allowed an obsolete snapshot or success completion.");

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The isolated Paint Save verification changed live hardware or the user's board.");
        return new { passed = true, nativeWidth = firstImage.Width, nativeHeight = firstImage.Height,
            savesInMemoryArtworkWithoutCamera = true, pngMatchesSnapshotExactly = true,
            floatingControlsAndStatusExcluded = true, formerHeaderContainsPaint = true,
            activeAnimationCapturedWithoutFinishing = true, snapshotImmutableWhilePaintingContinues = true,
            accumulatedPaintWithNewestCoatColorSaved = true,
            physicalAspectPreserved = true, previewCannotResizeExport = true,
            projectedGestureRoute = true, uniqueFiles = true, busyAndReplayRejected = true,
            failureRecoversWithoutFalseSuccess = true, temporaryBlankingReleasesBusyState = true,
            resetAndNavigationInvalidateSnapshots = true,
            imageSavedFeedbackThreeSeconds = true, liveHardwareUnchanged = true, noPicturesWrites = true,
            directory, images = new[] { first, second, third } };

        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, outputWidth, outputHeight, preview: false, runningSlowly: false);
        }
        void Advance(double seconds)
        {
            int frames = (int)Math.Ceiling(seconds * 60);
            for (int frame = 0; frame < frames; frame++)
            {
                now += TimeSpan.FromSeconds(seconds / frames);
                Draw();
            }
        }
        async Task<DateTimeOffset> Pinch(bool repeat = false)
        {
            Draw();
            await Task.Delay(10);
            var bounds = scene.CurrentBoardButtons.Single(button => button.Id == "paint-save").Bounds;
            var point = new PixelPoint(cameraSize * (.035 + .93 * (inset / 2 + (bounds.X + bounds.Width / 2) * (1 - inset))),
                cameraSize * (.035 + .93 * (inset / 2 + (bounds.Y + bounds.Height / 2) * (1 - inset))));
            var time = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(point, time.AddSeconds(1), repeat ? eventId : ++eventId) { TrackingId = 901 }], time);
            return time;
        }
        async Task AssertPng(string path, SceneCompositor.PaintMemoryImage expected)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            Require(decoder.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId &&
                    decoder.PixelWidth == expected.Width && decoder.PixelHeight == expected.Height,
                "Paint Save did not publish a PNG at the in-memory snapshot's native dimensions.");
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            Require(data.DetachPixelData().SequenceEqual(expected.BgraPixels),
                "The saved PNG differs from the frozen artwork pixels.");
        }
        static void AssertArtwork(SceneCompositor.PaintMemoryImage image, SceneCompositor.PaintMemoryImage blankFilm)
        {
            Require(image.BgraPixels.Length == checked(image.Width * image.Height * 4), "The Paint snapshot has invalid pixel storage.");
            Require(image.Width == blankFilm.Width && image.Height == blankFilm.Height,
                "The blank clear-film reference has different export geometry.");
            int upperPaint = 0, bodyPaint = 0;
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int offset = (y * image.Width + x) * 4;
                var pixels = image.BgraPixels;
                Require(pixels[offset + 3] == 255, "Paint export lost its opaque canvas background.");
                int difference = Math.Max(Math.Abs(pixels[offset] - blankFilm.BgraPixels[offset]),
                    Math.Max(Math.Abs(pixels[offset + 1] - blankFilm.BgraPixels[offset + 1]),
                        Math.Abs(pixels[offset + 2] - blankFilm.BgraPixels[offset + 2])));
                bool painted = difference > 12;
                double u = x / (double)image.Width, v = y / (double)image.Height;
                if (painted && u > .38 && u < .62 && v < .17) upperPaint++;
                if (painted && u > .35 && u < .65 && v > .35 && v < .75) bodyPaint++;
                // Quiet regions under the bottom controls, top-left title and
                // transient status must retain clear film in this export fixture.
                // Their live labels, backgrounds and hover are separate layers.
                if (((u > .09 && u < .17 || u > .83 && u < .91) && v > .90 && v < .935) ||
                    (u > .065 && u < .155 && v > .019 && v < .043) ||
                    (u > .35 && u < .65 && v > .90 && v < .935))
                    Require(difference <= 3,
                        "The saved painting includes a floating control, label or status overlay.");
            }
            Require(upperPaint > 1000 && bodyPaint > 1000,
                "The export omitted wet paint from the body or former header area.");
        }
        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
#endif
