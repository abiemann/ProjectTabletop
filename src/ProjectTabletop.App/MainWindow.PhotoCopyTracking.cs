using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private Task? _photoCopyObservationTask;
    private long _photoCopyObservationRevision = -1;
    private readonly PhotoCopyAcquisitionWindow _photoCopyAcquisition = new();
    private int _photoCopyMissingCount;
    private DateTimeOffset _photoCopyLastObservationFrame, _photoCopyLastHandFrame;
    private object? _lastPhotoCopyObservation;

    // Acquire a clean silhouette before illuminating it. Once locked, only
    // validate that target; never segment the projected halo as a new object.
    private void QueuePhotoCopyObservation(CameraFrame frame, IReadOnlyList<HandDetection> hands)
    {
        if (_scene.CurrentBoardScreen != BoardScreen.PhotoCopy) return;
        if (hands.Count > 0)
        {
            _photoCopyLastHandFrame = frame.Timestamp;
            _photoCopyAcquisition.Reset();
        }
        if (!_scene.TryGetPhotoCopyCaptureContext(out var context) ||
            frame.Timestamp < context.ReadyAfter) return;
        if (_photoCopyObservationRevision != context.Revision)
        {
            _photoCopyObservationRevision = context.Revision;
            _photoCopyAcquisition.Reset();
            _photoCopyMissingCount = 0;
            _photoCopyLastObservationFrame = DateTimeOffset.MinValue;
        }
        if (_photoCopyObservationTask is { IsCompleted: false } || _photoCopyTask is { IsCompleted: false } ||
            frame.Timestamp - _photoCopyLastObservationFrame < TimeSpan.FromMilliseconds(300)) return;
        if (context.Target is null && (hands.Count > 0 ||
            frame.Timestamp - _photoCopyLastHandFrame < TimeSpan.FromMilliseconds(850)))
        {
            _photoCopyAcquisition.Reset();
            return;
        }
        _photoCopyLastObservationFrame = frame.Timestamp;
        var generation = _handGeneration;
        _photoCopyObservationTask = ObservePhotoCopyObjectAsync(frame, hands.ToArray(), context, generation);
    }

    private async Task ObservePhotoCopyObjectAsync(CameraFrame frame, HandDetection[] hands,
        Projection.SceneCompositor.PhotoCopyCaptureContext context, long generation)
    {
        var captureAtStart = _photoCopyTask;
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
                !PhotoCopyObservationMayApply(captureAtStart, _photoCopyTask) ||
                !_scene.TryGetPhotoCopyCaptureContext(out var current) || current.Revision != context.Revision ||
                !ReferenceEquals(current.Target, context.Target)) return;
#if DEBUG
            if (context.Target is not null && observation.State == PhotoObjectTargetState.MissingOrMoved)
                _lastPhotoCopyFailure = new(frame, hands, context, observation.Failure);
