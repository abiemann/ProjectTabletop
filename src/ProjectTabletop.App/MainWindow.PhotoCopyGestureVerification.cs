#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Real gesture classification, scene readiness, object extraction and GPU
    // rendering on an isolated scene. No synthetic observation reaches hardware.
    private async Task<object> VerifyPhotoCopyGesturesAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        Require(BoardSession.PhotoCopyShutterBounds == new BoardRect(
            PhotoObjectTarget.CaptureLeft / (double)PhotoHandCutout.BoardPixels,
            PhotoObjectTarget.CaptureTop / (double)PhotoHandCutout.BoardPixels,
            (PhotoObjectTarget.CaptureRight - PhotoObjectTarget.CaptureLeft) / (double)PhotoHandCutout.BoardPixels,
            (PhotoObjectTarget.CaptureBottom - PhotoObjectTarget.CaptureTop) / (double)PhotoHandCutout.BoardPixels),
            "The gesture shutter area diverged from the Vision capture area.");
        using var scene = new SceneCompositor();
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowPhotoCopy();
        const int size = 1000;
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        var captionHolds = new PhotoCopyCaptionHoldFixture(scene, () =>
        {
            Draw();
            return target.GetPixelBytes();
        }, size);
        var tracker = new HandGestureTracker();
        bool reorder = false;
        var grouped = HandAt(.76, .52, separated: false);
        var opened = HandAt(.76, .52, separated: true);
        var companion = HandAt(.66, .32, separated: false);
        Draw();

        // A complete gesture cannot capture while the grey field is settling.
        var early = await Gesture(grouped, opened, companion);
        Require(!scene.TryTakePhotoCopyGestureShutter(early.FrameTime, out _),
            "A pre-settle gesture queued a Photo Copy shutter.");
        await Task.Delay(750);
        Require(scene.TryGetPhotoCopyCaptureContext(out var greyContext), "The isolated grey field never became ready.");
        var noSubject = await Gesture(grouped, opened);
        Require(!scene.TryTakePhotoCopyGestureShutter(noSubject.FrameTime, out _),
            "A lone hand without a locked object enabled the Photo Copy shutter.");

        // Build a camera-space object separated from both hands and their forearms.
        var fixture = new byte[size * size * 4];
        PixelPoint objectTopLeft = CameraPoint(.18, .28), objectBottomRight = CameraPoint(.35, .49);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            bool inside = x >= objectTopLeft.X && x <= objectBottomRight.X && y >= objectTopLeft.Y && y <= objectBottomRight.Y;
            fixture[offset] = inside ? (byte)20 : (byte)110;
            fixture[offset + 1] = inside ? (byte)35 : (byte)110;
            fixture[offset + 2] = inside ? (byte)185 : (byte)110;
            fixture[offset + 3] = 255;
        }
        var lockedObject = PhotoObjectLocator.Locate(size, size, size * 4, fixture, greyContext.CameraToBoard, out var locateFailure);
        Require(lockedObject is not null, "The object fixture could not be located: " + locateFailure);
        Require(scene.SetPhotoCopyObject(lockedObject!, greyContext.Revision), "The object fixture could not lock.");
        Draw();
        Require(!scene.TryGetPhotoCopyCaptureContext(out _), "The new object light skipped its settling interval.");
        await Task.Delay(450);
        Require(scene.TryGetPhotoCopyCaptureContext(out var readyContext) && ReferenceEquals(readyContext.Target, lockedObject),
            "The settled object target did not become ready.");

        // Entering with the index already apart must never act as a shutter.
        for (int index = 0; index < 3; index++)
        {
            var held = await Send(opened, companion);
            Require(!scene.TryTakePhotoCopyGestureShutter(held.FrameTime, out _),
                "Merely holding separated fingers captured the object.");
        }
        await Send(grouped, companion);
        await Send(grouped, companion);
        Require(scene.CurrentFingerSelectionFeedback.Any(feedback => feedback.ButtonId == "photo-shutter" &&
            feedback.Stage == BoardFingerSelectionStage.Armed), "The object-area target did not arm.");
        await Send(opened, companion);
        var busy = await Send(opened, companion, busy: true);
        Require(!scene.TryTakePhotoCopyGestureShutter(busy.FrameTime, out _), "A busy capture worker accepted another shutter.");
        for (int index = 0; index < 2; index++)
        {
            var held = await Send(opened, companion);
            Require(!scene.TryTakePhotoCopyGestureShutter(held.FrameTime, out _),
                "Returning from busy replayed the earlier separated pose.");
        }

        var selected = await Gesture(grouped, opened, companion);
        var selectingCursor = selected.Cursors.Single(cursor => cursor.Position == opened.IndexTip);
        Require(scene.TryTakePhotoCopyGestureShutter(selected.FrameTime, out long selectedId) && selectedId == selectingCursor.TrackingId,
            "Confirmed index separation did not preserve the selecting hand identity.");
        Require(!scene.TryTakePhotoCopyGestureShutter(selected.FrameTime, out _), "The same gesture shutter was consumed twice.");
        Require(scene.TryGetPhotoCopyCaptureContext(out var selectedContext) && selectedContext.Revision == readyContext.Revision &&
            ReferenceEquals(selectedContext.Target, lockedObject), "A shutter reset the Photo Copy session or discarded its object lock.");
        for (int index = 0; index < 2; index++)
        {
            var held = await Send(opened, companion);
            Require(!scene.TryTakePhotoCopyGestureShutter(held.FrameTime, out _), "Held index separation repeated capture.");
        }

        var wrongFrame = await Gesture(grouped, opened, companion);
        Require(!scene.TryTakePhotoCopyGestureShutter(wrongFrame.FrameTime.AddTicks(1), out _) &&
            !scene.TryTakePhotoCopyGestureShutter(wrongFrame.FrameTime, out _),
            "A mismatched source frame retained a shutter for later replay.");
        var capture = await Gesture(grouped, opened, companion);
        var shutterCursor = capture.Cursors.Single(cursor => cursor.Position == opened.IndexTip);
        Require(scene.TryTakePhotoCopyGestureShutter(capture.FrameTime, out long captureId) && captureId == shutterCursor.TrackingId,
            "Rejoining and separating the fingers did not rearm the shutter.");
        Require(PhotoCopyHandSelector.TrySelectGestureShutter(capture.Hands, shutterCursor, out var shutter) &&
            ReferenceEquals(shutter, opened), "The shutter hand was confused with the companion hand.");
        var otherHands = capture.Hands.Where(hand => !ReferenceEquals(hand, shutter)).ToArray();
        Require(otherHands.Length == 1 && ReferenceEquals(otherHands[0], companion), "The triggering hand was retained as a photo subject.");
        var cutout = PhotoObjectExtractor.ExtractTarget(size, size, size * 4, fixture, shutter!,
            selectedContext.CameraToBoard, lockedObject!, out var extractFailure, otherHands);
        Require(cutout is not null, "The index-separation shutter could not extract the locked object: " + extractFailure);
        cutout = PhotoCopyCameraImage.Capture(size, size, size * 4, fixture, selectedContext.CameraToBoard, cutout!);
        Require(cutout?.CameraGeometry is not null, "The photograph lost its original camera proportions before rendering.");
        Require(scene.SetPhotoCopyCapture(cutout!, selectedContext.Revision, lockedObject), "The gesture capture did not enter the renderer.");
        await captionHolds.OpenDrawerAsync();
        Require(scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(
                ["photo-drawer-close", "menu", "capture-again", "photo-save"]) &&
            scene.CurrentBoardButtons.Single(button => button.Id == "capture-again") is { Label: "Clear", Enabled: true } &&
            scene.CurrentBoardButtons.Single(button => button.Id == "photo-save") is { Label: "Save", Enabled: true },
            "Swirl did not expose Clear and Save for the retained photograph.");
        await Task.Delay(1200);
        Draw();
        Require(scene.PhotoCopyCount > 0, "The captured object did not begin stamping.");
        var rendered = target.GetPixelBytes();
        int coloredPixels = 0;
        for (int offset = 0; offset < rendered.Length; offset += 4)
            if (rendered[offset + 2] > rendered[offset] + 60 && rendered[offset + 2] > rendered[offset + 1] + 60)
                coloredPixels++;
        Require(coloredPixels > 200, "The object stamps were counted without rendered object pixels.");
        string directory = Path.Combine(_appDataDirectory, "PhotoCopyGestureSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "index-selection-first-copies.png");
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        int initialCopies = scene.PhotoCopyCount;

        // Visible controls require broken caption lettering. Their former
        // index gestures must neither reset the image nor become field shutters.
        var restart = scene.CurrentBoardButtons.Single(button => button.Id == "capture-again").Bounds;
        var ignoredClear = await Gesture(AtControl(restart, false), AtControl(restart, true));
        Require(!scene.TryTakePhotoCopyGestureShutter(ignoredClear.FrameTime, out _) &&
            scene.PhotoCopyCount >= initialCopies && scene.CurrentBoardScreen == BoardScreen.PhotoCopy,
            "An index gesture bypassed Clear's caption hold.");
        var restarted = await captionHolds.HoldAsync("capture-again");
        Require(!scene.TryTakePhotoCopyGestureShutter(restarted, out _) && scene.PhotoCopyCount == 0 &&
            scene.CurrentBoardScreen == BoardScreen.PhotoCopy && !scene.TryGetPhotoCopyCaptureContext(out _),
            "Clear's caption hold became a shutter or failed to reset the capture field.");
        Require(!scene.SetPhotoCopyCapture(cutout!, selectedContext.Revision, lockedObject), "Clear accepted an obsolete capture.");
        Require(scene.CurrentBoardButtons.Any(button => button.Id == "photo-swirl") &&
            scene.CurrentBoardButtons.All(button => button.Id != "capture-again"),
            "Clearing the Swirl did not restore the Swirl control.");
        var back = scene.CurrentBoardButtons.Single(button => button.Id == "menu").Bounds;
        var ignoredExit = await Gesture(AtControl(back, false), AtControl(back, true));
        Require(!scene.TryTakePhotoCopyGestureShutter(ignoredExit.FrameTime, out _) &&
            scene.CurrentBoardScreen == BoardScreen.PhotoCopy,
            "An index gesture bypassed Exit's caption hold.");
        var left = await captionHolds.HoldAsync("menu");
        Require(!scene.TryTakePhotoCopyGestureShutter(left, out _) && scene.CurrentBoardScreen == BoardScreen.Menu,
            "Exit's caption hold became a shutter instead of navigating.");
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The isolated Photo Copy gesture check changed live hardware or navigation.");
        return new { passed = true, sharedCaptureBounds = true, greyAndObjectSettleGated = true, subjectRequired = true, busyDoesNotQueue = true,
            actualIndexPose = true, correctShutterIdentity = true, noPinchEvents = true, exactFrameConsumedOnce = true,
            heldGestureDoesNotRepeat = true, sameHandRearms = true, objectLockPreserved = true,
            objectExtractedAndRendered = true, nativeCameraPhoto = true, initialCopies,
            bottomControlsNavigate = true, bottomControlsIgnoreIndexGestures = true,
            clearAndExitByCaptionHold = true, captionHolds.SuccessfulHolds, captionHolds.BrokenCaptionFrames,
            liveHardwareUnchanged = true, path };

        HandDetection AtControl(BoardRect bounds, bool separated) =>
            HandAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, separated);
        async Task<(DateTimeOffset FrameTime, HandCursor[] Cursors, HandDetection[] Hands)> Gesture(
            HandDetection together, HandDetection apart, HandDetection? other = null)
        {
            await Send(together, other);
            await Send(together, other);
            var opening = await Send(apart, other);
            Require(!scene.TryTakePhotoCopyGestureShutter(opening.FrameTime, out _), "One separated sample fired before confirmation.");
            return await Send(apart, other);
        }
        async Task<(DateTimeOffset FrameTime, HandCursor[] Cursors, HandDetection[] Hands)> Send(
            HandDetection hand, HandDetection? other = null, bool busy = false)
        {
            await Task.Delay(110);
            HandDetection[] hands = other is null ? [hand] : (reorder = !reorder) ? [other, hand] : [hand, other];
            var frameTime = DateTimeOffset.UtcNow;
            var cursors = tracker.Update(hands, frameTime, frameTime).ToArray();
            Require(cursors.Length == hands.Length && cursors.All(cursor => cursor.ExecuteEventId == 0),
                "The real index-selection fixture lost a hand or generated a raw pinch event.");
            scene.SetHandCursors(cursors, frameTime, busy);
            return (frameTime, cursors, hands);
        }
        HandDetection HandAt(double u, double v, bool separated)
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
            var middle = CameraPoint(u, v);
            var hand = new HandDetection(points.Select(point => new PixelPoint(middle.X + .65 * point.X,
                middle.Y + .65 * (point.Y + 113))).ToArray(), .95, .5);
            var pose = HandPoseClassifier.DescribeFingerSelection(hand);
            Require(HandPoseClassifier.AreFourFingersExtended(hand) && pose.Together == !separated && pose.IndexSeparated == separated,
                "The Photo Copy fixture did not have the intended real finger geometry.");
            return hand;
        }
        PixelPoint CameraPoint(double u, double v) => new(
            size * (.035 + .93 * (inset / 2 + u * (1 - inset))), size * (.035 + .93 * (inset / 2 + v * (1 - inset))));
        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
