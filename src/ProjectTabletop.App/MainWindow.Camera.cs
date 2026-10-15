using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Sample the owned camera image once per status tick. A driver can keep its
    // reader running while returning no new frames, or replay one old frame.
    private static readonly TimeSpan NoCameraFrameWarning = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FirstCameraFrameAllowance = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnchangedCameraImageWarning = TimeSpan.FromSeconds(8);
    private const string ManualCameraRecoveryHint = "Press Stop, then Start camera to retry.";
    private static readonly TimeSpan CameraPreviewInterval = TimeSpan.FromMilliseconds(40);
    private int _cameraPreviewRedrawPending;
    private readonly SemaphoreSlim _cameraOperation = new(1, 1);
    private Task<bool>? _cameraStartTask;
    private string? _cameraStartingDeviceId;
    private int _cameraStartingRate;
    private long _cameraStartingVersion;
    private readonly AsyncResultGate _pieceDetectionResults = new();
    private readonly CameraFrameSlot _cameraHealthFrames = new();
    private readonly LatestFrameWorker<CameraInputArrival> _cameraInputWorker;
    private long _cameraInputNotBeforeTicks;

    // Scheduling runs after delivery. Detector selection and reset generations
    // still belong to the moment the frame arrived, not to when it was scheduled.
    private readonly record struct CameraInputArrival(long PieceGeneration, bool BoardSetupActive);
    private DateTimeOffset _cameraStartedAtUtc;
    private DateTimeOffset _cameraImageChangedAtUtc;
    private string? _cameraLiveStatus;
    private bool _cameraHealthWarning;
    private volatile bool _cameraWanted;
    private string? _cameraWantedDeviceId;
    private long _cameraOperationVersion;
    private volatile bool _cameraManualRestartRequired;
    private volatile bool _boardScanSuspendedForCameraOutage;

    private CameraFrameLease? AcquireLatestCameraFrame() => _latestCameraFrames.Acquire();

    private static Task RunCameraFrameWork(CameraFrame frame, Action work)
    {
        var lease = frame.Retain();
        try
        {
            return Task.Run(() =>
            {
                try { work(); }
                finally { lease.Dispose(); }
            });
        }
        catch { lease.Dispose(); throw; }
    }

    private bool TryPostCameraFrame(CameraFrame frame, Action publication)
    {
        var lease = frame.Retain();
        bool accepted = false;
        try
        {
            return accepted = DispatcherQueue.TryEnqueue(() =>
            {
                try { publication(); }
                finally { lease.Dispose(); }
            });
        }
        finally { if (!accepted) lease.Dispose(); }
    }

    private void ClearLatestCameraFrame()
    {
        Interlocked.Exchange(ref _cameraInputNotBeforeTicks, MonotonicClock.UtcNow.UtcTicks);
        _cameraInputWorker.Clear();
        Volatile.Write(ref _latestCameraFrame, null);
        _latestCameraFrames.Clear();
    }

    private void ResetCameraHealth(bool preserveBoardScanSuspension = false)
    {
        _cameraHealthFrames.Clear();
        _cameraStartedAtUtc = DateTimeOffset.MinValue;
        _cameraImageChangedAtUtc = DateTimeOffset.MinValue;
        _cameraLiveStatus = null;
        _cameraHealthWarning = false;
        _cameraManualRestartRequired = false;
        _scene.SetCameraCheckRequired(false);
        if (!preserveBoardScanSuspension)
            _boardScanSuspendedForCameraOutage = false;
    }

    private void SuspendBoardScanForCameraOutage()
    {
        ClearHandTracking();
        ResetEyeTipTracking("waiting-for-fresh-camera-frame");
        var hadMediaClip = _scene.HasBoardMediaClip;
        _scene.ClearBoardMediaClip();
        if (hadMediaClip && !Volatile.Read(ref _boardSetupActive))
            SetStatus("Webcam video is stale. Check Camera; rescan the board after the camera recovers.");
        if (!Volatile.Read(ref _boardSetupActive) || _boardScanSuspendedForCameraOutage) return;
        _boardScanSuspendedForCameraOutage = true;
        ClearBoardPreview();
        BoardSetupStatusText.Text = "Webcam video is stale. Check Camera. " + ManualCameraRecoveryHint;
    }

    private void UpdateCameraHealth()
    {
        if (_closing || !_cameraWanted || _cameraManualRestartRequired ||
            _cameraOperation.CurrentCount == 0 ||
            SelectedCamera?.Device.Id != _cameraWantedDeviceId) return;

        var now = MonotonicClock.UtcNow;
        if (!_camera.IsRunning || _camera.ActiveDeviceId != _cameraWantedDeviceId)
        {
            RequireManualCameraRecovery("Webcam stopped delivering video.");
            return;
        }

        using var frameLease = AcquireLatestCameraFrame();
        var frame = frameLease?.Frame;
        var lastFrameAt = frame?.Timestamp ?? _cameraStartedAtUtc;
        if (now - lastFrameAt >= NoCameraFrameWarning)
        {
            var gap = $"{(int)(now - lastFrameAt).TotalSeconds} s";
            if (frame is null && now - lastFrameAt < FirstCameraFrameAllowance)
            {
                // Some drivers deliver their first frame slowly. Warn and keep input
                // paused, but do not latch a manual restart that the next attempt
                // would hit again; the first frame clears this warning below.
                CameraStatusText.Text = "Webcam connected, waiting for its first video frame (" + gap + ").";
                _cameraHealthWarning = true;
                return;
            }
            RequireManualCameraRecovery(frame is null
                ? "Webcam connected, but no video frames have arrived (" + gap + ")."
                : "Webcam stalled: no fresh video frame for " + gap + ".");
            return;
        }

        if (frame is null) return;
        using var previousLease = _cameraHealthFrames.Acquire();
        var previousSample = previousLease?.Frame;
        var changed = previousSample is null ||
            previousSample.Width != frame.Width || previousSample.Height != frame.Height ||
            previousSample.Stride != frame.Stride ||
            !previousSample.Bgra.AsSpan().SequenceEqual(frame.Bgra);
        if (changed)
            _cameraImageChangedAtUtc = now;
        _cameraHealthFrames.Set(frame);

        if (now - _cameraImageChangedAtUtc >= UnchangedCameraImageWarning)
        {
            var frozenFor = $"{(int)(now - _cameraImageChangedAtUtc).TotalSeconds} s";
            RequireManualCameraRecovery("Webcam frames are arriving, but the image has been byte-identical for " + frozenFor + ".");
        }
        else if (_cameraHealthWarning)
        {
            CameraStatusText.Text = _cameraLiveStatus;
            _cameraHealthWarning = false;
        }
    }

    private void RequireManualCameraRecovery(string reason)
    {
        if (_cameraManualRestartRequired) return;
        // A stale camera must not trigger driver close/reopen loops. Latch the
        // input pause until an explicit Stop, even if frames resume meanwhile.
        _cameraManualRestartRequired = true;
        _cameraHealthWarning = true;
        SuspendBoardScanForCameraOutage();
        _scene.SetCameraCheckRequired(true);
        CameraStatusText.Text = reason + " " + ManualCameraRecoveryHint;
    }

    private async void RefreshCameras_Click(object sender, RoutedEventArgs e) => await RefreshCamerasAsync();

    private async Task RefreshCamerasAsync()
    {
        try
        {
            var previousId = SelectedCamera?.Device.Id;
            var cameras = await CameraCaptureService.ListDevicesAsync();
            CameraComboBox.Items.Clear();
            foreach (var camera in cameras) CameraComboBox.Items.Add(new CameraChoice(camera));
            CameraComboBox.SelectedItem = CameraComboBox.Items.OfType<CameraChoice>()
                .FirstOrDefault(item => item.Device.Id == previousId)
                ?? CameraComboBox.Items.OfType<CameraChoice>()
                    .FirstOrDefault(item => item.Device.DisplayName.Contains("Android Webcam", StringComparison.OrdinalIgnoreCase))
                ?? CameraComboBox.Items.OfType<CameraChoice>().FirstOrDefault();
            if (cameras.Count == 0) CameraStatusText.Text = "No color webcams found. Check Windows camera access.";
        }
        catch (Exception ex) { CameraStatusText.Text = "Cannot list webcams: " + ex.Message; }
    }

    private void CameraComboBox_SelectionChanged(object sender,
        Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (!_initialized || SelectedCamera is not { } selected ||
            _selectedCameraId == selected.Device.Id) return;
        _selectedCameraId = selected.Device.Id;
        LoadSelectedCameraFrameRate();
        ClearHandTracking();
        ResetEyeTipTracking("camera-changed", selected.Device.Id);
        _scene.ClearBoardMediaClip();
        if (_cameraWantedDeviceId is not null && _cameraWantedDeviceId != selected.Device.Id)
        {
            _cameraWanted = false;
            _cameraWantedDeviceId = null;
            Interlocked.Increment(ref _cameraOperationVersion);
        }
        ClearBoardPreview();
        if (_calibration is not null || _boardCameraPoints.Count > 0 || _topCameraPoints.Count > 0)
            InvalidateCalibration("Webcam selection changed. Recalibrate both physical planes.");
        if (_camera.IsRunning && SelectedCamera?.Device.Id != _camera.ActiveDeviceId)
            CameraStatusText.Text = "Press Start camera to switch to the selected webcam.";
        else if (_camera.IsRunning && _cameraLiveStatus is not null && !_cameraHealthWarning)
            CameraStatusText.Text = _cameraLiveStatus;
    }

    private async void StartCamera_Click(object sender, RoutedEventArgs e) => await StartSelectedCameraAsync();

    private Task<bool> StartSelectedCameraAsync()
    {
        if (_cameraManualRestartRequired)
        {
            CameraStatusText.Text = "Camera input is paused. " + ManualCameraRecoveryHint;
            return Task.FromResult(false);
        }
        if (SelectedCamera is not { } choice)
        {
            CameraStatusText.Text = "Select a webcam first.";
            return Task.FromResult(false);
        }
        if (_cameraStartTask is { IsCompleted: false } && _cameraWanted &&
            _cameraStartingDeviceId == choice.Device.Id && _cameraStartingRate == _requestedCameraFramesPerSecond &&
            _cameraStartingVersion == _cameraOperationVersion) return _cameraStartTask;
        _cameraWanted = true;
        _cameraWantedDeviceId = choice.Device.Id;
        var version = Interlocked.Increment(ref _cameraOperationVersion);
        _cameraStartingDeviceId = choice.Device.Id;
        _cameraStartingRate = _requestedCameraFramesPerSecond;
        _cameraStartingVersion = version;
        return _cameraStartTask = StartCameraCoreAsync(choice, version, _requestedCameraFramesPerSecond);
    }

    private async Task<bool> StartCameraCoreAsync(CameraChoice choice, long version, int requestedFramesPerSecond)
    {
        int requestedRate = requestedFramesPerSecond;
        await _cameraOperation.WaitAsync();
        try
        {
            if (_closing || !_cameraWanted || _cameraManualRestartRequired || version != _cameraOperationVersion) return false;
            if (_camera.IsRunning && _camera.ActiveDeviceId == choice.Device.Id &&
                _camera.RequestedFramesPerSecond == requestedRate) return true;
            ClearHandTracking();
            ResetEyeTipTracking("camera-restarted", choice.Device.Id);
            _scene.ClearBoardMediaClip();
            ResetCameraHealth(preserveBoardScanSuspension:
                _boardScanSuspendedForCameraOutage && Volatile.Read(ref _boardSetupActive));
            CameraStatusText.Text = "Starting " + choice.Device.DisplayName + "…";
            InvalidateCalibration("Camera restarted. Recalibrate both physical planes.");
            ClearBoardPreview();
            _frozenFrame = null;
            ClearLatestCameraFrame();
            CameraCanvas.Invalidate();
            await _camera.StartAsync(choice.Device.Id, requestedRate);
            if (version != _cameraOperationVersion || !_cameraWanted || _closing)
            {
                // Still inside the operation gate: a superseded start must release
                // its device before a queued replacement or shutdown can proceed.
                await _camera.StopAsync();
                return false;
            }
            // A native failure callback can arrive while initialization is
            // awaiting the driver. Do not overwrite its manual recovery state.
            if (_cameraManualRestartRequired) return false;
            var format = _camera.NegotiatedFormat;
            _cameraLiveStatus = format is null
                ? $"Live: {choice.Device.DisplayName}"
                : $"Live: {choice.Device.DisplayName}, {format.Value.Width} × {format.Value.Height} " +
                  $"at {format.Value.FramesPerSecond:F1} fps ({_camera.NegotiatedSubtype ?? "unknown format"})";
            _cameraStartedAtUtc = MonotonicClock.UtcNow;
            CameraStatusText.Text = Volatile.Read(ref _latestCameraFrame) is null
                ? $"Connected: {choice.Device.DisplayName}. Waiting for video frames…"
                : _cameraLiveStatus;
            _cameraHealthWarning = CameraStatusText.Text != _cameraLiveStatus;
            UpdateCameraFrameRateStatus();
            return true;
        }
        catch (Exception ex)
        {
            if (version == _cameraOperationVersion && _cameraWanted && !_closing)
            {
                // Every failed open waits for an explicit user retry. Health
                // monitoring must not start a second attempt in the background.
                _cameraWanted = false;
                _cameraWantedDeviceId = null;
                CameraStatusText.Text = "Webcam could not start: " + ex.Message;
                _cameraHealthWarning = true;
                UpdateCameraFrameRateStatus();
            }
            return false;
        }
        finally { _cameraOperation.Release(); }
    }

    private async void StopCamera_Click(object sender, RoutedEventArgs e) => await StopCameraAsync();

    internal async Task StopCameraAsync()
    {
        _cameraWanted = false;
        ClearHandTracking();
        ResetEyeTipTracking("camera-stopped");
        _cameraWantedDeviceId = null;
        Interlocked.Increment(ref _cameraOperationVersion);
        await _cameraOperation.WaitAsync();
        try
        {
            _scene.ClearBoardMediaClip();
            await _camera.StopAsync();
            ResetCameraHealth();
            ClearBoardPreview();
            _frozenFrame = null;
            ClearLatestCameraFrame();
            CameraCanvas.Invalidate();
            CameraStatusText.Text = "Camera stopped.";
            UpdateCameraFrameRateStatus();
            _visionError = null;
        }
        catch (Exception ex) { CameraStatusText.Text = "Camera stop failed: " + ex.Message; }
        finally { _cameraOperation.Release(); }
    }

    private void Camera_CaptureFailed(object? sender, string message)
    {
        if (_camera.IsRunning) return;
        var version = Interlocked.Read(ref _cameraOperationVersion);
        ClearHandTracking();
        ResetEyeTipTracking("camera-stopped");
        _scene.ClearBoardMediaClip();
        ClearLatestCameraFrame();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || !_cameraWanted || _camera.IsRunning ||
                version != Interlocked.Read(ref _cameraOperationVersion)) return;
            ResetCameraHealth();
            ClearBoardPreview();
            _frozenFrame = null;
            _bitmapFrame = null;
            _bitmapFrameLease?.Dispose();
            _bitmapFrameLease = null;
            _cameraBitmap?.Dispose();
            _cameraBitmap = null;
            _cameraBitmapDevice = null;
            CameraCanvas.Invalidate();
            RequireManualCameraRecovery(message);
        });
    }

    private void Camera_FrameReceived(object? sender, CameraFrame frame)
    {
        var pieceGeneration = _pieceDetectionResults.Capture();
        if (_closing || !_cameraWanted || _cameraOperation.CurrentCount == 0 ||
            _camera.ActiveDeviceId != _cameraWantedDeviceId) return;
        _latestCameraFrames.Set(frame);
        Volatile.Write(ref _latestCameraFrame, frame);
        RequestCameraPreviewRedraw();
        if (_cameraManualRestartRequired) return;
        // This only reserves an independent worker; all caption/scene access
        // happens inside it, so hand scheduling cannot delay the next caption.
        var detectionTick = Stopwatch.GetTimestamp();
        QueueHoldButtonDetection(frame, detectionTick);
        QueueControlSwipeDetection(frame, detectionTick);
        // No detector or compositor lock belongs on the capture consumer. A
        // busy scene may delay scheduling, but cannot hold up webcam delivery.
        _cameraInputWorker.Enqueue(frame, new(pieceGeneration, Volatile.Read(ref _boardSetupActive)));
    }

    private void ProcessCameraInputFrame(CameraFrame frame, CameraInputArrival arrival)
    {
        // A frame delivered on the other side of a setup start or finish shows
        // the wrong projection (for example the scan's white field) for the
        // detectors that would now be chosen. A newer frame follows at once.
        if (_closing || !_cameraWanted || !_camera.IsRunning || _cameraManualRestartRequired ||
            _cameraOperation.CurrentCount == 0 || _camera.ActiveDeviceId != _cameraWantedDeviceId ||
            arrival.BoardSetupActive != Volatile.Read(ref _boardSetupActive) ||
            frame.Timestamp.UtcTicks < Interlocked.Read(ref _cameraInputNotBeforeTicks) ||
            MonotonicClock.UtcNow - frame.Timestamp > HandMarkerLifetime) return;
        ScheduleCameraInputFrame(frame, arrival);
    }

    private void ScheduleCameraInputFrame(CameraFrame frame, CameraInputArrival arrival)
    {
        var now = Stopwatch.GetTimestamp();
        // This board needs only its rail and stationary captions. Do not queue
        // unrelated full-frame detectors or the hand model behind its artwork.
        if (!arrival.BoardSetupActive && _scene.CurrentBoardScreen == BoardScreen.StrykersRun)
        {
            QueueStrykersRunDetection(frame);
            return;
        }
        QueueEyeTipDetection(frame);
        QueueFootballDetection(frame);
        QueueHandTrackingVideoFrame(frame);

        if (arrival.BoardSetupActive)
        {
            if (!_boardScanSuspendedForCameraOutage)
                QueueBoardDetection(frame, now);
            if (!IsBoardScanMeasuring) QueueHandDetection(frame, now);
            return;
        }

        QueuePaintDetection(frame, now);
        QueueHandDetection(frame, now);
        QueuePieceDetection(frame, arrival.PieceGeneration);
    }

    // Each preview redraw uploads a whole camera frame on the UI thread, which
    // also publishes hand results. Camera frames and detector results therefore
    // share one redraw at most every 40 ms. A request inside that interval is
    // deferred to its end, never dropped, so the latest overlay is always drawn.
    private void RequestCameraPreviewRedraw()
    {
        if (_closing || Interlocked.Exchange(ref _cameraPreviewRedrawPending, 1) != 0) return;
        var wait = CameraPreviewInterval - Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPreviewTick));
        if (wait <= TimeSpan.Zero) EnqueueCameraPreviewRedraw();
        else _ = Task.Delay(wait).ContinueWith(_ => EnqueueCameraPreviewRedraw(), TaskScheduler.Default);
    }

    private void EnqueueCameraPreviewRedraw()
    {
        if (DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _lastPreviewTick, Stopwatch.GetTimestamp());
                Volatile.Write(ref _cameraPreviewRedrawPending, 0);
                if (!_closing) CameraCanvas.Invalidate();
            })) return;
        Volatile.Write(ref _cameraPreviewRedrawPending, 0);
    }

    private void ResetPieceDetections()
    {
        _pieceDetectionResults.Invalidate(() =>
        {
            Volatile.Write(ref _latestDetections, Array.Empty<PieceDetection>());
            _scene.SetDetections(Array.Empty<PieceDetection>(), DateTimeOffset.MinValue);
            _visionError = null;
        });
    }

    private void QueuePieceDetection(CameraFrame frame, long generation)
    {
        VisionEngine vision;
        lock (_visionGate)
        {
            if (!_vision.IsTrained) return;
            vision = _vision;
        }
        bool Current() => !_closing && _cameraWanted && _camera.IsRunning &&
            _cameraOperation.CurrentCount != 0 && _camera.ActiveDeviceId == _cameraWantedDeviceId &&
            !Volatile.Read(ref _boardSetupActive) && !Volatile.Read(ref _cameraHealthWarning) &&
            ReferenceEquals(vision, Volatile.Read(ref _vision));
        if (!_pieceDetectionResults.TryApply(generation, () =>
            Current() && Interlocked.CompareExchange(ref _detecting, 1, 0) == 0)) return;
        _ = RunCameraFrameWork(frame, () =>
        {
            try
            {
                IReadOnlyList<PieceDetection> detections;
                lock (_visionGate)
                {
                    if (generation != _pieceDetectionResults.Capture() || !Current()) return;
                    detections = vision.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra);
                }
                bool published = _pieceDetectionResults.TryApply(generation, () =>
                {
                    if (!Current()) return false;
                    Volatile.Write(ref _latestDetections, detections);
                    _scene.SetDetections(detections, frame.Timestamp);
                    _visionError = null;
                    return true;
                });
                if (published) RequestCameraPreviewRedraw();
            }
            catch (Exception ex)
            {
                _pieceDetectionResults.TryApply(generation, () =>
                {
                    if (!Current()) return false;
                    _visionError = "Shape detection failed: " + ex.Message;
                    return true;
                });
            }
            finally { Interlocked.Exchange(ref _detecting, 0); }
        });
    }

    private void CaptureSnapshot_Click(object sender, RoutedEventArgs e)
    {
        _frozenFrame = _camera.CaptureSnapshot();
        if (_frozenFrame is null)
        {
            SetStatus("Start the webcam and wait for a frame before capturing a snapshot.");
            return;
        }
        if (_annotationMode is not (AnnotationMode.BoardCalibration or AnnotationMode.TopCalibration))
        {
            _pieceOutline.Clear();
            _pieceFront = null;
            _annotationMode = AnnotationMode.PieceOutline;
        }
        CameraCanvas.Invalidate();
        SetStatus($"Snapshot frozen at {_frozenFrame.Width} × {_frozenFrame.Height}. Mark the selected piece or calibration point.");
    }

    private void ResumeLive_Click(object sender, RoutedEventArgs e)
    {
        _frozenFrame = null;
        CameraCanvas.Invalidate();
        SetStatus("Live webcam preview resumed.");
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        _pieceOutline.Clear();
        _pieceFront = null;
        if (_annotationMode == AnnotationMode.BoardCalibration)
        {
            _boardCameraPoints.Clear();
            _scene.ShowCalibrationTarget(0, pieceTop: false);
        }
        else if (_annotationMode == AnnotationMode.TopCalibration)
        {
            _topCameraPoints.Clear();
            _scene.ShowCalibrationTarget(0, pieceTop: true);
        }
        CameraCanvas.Invalidate();
    }

    private void OutlineMode_Click(object sender, RoutedEventArgs e)
    {
        _annotationMode = AnnotationMode.PieceOutline;
        _scene.ShowCalibrationTarget(-1, pieceTop: false);
        SetStatus("Click vertices around one piece on a frozen snapshot. Click Front point when done.");
    }

    private void FrontMode_Click(object sender, RoutedEventArgs e)
    {
        _annotationMode = AnnotationMode.PieceFront;
        _scene.ShowCalibrationTarget(-1, pieceTop: false);
        SetStatus("Click a point at the front of that piece, away from its center.");
    }

    private void CameraCanvas_PointerPressed(object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Learning must inspect the pupil the user actually saw, even when a
        // newer camera frame arrived after the last preview draw.
        using var frameLease = IsLearningEyeTip || IsLearningFootballTip
            ? _bitmapFrame?.TryRetain() : _frozenFrame?.Retain() ?? AcquireLatestCameraFrame();
        var frame = frameLease?.Frame;
        if (frame is null) return;
        var point = e.GetCurrentPoint(CameraCanvas).Position;
        var rect = CameraImageRect(frame, (float)CameraCanvas.ActualWidth, (float)CameraCanvas.ActualHeight);
        if (point.X < rect.X || point.Y < rect.Y || point.X > rect.X + rect.Width || point.Y > rect.Y + rect.Height)
            return;
        var cameraPoint = new PixelPoint((point.X - rect.X) / rect.Width * frame.Width,
                                         (point.Y - rect.Y) / rect.Height * frame.Height);

        if (TryLearnFootballTipFromPreview(frame, cameraPoint) || TryLearnEyeTipFromPreview(frame, cameraPoint))
        {
            e.Handled = true;
            return;
        }

        switch (_annotationMode)
        {
            case AnnotationMode.PieceOutline when _frozenFrame is not null:
                _pieceOutline.Add(cameraPoint);
                SetStatus($"Piece outline: {_pieceOutline.Count} points. Mark the front point next.");
                break;
            case AnnotationMode.PieceFront when _frozenFrame is not null:
                _pieceFront = cameraPoint;
                SetStatus("Front marked. Add this labeled sample, or continue outlining.");
                break;
            case AnnotationMode.BoardCalibration:
                _boardCameraPoints.Add(cameraPoint);
                AdvanceCalibrationTarget(pieceTop: false);
                break;
            case AnnotationMode.TopCalibration:
                _topCameraPoints.Add(cameraPoint);
                _frozenFrame = null;
                AdvanceCalibrationTarget(pieceTop: true);
                break;
            default:
                SetStatus("Capture a snapshot and choose Outline, Front point, or a calibration mode before marking.");
                break;
        }
        CameraCanvas.Invalidate();
    }

    private static Rect CameraImageRect(CameraFrame frame, float width, float height)
    {
        var scale = Math.Min(width / frame.Width, height / frame.Height);
        var drawWidth = frame.Width * scale;
        var drawHeight = frame.Height * scale;
        return new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
    }

    private void CameraCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(Colors.Black);
        using var frameLease = _frozenFrame?.Retain() ?? AcquireLatestCameraFrame();
        var frame = frameLease?.Frame;
        if (frame is null) return;
        if (!ReferenceEquals(frame, _bitmapFrame) || !Equals(_cameraBitmapDevice, sender.Device))
        {
            _cameraBitmap?.Dispose();
            _cameraBitmap = CanvasBitmap.CreateFromBytes(sender, frame.Bgra, frame.Width, frame.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
            var bitmapLease = frame.Retain();
            _bitmapFrameLease?.Dispose();
            _bitmapFrameLease = bitmapLease;
            _bitmapFrame = frame;
            _cameraBitmapDevice = sender.Device;
        }
        var rect = CameraImageRect(frame, (float)sender.ActualWidth, (float)sender.ActualHeight);
        if (_cameraBitmap is not null) ds.DrawImage(_cameraBitmap, rect);

        Vector2 View(PixelPoint p) => new((float)(rect.X + p.X / frame.Width * rect.Width),
                                          (float)(rect.Y + p.Y / frame.Height * rect.Height));
        var outline = _pieceOutline.Select(View).ToArray();
        for (var i = 0; i < outline.Length; i++)
        {
            ds.FillCircle(outline[i], 4, Colors.Yellow);
            if (i > 0) ds.DrawLine(outline[i - 1], outline[i], Colors.Yellow, 2);
        }
        if (_pieceFront is { } front) ds.FillCircle(View(front), 7, Colors.Red);
        foreach (var point in _boardCameraPoints) ds.FillCircle(View(point), 6, Colors.Cyan);
        foreach (var point in _topCameraPoints) ds.FillCircle(View(point), 6, Colors.Magenta);

        if (_frozenFrame is null)
        {
            DrawBoardPreview(ds, frame, rect);
            DrawHandPreview(ds, frame, rect);
            DrawEyeTipPreview(ds, frame, rect);
            DrawFootballInputPreview(ds, frame, rect);
            if (!Volatile.Read(ref _boardSetupActive)) foreach (var detection in Volatile.Read(ref _latestDetections))
            {
                var corners = detection.Outline.Select(View).ToArray();
                for (var i = 0; i < corners.Length; i++)
                    ds.DrawLine(corners[i], corners[(i + 1) % corners.Length], Colors.Lime, 2);
                var center = View(detection.Center);
                ds.DrawText($"{detection.PieceId} {detection.Confidence:P0}", center.X + 5, center.Y + 5, Colors.Lime);
            }
        }
    }
}
