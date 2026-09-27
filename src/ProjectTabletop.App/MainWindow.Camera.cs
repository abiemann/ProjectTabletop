using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
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
        ClearBoardPreview();
        if (_calibration is not null || _boardCameraPoints.Count > 0 || _topCameraPoints.Count > 0)
            InvalidateCalibration("Webcam selection changed. Recalibrate both physical planes.");
        if (_camera.IsRunning && SelectedCamera?.Device.Id != _camera.ActiveDeviceId)
            CameraStatusText.Text = "Press Start camera to switch to the selected webcam.";
    }

    private async void StartCamera_Click(object sender, RoutedEventArgs e) => await StartSelectedCameraAsync();

    private async Task<bool> StartSelectedCameraAsync()
    {
        if (SelectedCamera is not { } choice)
        {
            CameraStatusText.Text = "Select a webcam first.";
            return false;
        }
        try
        {
            CameraStatusText.Text = "Starting " + choice.Device.DisplayName + "…";
            InvalidateCalibration("Camera restarted. Recalibrate both physical planes.");
            ClearBoardPreview();
            _frozenFrame = null;
            Volatile.Write(ref _latestCameraFrame, null);
            Volatile.Write(ref _latestDetections, Array.Empty<PieceDetection>());
            _scene.SetDetections(Array.Empty<PieceDetection>(), DateTimeOffset.MinValue);
            _visionError = null;
            CameraCanvas.Invalidate();
            await _camera.StartAsync(choice.Device.Id);
            var format = _camera.NegotiatedFormat;
            CameraStatusText.Text = format is null
                ? $"Live: {choice.Device.DisplayName}"
                : $"Live: {choice.Device.DisplayName}, {format.Value.Width} × {format.Value.Height} " +
                  $"at {format.Value.FramesPerSecond:F1} fps";
            return true;
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = "Webcam could not start: " + ex.Message +
                " Check Windows Settings > Privacy & security > Camera > desktop app access.";
            return false;
        }
    }

    private async void StopCamera_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _camera.StopAsync();
            ClearBoardPreview();
            _frozenFrame = null;
            Volatile.Write(ref _latestCameraFrame, null);
            _scene.SetDetections(Array.Empty<PieceDetection>(), DateTimeOffset.MinValue);
            CameraCanvas.Invalidate();
            CameraStatusText.Text = "Camera stopped.";
            _visionError = null;
        }
        catch (Exception ex) { CameraStatusText.Text = "Camera stop failed: " + ex.Message; }
    }

    private void Camera_CaptureFailed(object? sender, string message)
    {
        _scene.SetDetections(Array.Empty<PieceDetection>(), DateTimeOffset.MinValue);
        Volatile.Write(ref _latestCameraFrame, null);
        Volatile.Write(ref _latestDetections, Array.Empty<PieceDetection>());
        DispatcherQueue.TryEnqueue(() =>
        {
            ClearBoardPreview();
            _frozenFrame = null;
            _bitmapFrame = null;
            _cameraBitmap?.Dispose();
            _cameraBitmap = null;
            _cameraBitmapDevice = null;
            CameraCanvas.Invalidate();
            CameraStatusText.Text = message;
        });
    }

    private void Camera_FrameReceived(object? sender, CameraFrame frame)
    {
        Volatile.Write(ref _latestCameraFrame, frame);
        var now = Stopwatch.GetTimestamp();
        if (_lastPreviewTick == 0 || Stopwatch.GetElapsedTime(_lastPreviewTick, now) >= TimeSpan.FromMilliseconds(40))
        {
            _lastPreviewTick = now;
            DispatcherQueue.TryEnqueue(() => { if (!_closing) CameraCanvas.Invalidate(); });
        }

        if (Volatile.Read(ref _boardSetupActive))
        {
            QueueBoardDetection(frame, now);
            return;
        }

        bool trained;
        lock (_visionGate) trained = _vision.IsTrained;
        if (!trained || Interlocked.CompareExchange(ref _detecting, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<PieceDetection> detections;
                lock (_visionGate) detections = _vision.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra);
                Volatile.Write(ref _latestDetections, detections);
                _scene.SetDetections(detections, frame.Timestamp);
                _visionError = null;
                DispatcherQueue.TryEnqueue(() => { if (!_closing && _frozenFrame is null) CameraCanvas.Invalidate(); });
            }
            catch (Exception ex) { _visionError = "Shape detection failed: " + ex.Message; }
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
        var frame = _frozenFrame ?? Volatile.Read(ref _latestCameraFrame);
        if (frame is null) return;
        var point = e.GetCurrentPoint(CameraCanvas).Position;
        var rect = CameraImageRect(frame, (float)CameraCanvas.ActualWidth, (float)CameraCanvas.ActualHeight);
        if (point.X < rect.X || point.Y < rect.Y || point.X > rect.X + rect.Width || point.Y > rect.Y + rect.Height)
            return;
        var cameraPoint = new PixelPoint((point.X - rect.X) / rect.Width * frame.Width,
                                         (point.Y - rect.Y) / rect.Height * frame.Height);

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
