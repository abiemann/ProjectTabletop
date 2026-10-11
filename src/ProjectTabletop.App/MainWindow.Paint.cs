using System.Diagnostics;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly PaintDisturbanceTracker _paintDisturbances = new();
    private readonly FrameWorker _paintWorker = new();
    private static readonly TimeSpan PaintDetectionInterval = TimeSpan.FromMilliseconds(125);
    private long _paintTrackerGeneration = -1;
    private object? _lastPaintDetection;

    private void UpdatePaintInputAvailability()
    {
        var frame = Volatile.Read(ref _latestCameraFrame);
        var now = MonotonicClock.UtcNow;
        _scene.SetPaintInputAvailable(!_closing && _handTrackingEnabled && _cameraWanted && _camera.IsRunning &&
            _cameraOperation.CurrentCount != 0 && !Volatile.Read(ref _cameraHealthWarning) &&
            !IsBoardScanMeasuring && frame is not null && frame.Timestamp <= now &&
            now - frame.Timestamp <= TimeSpan.FromMilliseconds(500));
    }

    private void ShowPaint()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowPaint();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_scene.HasBoardMediaClip ? "Paint: colour fills the board beneath floating controls. Save exports the artwork to Pictures / Project Tabletop / Paint."
            : "Paint selected. Scan the board to start painting.");
    }

    private InputFrameQueueResult QueuePaintDetection(CameraFrame frame, long tick)
    {
        lock (_handGate)
        {
            if (frame.Timestamp < _handInputNotBefore) return InputFrameQueueResult.PredatesReset;
            if (_closing || !_handTrackingEnabled || IsBoardScanMeasuring ||
                Volatile.Read(ref _cameraHealthWarning) ||
                !_paintWorker.CanRun(tick, PaintDetectionInterval)) return InputFrameQueueResult.Skipped;
            var context = _scene.GetPaintDisturbanceContext();
            if (context is null) return InputFrameQueueResult.Skipped;
            long generation = _handGeneration;
            bool queued = _paintWorker.TryRun(tick, PaintDetectionInterval, work =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    if (_paintTrackerGeneration != generation)
                    {
                        _paintDisturbances.Reset();
                        _paintTrackerGeneration = generation;
                    }
                    var result = _paintDisturbances.Update(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                        context, frame.Timestamp, MonotonicClock.UtcNow);
#if DEBUG
                    var diagnostic = new PaintDiagnosticObservation(frame, context, result);
                    Volatile.Write(ref _lastPaintDiagnostic, diagnostic);
                    if (result.ConfirmedCandidateCount > 0) Volatile.Write(ref _lastConfirmedPaintDiagnostic, diagnostic);
#endif
                    var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    work.TryPost(_dispatchInputResult, () =>
                    {
                        lock (_handGate)
                        {
                            if (_closing || generation != _handGeneration || !_handTrackingEnabled || !_cameraWanted ||
                                !_camera.IsRunning || Volatile.Read(ref _cameraHealthWarning) || IsBoardScanMeasuring) return;
                            int accepted = _scene.CompletePaintDisturbance(context, result, frame.Timestamp);
                            _lastPaintDetection = new { frameTime = frame.Timestamp, context.Revision,
                                inferenceMilliseconds = milliseconds, result, acceptedDrops = accepted };
                        }
                    });
                }
                catch (Exception error)
                {
                    _paintDisturbances.Reset();
                    lock (_handGate)
                        if (!_closing && generation == _handGeneration)
                            Volatile.Write(ref _lastPaintDetection, new { frameTime = frame.Timestamp, error = error.Message });
                }
            });
            return queued ? InputFrameQueueResult.Queued : InputFrameQueueResult.Skipped;
        }
    }
}
