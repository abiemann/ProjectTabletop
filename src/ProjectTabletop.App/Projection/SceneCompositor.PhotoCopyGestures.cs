using ProjectTabletop.Vision;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public sealed record PhotoCopyGestureShutter(long TrackingId, DateTimeOffset FrameTime,
        PhotoCopyCaptureContext Context, PhotoCopyAction Action = PhotoCopyAction.Swirl,
        BoardSelectionGesture Gesture = BoardSelectionGesture.IndexSeparation);
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
        bool enabled = ready is not null;
        if (_boardSession.PhotoCopyShutterEnabled != enabled) _renderedBoardState = null;
        _boardSession.PhotoCopyShutterEnabled = enabled;
        return ready;
    }

    // Consumed by the camera pipeline immediately after this very observation.
    // A later frame, changed object or new board session cannot replay it.
    public bool TryTakePhotoCopyGestureShutter(DateTimeOffset frameTime, out long trackingId)
    {
        bool accepted = TryTakePhotoCopyCaptureRequest(frameTime, out var request);
        trackingId = accepted ? request.TrackingId : 0;
        return accepted;
    }

    public bool TryTakePhotoCopyCaptureRequest(DateTimeOffset frameTime, out PhotoCopyGestureShutter request)
    {
        lock (_gate)
        {
            var shutter = _photoCopyGestureShutter;
            _photoCopyGestureShutter = null;
            request = null!;
            if (shutter is null || shutter.FrameTime != frameTime ||
                !TryGetPhotoCopyCaptureContext(out var current) || current.Revision != shutter.Context.Revision ||
                !ReferenceEquals(current.Target, shutter.Context.Target)) return false;
            request = shutter;
            return request.TrackingId > 0;
        }
    }

    public bool IsPhotoCopyCaptureCurrent(PhotoCopyCaptureContext context)
    {
        lock (_gate)
            return TryGetPhotoCopyCaptureContext(out var current) && current.Revision == context.Revision &&
                ReferenceEquals(current.Target, context.Target);
    }
}
