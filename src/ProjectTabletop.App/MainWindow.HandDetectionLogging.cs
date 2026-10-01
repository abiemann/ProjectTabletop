using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private HandDetectionLog? _handDetectionLog;
    private HandTrackingVideoRecorder? _handVideoRecorder;
    private BoardScreen? _lastLoggedHandBoard;
    private long _handDetectionSequence;
    private DateTimeOffset _lastHandLogStatusRefresh;
    private DateTimeOffset _handVideoNotBefore;
    // Extra logging outside the tester is opt-in through the Debug control.
    private volatile bool _boardHandDiagnosticLogging = false;

    private bool IsHandTrackingTester => _scene.CurrentBoardScreen == BoardScreen.HandTracking;
    private string? ActiveHandRecordingId => _handVideoRecorder?.Status is { IsRecording: true } video
        ? video.RecordingId : null;

    private void InitializeHandDetectionLogging()
    {
        _handDetectionLog = new(Path.Combine(_appDataDirectory, "HandTrackingLogs"));
        _handVideoRecorder = new(Path.Combine(_appDataDirectory, "HandTrackingRecordings"));
    }

    // Camera delivery and inference completion share this gate with tracking resets.
    // Only frame references are queued here; encoding and file writes stay off these threads.
    private void QueueHandTrackingVideoFrame(CameraFrame frame)
    {
        lock (_handGate)
        {
            if (UpdateHandVideoEligibility() && frame.Timestamp >= _handVideoNotBefore)
                _handVideoRecorder?.ObserveFrame(frame);
        }
    }

    private bool UpdateHandVideoEligibility()
    {
        var reason = _closing ? "closing" : !IsHandTrackingTester ? "left_tester" :
            !_handTrackingEnabled ? "tracking_disabled" : _handTrackingError is not null ? "tracking_error" :
            !_cameraWanted || !_camera.IsRunning ? "camera_stopped" :
            _cameraOperation.CurrentCount == 0 ? "camera_changing" :
            Volatile.Read(ref _cameraHealthWarning) ? "camera_unhealthy" :
            IsBoardScanMeasuring ? "board_scan" : null;
        if (reason is not null) _handVideoRecorder?.Stop(reason);
        return reason is null;
    }

    private void RecordHandDiagnostic(object entry)
    {
        _handDetectionLog?.Record(entry);
        _handVideoRecorder?.Record(entry);
    }

    private void LogHandTrackingEvent(string kind, object? details = null, bool force = false)
    {
        if (!force && !IsHandTrackingTester && !_boardHandDiagnosticLogging) return;
        RecordHandDiagnostic(new
        {
            schemaVersion = 1, type = kind, loggedAt = DateTimeOffset.UtcNow,
            generation = _handGeneration, recordingId = ActiveHandRecordingId, details,
            lighting = _scene.GetHandLightingDiagnostics()
        });
    }

    private void LogHandDetection(long sequence, bool requestedInTester, CameraFrame frame,
        long generation, bool engineReset, double inferenceMilliseconds, double? frameIntervalMilliseconds,
        object? detectorTrace,
        IReadOnlyList<HandDetection> hands, IReadOnlyList<HandDetection> visibleHands,
        IReadOnlyList<HandCursor> cursors, string outcome)
    {
        if (!requestedInTester && !IsHandTrackingTester && !_boardHandDiagnosticLogging) return;
        var now = DateTimeOffset.UtcNow;
        // Do not let a menu inference that navigated into the tester start a clip,
        // or an obsolete/stale inference restart recording after a reset.
        if (requestedInTester && outcome == "accepted" && visibleHands.Count > 0 &&
            frame.Timestamp >= _handVideoNotBefore &&
            UpdateHandVideoEligibility())
            _handVideoRecorder?.HandDetected(frame);
        RecordHandDiagnostic(new
        {
            schemaVersion = 1, type = "detection", loggedAt = now, sequence,
            board = _scene.CurrentBoardScreen.ToString(),
            lastBoardSelection = _scene.GetLastHandBoardSelection(),
            recordingId = ActiveHandRecordingId,
            frameTime = frame.Timestamp, width = frame.Width, height = frame.Height,
            generation, currentGeneration = _handGeneration, engineReset,
            inferenceMilliseconds, resultAgeMilliseconds = (now - frame.Timestamp).TotalMilliseconds,
            frameIntervalMilliseconds, outcome, detectorTrace,
            acquisition = outcome == "accepted" ? _lastHandAcquisitionDetection : null,
            acquisitionLighting = _scene.GetHandAcquisitionDiagnostics(),
            hands = hands.Select((hand, index) => new
            {
                index, hand.Confidence, hand.RightHandProbability,
                visible = visibleHands.Contains(hand),
                pinchRatio = hand.Landmarks.Count == 21 ? DescribeHandObservation(hand).PinchRatio : (double?)null,
                spreadOutPose = HandPoseClassifier.IsSpreadOut(hand),
                fourFingersExtended = HandPoseClassifier.AreFourFingersExtended(hand),
                fingerSelection = HandPoseClassifier.DescribeFingerSelection(hand),
                landmarks = hand.Landmarks.ToArray()
            }).ToArray(),
            cursors = cursors.Select(cursor => new
            {
                cursor.Position, cursor.ExecuteEventId, cursor.ExecuteUntil,
                cursor.TrackingId, cursor.FingerTips, cursor.HasFourExtendedFingers,
                cursor.FingersTogether, cursor.IndexFingerSeparated,
                executing = cursor.IsExecuting(now), cursor.IsSpreadOut, cursor.SelectionPosition, cursor.SelectionFrameTime
            }).ToArray(),
            visualCursors = _handPreview is { } preview && preview.Timestamp == frame.Timestamp
                ? preview.VisualCursors.Select(cursor => new
                    { cursor.TrackingId, cursor.Position, cursor.FingerTips }).ToArray() : null,
            hoveredButtons = _scene.HoveredBoardButtons,
            fingerSelection = _scene.CurrentFingerSelectionFeedback,
            lighting = _scene.GetHandLightingDiagnostics()
        });
    }

    private void UpdateHandDetectionLogStatus()
    {
        var board = _scene.CurrentBoardScreen;
        var changed = _lastLoggedHandBoard != board;
        lock (_handGate)
        {
            if (changed)
            {
                _handVideoNotBefore = board == BoardScreen.HandTracking
                    ? DateTimeOffset.UtcNow : DateTimeOffset.MaxValue;
                if (board == BoardScreen.HandTracking || _lastLoggedHandBoard == BoardScreen.HandTracking)
                    LogHandTrackingEvent("board_changed", new { from = _lastLoggedHandBoard?.ToString(), to = board.ToString() }, force: true);
                _lastLoggedHandBoard = board;
            }
            if (UpdateHandVideoEligibility()) _handVideoRecorder?.Tick(DateTimeOffset.UtcNow);
        }
        var now = DateTimeOffset.UtcNow;
        if (!changed && now - _lastHandLogStatusRefresh < TimeSpan.FromSeconds(1)) return;
        _lastHandLogStatusRefresh = now;
        var status = _handDetectionLog?.Status;
        HandDetectionLogStatusText.Text = status?.Error is { } error ? "Detection log error: " + error :
            board == BoardScreen.HandTracking || _boardHandDiagnosticLogging
                ? $"Detection logging on: {status?.WrittenRecords ?? 0} records, {status?.DroppedRecords ?? 0} dropped.\n" +
                  (status?.CurrentPath ?? Path.Combine(_appDataDirectory, "HandTrackingLogs"))
                : "Full detection logging starts automatically in the Hand-Tracking tester.";
        var video = _handVideoRecorder?.Status;
        CameraPreviewTitleText.Text = video?.Error is not null ? "CAMERA  ·  recording error (see controls)" :
            video is { IsRecording: true } ? "CAMERA  ·  RECORDING hand test · UTC timestamps" :
            board == BoardScreen.HandTracking ? "CAMERA  ·  hand test · waiting to record a hand" :
            "CAMERA  ·  live fingertip tracking";
        HandVideoRecordingStatusText.Text = video?.Error is { } videoError ? "Hand video error: " + videoError :
            video is { IsRecording: true }
                ? $"Recording hand video: {video.WrittenFrames} frames; dropped: {video.DroppedFrames} frames, " +
                  $"{video.DroppedRecords} log records.\n{video.VideoPath}"
                : board == BoardScreen.HandTracking
                    ? "Video starts when a hand is detected; stops after 5 seconds without hands.\n" +
                      (video?.VideoPath ?? Path.Combine(_appDataDirectory, "HandTrackingRecordings"))
                    : "Hand video records automatically in the Hand-Tracking tester only.";
    }
}
