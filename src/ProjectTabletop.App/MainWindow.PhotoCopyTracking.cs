using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private Task? _photoCopyObservationTask;
    private long _photoCopyObservationRevision = -1;
    private PhotoObjectTarget? _photoCopyCandidate;
    private int _photoCopyCandidateCount, _photoCopyMissingCount;
    private DateTimeOffset _photoCopyLastObservationFrame, _photoCopyLastHandFrame;
    private object? _lastPhotoCopyObservation;

    // Acquire a clean silhouette before illuminating it. Once locked, only
    // validate that target; never segment the projected halo as a new object.
    private void QueuePhotoCopyObservation(CameraFrame frame, IReadOnlyList<HandDetection> hands)
    {
        if (_scene.CurrentBoardScreen != BoardScreen.PhotoCopy) return;
        if (hands.Count > 0) _photoCopyLastHandFrame = frame.Timestamp;
        if (!_scene.TryGetPhotoCopyCaptureContext(out var context) ||
            frame.Timestamp < context.ReadyAfter) return;
        if (_photoCopyObservationRevision != context.Revision)
        {
            _photoCopyObservationRevision = context.Revision;
            _photoCopyCandidate = null;
            _photoCopyCandidateCount = _photoCopyMissingCount = 0;
            _photoCopyLastObservationFrame = DateTimeOffset.MinValue;
        }
        if (_photoCopyObservationTask is { IsCompleted: false } || _photoCopyTask is { IsCompleted: false } ||
            frame.Timestamp - _photoCopyLastObservationFrame < TimeSpan.FromMilliseconds(300)) return;
        if (context.Target is null && (hands.Count > 0 ||
            frame.Timestamp - _photoCopyLastHandFrame < TimeSpan.FromMilliseconds(850)))
        {
            _photoCopyCandidate = null;
            _photoCopyCandidateCount = 0;
            return;
        }
        _photoCopyLastObservationFrame = frame.Timestamp;
        var generation = _handGeneration;
        _photoCopyObservationTask = ObservePhotoCopyObjectAsync(frame, hands.ToArray(), context, generation);
    }

    private async Task ObservePhotoCopyObjectAsync(CameraFrame frame, HandDetection[] hands,
        Projection.SceneCompositor.PhotoCopyCaptureContext context, long generation)
    {
        try
        {
            var observation = await Task.Run(() =>
            {
                if (context.Target is { } target)
                {
                    var state = PhotoObjectLocator.ObserveTarget(frame.Width, frame.Height, frame.Stride,
                        frame.Bgra, context.CameraToBoard, target, out var failure, hands);
                    return (Target: (PhotoObjectTarget?)null, State: state, Failure: failure);
                }
                var candidate = PhotoObjectLocator.Locate(frame.Width, frame.Height, frame.Stride,
                    frame.Bgra, context.CameraToBoard, out var message);
                return (Target: candidate, State: PhotoObjectTargetState.Unavailable, Failure: message);
            });
            if (_closing || generation != _handGeneration ||
                !_scene.TryGetPhotoCopyCaptureContext(out var current) || current.Revision != context.Revision ||
                !ReferenceEquals(current.Target, context.Target)) return;
            _lastPhotoCopyObservation = new { frameTime = frame.Timestamp, context.Revision,
                state = context.Target is null ? observation.Target is null ? "searching" : "candidate" : observation.State.ToString(),
                observation.Failure, cameraToBoard = context.CameraToBoard,
                center = observation.Target?.Center, area = observation.Target?.ForegroundArea };
            if (context.Target is { } locked)
            {
                if (observation.State == PhotoObjectTargetState.MissingOrMoved)
                {
                    if (++_photoCopyMissingCount >= 3)
                    {
                        _scene.ClearPhotoCopyObject(locked, context.Revision);
                        _photoCopyCandidate = null;
                        _photoCopyCandidateCount = _photoCopyMissingCount = 0;
                    }
                }
                else _photoCopyMissingCount = 0;
            }
            else if (observation.Target is { } found)
            {
                // A hand that entered while the worker ran invalidates acquisition.
                if (_photoCopyLastHandFrame >= frame.Timestamp) return;
                bool stable = _photoCopyCandidate is { } previous &&
                    Math.Abs(previous.Center.X - found.Center.X) <= 12 &&
                    Math.Abs(previous.Center.Y - found.Center.Y) <= 12 &&
                    Math.Abs(previous.Width - found.Width) <= 16 && Math.Abs(previous.Height - found.Height) <= 16;
                _photoCopyCandidate = found;
                _photoCopyCandidateCount = stable ? _photoCopyCandidateCount + 1 : 1;
                if (_photoCopyCandidateCount >= 2 && _scene.SetPhotoCopyObject(found, context.Revision))
                    _photoCopyMissingCount = 0;
            }
            else
            {
                _photoCopyCandidate = null;
                _photoCopyCandidateCount = 0;
                _scene.SetPhotoCopyStatus(observation.Failure ??
                    "Place one object on grey, then lift your hand briefly.", context.Revision);
            }
            UpdateBoardAppStatus();
        }
        catch (Exception error)
        {
            if (_closing) return;
            _scene.SetPhotoCopyStatus("Object scan could not finish. Keep one object still on grey and try again.",
                context.Revision);
            AppLog.Write("Photo Copy object lighting", error);
        }
    }
}
