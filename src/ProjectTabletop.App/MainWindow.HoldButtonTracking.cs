using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly FrameWorker _holdWorker = new();
    private readonly object _captionGate = new();
    private long _captionGeneration;
    private DateTimeOffset _captionInputNotBefore;

    // These capture boards intentionally consume the same hand/caption frame
    // when making a photograph or saving paint. Other holds need no landmarks.
    private static bool HandFrameOwnsCaptions(BoardScreen screen) =>
        screen is BoardScreen.PhotoCopy or BoardScreen.Paint;

    private void QueueHoldButtonDetection(CameraFrame frame, long tick)
    {
        if (!_holdWorker.CanRun(tick, HandDetectionInterval)) return;
        // Reserve without a scene/generation lock on the capture thread. All
        // state reads below run on this worker, independently of hand/UI work.
        _holdWorker.TryRun(tick, HandDetectionInterval, frame, _ =>
        {
            long generation, handGeneration;
            lock (_captionGate)
            {
                if (frame.Timestamp < _captionInputNotBefore || !HoldDetectionAvailable()) return;
                generation = _captionGeneration;
                handGeneration = Volatile.Read(ref _handGeneration);
            }
            try
            {
                var board = _scene.CurrentBoardScreen;
                if (HandFrameOwnsCaptions(board)) return;
                var context = _scene.GetHoldButtonContext(frame.Timestamp);
                if (context is null) return;
                var query = FindHeldButtons(frame, context, handGeneration);
                lock (_captionGate)
                {
                    if (generation != _captionGeneration || !HoldDetectionAvailable() ||
                        board != _scene.CurrentBoardScreen) return;
                    var age = MonotonicClock.UtcNow - frame.Timestamp;
                    if (age < TimeSpan.Zero || age > HandMarkerLifetime) return;
                    _captionCheckError = null;
                    // Scene methods validate freshness again after acquiring
                    // their render lock. Never extend the 350 ms safety limit.
                    PublishHoldButtonEvidence(frame, context, query);
                }
            }
            catch (Exception error)
            {
                lock (_captionGate)
                {
                    if (_closing || generation != _captionGeneration) return;
                    if (_captionCheckError != error.Message) AppLog.Write("Caption hold check", error);
                    _captionCheckError = error.Message;
                }
            }
        });
    }

    // Called under the same generation gate as camera/tracking resets.
    private bool HoldDetectionAvailable() => !_closing && _handTrackingEnabled &&
        _cameraWanted && _camera.IsRunning && _cameraOperation.CurrentCount != 0 &&
        !Volatile.Read(ref _cameraHealthWarning) && !IsBoardScanMeasuring;

    private void PublishHoldButtonEvidence(CameraFrame frame, SceneCompositor.HoldButtonContext? context,
        HoldButtonQuery query)
    {
        var board = _scene.CurrentBoardScreen;
        _scene.ObserveHoldControlExposure(context, query.Presence, frame.Timestamp);
        var activations = _scene.ObserveHoldButtons(context, query.Held, frame.Timestamp, query.Cleared);
        _lastHoldButtonDetection = new { frameTime = frame.Timestamp,
            board = board.ToString(), context = context is not null, held = query.Held,
            cleared = query.Cleared, revision = context?.Revision,
            buttonIds = context?.ButtonIds, enabledHoldIds = context?.EnabledHoldIds,
            ageMilliseconds = (MonotonicClock.UtcNow - frame.Timestamp).TotalMilliseconds,
            reason = query.Presence?.Reason, baselineReady = query.Presence?.BaselineReady,
            textPatterns = query.Presence?.TextPatterns, localFits = query.Presence?.LocalFits,
            progress = _scene.CurrentHoldProgress };
        if (activations.Count > 0)
        {
            _lastHoldButtonActivation = new { buttons = activations, evidence = _lastHoldButtonDetection };
            LogHandTrackingEvent("hold_activation", _lastHoldButtonActivation, force: true);
        }
        if (context is { EnabledHoldIds.Count: > 0 })
            LogHandTrackingEvent("hold_frame", _lastHoldButtonDetection);
    }
}
