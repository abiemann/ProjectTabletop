using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private static readonly TimeSpan HandDetectionInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan TrackedHandInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan HandMarkerLifetime = TimeSpan.FromMilliseconds(350);
    private readonly object _handGate = new();
    private readonly HandGestureTracker _handGestures = new();
    private HandTrackingEngine? _handEngine;
    // Accessed only by the single inference worker. Reset its cropped hand
    // history there, so camera/UI resets never wait for a network inference.
    private long _handEngineGeneration = -1;
    private DateTimeOffset? _lastHandEngineFrameTime;
    private Task? _handDetectionTask;
    private DispatcherQueueTimer? _handStatusTimer;
    private HandPreview? _handPreview;
    private volatile bool _handTrackingEnabled = true;
    private bool _handDetecting;
    private long _handGeneration;
    private long _lastHandDetectionTick;
    private string? _handTrackingError;
    private string? _handLatencyWarning;
    private HandDetectionDiagnostics? _lastHandDetection;

    private sealed record HandPreview(HandCursor[] Cursors, int Width, int Height, DateTimeOffset Timestamp);
    private sealed record HandObservationDiagnostics(double Confidence, PixelPoint IndexTip, double PinchRatio,
        bool IsSpreadOut, bool HasFourExtendedFingers, PixelPoint[] FingerTips, FingerSelectionPose FingerSelection);
    private sealed record HandDetectionDiagnostics(DateTimeOffset FrameTime, double InferenceMilliseconds,
        double ResultAgeMilliseconds, double? FrameIntervalMilliseconds, bool DiscardedAsStale,
        HandObservationDiagnostics[] Hands);

    internal bool HandTrackingEnabled => _handTrackingEnabled;
    internal string HandTrackingControlStatus => HandTrackingStatusText.Text;
    internal int TrackedHandCount
    {
        get
        {
            lock (_handGate) return _handPreview is { } preview &&
                DateTimeOffset.UtcNow - preview.Timestamp <= HandMarkerLifetime ? preview.Cursors.Length : 0;
        }
    }

    internal int ExecutingHandCount
    {
        get
        {
            lock (_handGate)
            {
                var now = DateTimeOffset.UtcNow;
                return _handPreview is { } preview && now - preview.Timestamp <= HandMarkerLifetime
                    ? preview.Cursors.Count(cursor => cursor.IsExecuting(now)) : 0;
            }
        }
    }

    internal int SpreadOutHandCount
    {
        get
        {
            lock (_handGate)
            {
                var now = DateTimeOffset.UtcNow;
                return _handPreview is { } preview && preview.Timestamp <= now && now - preview.Timestamp <= HandMarkerLifetime
                    ? preview.Cursors.Count(cursor => cursor.IsSpreadOut) : 0;
            }
        }
    }

    internal int FourFingerHandCount
    {
        get
        {
            lock (_handGate)
            {
                var now = DateTimeOffset.UtcNow;
                return _handPreview is { } preview && preview.Timestamp <= now && now - preview.Timestamp <= HandMarkerLifetime
                    ? preview.Cursors.Count(cursor => cursor.HasFourExtendedFingers) : 0;
            }
        }
    }

    private bool IsBoardScanMeasuring => Volatile.Read(ref _boardSetupActive) &&
        (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) is
            BoardSetupPhase.Switching or BoardSetupPhase.ScanAmbient or
            BoardSetupPhase.ScanWhite or BoardSetupPhase.MeasureSpots;

    private void StartHandTrackingStatus()
    {
        _handStatusTimer = DispatcherQueue.CreateTimer();
        _handStatusTimer.Interval = TimeSpan.FromMilliseconds(100);
        _handStatusTimer.Tick += (_, _) =>
        {
            var expired = false;
            lock (_handGate)
            {
                if (_handPreview is { } preview && DateTimeOffset.UtcNow - preview.Timestamp > HandMarkerLifetime)
                {
                    LogHandTrackingEvent("cursor_expired", new { sourceFrameTime = preview.Timestamp });
                    _handPreview = null;
                    _scene.ClearHandTips(resetInput: false);
                    expired = true;
                }
            }
            if (expired && !_closing) CameraCanvas.Invalidate();
            UpdateHandTrackingStatus();
        };
        _handStatusTimer.Start();
        UpdateHandTrackingStatus();
    }

    private void HandTrackingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initialized && _handTrackingEnabled != HandTrackingToggle.IsOn)
            SetHandTrackingEnabled(HandTrackingToggle.IsOn);
    }

    internal void SetHandTrackingEnabled(bool enabled)
    {
        _handTrackingEnabled = enabled;
        if (HandTrackingToggle.IsOn != enabled) HandTrackingToggle.IsOn = enabled;
        _handTrackingError = null;
        ClearHandTracking();
        CameraCanvas.Invalidate();
        UpdateHandTrackingStatus();
    }

    // Called before any camera or board-state change. A result already in flight
    // can finish, but cannot publish into the new camera/registration generation.
    private void ClearHandTracking([CallerMemberName] string reason = "")
    {
        lock (_handGate)
        {
            _handGeneration++;
            _handPreview = null;
            _handGestures.Reset();
            _handLatencyWarning = null;
            _lastHandDetection = null;
            _lastHandDetectionTick = 0;
            _scene.ClearHandTips();
            _scene.InvalidatePhotoCopyCapture();
            LogHandTrackingEvent("tracking_reset", new { reason });
            _handVideoNotBefore = DateTimeOffset.UtcNow;
            _handVideoRecorder?.Stop("tracking_reset:" + reason);
        }
        if (_initialized && !_closing)
            DispatcherQueue.TryEnqueue(() => { if (!_closing) CameraCanvas.Invalidate(); });
    }

    private void QueueHandDetection(CameraFrame frame, long tick)
    {
        lock (_handGate)
        {
            if (_closing || !_cameraWanted || !_camera.IsRunning || _cameraOperation.CurrentCount == 0 ||
                Volatile.Read(ref _cameraHealthWarning) ||
                !_handTrackingEnabled || _handTrackingError is not null ||
                IsBoardScanMeasuring || _handDetecting) return;
            var interval = _handPreview is { Cursors.Length: > 0 } preview &&
                frame.Timestamp - preview.Timestamp <= HandMarkerLifetime
                ? TrackedHandInterval : HandDetectionInterval;
            if (_lastHandDetectionTick != 0 &&
                Stopwatch.GetElapsedTime(_lastHandDetectionTick, tick) < interval) return;
            _lastHandDetectionTick = tick;
            _handDetecting = true;
            var generation = _handGeneration;
            var requestedInTester = IsHandTrackingTester;
            var sequence = Interlocked.Increment(ref _handDetectionSequence);
            _handDetectionTask = Task.Run(() =>
            {
                try
                {
                    _handEngine ??= new HandTrackingEngine(Path.Combine(AppContext.BaseDirectory, "Models", "Hands"));
                    _handEngine.CaptureDiagnostics = requestedInTester;
                    double? frameInterval = _lastHandEngineFrameTime is { } previousFrame
                        ? (frame.Timestamp - previousFrame).TotalMilliseconds : null;
                    var engineReset = _handEngineGeneration != generation || frameInterval is <= 0 or > 350;
                    if (engineReset)
                    {
                        _handEngine.ResetTracking();
                        _handEngineGeneration = generation;
                    }
                    _lastHandEngineFrameTime = frame.Timestamp;
                    var detectionStarted = Stopwatch.GetTimestamp();
                    var hands = _handEngine.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra);
                    var detectorTrace = _handEngine.LastDiagnostics;
                    var inferenceMilliseconds = Stopwatch.GetElapsedTime(detectionStarted).TotalMilliseconds;
                    var visibleHands = hands.Where(hand =>
                            double.IsFinite(hand.IndexTip.X) && double.IsFinite(hand.IndexTip.Y) &&
                            hand.IndexTip.X >= 0 && hand.IndexTip.X < frame.Width &&
                            hand.IndexTip.Y >= 0 && hand.IndexTip.Y < frame.Height)
                        .ToArray();
                    var enqueued = DispatcherQueue.TryEnqueue(() =>
                    {
                        lock (_handGate)
                        {
                            if (_closing || generation != _handGeneration || !_handTrackingEnabled ||
                                !_cameraWanted || !_camera.IsRunning ||
                                Volatile.Read(ref _cameraHealthWarning) || IsBoardScanMeasuring)
                            {
                                LogHandDetection(sequence, requestedInTester, frame, generation, engineReset,
                                    inferenceMilliseconds, frameInterval, detectorTrace, hands, visibleHands, [],
                                    _closing ? "closing" : generation != _handGeneration ? "generation_changed" :
                                    !_handTrackingEnabled ? "tracking_disabled" : !_cameraWanted || !_camera.IsRunning ? "camera_stopped" :
                                    Volatile.Read(ref _cameraHealthWarning) ? "camera_unhealthy" : "board_scan");
                                return;
                            }
                            var age = DateTimeOffset.UtcNow - frame.Timestamp;
                            _lastHandDetection = new(frame.Timestamp, inferenceMilliseconds,
                                age.TotalMilliseconds, frameInterval, age > HandMarkerLifetime,
                                visibleHands.Select(DescribeHandObservation).ToArray());
                            if (age > HandMarkerLifetime)
                            {
                                _handPreview = null;
                                _scene.ClearHandTips(resetInput: false);
                                _handLatencyWarning = $"Hand tracking result was {age.TotalMilliseconds:F0} ms old. " +
                                    "Waiting for a result under 350 ms before showing the circle.";
                                LogHandDetection(sequence, requestedInTester, frame, generation, engineReset,
                                    inferenceMilliseconds, frameInterval, detectorTrace, hands, visibleHands, [], "stale");
                            }
                            else
                            {
                                _handLatencyWarning = null;
                                var cursors = _handGestures.Update(visibleHands, frame.Timestamp,
                                    DateTimeOffset.UtcNow).ToArray();
                                _handPreview = new HandPreview(cursors, frame.Width, frame.Height, frame.Timestamp);
                                _scene.SetHandCursors(cursors, frame.Timestamp, _photoCopyTask is { IsCompleted: false });
                                _scene.SetHandSpotlights(visibleHands, frame.Timestamp);
                                LogHandDetection(sequence, requestedInTester, frame, generation, engineReset,
                                    inferenceMilliseconds, frameInterval, detectorTrace, hands, visibleHands, cursors, "accepted");
                                QueuePhotoCopyObservation(frame, visibleHands);
                                QueuePhotoCopyCapture(frame, visibleHands, cursors);
                            }
                        }
                        if (_frozenFrame is null) CameraCanvas.Invalidate();
                        UpdateHandTrackingStatus();
                    });
                    if (!enqueued)
                        LogHandDetection(sequence, requestedInTester, frame, generation, engineReset,
                            inferenceMilliseconds, frameInterval, detectorTrace, hands, visibleHands, [], "dispatch_rejected");
                }
                catch (Exception ex)
                {
                    LogHandTrackingEvent("detection_error", new { sequence, frameTime = frame.Timestamp,
                        requestedGeneration = generation, error = ex.ToString() }, force: requestedInTester);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        lock (_handGate)
                        {
                            if (_closing || generation != _handGeneration) return;
                            _handTrackingError = "Hand tracking unavailable: " + ex.Message;
                            ClearHandTracking();
                        }
                        CameraCanvas.Invalidate();
                        UpdateHandTrackingStatus();
                    });
                }
                finally { lock (_handGate) _handDetecting = false; }
            });
        }
    }

    private static HandObservationDiagnostics DescribeHandObservation(HandDetection hand)
    {
        static double Distance(PixelPoint a, PixelPoint b) =>
            Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var points = hand.Landmarks;
        var scale = Math.Max(Distance(points[0], points[9]), Distance(points[5], points[17]));
        return new(hand.Confidence, hand.IndexTip, scale > 1 ? Distance(points[4], points[8]) / scale : 0,
            HandPoseClassifier.IsSpreadOut(hand), HandPoseClassifier.AreFourFingersExtended(hand),
            new[] { points[8], points[12], points[16], points[20] }, HandPoseClassifier.DescribeFingerSelection(hand));
    }

    private void UpdateHandTrackingStatus()
    {
        if (_closing) return;
        UpdateHandDetectionLogStatus();
        var selection = _scene.CurrentFingerSelectionFeedback.FirstOrDefault();
        HandTrackingStatusText.Text = !_handTrackingEnabled ? "Hand tracking is off." :
            _handTrackingError ??
            (IsBoardScanMeasuring ? "Hand tracking pauses while the board is being scanned." :
             !_cameraWanted || !_camera.IsRunning ? "Start the webcam to track your fingers." :
             _cameraHealthWarning ? "Waiting for fresh webcam video before tracking your fingertip." :
             _handLatencyWarning is not null ? _handLatencyWarning :
             TrackedHandCount == 0 ? "Looking for a hand. Show your hand clearly in the camera view." :
             selection is { Stage: BoardFingerSelectionStage.Armed } ? "Ready: move your index finger sideways to select the highlighted button." :
             selection is { Stage: BoardFingerSelectionStage.Separating } ? "Selecting the highlighted button…" :
             selection is { Stage: BoardFingerSelectionStage.Selected } ? "Selected. Bring your index finger back beside the other three to select again." :
             IsHandTrackingTester && SpreadOutHandCount > 0 ? "Spread out hand" :
             ExecutingHandCount > 0 ? "Pinch detected: execute signal. " +
                 (IsHandTrackingTester ? "Red circle for one second. " : "") +
                 "Separate thumb and index finger before the next pinch." :
             FourFingerHandCount > 0 ? "Aim with your middle fingertip and bring the four fingers together. When ready, move your index finger sideways to select. Bring it back to select again." :
             $"Tracking {TrackedHandCount} hand{(TrackedHandCount == 1 ? "" : "s")}. " +
                 (_scene.HasBoardMediaClip ? "A white spotlight illuminates each detected hand." :
                     "Complete board setup to illuminate your hand.") + " Aim with four fingers together, then separate your index finger to select. Pinch also works.");
    }

    private void DrawHandPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect)
    {
        HandPreview? preview;
        lock (_handGate) preview = _handPreview;
        if (!_handTrackingEnabled || IsBoardScanMeasuring || preview is null ||
            preview.Width != frame.Width || preview.Height != frame.Height ||
            DateTimeOffset.UtcNow - preview.Timestamp > HandMarkerLifetime) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var cursor in preview.Cursors)
        {
            if (cursor.HasFourExtendedFingers && cursor.FingerTips.Count == 4)
            {
                for (int index = 0; index < cursor.FingerTips.Count; index++)
                {
                    var finger = cursor.FingerTips[index];
                    var point = new Vector2((float)(rect.X + finger.X / frame.Width * rect.Width),
                        (float)(rect.Y + finger.Y / frame.Height * rect.Height));
                    ds.DrawCircle(point, index == 1 ? 12 : 5, Colors.Black, 5);
                    ds.DrawCircle(point, index == 1 ? 12 : 5,
                        index == 1 ? Windows.UI.Color.FromArgb(255, 244, 207, 111) : Colors.White, 2);
                }
                if (!IsHandTrackingTester || !cursor.IsExecuting(now)) continue;
            }
            var tip = cursor.Position;
            var center = new Vector2((float)(rect.X + tip.X / frame.Width * rect.Width),
                                    (float)(rect.Y + tip.Y / frame.Height * rect.Height));
            ds.DrawCircle(center, 12, Colors.Black, 6);
            ds.DrawCircle(center, 12, IsHandTrackingTester && cursor.IsExecuting(now) ? Colors.Red : Colors.White, 3);
        }
    }

    private async Task DisposeHandTrackingAsync()
    {
        _handStatusTimer?.Stop();
        ClearHandTracking();
        Task? pending;
        lock (_handGate) pending = _handDetectionTask;
        if (pending is not null) await pending;
        if (_photoCopyTask is { } photoCopyTask) await photoCopyTask;
        if (_photoCopyObservationTask is { } observationTask) await observationTask;
        _handEngine?.Dispose();
        _handEngine = null;
        if (_handVideoRecorder is not null) await _handVideoRecorder.DisposeAsync();
        if (_handDetectionLog is not null) await _handDetectionLog.DisposeAsync();
    }
}
