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
    // Exercise the actual Save route using a retained photograph and an isolated
    // board. No webcam frame, Pictures write or live projector change is needed.
    private async Task<object> VerifyPhotoCopyMemorySaveAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int size = 1000, width = 37, height = 23;
        string directory = Path.Combine(_appDataDirectory, "PhotoCopyMemorySaveVerification", Guid.NewGuid().ToString("N"));
        var pixels = Fixture();
        var original = pixels.ToArray();
        var cutout = new PhotoHandCutout(width, height, pixels, new(17, 13), new(0, -1))
        {
            CameraGeometry = new(1920, 1080, [1.0 / 1920, 0, 0, 0, 1.0 / 1080, 0, 0, 0, 1])
        };
        using var scene = new SceneCompositor();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        var tracker = new HandGestureTracker();
        long pinchId = 90000;
        var holds = new PhotoCopyCaptionHoldFixture(scene, () => { Draw(); return target.GetPixelBytes(); }, size);
        await CaptureFixture();
        Require(scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(
                ["menu", "capture-again", "photo-save"]) &&
            scene.CurrentBoardButtons[1] is { Label: "Clear", Enabled: true } &&
            scene.CurrentBoardButtons[2] is { Label: "Save", Enabled: true } &&
            scene.CurrentBoardButtons.All(button => button.Hold == BoardButtonHold.Once),
            "Swirl did not expose Clear and an enabled Save button.");
        Require(scene.TryGetPhotoCopyMemoryImage(out var retained) && ReferenceEquals(retained.Cutout, cutout) &&
            !scene.TryGetPhotoCopyCaptureContext(out _),
            "Save required another camera capture or lost the retained source photograph.");
        int stampCountBeforeExport = scene.PhotoCopyCount;
        var artwork = scene.RenderPhotoCopySwirlImage(retained);
        double expectedEdge = size * .93 * (1 - inset);
        Require(Math.Abs(artwork.Width - expectedEdge) <= 3 && Math.Abs(artwork.Height - expectedEdge) <= 3 &&
                artwork.BgraPixels.Length == artwork.Width * artwork.Height * 4,
            "The full Swirl export did not crop to the calibrated board at projector-pixel density.");
        Require(scene.PhotoCopyCount < PhotoCopyLayout.Create(width / (double)height).Count &&
                scene.PhotoCopyCount >= stampCountBeforeExport,
            "Rendering the completed export advanced the visible Swirl to its final frame.");
        AssertCompleteArtwork(artwork);

        foreach (string id in new[] { "photo-save", "capture-again", "menu" })
        {
            var pinch = await Pinch(id);
            Require(!scene.TryTakePhotoCopyMemorySaveRequest(pinch, out _) &&
                    !scene.TryTakePhotoCopyCaptureRequest(pinch, out _) &&
                    scene.CurrentBoardScreen == BoardScreen.PhotoCopy && scene.IsPhotoCopyMemoryImageCurrent(retained),
                id + " accepted a pinch instead of its caption hold.");
            var index = await FingerGesture(id);
            Require(!scene.TryTakePhotoCopyMemorySaveRequest(index, out _) &&
                    !scene.TryTakePhotoCopyCaptureRequest(index, out _) &&
                    scene.CurrentBoardScreen == BoardScreen.PhotoCopy && scene.IsPhotoCopyMemoryImageCurrent(retained),
                id + " accepted index separation instead of its caption hold.");
        }
        await holds.CheckReleaseCancelsAsync("photo-save");
        var saveTime = await holds.HoldAsync("photo-save");
        Require(scene.TryTakePhotoCopyMemorySaveRequest(saveTime, out var saveImage) &&
                ReferenceEquals(saveImage.Cutout, cutout) && saveImage.Revision == retained.Revision,
            "Holding Save's rendered caption did not select the memory image with zero cursors and no object lock.");
        Require(!scene.TryTakePhotoCopyMemorySaveRequest(saveTime, out _) &&
                !scene.TryTakePhotoCopyCaptureRequest(saveTime, out _),
            "Save was consumed twice or also requested a webcam capture.");
        var held = await holds.ContinueHeldAsync("photo-save");
        Require(!scene.TryTakePhotoCopyMemorySaveRequest(held, out _), "A continuously covered caption repeated Save.");

        await Task.Delay(1100);
        Draw();
        int copiesBeforeSave = scene.PhotoCopyCount;
        Require(copiesBeforeSave > 0, "The retained photograph did not start its Swirl.");
        string? first = await SavePhotoCopyMemoryImageAsync(scene, saveImage, directory);
        Require(first is not null && scene.PhotoCopyStatus == "Image Saved", "Memory save did not report a completed PNG.");
        await AssertImage(first!);
        Require(pixels.SequenceEqual(original) && scene.PhotoCopyCount >= copiesBeforeSave &&
                scene.TryGetPhotoCopyMemoryImage(out var afterSave) && ReferenceEquals(afterSave.Cutout, cutout) &&
                afterSave.Revision == retained.Revision && !scene.TryGetPhotoCopyCaptureContext(out _),
            "Saving changed the photograph, restarted Swirl, or returned to webcam capture.");

        var repeatTime = await holds.HoldAsync("photo-save");
        Require(scene.TryTakePhotoCopyMemorySaveRequest(repeatTime, out var repeatImage) &&
                ReferenceEquals(repeatImage.Cutout, cutout) && !scene.TryTakePhotoCopyCaptureRequest(repeatTime, out _),
            "A fresh Save caption hold did not route to the same retained photo.");
        byte[] firstFile = await File.ReadAllBytesAsync(first!);
        string? second = await SavePhotoCopyMemoryImageAsync(scene, repeatImage, directory);
        byte[] firstFileAfterSave = await File.ReadAllBytesAsync(first!);
        Require(second is not null && !string.Equals(first, second, StringComparison.OrdinalIgnoreCase) &&
                firstFile.SequenceEqual(firstFileAfterSave),
            "Saving again reused the filename or overwrote the previous image.");
        await AssertImage(second!);

        var wrongFrame = await holds.HoldAsync("photo-save");
        Require(!scene.TryTakePhotoCopyMemorySaveRequest(wrongFrame.AddTicks(1), out _) &&
                !scene.TryTakePhotoCopyMemorySaveRequest(wrongFrame, out _),
            "A mismatched camera observation left a Save request available for replay.");
        var unprepared = await holds.HoldAsync("photo-save", prepareHeldFrames: false);
        Require(!scene.TryTakePhotoCopyMemorySaveRequest(unprepared, out _),
            "Save reused input preparation from an earlier camera observation.");
        // Save stays visibly available while a worker is busy; its completed
        // caption hold must still fail to queue concurrent work.
        var externallyBusy = await holds.HoldAsync("photo-save", busy: true);
        Require(!scene.TryTakePhotoCopyMemorySaveRequest(externallyBusy, out _),
            "A busy photo worker queued another Save.");
        Require(scene.BeginPhotoCopyMemorySave(retained), "A valid retained image could not begin saving.");
        var savedAt = DateTimeOffset.UtcNow;
        Require(scene.CompletePhotoCopyMemorySave(retained, savedAt) &&
                scene.GetPhotoCopyDisplayStatus(savedAt) == "Image Saved" &&
                scene.GetPhotoCopyDisplayStatus(savedAt.AddSeconds(3).AddTicks(-1)) == "Image Saved" &&
                scene.GetPhotoCopyDisplayStatus(savedAt.AddSeconds(3)) != "Image Saved",
            "Memory-save success did not last exactly three seconds.");

        // A deterministic write failure must not leave the previous success
        // message, publish a partial image or prevent the user from retrying.
        string blocked = Path.Combine(directory, "not-a-directory");
        byte[] marker = [7, 11, 19, 23];
        await File.WriteAllBytesAsync(blocked, marker);
        bool failed = false;
        try { await SavePhotoCopyMemoryImageAsync(scene, retained, blocked); }
        catch (IOException) { failed = true; }
        byte[] markerAfterFailure = await File.ReadAllBytesAsync(blocked);
        Require(failed && scene.PhotoCopyStatus != "Image Saved" &&
                marker.SequenceEqual(markerAfterFailure) &&
                scene.TryGetPhotoCopyMemoryImage(out var retryImage) && ReferenceEquals(retryImage.Cutout, cutout),
            "A failed memory save reported success, damaged an existing file or discarded the retained image.");
        var retryTime = await holds.HoldAsync("photo-save");
        Require(scene.TryTakePhotoCopyMemorySaveRequest(retryTime, out var retry),
            "A failed write left Save unable to accept another selection.");
        string? third = await SavePhotoCopyMemoryImageAsync(scene, retry, directory);
        Require(third is not null, "Saving the retained image did not recover after a write failure.");
        await AssertImage(third!);
        Require(Directory.GetFiles(directory, "*.png").Length == 3 &&
                Directory.GetFiles(directory, "*.tmp").Length == 0,
            "A failed memory save published a PNG or left an incomplete temporary file.");

        var pending = await holds.HoldAsync("photo-save");
        var clearedAt = await holds.HoldAsync("capture-again");
        Require(!scene.TryTakePhotoCopyMemorySaveRequest(pending, out _) &&
                !scene.TryTakePhotoCopyMemorySaveRequest(clearedAt, out _) &&
                !scene.IsPhotoCopyMemoryImageCurrent(retained) && !scene.BeginPhotoCopyMemorySave(retained) &&
                !scene.CompletePhotoCopyMemorySave(retained) && !scene.TryGetPhotoCopyMemoryImage(out _) &&
                scene.PhotoCopyCount == 0 && scene.CurrentBoardButtons[2].Label == "Copy",
            "Clear retained an old Save request, image, success completion or Save label.");
        string obsoleteDirectory = Path.Combine(directory, "obsolete");
        Require(await SavePhotoCopyMemoryImageAsync(scene, retained, obsoleteDirectory) is null &&
                !Directory.Exists(obsoleteDirectory), "An obsolete memory image was written after Clear.");

        await CaptureFixture();
        Require(scene.TryGetPhotoCopyMemoryImage(out var beforeNavigation), "Navigation fixture lost its photo.");
        var leavingRequest = await holds.HoldAsync("photo-save");
        await holds.HoldAsync("menu");
        Require(!scene.TryTakePhotoCopyMemorySaveRequest(leavingRequest, out _) &&
                scene.CurrentBoardScreen == BoardScreen.Menu &&
                !scene.BeginPhotoCopyMemorySave(beforeNavigation) && !scene.CompletePhotoCopyMemorySave(beforeNavigation) &&
                !scene.TryGetPhotoCopyMemoryImage(out _),
            "Navigation allowed an old memory save request or completion to act on the new board.");
        string widePath = await VerifyWideArtwork();
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The isolated memory-save check changed the live camera, output or board.");
        return new { passed = true, saveEnabledWithoutObjectLock = true, renderedCaptionHoldRoutesToMemory = true,
            pinchAndIndexButtonsRejected = true, zeroTrackedCursors = true, sameFramePreparationRequired = true,
            intactCaptionCancelsPartialHold = true, clearAndExitUseCaptionHolds = true,
            holds.SuccessfulHolds, holds.BrokenCaptionFrames,
            noCameraFrameNeeded = true, completeSwirlAtBoardResolution = true, opaqueArtworkWithoutControls = true,
            sourcePixelsUnchanged = true, originalSwirlPreserved = true,
            savedFeedbackThreeSeconds = true, uniqueFiles = true, heldAndBusyDoNotRepeat = true,
            obsoleteFrameClearAndNavigationRejected = true, failuresRecoverWithoutFalseSuccess = true,
            physicalBoardAspectPreserved = true, previewCannotResizeExport = true,
            liveHardwareUnchanged = true, noPicturesWrites = true, directory, images = new[] { first, second, third, widePath } };

        async Task<string> VerifyWideArtwork()
        {
            const int outputWidth = 2000, outputHeight = 1000;
            using var wide = new SceneCompositor();
            wide.SetDisplayAspect(outputWidth / (double)outputHeight);
            wide.SetBoardSetup(true);
            double wideInset = wide.SetDetectedBoardGrid([new(.2f, .25f), new(.8f, .25f), new(.8f, .75f), new(.2f, .75f)],
                Homography.FromFourPoints([new(0, 0), new(outputWidth, 0), new(outputWidth, outputHeight), new(0, outputHeight)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            wide.SetBoardSetup(false);
            wide.ShowPhotoCopy();
            using var fullTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), outputWidth, outputHeight, 96);
            using (var drawing = fullTarget.CreateDrawingSession())
                wide.Draw(drawing, outputWidth, outputHeight, preview: false, runningSlowly: false);
            await Task.Delay(1100);
            Require(wide.TryGetPhotoCopyCaptureContext(out var wideContext) &&
                    wide.SetPhotoCopyCapture(cutout, wideContext.Revision), "Wide-board export fixture did not settle.");
            Require(wide.TryGetPhotoCopyMemoryImage(out var image), "Wide board did not retain its photograph.");
            var full = wide.RenderPhotoCopySwirlImage(image);
            double expectedWidth = 1200 * (1 - wideInset), expectedHeight = 500 * (1 - wideInset);
            Require(Math.Abs(full.Width - expectedWidth) <= 3 && Math.Abs(full.Height - expectedHeight) <= 3 &&
                    Math.Abs(full.Width / (double)full.Height - 2.4) < .02 && full.Height < 1000,
                "A wide physical board was exported using the square texture cache's aspect ratio.");
            AssertCompleteArtwork(full);
            using var preview = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 500, 250, 96);
            using (var drawing = preview.CreateDrawingSession())
                wide.Draw(drawing, 500, 250, preview: true, runningSlowly: false);
            var afterPreview = wide.RenderPhotoCopySwirlImage(image);
            Require(afterPreview.Width == full.Width && afterPreview.Height == full.Height &&
                    afterPreview.BgraPixels.SequenceEqual(full.BgraPixels),
                "A small laptop preview resized or stretched the saved physical-board artwork.");
            string? path = await SavePhotoCopyMemoryImageAsync(wide, image, Path.Combine(directory, "wide-board"));
            Require(path is not null, "The wide-board artwork could not be saved.");
            await AssertImage(path!, full);
            return path!;
        }

        async Task CaptureFixture()
        {
            scene.ShowPhotoCopy();
            Draw();
            await Task.Delay(1100);
            Require(scene.TryGetPhotoCopyCaptureContext(out var context) && context.Target is null &&
                    scene.SetPhotoCopyCapture(cutout, context.Revision), "Memory-save photograph fixture did not settle.");
        }
        async Task<DateTimeOffset> Pinch(string id, bool busy = false, bool repeat = false)
        {
            await Task.Delay(10);
            var time = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(PointForButton(id), time.AddSeconds(1), repeat ? pinchId : ++pinchId)
                { TrackingId = 801 }], time, busy);
            return time;
        }
        async Task<DateTimeOffset> FingerGesture(string id)
        {
            var together = HandAtButton(id, false);
            var apartHand = HandAtButton(id, true);
            await Send(together); await Send(together); await Send(apartHand);
            return await Send(apartHand);
        }
        async Task<DateTimeOffset> Send(HandDetection hand)
        {
            await Task.Delay(110);
            var time = DateTimeOffset.UtcNow;
            var cursors = tracker.Update([hand], time, time).ToArray();
            Require(cursors.Length == 1 && cursors[0].ExecuteEventId == 0,
                "Memory-save index fixture did not produce one non-pinching hand.");
            scene.SetHandCursors(cursors, time);
            return time;
        }
        HandDetection HandAtButton(string id, bool separated)
        {
            PixelPoint[] points = [new(0, 100), new(-28, 72), new(-57, 48), new(-84, 25), new(-110, 8),
                new(-35, 10), default, default, default, new(0, 0), default, default, default,
                new(30, 10), default, default, default, new(55, 30), default, default, default];
            foreach (int root in new[] { 5, 9, 13, 17 })
            {
                double tipX = root == 5 ? separated ? -48 : -20 : points[root].X;
                double tipY = root switch { 5 => -98, 9 => -113, 13 => -96, _ => -65 };
                for (int part = 1; part <= 3; part++)
                    points[root + part] = new(points[root].X + (tipX - points[root].X) * part / 3,
                        points[root].Y + (tipY - points[root].Y) * part / 3);
            }
            var middle = PointForButton(id);
            var hand = new HandDetection(points.Select(point => new PixelPoint(middle.X + .65 * point.X,
                middle.Y + .65 * (point.Y + 113))).ToArray(), .95, .5);
            var pose = HandPoseClassifier.DescribeFingerSelection(hand);
            Require(HandPoseClassifier.AreFourFingersExtended(hand) && pose.Together == !separated && pose.IndexSeparated == separated,
                "The Save fixture did not have the intended finger geometry.");
            return hand;
        }
        PixelPoint PointForButton(string id)
        {
            var bounds = scene.CurrentBoardButtons.Single(button => button.Id == id).Bounds;
            return new(size * (.035 + .93 * (inset / 2 + (bounds.X + bounds.Width / 2) * (1 - inset))),
                size * (.035 + .93 * (inset / 2 + (bounds.Y + bounds.Height / 2) * (1 - inset))));
        }
        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
        }
        static byte[] Fixture()
        {
            var result = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                result[offset] = (byte)(23 + x);
                result[offset + 1] = (byte)(41 + y * 2);
                result[offset + 2] = (byte)(190 - x * 2);
                bool transparent = x < 2 || y < 2 || x >= width - 2 || y >= height - 2 ||
                    x >= 17 && x <= 19 && y >= 10 && y <= 12;
                result[offset + 3] = transparent ? (byte)0 : x == 2 ? (byte)1 : x == 3 ? (byte)64 :
                    y == 2 ? (byte)128 : y == 3 ? (byte)254 : (byte)255;
            }
            return result;
        }
        async Task AssertImage(string path, SceneCompositor.PhotoCopySwirlImage? expected = null)
        {
            expected ??= artwork;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            Require(decoder.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId &&
                    decoder.PixelWidth == expected.Width && decoder.PixelHeight == expected.Height,
                "Memory Save exported a source crop instead of the complete board-resolution artwork.");
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            Require(data.DetachPixelData().SequenceEqual(expected.BgraPixels),
                "Memory Save differed from the completed in-memory artwork or included live board controls.");
        }
        static void AssertCompleteArtwork(SceneCompositor.PhotoCopySwirlImage image)
        {
            var regions = new int[4];
            int titleArt = 0, controlsArt = 0, centerArt = 0;
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int offset = (y * image.Width + x) * 4;
                byte b = image.BgraPixels[offset], g = image.BgraPixels[offset + 1], r = image.BgraPixels[offset + 2];
                Require(image.BgraPixels[offset + 3] == 255, "The full Swirl export lost its opaque grey background.");
                Require(b <= r + 3 && g <= r + 3 && (b > 20 || g > 20 || r > 20),
                    "The exported artwork contains blue UI chrome or black status text.");
                if (r <= b + 25 || r <= g + 25) continue;
                regions[(y >= image.Height / 2 ? 2 : 0) + (x >= image.Width / 2 ? 1 : 0)]++;
                if (y < image.Height * .06) titleArt++;
                if (y > image.Height * .835 && y < image.Height * .94) controlsArt++;
                if (x > image.Width * .4 && x < image.Width * .6 && y > image.Height * .4 && y < image.Height * .6)
                    centerArt++;
            }
            Require(regions.All(count => count > 1000) && titleArt > 50 && controlsArt > 1000 && centerArt > 1000,
                "The completed export did not fill the board's center, title and button regions with Swirl artwork.");
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