#endif
            if (context.Target is { } locked)
            {
                if (observation.State == PhotoObjectTargetState.MissingOrMoved)
                {
                    if (++_photoCopyMissingCount >= 3)
                    {
                        _scene.ClearPhotoCopyObject(locked, context.Revision);
                        _photoCopyAcquisition.Reset();
                        _photoCopyMissingCount = 0;
                    }
                }
                else _photoCopyMissingCount = 0;
            }
            else if (observation.Target is { } found)
            {
                // A hand that entered while the worker ran invalidates acquisition.
                if (_photoCopyLastHandFrame >= frame.Timestamp) return;
                if (_photoCopyAcquisition.Observe(found, frame.Timestamp) &&
                    _scene.SetPhotoCopyObject(found, context.Revision))
                    _photoCopyMissingCount = 0;
            }
            else
            {
                _photoCopyAcquisition.Reset();
                _scene.SetPhotoCopyStatus(observation.Failure ??
                    "Place one object on grey, then lift your hand briefly.", context.Revision);
            }
            _lastPhotoCopyObservation = new { frameTime = frame.Timestamp, context.Revision,
                state = context.Target is null ? observation.Target is null ? "searching" : "candidate" : observation.State.ToString(),
                observation.Failure, cameraToBoard = context.CameraToBoard,
                center = observation.Target?.Center, area = observation.Target?.ForegroundArea,
                shape = observation.Target?.Spotlight.Shape.ToString(),
                candidateCount = _photoCopyAcquisition.Count, candidateSinceUtc = _photoCopyAcquisition.StartedAt,
                candidateAnchorArea = _photoCopyAcquisition.Anchor?.ForegroundArea,
                candidateAnchorShape = _photoCopyAcquisition.Anchor?.Spotlight.Shape.ToString() };
            UpdateBoardAppStatus();
        }
        catch (Exception error)
        {
            if (_closing || generation != _handGeneration ||
                !PhotoCopyObservationMayApply(captureAtStart, _photoCopyTask)) return;
            _photoCopyAcquisition.Reset();
            _scene.SetPhotoCopyStatus("Object scan could not finish. Keep one object still on grey and try again.",
                context.Revision);
            AppLog.Write("Photo Copy object lighting", error);
        }
    }

    // A scan begun before a capture cannot change its locked target or status,
    // even if the capture finishes before this worker returns. Capture validates
    // its own fresh image; later background scans resume from the completed task.
    private static bool PhotoCopyObservationMayApply(Task? captureAtStart, Task? currentCapture) =>
        ReferenceEquals(captureAtStart, currentCapture) && currentCapture is not { IsCompleted: false };

    private sealed class PhotoCopyAcquisitionWindow
    {
        public PhotoObjectTarget? Anchor { get; private set; }
        public int Count { get; private set; }
        public DateTimeOffset? StartedAt { get; private set; }
        private DateTimeOffset _lastFrame;

        public bool Observe(PhotoObjectTarget found, DateTimeOffset frameTime)
        {
            if (Anchor is not null && frameTime <= _lastFrame) return false;
            bool stable = Anchor is { } first && first.Spotlight.Shape == found.Spotlight.Shape &&
                Math.Abs(first.Center.X - found.Center.X) <= 12 &&
                Math.Abs(first.Center.Y - found.Center.Y) <= 12 &&
                Math.Abs(first.Width - found.Width) <= 16 && Math.Abs(first.Height - found.Height) <= 16 &&
                Math.Abs(first.ForegroundArea - found.ForegroundArea) <= first.ForegroundArea * .12 &&
                SilhouettesAgree(first, found);
            if (!stable)
            {
                Anchor = found;
                StartedAt = frameTime;
                Count = 0;
            }
            _lastFrame = frameTime;
            Count++;
            // Keep the first silhouette as the comparison anchor. Following
            // each small change could lock a progressively incomplete face.
            return Count >= 3 && frameTime - StartedAt!.Value >= TimeSpan.FromMilliseconds(600);
        }

        private static bool SilhouettesAgree(PhotoObjectTarget first, PhotoObjectTarget second)
        {
            int intersection = 0;
            int left = Math.Max(first.Left, second.Left), top = Math.Max(first.Top, second.Top);
            int right = Math.Min(first.Left + first.Width, second.Left + second.Width);
            int bottom = Math.Min(first.Top + first.Height, second.Top + second.Height);
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                    if (first.Alpha[(y - first.Top) * first.Width + x - first.Left] >= 200 &&
                        second.Alpha[(y - second.Top) * second.Width + x - second.Left] >= 200)
                        intersection++;
            int union = first.Alpha.Count(value => value >= 200) + second.Alpha.Count(value => value >= 200) - intersection;
            return union > 0 && intersection >= union * .80;
        }

        public void Reset()
        {
            Anchor = null;
            Count = 0;
            StartedAt = null;
            _lastFrame = default;
        }
    }
}
