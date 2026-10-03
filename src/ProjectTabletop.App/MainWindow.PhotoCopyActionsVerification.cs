#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task<object> VerifyPhotoCopyActionsAsync()
    {
        VerifyPhotoCopyObservationCaptureBoundary();
        VerifyPhotoCopyAcquisitionStability();
        VerifyPhotoCopyHoldSubjectSelection();
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int size = 1000;
        using var scene = new SceneCompositor();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowPhotoCopy();
        Draw();
        await Task.Delay(1100);
        Require(scene.TryGetPhotoCopyCaptureContext(out var grey), "Photo Copy fixture did not settle.");
        var holds = new PhotoCopyCaptionHoldFixture(scene, Draw, size);
        var unavailable = await holds.HoldAsync("photo-copy-once", expectActivation: false);
        Require(!scene.TryTakePhotoCopyCaptureRequest(unavailable, out _),
            "A caption hold captured without a locked object or two-hand capture permission.");

        var fixture = new byte[size * size * 4];
        var objectTopLeft = CameraPoint(.2, .28);
        var objectBottomRight = CameraPoint(.38, .48);
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
        var lockedObject = PhotoObjectLocator.Locate(size, size, size * 4, fixture, grey.CameraToBoard, out var locateFailure);
        Require(lockedObject is not null && scene.SetPhotoCopyObject(lockedObject!, grey.Revision),
            "Photo Copy object could not lock: " + locateFailure);
        Draw();
        await Task.Delay(450);
        Require(scene.TryGetPhotoCopyCaptureContext(out var context) && ReferenceEquals(context.Target, lockedObject),
            "Photo Copy object light did not become ready.");

        var tracker = new HandGestureTracker();
        long pinchId = 70000;
        (string Id, PhotoCopyAction Action)[] actions =
            [("photo-swirl", PhotoCopyAction.Swirl), ("photo-copy-once", PhotoCopyAction.Copy)];
        Require(scene.CurrentBoardButtons.Select(button => button.Id).SequenceEqual(
            ["menu", "photo-swirl", "photo-copy-once"]),
            "Photo Copy did not expose just Exit, Swirl and Copy.");
        Require(scene.CurrentBoardButtons[0].Label == "Exit" &&
            scene.CurrentBoardButtons.All(button => button.Bounds.Y == .835 && button.Hold == BoardButtonHold.Once) &&
            scene.CurrentBoardButtons.All(button => button.Bounds.Y >
                BoardSession.PhotoCopyShutterBounds.Y + BoardSession.PhotoCopyShutterBounds.Height),
            "Photo Copy controls did not sit below the capture area near the bottom edge.");
        foreach (var action in actions)
        {
            var pinch = await Pinch(action.Id);
            Require(!scene.TryTakePhotoCopyCaptureRequest(pinch, out _),
                action.Id + " accepted a pinch instead of broken-caption evidence.");
            var selected = await FingerGesture(action.Id);
            Require(!scene.TryTakePhotoCopyCaptureRequest(selected.Time, out _),
                action.Id + " accepted index separation over its hold caption.");
            await holds.CheckReleaseCancelsAsync(action.Id);
            var source = await holds.HoldAsync(action.Id);
            Require(scene.TryTakePhotoCopyCaptureRequest(source, out var request) && request.Action == action.Action &&
                request.CaptionHold && request.TrackingId == 0 && request.FrameTime == source &&
                request.Context.Revision == context.Revision && ReferenceEquals(request.Context.Target, lockedObject),
                action.Id + " caption hold lost its exact frame, action or locked object without tracked hands.");
            Require(!scene.TryTakePhotoCopyCaptureRequest(source, out _) && scene.PhotoCopyCount == 0,
                "Routing an explicit action repeated its request or prematurely created Swirl stamps.");
            var continued = await holds.ContinueHeldAsync(action.Id);
            Require(!scene.TryTakePhotoCopyCaptureRequest(continued, out _),
                "A continuously broken caption repeated an explicit action.");
        }

        var wrongSource = await holds.HoldAsync("photo-copy-once");
        Require(!scene.TryTakePhotoCopyCaptureRequest(wrongSource.AddTicks(1), out _) &&
            !scene.TryTakePhotoCopyCaptureRequest(wrongSource, out _),
            "A wrong-frame read preserved a request for replay.");
        var unprepared = await holds.HoldAsync("photo-copy-once", prepareHeldFrames: false);
        Require(!scene.TryTakePhotoCopyCaptureRequest(unprepared, out _),
            "A completed hold reused capture preparation from an earlier camera frame.");
        var busySource = await holds.HoldAsync("photo-copy-once", busy: true, expectActivation: false);
        Require(!scene.TryTakePhotoCopyCaptureRequest(busySource, out _), "Busy capture queued a timed copy.");
        var freshSource = await holds.HoldAsync("photo-copy-once");
        Require(scene.TryTakePhotoCopyCaptureRequest(freshSource, out var timed) && timed.Action == PhotoCopyAction.Copy,
            "Capture readiness did not recover after busy work.");

        DateTimeOffset selectedAt = DateTimeOffset.UtcNow, due = selectedAt.AddSeconds(3);
        Require(!TimedPhotoFrameReady(selectedAt, due, selectedAt) &&
            !TimedPhotoFrameReady(due.AddTicks(-1), due, due) &&
            TimedPhotoFrameReady(due, due, due) &&
            TimedPhotoFrameReady(due.AddMilliseconds(10), due, due.AddMilliseconds(30)) &&
            !TimedPhotoFrameReady(due.AddMilliseconds(40), due, due.AddMilliseconds(30)) &&
            !TimedPhotoFrameReady(due, due, due + HandMarkerLifetime + TimeSpan.FromTicks(1)),
            "Timed Copy did not require a fresh camera image captured at or after its three-second deadline.");
        Require(scene.BeginPhotoCopyCountdown(context, due) && scene.PhotoCopyStatus.StartsWith("Copy in 3"),
            "Timed Copy did not display its countdown.");
        Require(scene.GetPhotoCopyDisplayStatus(selectedAt).StartsWith("Copy in 3") &&
            scene.GetPhotoCopyDisplayStatus(selectedAt.AddSeconds(1)).StartsWith("Copy in 2") &&
            scene.GetPhotoCopyDisplayStatus(selectedAt.AddSeconds(2)).StartsWith("Copy in 1") &&
            scene.GetPhotoCopyDisplayStatus(due) == "Taking photo…",
            "The visible countdown did not follow its exact three-second deadline.");
        scene.SetHandCursors([], DateTimeOffset.UtcNow, photoCopyCaptureBusy: true);
        var countdown = Draw();
        string directory = Path.Combine(_appDataDirectory, "PhotoCopyActionSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string countdownPath = Path.Combine(directory, "copy-countdown-bottom-controls.png");
        await target.SaveAsync(countdownPath, CanvasBitmapFileFormat.Png);
        scene.EndPhotoCopyCountdown(context.Revision);
        Require(scene.BeginPhotoCopyCapture(context) && scene.PhotoCopyStatus.Contains("Taking a photo"),
            "Ending the timer did not allow the same ready object to be captured.");

        Require(scene.SetPhotoCopyImageSaved(context) && scene.PhotoCopyStatus == "Image Saved" &&
            scene.PhotoCopyCount == 0 && scene.TryGetPhotoCopyCaptureContext(out var afterSave) &&
            ReferenceEquals(afterSave.Target, lockedObject) && afterSave.Revision == context.Revision,
            "Saving a Copy created Swirl stamps, reset the object, or failed to show success.");
        scene.SetHandCursors([], DateTimeOffset.UtcNow);
        var saved = Draw();
        Require(!saved.SequenceEqual(countdown), "Countdown and saved status did not update the cached pixels.");
        var objectCenter = CameraPoint(lockedObject!.Center.X / PhotoHandCutout.BoardPixels,
            lockedObject.Center.Y / PhotoHandCutout.BoardPixels);
        Require(WhiteAt(saved, objectCenter), "Saving a Copy removed the object's spotlight.");
        string savedPath = Path.Combine(directory, "image-saved-bottom-controls.png");
        await target.SaveAsync(savedPath, CanvasBitmapFileFormat.Png);
        var savedAt = DateTimeOffset.UtcNow;
        Require(scene.SetPhotoCopyImageSaved(context, savedAt) &&
            scene.GetPhotoCopyDisplayStatus(savedAt) == "Image Saved" &&
            scene.GetPhotoCopyDisplayStatus(savedAt.AddSeconds(3).AddTicks(-1)) == "Image Saved",
            "Saved feedback ended before three seconds.");
        Require(scene.GetPhotoCopyDisplayStatus(savedAt.AddSeconds(3)) != "Image Saved" && scene.PhotoCopyCount == 0,
            "Saved feedback outlived three seconds or began stamping later.");

        // Success/reset and timer/reset are separate because starting either presentation
        // intentionally replaces the other. Neither writes a PNG into the user's Pictures.
        Require(scene.SetPhotoCopyImageSaved(context), "Could not restore the success fixture.");
        scene.ShowPhotoCopy();
        Require(scene.PhotoCopyStatus != "Image Saved" && !scene.SetPhotoCopyImageSaved(context),
            "Reset retained success or accepted completion from an obsolete capture.");
        Draw(); await Task.Delay(1100);
        Require(scene.TryGetPhotoCopyCaptureContext(out var resetGrey) && scene.SetPhotoCopyObject(lockedObject!, resetGrey.Revision),
            "Reset fixture could not reacquire the object.");
        Draw(); await Task.Delay(450);
        Require(scene.TryGetPhotoCopyCaptureContext(out var resetContext), "Reset object did not settle.");
        var pending = await holds.HoldAsync("photo-copy-once");
        Require(scene.BeginPhotoCopyCountdown(resetContext, DateTimeOffset.UtcNow.AddSeconds(3)), "Reset timer fixture failed.");
        scene.ShowPhotoCopy();
        Require(!scene.TryTakePhotoCopyCaptureRequest(pending, out _) && !scene.PhotoCopyStatus.StartsWith("Copy in") &&
            !scene.BeginPhotoCopyCountdown(resetContext, DateTimeOffset.UtcNow.AddSeconds(3)) &&
            !scene.BeginPhotoCopyCapture(resetContext) && !scene.SetPhotoCopyImageSaved(resetContext),
            "Reset retained a timer/request or accepted an obsolete capture context.");
        scene.EndPhotoCopyCountdown(resetContext.Revision);

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Isolated Photo Copy action verification changed live hardware or navigation.");
        return new { passed = true, twoActionsRouteByRenderedCaptionHold = true, pinchAndIndexButtonsRejected = true,
            zeroTrackedCursorsWithLockedObject = true, intactCaptionCancelsPartialHold = true,
            sameFramePreparationRequired = true, capturePermissionRequired = true, exactFrameConsumedOnce = true,
            onlyOneInFieldSubjectQualifiesWithoutObjectLock = true,
            holds.SuccessfulHolds, holds.BrokenCaptionFrames,
            preCaptureObservationCannotCancelCountdown = true,
            acquisitionRequiresStableShapeAreaAndMask = true, acquisitionAnchoredForThreeFramesAnd600ms = true,
            busyAndResetReject = true, timedCopyRequiresFreshPostDeadlineFrame = true, savedFeedbackThreeSeconds = true,
            savedCopyHasNoSwirlStamps = true, objectLightPreserved = true, liveHardwareUnchanged = true,
            noPicturesWrites = true, directory, images = new[]
                { new { name = "countdown", path = countdownPath }, new { name = "image-saved", path = savedPath } } };

        async Task<DateTimeOffset> Pinch(string id, bool busy = false)
        {
            await Task.Delay(10);
            var time = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(CameraPointForButton(id), time.AddSeconds(1), ++pinchId)
                { TrackingId = 501 }], time, busy);
            return time;
        }
        async Task<(DateTimeOffset Time, long TrackingId)> FingerGesture(string id)
        {
            var bounds = scene.CurrentBoardButtons.Single(button => button.Id == id).Bounds;
            var together = HandAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, false);
            var apart = HandAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, true);
            await Send(together); await Send(together); await Send(apart);
            return await Send(apart);
        }
        async Task<(DateTimeOffset Time, long TrackingId)> Send(HandDetection hand)
        {
            await Task.Delay(110);
            var time = DateTimeOffset.UtcNow;
            var cursors = tracker.Update([hand], time, time).ToArray();
            Require(cursors.Length == 1 && cursors[0].ExecuteEventId == 0,
                "Real index fixture did not produce one non-pinching hand.");
            scene.SetHandCursors(cursors, time);
            return (time, cursors[0].TrackingId);
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
                "The index fixture did not have the intended finger geometry.");
            return hand;
        }
        PixelPoint CameraPointForButton(string id)
        {
            var bounds = scene.CurrentBoardButtons.Single(button => button.Id == id).Bounds;
            return CameraPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }
        PixelPoint CameraPoint(double u, double v) => new(size * (.035 + .93 * (inset / 2 + u * (1 - inset))),
            size * (.035 + .93 * (inset / 2 + v * (1 - inset))));
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        static bool WhiteAt(byte[] pixels, PixelPoint point)
        {
            int offset = ((int)Math.Round(point.Y) * size + (int)Math.Round(point.X)) * 4;
            return pixels[offset] > 245 && pixels[offset + 1] > 245 && pixels[offset + 2] > 245;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static void VerifyPhotoCopyHoldSubjectSelection()
    {
        var context = new SceneCompositor.PhotoCopyCaptureContext(1,
            [.001, 0, 0, 0, .001, 0, 0, 0, 1], DateTimeOffset.MinValue, null);
        var subject = Hand(500, 400);
        var otherSubject = Hand(300, 300);
        var buttonHand = Hand(500, 890);
        var outside = Hand(1100, 400);
        Require(PhotoCopyHoldSubjects([], context).Count == 0 &&
                PhotoCopyHoldSubjects([buttonHand, outside], context).Count == 0,
            "A caption hold selected the pressing hand or an off-board hand as its photograph subject.");
        Require(PhotoCopyHoldSubjects([buttonHand, subject, outside], context).SequenceEqual([subject]),
            "A caption hold did not retain the one in-field subject independently of the pressing hand.");
        Require(PhotoCopyHoldSubjects([subject, otherSubject, buttonHand], context).Count == 2,
            "The subject filter silently selected one of two ambiguous hands inside the capture field.");
        var malformed = new HandDetection([new(500, 400)], .95, .5);
        Require(PhotoCopyHoldSubjects([malformed, Hand(double.NaN, 400), Hand(500, double.PositiveInfinity)], context).Count == 0,
            "Malformed or non-finite hand landmarks qualified as a capture subject.");
        Require(PhotoCopyHoldSubjects([subject], context with { CameraToBoard = [.001, 0, 0, 0, .001, 0, 0, 0, 0] }).Count == 0,
            "A hand on a projective horizon qualified as a capture subject.");
        var rotated = context with { CameraToBoard = [-.001, 0, 1, 0, -.001, 1, 0, 0, 1] };
        Require(PhotoCopyHoldSubjects([subject, Hand(500, 100)], rotated).SequenceEqual([subject]),
            "Subject selection ignored the calibrated camera-to-board transform.");

        static HandDetection Hand(double x, double y) => new(
            Enumerable.Repeat(new PixelPoint(x, y), 21).ToArray(), .95, .5);
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static void VerifyPhotoCopyObservationCaptureBoundary()
    {
        if (!PhotoCopyObservationMayApply(null, null))
            throw new InvalidOperationException("An idle Photo Copy observation could not apply.");

        var capture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The object scan starts first, then an accepted button begins a timer.
        // Its late result must not clear the lock or replace the countdown status.
        if (PhotoCopyObservationMayApply(null, capture.Task) ||
            PhotoCopyObservationMayApply(capture.Task, capture.Task))
            throw new InvalidOperationException("An observation could apply while Photo Copy was capturing.");
        capture.SetResult();
        if (PhotoCopyObservationMayApply(null, capture.Task))
            throw new InvalidOperationException("A pre-capture observation survived the completed capture.");
        if (!PhotoCopyObservationMayApply(capture.Task, capture.Task))
            throw new InvalidOperationException("Background observation did not resume after capture.");

        var nextCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (PhotoCopyObservationMayApply(capture.Task, nextCapture.Task))
            throw new InvalidOperationException("An observation from the previous capture crossed a new countdown.");
        nextCapture.SetResult();
        if (PhotoCopyObservationMayApply(capture.Task, nextCapture.Task) ||
            !PhotoCopyObservationMayApply(nextCapture.Task, nextCapture.Task))
            throw new InvalidOperationException("Repeated captures did not isolate background observations.");
    }

    private static void VerifyPhotoCopyAcquisitionStability()
    {
        var solid = Target();
        var shifted8 = Target(offsetX: 8);
        var shifted16 = Target(offsetX: 16);
        var smallHole = Target(hole: (90, 60, 60, 60));
        var largerHole = Target(hole: (75, 60, 90, 80));
        var holeLeft = Target(hole: (20, 60, 120, 80));
        var holeRight = Target(hole: (100, 60, 120, 80));
        var round = Target(ellipse: true);
        var comparableRectangle = Target(hole: (70, 50, 100, 100));
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var window = new PhotoCopyAcquisitionWindow();

        Require(!Seen(solid, 0) && !Seen(solid, 300) && Seen(solid, 600),
            "A stable object did not require three observations across 600 milliseconds.");
        window.Reset();
        Require(!Seen(solid, 0) && !Seen(solid, 100) && !Seen(solid, 200) && Seen(solid, 600),
            "Three closely spaced scans bypassed camera settling time.");
        window.Reset();
        Require(!Seen(solid, 0) && !Seen(solid, 0) && !Seen(solid, -1) && window.Count == 1 &&
                !Seen(solid, 300) && Seen(solid, 600),
            "A repeated or older frame contributed to silhouette stability.");

        window.Reset();
        Require(!Seen(solid, 0) && !Seen(shifted8, 300) && !Seen(shifted16, 600) && window.Count == 1 &&
                !Seen(shifted16, 900) && Seen(shifted16, 1200),
            "Small per-frame movement drifted away from the first candidate and still locked.");
        window.Reset();
        Require(!Seen(solid, 0) && !Seen(smallHole, 300) && window.Count == 2 &&
                !Seen(largerHole, 600) && window.Count == 1 &&
                !Seen(largerHole, 900) && Seen(largerHole, 1200),
            "Progressive loss of the pale face bypassed the anchor's silhouette-area check.");

        Require(holeLeft.Center == holeRight.Center && holeLeft.Width == holeRight.Width &&
                holeLeft.Height == holeRight.Height && holeLeft.ForegroundArea == holeRight.ForegroundArea &&
                holeLeft.Spotlight.Shape == holeRight.Spotlight.Shape,
            "The mask-stability fixture no longer isolates different silhouettes with matching size, area and shape.");
        window.Reset();
        Require(!Seen(holeLeft, 0) && !Seen(holeLeft, 300) && !Seen(holeRight, 600) && window.Count == 1 &&
                !Seen(holeRight, 900) && Seen(holeRight, 1200),
            "A different missing region with unchanged bounds and area counted as the same silhouette.");

        Require(round.Spotlight.Shape != comparableRectangle.Spotlight.Shape &&
                Math.Abs(round.ForegroundArea - comparableRectangle.ForegroundArea) < round.ForegroundArea * .12,
            "The shape-change fixture no longer represents similarly sized circle/rectangle candidates.");
        window.Reset();
        Require(!Seen(round, 0) && !Seen(round, 300) && !Seen(comparableRectangle, 600) && window.Count == 1 &&
                !Seen(comparableRectangle, 900) && Seen(comparableRectangle, 1200),
            "A circular partial lock and the completed rectangle shared an acquisition window.");

        // The live path invokes this same reset on a hand, revision change,
        // failed acquisition or cleared object. Old evidence must not survive.
        window.Reset();
        Require(window.Anchor is null && window.Count == 0 && window.StartedAt is null &&
                !Seen(solid, 1500) && !Seen(solid, 1800) && Seen(solid, 2100),
            "Resetting acquisition retained observations from a previous hand or board revision.");

        bool Seen(PhotoObjectTarget target, int milliseconds) => window.Observe(target, start.AddMilliseconds(milliseconds));
        static PhotoObjectTarget Target(int offsetX = 0, (int X, int Y, int Width, int Height)? hole = null,
            bool ellipse = false)
        {
            const int size = 1000, left = 300, top = 230, width = 240, height = 200;
            byte[] photo = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int localX = x - left - offsetX, localY = y - top;
                bool inside = localX >= 0 && localX < width && localY >= 0 && localY < height;
                if (inside && ellipse)
                    inside = Math.Pow((localX - (width - 1) / 2.0) / (width / 2.0), 2) +
                        Math.Pow((localY - (height - 1) / 2.0) / (height / 2.0), 2) <= 1;
                if (inside && hole is { } opening && localX >= opening.X && localX < opening.X + opening.Width &&
                    localY >= opening.Y && localY < opening.Y + opening.Height) inside = false;
                int pixel = (y * size + x) * 4;
                photo[pixel] = photo[pixel + 1] = photo[pixel + 2] = inside ? (byte)30 : (byte)110;
                photo[pixel + 3] = 255;
            }
            return PhotoObjectLocator.Locate(size, size, size * 4, photo,
                [1.0 / size, 0, 0, 0, 1.0 / size, 0, 0, 0, 1], out var failure) ??
                throw new InvalidOperationException("Acquisition stability fixture failed: " + failure);
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
