using System.Diagnostics;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly PaintDisturbanceTracker _paintDisturbances = new();
    private Task? _paintDetectionTask;
    private int _paintDetecting;
    private long _lastPaintDetectionTick;
    private long _paintTrackerGeneration = -1;
    private object? _lastPaintDetection;

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

    private void QueuePaintDetection(CameraFrame frame, long tick)
    {
        lock (_handGate)
        {
            if (_closing || !_handTrackingEnabled || IsBoardScanMeasuring ||
                Volatile.Read(ref _cameraHealthWarning) || Volatile.Read(ref _paintDetecting) != 0 ||
                _lastPaintDetectionTick != 0 && Stopwatch.GetElapsedTime(_lastPaintDetectionTick, tick) < TimeSpan.FromMilliseconds(125)) return;
            var context = _scene.GetPaintDisturbanceContext();
            if (context is null) return;
            _paintDetecting = 1;
            _lastPaintDetectionTick = tick;
            long generation = _handGeneration;
            _paintDetectionTask = Task.Run(() =>
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
                        context, frame.Timestamp, DateTimeOffset.UtcNow);
                    var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    DispatcherQueue.TryEnqueue(() =>
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
                    Volatile.Write(ref _lastPaintDetection, new { frameTime = frame.Timestamp, error = error.Message });
                }
                finally { Interlocked.Exchange(ref _paintDetecting, 0); }
            });
        }
    }
}
