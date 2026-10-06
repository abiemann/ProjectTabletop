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
    private static readonly TimeSpan NoCameraFrameReconnect = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnchangedCameraImageWarning = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UnchangedCameraImageReconnect = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] CameraReconnectBackoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private readonly SemaphoreSlim _cameraOperation = new(1, 1);
    private readonly AsyncResultGate _pieceDetectionResults = new();
    private CameraFrame? _cameraHealthSample;
    private DateTimeOffset _cameraStartedAtUtc;
    private DateTimeOffset _cameraImageChangedAtUtc;
    private string? _cameraLiveStatus;
    private bool _cameraHealthWarning;
    private volatile bool _cameraWanted;
    private string? _cameraWantedDeviceId;
    private long _cameraOperationVersion;
    private int _cameraReconnectAttempts;
    private DateTimeOffset _nextCameraReconnectAtUtc;
    private bool _cameraReconnectInProgress;
    private string? _cameraRecoveryReason;
    private volatile bool _boardScanSuspendedForCameraOutage;

    private void ResetCameraHealth(bool preserveBoardScanSuspension = false)
    {
        _cameraHealthSample = null;
        _cameraStartedAtUtc = DateTimeOffset.MinValue;
        _cameraImageChangedAtUtc = DateTimeOffset.MinValue;
        _cameraLiveStatus = null;
        _cameraHealthWarning = false;
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
            SetStatus("Webcam video is stale. Projection is black; rescan the board after the camera recovers.");
        if (!Volatile.Read(ref _boardSetupActive) || _boardScanSuspendedForCameraOutage) return;
        _boardScanSuspendedForCameraOutage = true;
        ClearBoardPreview();
        BoardSetupStatusText.Text = "Webcam video is stale. Projector is black until a changing frame arrives.";
    }

    private void UpdateCameraHealth()
    {
        if (_closing || !_cameraWanted || _cameraReconnectInProgress ||
            _cameraOperation.CurrentCount == 0 ||
            SelectedCamera?.Device.Id != _cameraWantedDeviceId) return;

        var now = MonotonicClock.UtcNow;
        if (!_camera.IsRunning || _camera.ActiveDeviceId != _cameraWantedDeviceId)
        {
            SuspendBoardScanForCameraOutage();
            ScheduleCameraReconnect(now, _cameraRecoveryReason ?? "Webcam stopped delivering video.");
            return;
        }

        var frame = Volatile.Read(ref _latestCameraFrame);
        var lastFrameAt = frame?.Timestamp ?? _cameraStartedAtUtc;
        if (now - lastFrameAt >= NoCameraFrameWarning)
        {
            SuspendBoardScanForCameraOutage();
            var gap = $"{(int)(now - lastFrameAt).TotalSeconds} s";
            if (now - lastFrameAt >= NoCameraFrameReconnect)
            {
                ScheduleCameraReconnect(now, "No fresh webcam frame for " + gap + ".");
                return;
            }
            CameraStatusText.Text = frame is null
                ? "Webcam connected, but no video frames have arrived (" + gap + ")."
                : "Webcam stalled: no fresh video frame for " + gap + ".";
            _cameraHealthWarning = true;
            return;
        }

        if (frame is null) return;
        var previousSample = _cameraHealthSample;
        var hadPreviousSample = previousSample is not null;
        var changed = previousSample is null ||
            previousSample.Width != frame.Width || previousSample.Height != frame.Height ||
            previousSample.Stride != frame.Stride ||
            !previousSample.Bgra.AsSpan().SequenceEqual(frame.Bgra);
        if (changed)
            _cameraImageChangedAtUtc = now;
        // The first frame after a restart establishes a baseline; only a later,
        // different frame proves that a frozen driver has actually recovered.
        if (changed && hadPreviousSample && _cameraReconnectAttempts != 0)
        {
            _cameraReconnectAttempts = 0;
            _nextCameraReconnectAtUtc = DateTimeOffset.MinValue;
            _cameraRecoveryReason = null;
        }
        _cameraHealthSample = frame;

        if (now - _cameraImageChangedAtUtc >= UnchangedCameraImageWarning)
        {
            SuspendBoardScanForCameraOutage();
            var frozenFor = $"{(int)(now - _cameraImageChangedAtUtc).TotalSeconds} s";
            if (now - _cameraImageChangedAtUtc >= UnchangedCameraImageReconnect)
            {
                ScheduleCameraReconnect(now, "Webcam frames are byte-identical for " + frozenFor + ".");
                return;
            }
            CameraStatusText.Text = "Webcam frames are arriving, but the image has been byte-identical for " +
                frozenFor + (Volatile.Read(ref _boardSetupActive)
                    ? ". Projector is black while the image is frozen."
                    : ". Automatic reconnect will follow if the image remains frozen.");
            _cameraHealthWarning = true;
        }
        else
        {
            if (_boardScanSuspendedForCameraOutage && changed && hadPreviousSample)
            {
                _boardScanSuspendedForCameraOutage = false;
                ClearBoardPreview();
            }
            if (_boardScanSuspendedForCameraOutage)
            {
                CameraStatusText.Text = Volatile.Read(ref _boardSetupActive)
                    ? "Webcam frames resumed, but the image has not changed. Projector remains black."
                    : "Webcam frames resumed, but the image has not changed.";
                _cameraHealthWarning = true;
            }
            else if (_cameraHealthWarning)
            {
                CameraStatusText.Text = _cameraLiveStatus;
                _cameraHealthWarning = false;
            }
        }
    }

    private void ScheduleCameraReconnect(DateTimeOffset now, string reason)
    {
        _cameraRecoveryReason = reason;
        _cameraHealthWarning = true;
        if (_cameraReconnectAttempts >= CameraReconnectBackoff.Length)
        {
            CameraStatusText.Text = reason + " Automatic reconnect attempts are exhausted. Press Start camera to try again.";
            return;
        }

        if (_nextCameraReconnectAtUtc > now)
        {
            var wait = Math.Max(1, (int)Math.Ceiling((_nextCameraReconnectAtUtc - now).TotalSeconds));
            CameraStatusText.Text = reason + $" Reconnecting in {wait} s " +
                $"(attempt {_cameraReconnectAttempts + 1}/{CameraReconnectBackoff.Length}).";
            return;
        }

        var attempt = ++_cameraReconnectAttempts;
        _nextCameraReconnectAtUtc = now + CameraReconnectBackoff[attempt - 1];
        _cameraReconnectInProgress = true;
        CameraStatusText.Text = reason + $" Reconnecting (attempt {attempt}/{CameraReconnectBackoff.Length})…";
        _ = ReconnectCameraAsync(_cameraOperationVersion, attempt);
    }

    private async Task ReconnectCameraAsync(long version, int attempt)
    {
        try
        {
            if (SelectedCamera is not { } choice || choice.Device.Id != _cameraWantedDeviceId) return;
            var started = await StartCameraCoreAsync(choice, version);
            if (version != _cameraOperationVersion || !_cameraWanted || _closing) return;
            if (!started && attempt < CameraReconnectBackoff.Length)
                CameraStatusText.Text += $" Retry {attempt + 1}/{CameraReconnectBackoff.Length} will follow after the backoff.";
        }
        catch (Exception ex)
        {
            if (version == _cameraOperationVersion && _cameraWanted && !_closing)
                CameraStatusText.Text = "Webcam reconnect failed: " + ex.Message;
        }
        finally { _cameraReconnectInProgress = false; }
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

    private async Task<bool> StartSelectedCameraAsync()
    {
        if (SelectedCamera is not { } choice)
        {
            CameraStatusText.Text = "Select a webcam first.";
            return false;
        }
        _cameraWanted = true;
        _cameraWantedDeviceId = choice.Device.Id;
        _cameraReconnectAttempts = 0;
        _nextCameraReconnectAtUtc = DateTimeOffset.MinValue;
        _cameraRecoveryReason = null;
        var version = Interlocked.Increment(ref _cameraOperationVersion);
        return await StartCameraCoreAsync(choice, version);
    }

    private async Task<bool> StartCameraCoreAsync(CameraChoice choice, long version)
    {
        await _cameraOperation.WaitAsync();
        try
        {
            if (_closing || !_cameraWanted || version != _cameraOperationVersion) return false;
            ClearHandTracking();
            ResetEyeTipTracking("camera-restarted", choice.Device.Id);
            _scene.ClearBoardMediaClip();
            ResetCameraHealth(preserveBoardScanSuspension:
                _boardScanSuspendedForCameraOutage && Volatile.Read(ref _boardSetupActive));
            CameraStatusText.Text = "Starting " + choice.Device.DisplayName + "…";
            InvalidateCalibration("Camera restarted. Recalibrate both physical planes.");
            ClearBoardPreview();
            _frozenFrame = null;
            Volatile.Write(ref _latestCameraFrame, null);
            CameraCanvas.Invalidate();
            await _camera.StartAsync(choice.Device.Id);
            if (version != _cameraOperationVersion || !_cameraWanted || _closing) return false;
            var format = _camera.NegotiatedFormat;
            _cameraLiveStatus = format is null
                ? $"Live: {choice.Device.DisplayName}"
                : $"Live: {choice.Device.DisplayName}, {format.Value.Width} × {format.Value.Height} " +
                  $"at {format.Value.FramesPerSecond:F1} fps";
            _cameraStartedAtUtc = MonotonicClock.UtcNow;
            CameraStatusText.Text = Volatile.Read(ref _latestCameraFrame) is null
                ? $"Connected: {choice.Device.DisplayName}. Waiting for video frames…"
                : _cameraLiveStatus;
            _cameraHealthWarning = CameraStatusText.Text != _cameraLiveStatus;
            return true;
        }
        catch (Exception ex)
        {
            if (version == _cameraOperationVersion && _cameraWanted && !_closing)
            {
                _cameraRecoveryReason = "Webcam could not start: " + ex.Message + ".";
                CameraStatusText.Text = _cameraRecoveryReason;
                _cameraHealthWarning = true;
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
        _cameraReconnectAttempts = 0;
        _nextCameraReconnectAtUtc = DateTimeOffset.MinValue;
        _cameraRecoveryReason = null;
        await _cameraOperation.WaitAsync();
        try
        {
            _scene.ClearBoardMediaClip();
            await _camera.StopAsync();
            ResetCameraHealth();
            ClearBoardPreview();
            _frozenFrame = null;
            Volatile.Write(ref _latestCameraFrame, null);
            CameraCanvas.Invalidate();
            CameraStatusText.Text = "Camera stopped.";
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
        Volatile.Write(ref _latestCameraFrame, null);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || !_cameraWanted || _camera.IsRunning ||
                version != Interlocked.Read(ref _cameraOperationVersion)) return;
            ResetCameraHealth();
            if (Volatile.Read(ref _boardSetupActive))
                SuspendBoardScanForCameraOutage();
            else
                ClearBoardPreview();
            _frozenFrame = null;
            _bitmapFrame = null;
            _cameraBitmap?.Dispose();
            _cameraBitmap = null;
            _cameraBitmapDevice = null;
            CameraCanvas.Invalidate();
            _cameraRecoveryReason = message;
            var soon = MonotonicClock.UtcNow + TimeSpan.FromSeconds(2);
            if (_nextCameraReconnectAtUtc < soon) _nextCameraReconnectAtUtc = soon;
            _cameraHealthWarning = true;
            CameraStatusText.Text = message + " Reconnecting shortly.";
        });
    }

    private void Camera_FrameReceived(object? sender, CameraFrame frame)
    {
        var pieceGeneration = _pieceDetectionResults.Capture();
        if (_closing || !_cameraWanted || _cameraOperation.CurrentCount == 0 ||
            _camera.ActiveDeviceId != _cameraWantedDeviceId) return;
        Volatile.Write(ref _latestCameraFrame, frame);
        QueueEyeTipDetection(frame);
        QueueHandTrackingVideoFrame(frame);
        var now = Stopwatch.GetTimestamp();
        if (_lastPreviewTick == 0 || Stopwatch.GetElapsedTime(_lastPreviewTick, now) >= TimeSpan.FromMilliseconds(40))
        {
            _lastPreviewTick = now;
            DispatcherQueue.TryEnqueue(() => { if (!_closing) CameraCanvas.Invalidate(); });
        }

        if (Volatile.Read(ref _boardSetupActive))
        {
            if (!_boardScanSuspendedForCameraOutage)
                QueueBoardDetection(frame, now);
            if (!IsBoardScanMeasuring) QueueHandDetection(frame, now);
            return;
        }

        QueuePaintDetection(frame, now);
        QueueHandDetection(frame, now);
        QueuePieceDetection(frame, pieceGeneration);
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
        _ = Task.Run(() =>
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
                if (published)
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_closing && _frozenFrame is null &&
                            generation == _pieceDetectionResults.Capture()) CameraCanvas.Invalidate();
                    });
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
        var frame = IsLearningEyeTip ? _bitmapFrame : _frozenFrame ?? Volatile.Read(ref _latestCameraFrame);
        if (frame is null) return;
        var point = e.GetCurrentPoint(CameraCanvas).Position;
        var rect = CameraImageRect(frame, (float)CameraCanvas.ActualWidth, (float)CameraCanvas.ActualHeight);
        if (point.X < rect.X || point.Y < rect.Y || point.X > rect.X + rect.Width || point.Y > rect.Y + rect.Height)
            return;
        var cameraPoint = new PixelPoint((point.X - rect.X) / rect.Width * frame.Width,
                                         (point.Y - rect.Y) / rect.Height * frame.Height);

        if (TryLearnEyeTipFromPreview(frame, cameraPoint))
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
        var frame = _frozenFrame ?? Volatile.Read(ref _latestCameraFrame);
        if (frame is null) return;
        if (!ReferenceEquals(frame, _bitmapFrame) || !Equals(_cameraBitmapDevice, sender.Device))
        {
            _cameraBitmap?.Dispose();
            _cameraBitmap = CanvasBitmap.CreateFromBytes(sender, frame.Bgra, frame.Width, frame.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
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
