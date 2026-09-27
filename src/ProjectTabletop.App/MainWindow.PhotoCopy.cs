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
        int TransparentPixels, int PartialAlphaPixels, bool TransparentBorder);

    // Run on the UI thread after navigation has consumed the same frame. Events
    // from entering Photo Copy, a settling white field, or another app never replay.
    private void QueuePhotoCopyCapture(CameraFrame frame, IReadOnlyList<HandDetection> hands,
        IReadOnlyList<HandCursor> cursors)
    {
        var previousEvent = _photoCopyConsumedEventId;
        foreach (var cursor in cursors)
            _photoCopyConsumedEventId = Math.Max(_photoCopyConsumedEventId, cursor.ExecuteEventId);
        var now = DateTimeOffset.UtcNow;
        var shutters = cursors.Where(cursor => cursor.ExecuteEventId > previousEvent &&
            cursor.IsExecuting(now)).ToArray();
        if (shutters.Length == 0 || _photoCopyTask is { IsCompleted: false } ||
            !_scene.TryGetPhotoCopyCaptureContext(out var context) ||
            frame.Timestamp < context.ReadyAfter || frame.Timestamp > now ||
            now - frame.Timestamp > HandMarkerLifetime) return;

        if (shutters.Length != 1 ||
            !PhotoCopyHandSelector.TrySelectShutter(hands, shutters[0], out var shutter) || shutter is null)
        {
            _scene.SetPhotoCopyStatus("Pinch with one visible hand, keeping it separate from the object.", context.Revision);
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
                var cutout = PhotoObjectExtractor.Extract(frame.Width, frame.Height,
                    frame.Stride, frame.Bgra, shutter, context.CameraToBoard, out var failure, otherHands);
                return (Cutout: cutout, Failure: failure);
            });
            if (_closing) return;
            if (result.Cutout is null)
                _scene.SetPhotoCopyStatus(result.Failure ?? "No separate object found. Place it in the white area and pinch again.", context.Revision);
            else if (_scene.SetPhotoCopyCapture(result.Cutout, context.Revision))
                _lastPhotoCopyCapture = DescribeCutout(result.Cutout, frame.Timestamp);
            UpdateBoardAppStatus();
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                _scene.SetPhotoCopyStatus("Photo capture failed. Release, then pinch again away from the object.", context.Revision);
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
        return new(frameTime, cutout.Width, cutout.Height, transparent, partial, border);
    }
}
