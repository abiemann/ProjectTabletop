using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private long _photoCopyConsumedEventId;
    private Task? _photoCopyTask;
    private PhotoCopyCaptureDiagnostics? _lastPhotoCopyCapture;

    private sealed record PhotoCopyCaptureDiagnostics(DateTimeOffset FrameTime, int Width, int Height,
        int TransparentPixels, int PartialAlphaPixels, bool TransparentBorder, bool NativeCameraPixels);

    // Run on the UI thread after navigation has consumed the same frame. Events
    // from entering Photo Copy, settling illumination, or another app never replay.
    private void QueuePhotoCopyCapture(CameraFrame frame, IReadOnlyList<HandDetection> hands,
        IReadOnlyList<HandCursor> cursors)
    {
        var previousEvent = _photoCopyConsumedEventId;
        foreach (var cursor in cursors)
            _photoCopyConsumedEventId = Math.Max(_photoCopyConsumedEventId, cursor.ExecuteEventId);
        var now = DateTimeOffset.UtcNow;
        var gestureHandId = _scene.TryTakePhotoCopyGestureShutter(frame.Timestamp, out var selectedHandId)
            ? selectedHandId : 0;
        var shutters = cursors.Where(cursor => cursor.ExecuteEventId > previousEvent &&
            cursor.IsExecuting(now) || gestureHandId > 0 && cursor.TrackingId == gestureHandId).ToArray();
        if (shutters.Length == 0 || _photoCopyTask is { IsCompleted: false } ||
            !_scene.TryGetPhotoCopyCaptureContext(out var context) ||
            frame.Timestamp < context.ReadyAfter || frame.Timestamp > now ||
            now - frame.Timestamp > HandMarkerLifetime) return;

        HandDetection? shutter = null;
        bool selected = shutters.Length == 1 && (gestureHandId > 0 && shutters[0].TrackingId == gestureHandId
            ? PhotoCopyHandSelector.TrySelectGestureShutter(hands, shutters[0], out shutter)
            : PhotoCopyHandSelector.TrySelectShutter(hands, shutters[0], out shutter));
        if (!selected || shutter is null)
        {
            _scene.SetPhotoCopyStatus("Select with one visible hand: fingers together, then index sideways. Keep a gap from the subject.", context.Revision);
            return;
        }

        if (context.Target is null && hands.Count < 2)
        {
            _scene.SetPhotoCopyStatus("First place an object on grey and lift your hand briefly to lock its light.",
                context.Revision);
            return;
        }

        _scene.SetPhotoCopyStatus("Taking a photo of the object...", context.Revision);
        _photoCopyTask = CapturePhotoCopyAsync(frame, shutter,
            hands.Where(hand => !ReferenceEquals(hand, shutter)).ToArray(), context);
    }

    private async Task CapturePhotoCopyAsync(CameraFrame frame, HandDetection shutter,
        IReadOnlyList<HandDetection> otherHands,
        SceneCompositor.PhotoCopyCaptureContext context)
    {
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
            if (_closing) return;
            if (result.Cutout is null)
                _scene.SetPhotoCopyStatus(result.Failure ?? "Keep the lit object still, then select beside it again.", context.Revision);
            else if (_scene.SetPhotoCopyCapture(result.Cutout, context.Revision, context.Target))
                _lastPhotoCopyCapture = DescribeCutout(result.Cutout, frame.Timestamp);
            UpdateBoardAppStatus();
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                _scene.SetPhotoCopyStatus("Photo capture failed. Bring fingers together, then move index sideways away from the object.", context.Revision);
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
