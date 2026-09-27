using ProjectTabletop.Vision;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private sealed record PhotoCopyGestureShutter(long TrackingId, DateTimeOffset FrameTime,
        PhotoCopyCaptureContext Context);
    private PhotoCopyGestureShutter? _photoCopyGestureShutter;

    // Feed the same board gesture recognizer only while a fresh capture can be
    // made. Settling, busy work and unavailable subjects never queue a shutter.
    private PhotoCopyCaptureContext? PreparePhotoCopyGesture(IReadOnlyList<HandCursor> cursors,
        DateTimeOffset frameTime, bool acceptFrame, bool busy)
    {
        _photoCopyGestureShutter = null;
        PhotoCopyCaptureContext? ready = null;
        if (acceptFrame && !busy && TryGetPhotoCopyCaptureContext(out var context) &&
            frameTime >= context.ReadyAfter && (context.Target is not null || cursors.Count == 2))
            ready = context;
        _boardSession.PhotoCopyShutterEnabled = ready is not null;
        return ready;
    }

    // Consumed by the camera pipeline immediately after this very observation.
    // A later frame, changed object or new board session cannot replay it.
    public bool TryTakePhotoCopyGestureShutter(DateTimeOffset frameTime, out long trackingId)
    {
        lock (_gate)
        {
            var shutter = _photoCopyGestureShutter;
            _photoCopyGestureShutter = null;
            trackingId = 0;
            if (shutter is null || shutter.FrameTime != frameTime ||
                !TryGetPhotoCopyCaptureContext(out var current) || current.Revision != shutter.Context.Revision ||
                !ReferenceEquals(current.Target, shutter.Context.Target)) return false;
            trackingId = shutter.TrackingId;
            return trackingId > 0;
        }
    }
}
