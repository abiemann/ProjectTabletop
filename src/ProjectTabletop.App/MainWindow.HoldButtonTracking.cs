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

    // Photo Copy intentionally consumes the same hand/caption frame when
    // making a photograph. Other holds, including Paint's Save, need no landmarks.
    private static bool HandFrameOwnsCaptions(BoardScreen screen) =>
        screen == BoardScreen.PhotoCopy;

    private void QueueHoldButtonDetection(CameraFrame frame, long tick)
    {
        if (!_holdWorker.CanRun(tick, HandDetectionInterval)) return;
        // Reserve without a scene/generation lock on the capture thread. All
        // state reads below run on this worker, independently of hand/UI work.
        _holdWorker.TryRun(tick, HandDetectionInterval, frame, work =>
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
                bool savePaint, saveGlobe;
                lock (_captionGate)
                {
                    if (generation != _captionGeneration || !HoldDetectionAvailable() ||
                        board != _scene.CurrentBoardScreen) return;
                    var age = MonotonicClock.UtcNow - frame.Timestamp;
                    if (age < TimeSpan.Zero || age > HandMarkerLifetime) return;
                    _captionCheckError = null;
                    // Scene methods validate freshness again after acquiring
                    // their render lock. Never extend the 350 ms safety limit.
                    var activated = PublishHoldButtonEvidence(frame, context, query);
                    savePaint = activated.Contains("paint-save");
                    saveGlobe = activated.Contains("globe-save");
                }
                if (savePaint || saveGlobe)
                    work.TryPost(_dispatchInputResult, () =>
                    {
                        lock (_captionGate)
                        {
                            var age = MonotonicClock.UtcNow - frame.Timestamp;
                            if (generation != _captionGeneration || !HoldDetectionAvailable() ||
                                _scene.CurrentBoardScreen != board ||
                                age < TimeSpan.Zero || age > HandMarkerLifetime) return;
                        }
                        // Freeze and save on the UI thread, without holding a
                        // caption lock across application status/hand queries.
                        // The scene checks this exact source frame and visit.
                        if (savePaint) QueuePaintSave(frame.Timestamp);
                        if (saveGlobe) QueueGlobeSave();
                    });
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

    private IReadOnlyList<string> PublishHoldButtonEvidence(CameraFrame frame, SceneCompositor.HoldButtonContext? context,
        HoldButtonQuery query)
    {
        var board = _scene.CurrentBoardScreen;
        _scene.ObserveHoldControlExposure(context, query.Presence, frame.Timestamp);
        var blocked = context?.BlockedButtons(query.Presence) ?? [];
        var activations = _scene.ObserveHoldButtons(context, query.Held, frame.Timestamp, query.Cleared, blocked);
        _lastHoldButtonDetection = new { frameTime = frame.Timestamp,
            board = board.ToString(), context = context is not null, held = query.Held,
            cleared = query.Cleared, blocked, revision = context?.Revision,
            buttonIds = context?.ButtonIds, enabledHoldIds = context?.EnabledHoldIds,
            ageMilliseconds = (MonotonicClock.UtcNow - frame.Timestamp).TotalMilliseconds,
            reason = query.Presence?.Reason, baselineReady = query.Presence?.BaselineReady,
            outsideControlDisturbance = query.Presence?.OutsideControlDisturbance,
            outsideControlForegroundFraction = query.Presence?.OutsideControlForegroundFraction,
            textPatterns = query.Presence?.TextPatterns, localFits = query.Presence?.LocalFits,
            progress = _scene.CurrentHoldProgress };
        if (activations.Count > 0)
        {
            _lastHoldButtonActivation = new { buttons = activations, evidence = _lastHoldButtonDetection };
            LogHandTrackingEvent("hold_activation", _lastHoldButtonActivation, force: true);
        }
        if (context is { EnabledHoldIds.Count: > 0 })
            LogHandTrackingEvent("hold_frame", _lastHoldButtonDetection);
        return activations;
    }
}
