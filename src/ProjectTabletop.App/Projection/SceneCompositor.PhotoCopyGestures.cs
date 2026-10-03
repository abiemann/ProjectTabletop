using ProjectTabletop.Vision;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public sealed record PhotoCopyGestureShutter(long TrackingId, DateTimeOffset FrameTime,
        PhotoCopyCaptureContext Context, PhotoCopyAction Action = PhotoCopyAction.Swirl,
        BoardSelectionGesture Gesture = BoardSelectionGesture.IndexSeparation, bool CaptionHold = false);
    private PhotoCopyGestureShutter? _photoCopyGestureShutter;
    private DateTimeOffset _photoCopyInputFrame;
    private bool _photoCopyInputAllowed;
    private PhotoCopyCaptureContext? _photoCopyReadyContext;
    private sealed record PhotoCopyMemorySaveRequest(DateTimeOffset FrameTime, PhotoCopyMemoryImage Image);
    private PhotoCopyMemorySaveRequest? _photoCopyMemorySaveRequest;

    internal bool IsPhotoCopyHoldControl(PixelPoint camera)
    {
        lock (_gate)
            return _boardSession.Screen == BoardScreen.PhotoCopy && OnHoldButton(camera);
    }

    // Feed the same board gesture recognizer only while a fresh capture can be
    // made. Settling, busy work and unavailable subjects never queue a shutter.
    private PhotoCopyCaptureContext? PreparePhotoCopyGesture(IReadOnlyList<HandCursor> cursors,
        DateTimeOffset frameTime, bool acceptFrame, bool busy)
    {
        _photoCopyGestureShutter = null;
        _photoCopyMemorySaveRequest = null;
        PhotoCopyCaptureContext? ready = null;
        if (acceptFrame && !busy && TryGetPhotoCopyCaptureContext(out var context) &&
            frameTime >= context.ReadyAfter && (context.Target is not null || cursors.Count == 2))
            ready = context;
        bool enabled = ready is not null;
        _photoCopyInputFrame = frameTime;
        _photoCopyInputAllowed = acceptFrame && !busy;
        _photoCopyReadyContext = ready;
        if (_boardSession.PhotoCopyShutterEnabled != enabled) _renderedBoardState = null;
        _boardSession.PhotoCopyShutterEnabled = enabled;
        return ready;
    }

    // Called after this same camera observation has completed a caption hold.
    // A locked object's buttons need no tracked fingertip or selection gesture.
    private void QueuePhotoCopyHoldAction(IReadOnlyList<string> activated, DateTimeOffset frameTime)
    {
        if (_boardSession.Screen != BoardScreen.PhotoCopy || !_photoCopyInputAllowed ||
            _photoCopyInputFrame != frameTime) return;
        if (activated.Contains("photo-save") && TryGetPhotoCopyMemoryImage(out var memoryImage))
        {
            _photoCopyGestureShutter = null;
            _photoCopyMemorySaveRequest = new(frameTime, memoryImage);
        }
        else if (_photoCopyReadyContext is { } context)
        {
            string? id = activated.FirstOrDefault(id => id is "photo-swirl" or "photo-copy-once");
            if (id is not null && BoardSession.TryGetPhotoCopyAction(id, out var action))
                _photoCopyGestureShutter = new(0, frameTime, context, action, CaptionHold: true);
        }
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
            return request.CaptionHold || request.TrackingId > 0;
        }
    }

    internal bool TryTakePhotoCopyMemorySaveRequest(DateTimeOffset frameTime, out PhotoCopyMemoryImage image)
    {
        lock (_gate)
        {
            var request = _photoCopyMemorySaveRequest;
            _photoCopyMemorySaveRequest = null;
            image = null!;
            if (request is null || request.FrameTime != frameTime ||
                !IsPhotoCopyMemoryImageCurrent(request.Image)) return false;
            image = request.Image;
            return true;
        }
    }

    public bool IsPhotoCopyCaptureCurrent(PhotoCopyCaptureContext context)
    {
        lock (_gate)
            return TryGetPhotoCopyCaptureContext(out var current) && current.Revision == context.Revision &&
                ReferenceEquals(current.Target, context.Target);
    }
}
