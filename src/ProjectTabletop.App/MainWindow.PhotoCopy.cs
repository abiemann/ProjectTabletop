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
            !PhotoCopyHandSelector.TrySelect(hands, shutters[0], out var target) || target is null)
        {
            _scene.SetPhotoCopyStatus("Show both hands. Pinch with only the hand you do not want copied.", context.Revision);
            return;
        }
        if (!HandInsidePhotoArea(target, context.CameraToBoard))
        {
            _scene.SetPhotoCopyStatus("Keep the whole hand to copy in the white area, below the buttons.", context.Revision);
            return;
        }

        _scene.SetPhotoCopyStatus("Taking a photo of the other hand...", context.Revision);
        _photoCopyTask = CapturePhotoCopyAsync(frame, target, context);
    }

    private async Task CapturePhotoCopyAsync(CameraFrame frame, HandDetection target,
        SceneCompositor.PhotoCopyCaptureContext context)
    {
        try
        {
            var cutout = await Task.Run(() => PhotoHandExtractor.Extract(frame.Width, frame.Height,
                frame.Stride, frame.Bgra, target, context.CameraToBoard));
            if (_closing) return;
            if (cutout is null)
                _scene.SetPhotoCopyStatus("Keep the hand flat with the middle finger extended; pinch with the other hand again.", context.Revision);
            else if (_scene.SetPhotoCopyCapture(cutout, context.Revision))
                _lastPhotoCopyCapture = DescribeCutout(cutout, frame.Timestamp);
            UpdateBoardAppStatus();
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                _scene.SetPhotoCopyStatus("Photo capture failed. Release, then pinch again with the other hand.", context.Revision);
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

    private static bool HandInsidePhotoArea(HandDetection hand, IReadOnlyList<double> map)
    {
        if (hand.Landmarks.Count != 21 || map.Count != 9) return false;
        var boardPoints = new List<PixelPoint>(21);
        foreach (var point in hand.Landmarks)
        {
            var denominator = map[6] * point.X + map[7] * point.Y + map[8];
            if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12) return false;
            var u = (map[0] * point.X + map[1] * point.Y + map[2]) / denominator;
            var v = (map[3] * point.X + map[4] * point.Y + map[5]) / denominator;
            if (!double.IsFinite(u) || !double.IsFinite(v) || u is < .06 or > .94 || v is < .23 or > .94)
                return false;
            boardPoints.Add(new PixelPoint(u, v));
        }
        static double Distance(PixelPoint a, PixelPoint b) =>
            Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var palmScale = Math.Max(Distance(boardPoints[0], boardPoints[9]),
            Distance(boardPoints[5], boardPoints[17]));
        // Finger contours extend beyond the landmark centers. Keep the entire
        // segmentation crop below projected text, including its color-reference margin.
        return boardPoints.Min(point => point.Y) - (palmScale * .22 + .004) > .215;
    }
}
