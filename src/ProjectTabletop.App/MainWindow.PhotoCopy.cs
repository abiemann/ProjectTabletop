using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Vision;
using ProjectTabletop.Interaction;
using ProjectTabletop.App.Media;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private long _photoCopyConsumedEventId;
    private Task? _photoCopyTask;
    private PhotoCopyCaptureDiagnostics? _lastPhotoCopyCapture;
    private PhotoCopyObservation? _latestPhotoCopyFrame;
    private string? _lastSavedPhotoPath;
    private readonly PhotocopierSound _photocopierSound = new();
    private sealed record PhotoCopyObservation(CameraFrame Frame, HandDetection[] Hands, long Generation);

    private sealed record PhotoCopyCaptureDiagnostics(DateTimeOffset FrameTime, int Width, int Height,
        int TransparentPixels, int PartialAlphaPixels, bool TransparentBorder, bool NativeCameraPixels);

    // Run on the UI thread after navigation has consumed the same frame. Events
    // from entering Photo Copy, settling illumination, or another app never replay.
    private void QueuePhotoCopyCapture(CameraFrame frame, IReadOnlyList<HandDetection> hands,
        IReadOnlyList<HandCursor> cursors)
    {
        _latestPhotoCopyFrame = new(frame, hands.ToArray(), _handGeneration);
        var previousEvent = _photoCopyConsumedEventId;
        foreach (var cursor in cursors)
            _photoCopyConsumedEventId = Math.Max(_photoCopyConsumedEventId, cursor.ExecuteEventId);
        // The gesture uses the webcam, but saving an existing swirl uses only
        // the captured photograph to render its complete pattern from memory.
        // It requires no subject or new extraction.
        if (_scene.TryTakePhotoCopyMemorySaveRequest(frame.Timestamp, out var memoryImage))
        {
            if (_photoCopyTask is not { IsCompleted: false })
                _photoCopyTask = SaveStoredPhotoCopyAsync(memoryImage);
            return;
        }
        var now = MonotonicClock.UtcNow;
        bool explicitRequest = _scene.TryTakePhotoCopyCaptureRequest(frame.Timestamp, out var request);
        if (explicitRequest && request.CaptionHold)
        {
            if (_photoCopyTask is { IsCompleted: false } || !_scene.IsPhotoCopyCaptureCurrent(request.Context) ||
                frame.Timestamp < request.Context.ReadyAfter || frame.Timestamp > now ||
                now - frame.Timestamp > HandMarkerLifetime) return;
            IReadOnlyList<HandDetection> subjects = hands;
            if (request.Context.Target is null)
            {
                // The held bottom caption identifies the command independently
                // of landmarks. Only the other hand inside the capture field
                // may become the subject; ambiguous subjects are rejected.
                subjects = PhotoCopyHoldSubjects(hands, request.Context);
                if (subjects.Count != 1)
                {
                    _scene.SetPhotoCopyStatus("Keep one subject hand inside the photo area, separate from the button.",
                        request.Context.Revision);
                    return;
                }
            }
            if (_scene.BeginPhotoCopyCapture(request.Context))
                _photoCopyTask = CapturePhotoCopyAsync(frame, null, subjects, request.Context, request.Action);
            return;
        }
        // An accepted button request wins over the same raw pinch, so Copy cannot also Swirl.
        var shutters = explicitRequest
            ? cursors.Where(cursor => cursor.TrackingId == request.TrackingId).ToArray()
            : cursors.Where(cursor => cursor.ExecuteEventId > previousEvent && cursor.IsExecuting(now) &&
                !_scene.IsPhotoCopyHoldControl(cursor.Position) &&
                !_scene.IsPhotoCopyHoldControl(cursor.SelectionPosition ?? cursor.Position)).ToArray();
        if (shutters.Length == 0 || _photoCopyTask is { IsCompleted: false } ||
            !_scene.TryGetPhotoCopyCaptureContext(out var context) ||
            frame.Timestamp < context.ReadyAfter || frame.Timestamp > now ||
            now - frame.Timestamp > HandMarkerLifetime) return;

        HandDetection? shutter = null;
        bool fingerGesture = explicitRequest && request.Gesture == BoardSelectionGesture.IndexSeparation;
        bool selected = shutters.Length == 1 && (fingerGesture
            ? PhotoCopyHandSelector.TrySelectGestureShutter(hands, shutters[0], out shutter)
            : PhotoCopyHandSelector.TrySelectShutter(hands, shutters[0], out shutter));
        if (!selected || shutter is null)
        {
            _scene.SetPhotoCopyStatus("Select with one visible hand. Keep a gap between your hand and the subject.", context.Revision);
            return;
        }

        var action = explicitRequest ? request.Action : PhotoCopyAction.Swirl;
        if (context.Target is null && (hands.Count < 2 || action == PhotoCopyAction.TimedCopy))
        {
            _scene.SetPhotoCopyStatus("First place an object on grey and lift your hand briefly to lock its light.",
                context.Revision);
            return;
        }

        if (action == PhotoCopyAction.TimedCopy)
            _photoCopyTask = CaptureDelayedPhotoCopyAsync(context, _handGeneration);
        else
        {
            _scene.BeginPhotoCopyCapture(context);
            _photoCopyTask = CapturePhotoCopyAsync(frame, shutter,
                hands.Where(hand => !ReferenceEquals(hand, shutter)).ToArray(), context, action);
        }
    }

    private static IReadOnlyList<HandDetection> PhotoCopyHoldSubjects(IReadOnlyList<HandDetection> hands,
        SceneCompositor.PhotoCopyCaptureContext context)
    {
        var mapping = context.CameraToBoard;
        return hands.Where(hand =>
        {
            if (hand.Landmarks.Count != 21) return false;
            var tip = hand.IndexTip;
            double w = mapping[6] * tip.X + mapping[7] * tip.Y + mapping[8];
            if (!double.IsFinite(w) || Math.Abs(w) < 1e-10) return false;
            double u = (mapping[0] * tip.X + mapping[1] * tip.Y + mapping[2]) / w;
            double v = (mapping[3] * tip.X + mapping[4] * tip.Y + mapping[5]) / w;
            return BoardSession.PhotoCopyShutterBounds.Contains(u, v);
        }).ToArray();
    }

    private async Task SaveStoredPhotoCopyAsync(SceneCompositor.PhotoCopyMemoryImage image)
    {
        if (!_scene.IsPhotoCopyMemoryImageCurrent(image)) return;
        _photocopierSound.Play();
        try
        {
            var path = await SavePhotoCopyMemoryImageAsync(_scene, image);
            if (path is not null) _lastSavedPhotoPath = path;
        }
        catch (Exception error) { AppLog.Write("Photo Copy memory save", error); }
        finally { if (!_closing) UpdateBoardAppStatus(); }
    }

    private static async Task<string?> SavePhotoCopyMemoryImageAsync(SceneCompositor scene,
        SceneCompositor.PhotoCopyMemoryImage image, string? directory = null)
    {
        if (!scene.BeginPhotoCopyMemorySave(image)) return null;
        try
        {
            var artwork = scene.RenderPhotoCopySwirlImage(image);
            var path = await PhotoCopyImageStore.SaveBgraAsync(artwork.Width, artwork.Height,
                artwork.Width * 4, artwork.BgraPixels, directory);
            scene.CompletePhotoCopyMemorySave(image);
            return path;
        }
        catch
        {
            scene.FailPhotoCopyMemorySave(image);
            throw;
        }
    }

    private async Task CaptureDelayedPhotoCopyAsync(SceneCompositor.PhotoCopyCaptureContext context, long generation)
    {
        DateTimeOffset due = MonotonicClock.UtcNow.AddSeconds(3);
        if (!_scene.BeginPhotoCopyCountdown(context, due)) return;
        try
        {
            // Use an observation captured AFTER the countdown, never the button-selection photo.
            // Poll only while this short operation is pending; camera inference supplies frame + hands together.
            while (!_closing && generation == _handGeneration && _scene.IsPhotoCopyCaptureCurrent(context))
            {
                var now = MonotonicClock.UtcNow;
                if (_latestPhotoCopyFrame is { } observation && observation.Generation == generation &&
                    TimedPhotoFrameReady(observation.Frame.Timestamp, due, now))
                {
                    _scene.EndPhotoCopyCountdown(context.Revision);
                    _scene.BeginPhotoCopyCapture(context);
                    await CapturePhotoCopyAsync(observation.Frame, null, observation.Hands, context, PhotoCopyAction.Copy);
                    return;
                }
                if (now >= due.AddSeconds(2))
                {
                    _scene.SetPhotoCopyStatus("No fresh camera image. Check the webcam and select Copy again.", context.Revision);
                    return;
                }
                await Task.Delay(50);
            }
        }
        finally { _scene.EndPhotoCopyCountdown(context.Revision); }
    }

    private static bool TimedPhotoFrameReady(DateTimeOffset frameTime, DateTimeOffset due, DateTimeOffset now) =>
        now >= due && frameTime >= due && frameTime <= now && now - frameTime <= HandMarkerLifetime;

    private async Task CapturePhotoCopyAsync(CameraFrame frame, HandDetection? shutter,
        IReadOnlyList<HandDetection> otherHands,
        SceneCompositor.PhotoCopyCaptureContext context, PhotoCopyAction action = PhotoCopyAction.Swirl)
    {
        if (action == PhotoCopyAction.Copy)
            _photocopierSound.Play();
        try
        {
            var result = await Task.Run(() =>
            {
                string? failure;
                PhotoHandCutout? cutout;
                if (context.Target is { } target)
                    cutout = PhotoObjectExtractor.ExtractTarget(frame.Width, frame.Height,
                        frame.Stride, frame.Bgra, shutter, context.CameraToBoard, target, out failure, otherHands);
                else
                {
                    // Preserve the original two-hand photo without trying to
                    // discover objects in a field containing moving hand lights.
                    cutout = otherHands.Count == 1 ? PhotoHandExtractor.Extract(frame.Width, frame.Height,
                        frame.Stride, frame.Bgra, otherHands[0], context.CameraToBoard) : null;
                    failure = cutout is null ? "Keep the other hand open, still and separate from the selecting hand." : null;
                }
                if (cutout is not null)
                {
                    // Use segmentation only as a mask. Preserve the photograph's
                    // native camera pixels and proportions for rotation/copying.
                    cutout = PhotoCopyCameraImage.Capture(frame.Width, frame.Height, frame.Stride,
                        frame.Bgra, context.CameraToBoard, cutout);
                    if (cutout is null) failure = "The photograph could not be mapped. Keep the subject fully on the board and try again.";
                }
                return (Cutout: cutout, Failure: failure);
            });
            if (_closing || !_scene.IsPhotoCopyCaptureCurrent(context)) return;
            if (result.Cutout is null)
                _scene.SetPhotoCopyStatus(result.Failure ?? "Keep the lit object still, then select beside it again.", context.Revision);
            else if (action == PhotoCopyAction.Swirl)
            {
                if (_scene.SetPhotoCopyCapture(result.Cutout, context.Revision, context.Target))
                    _lastPhotoCopyCapture = DescribeCutout(result.Cutout, frame.Timestamp);
            }
            else if (_scene.IsPhotoCopyCaptureCurrent(context))
            {
                // The frozen photo was accepted against this session/target before I/O.
                // Navigation during saving cannot show a stale success message on another board.
                var path = await PhotoCopyImageStore.SaveAsync(result.Cutout);
                _lastSavedPhotoPath = path;
                _lastPhotoCopyCapture = DescribeCutout(result.Cutout, frame.Timestamp);
                if (!_closing) _scene.SetPhotoCopyImageSaved(context);
            }
            if (_closing) return;
            UpdateBoardAppStatus();
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                _scene.SetPhotoCopyStatus(action == PhotoCopyAction.Swirl
                    ? "Photo capture failed. Keep the subject still and select Swirl again."
                    : "Image could not be saved. Check the Pictures folder and select Copy again.", context.Revision);
                AppLog.Write("Photo Copy capture", ex);
            }
        }
    }

    private static PhotoCopyCaptureDiagnostics DescribeCutout(PhotoHandCutout cutout, DateTimeOffset frameTime)
    {
        int transparent = 0, partial = 0;
        bool border = true;
        for (int y = 0; y < cutout.Height; y++)
        for (int x = 0; x < cutout.Width; x++)
        {
            byte alpha = cutout.BgraPixels[(y * cutout.Width + x) * 4 + 3];
            if (alpha == 0) transparent++;
            else if (alpha < 255) partial++;
            if ((x == 0 || y == 0 || x == cutout.Width - 1 || y == cutout.Height - 1) && alpha != 0)
                border = false;
        }
        return new(frameTime, cutout.Width, cutout.Height, transparent, partial, border,
            cutout.CameraGeometry is not null);
    }
}
