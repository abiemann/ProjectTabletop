using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private static readonly TimeSpan ControlSwipeInterval = TimeSpan.FromMilliseconds(32);
    private readonly FrameWorker _controlSwipeWorker = new();
    private readonly object _controlSwipeGate = new();
    private long _controlSwipeGeneration;
    private DateTimeOffset _controlSwipeInputNotBefore;
    // Mutable image/path history belongs exclusively to this worker.
    private readonly BoardDisturbanceTracker _controlSwipeDisturbance = new();
    private readonly BoardControlSwipeTracker _controlSwipePath = new();
    private readonly List<BoardSwipeSample> _controlSwipeSamples = new(4);
    private long _controlSwipeWorkerGeneration = -1;
    private int _controlSwipeFrameWidth, _controlSwipeFrameHeight, _controlSwipeFrameStride;
    private SceneCompositor.ControlSwipeContext? _controlSwipeWorkerContext;
    private ControlSwipeDetectionDiagnostics? _lastControlSwipeDetection;
    private readonly record struct ControlSwipeDetectionDiagnostics(DateTimeOffset FrameTime, long Revision,
        int MotionCount, long? TrackingId, double? U, double? V, bool Traversing,
        BoardControlSwipe? Gesture, bool Applied, string? Error,
        BoardSwipeSample? SecondDisturbance = null, BoardSwipeSample? ThirdDisturbance = null,
        BoardSwipeSample? FourthDisturbance = null, object? MarkerWait = null);
    private ControlSwipeDetectionDiagnostics? ControlSwipeDiagnostics
    {
        get { lock (_controlSwipeGate) return _lastControlSwipeDetection is { } result
            ? result with { MarkerWait = _scene.GetControlSwipeMarkerWaitDiagnostics() } : null; }
    }

    private void QueueControlSwipeDetection(CameraFrame frame, long tick)
    {
        if (!_controlSwipeWorker.CanRun(tick, ControlSwipeInterval)) return;
        // Reserve a retained frame without taking scene or hand locks on the
        // capture consumer. Busy arrivals are dropped, never queued or copied.
        _controlSwipeWorker.TryRun(tick, ControlSwipeInterval, frame, _ =>
        {
            long generation;
            lock (_controlSwipeGate)
            {
                if (frame.Timestamp <= _controlSwipeInputNotBefore || !ControlSwipeDetectionAvailable()) return;
                generation = _controlSwipeGeneration;
            }
            try
            {
                var context = _scene.GetControlSwipeContext(frame.Timestamp);
                if (generation != _controlSwipeWorkerGeneration ||
                    !ReferenceEquals(context, _controlSwipeWorkerContext) || context is null ||
                    frame.Width != _controlSwipeFrameWidth || frame.Height != _controlSwipeFrameHeight ||
                    frame.Stride != _controlSwipeFrameStride)
                {
                    _controlSwipeDisturbance.Reset();
                    _controlSwipePath.Reset();
                    _controlSwipeWorkerGeneration = generation;
                    _controlSwipeWorkerContext = context;
                    _controlSwipeFrameWidth = frame.Width;
                    _controlSwipeFrameHeight = frame.Height;
                    _controlSwipeFrameStride = frame.Stride;
                }
                if (context is null)
                {
                    lock (_controlSwipeGate)
                        if (generation == _controlSwipeGeneration) _lastControlSwipeDetection = null;
                    return;
                }
                var now = MonotonicClock.UtcNow;
                var observations = _controlSwipeDisturbance.Update(frame.Width, frame.Height, frame.Stride,
                    frame.Bgra, context.SearchPolygon, frame.Timestamp, now);
                _controlSwipeSamples.Clear();
                for (int index = 0; index < observations.Count; index++)
                {
                    var observation = observations[index];
                    _controlSwipeSamples.Add(new(observation.TrackingId, observation.U,
                        context.Top + (context.Bottom - context.Top) * observation.V));
                }
                var command = _controlSwipePath.Update(_controlSwipeSamples, frame.Timestamp, now);
                bool traversing = _controlSwipePath.HasTraversalInProgress;
                // Never take marker locks while holding the swipe publication lock.
                bool markerCheckRequired = command == BoardControlSwipe.Toggle && ControlSwipeNeedsMarkerCheck();
                lock (_controlSwipeGate)
                {
                    if (generation != _controlSwipeGeneration || !ControlSwipeDetectionAvailable()) return;
                    bool applied = _scene.CompleteControlSwipe(context, command, traversing, frame.Timestamp, markerCheckRequired);
                    _lastControlSwipeDetection = new(frame.Timestamp, context.Revision, observations.Count,
                        observations.Count > 0 ? observations[0].TrackingId : null,
                        observations.Count > 0 ? observations[0].U : null,
                        observations.Count > 0 ? context.Top + (context.Bottom - context.Top) * observations[0].V : null,
                        traversing, command, applied, null,
                        _controlSwipeSamples.Count > 1 ? _controlSwipeSamples[1] : null,
                        _controlSwipeSamples.Count > 2 ? _controlSwipeSamples[2] : null,
                        _controlSwipeSamples.Count > 3 ? _controlSwipeSamples[3] : null);
                }
            }
            catch (Exception error)
            {
                _controlSwipeDisturbance.Reset();
                _controlSwipePath.Reset();
                _controlSwipeWorkerContext = null;
                lock (_controlSwipeGate)
                {
                    if (_closing || generation != _controlSwipeGeneration) return;
                    if (_lastControlSwipeDetection?.Error != error.Message) AppLog.Write("Control swipe check", error);
                    _lastControlSwipeDetection = new(frame.Timestamp, 0, 0, null, null, null, false, null, false, error.Message);
                }
            }
        });
    }

    // Null means no valid check occurred; it must never be mistaken for clear.
    private static bool? MarkerResultForControlSwipes(MarkerTipDetectionResult detection, bool published) =>
        published || detection.Candidates.Count > 0 || detection.AmbiguousMarkerCenters.Count > 0 ? true :
        detection.Reason == "waiting-for-projected-bat-reference" ? null : false;

    private static bool? CombineControlSwipeMarkerResults(bool? previous, bool? next) =>
        previous == true || next == true ? true : previous == false && next == false ? false : null;

    // Worker occupancy is deliberately excluded: dropping a frame is not evidence
    // that it was clear. Only a completed detector result can release a waiting swipe.
    private bool ControlSwipeNeedsMarkerCheck()
    {
        var board = _scene.CurrentBoardScreen;
        if (board is BoardScreen.WaterGarden or BoardScreen.Paint)
        {
            lock (_markerTrackingGate) return _markerTrackingEnabled && !_wandMarkerLearning &&
                LearnedWandMarkerProfileLocked() is not null;
        }
        if (board == BoardScreen.Football)
        {
            lock (_footballInputGate)
            {
                if (!_scene.FootballInputActive || _footballLearningPlayer >= 0 ||
                    _camera.ActiveDeviceId is not { } cameraId ||
                    !_footballMarkerProfiles.TryGetValue(cameraId, out var profiles)) return false;
                for (int player = 0; player < 2; player++)
                    if (FootballHumanPlayer(player) && !_footballFingerInputs[player] && profiles.ForPlayer(player) is not null)
                        return true;
                return false;
            }
        }
        return false;
    }

    private bool ControlSwipeDetectionAvailable() => !_closing && _cameraWanted && _camera.IsRunning &&
        !_cameraManualRestartRequired && _cameraOperation.CurrentCount != 0 &&
        _camera.ActiveDeviceId == _cameraWantedDeviceId && !Volatile.Read(ref _cameraHealthWarning) &&
        !Volatile.Read(ref _boardSetupActive) && !IsBoardScanMeasuring;

    private void ResetControlSwipeInput()
    {
        lock (_controlSwipeGate)
        {
            _controlSwipeGeneration++;
            _controlSwipeInputNotBefore = MonotonicClock.UtcNow;
            _lastControlSwipeDetection = null;
            _scene.ResetControlSwipeMarkerChecks();
        }
        _controlSwipeWorker.ResetCadence();
    }
}
