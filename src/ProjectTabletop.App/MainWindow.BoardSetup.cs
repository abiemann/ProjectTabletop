using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private bool _boardSetupActive;
    private long _boardSetupGeneration;
    private long _lastBoardDetectTick;
    private BoardPreviewState? _latestBoardPreview;
    private Vector2[]? _animatedBoardCorners;
    private long _boardAnimationStarted;
    private long _boardAnimationUpdated;

    private sealed record BoardPreviewState(BoardDetection Detection, int Width, int Height,
                                            DateTimeOffset Timestamp);

    private async void BoardSetup_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _boardSetupActive))
        {
            EndBoardSetup();
            return;
        }

        if (SelectedDisplay is null)
        {
            BoardSetupStatusText.Text = "Select the projector display first.";
            return;
        }

        Volatile.Write(ref _boardSetupActive, true);
        ClearBoardPreview();
        Volatile.Write(ref _latestDetections, Array.Empty<PieceDetection>());
        _scene.SetDetections(Array.Empty<PieceDetection>(), DateTimeOffset.MinValue);
        _scene.SetBoardSetup(true);
        BoardSetupButton.Content = "End board setup";
        if (_output is null || !_output.IsFullScreen || _outputDisplayId != SelectedDisplay.Id)
            OpenOutput_Click(sender, e);
        if (SelectedCamera is not null && (!_camera.IsRunning ||
            _camera.ActiveDeviceId != SelectedCamera.Device.Id))
            await StartSelectedCameraAsync();
        if (!Volatile.Read(ref _boardSetupActive) || _closing) return;
        BoardSetupStatusText.Text = _camera.IsRunning
            ? "Looking for the white board. Orange brackets in the camera preview mark detected corners."
            : "Grid is showing. Select and start a webcam to detect the white board.";
        SetStatus("Board setup is active. Center the board in the projected grid.");
    }

    private void EndBoardSetup()
    {
        Volatile.Write(ref _boardSetupActive, false);
        ClearBoardPreview();
        _scene.SetBoardSetup(false);
        BoardSetupButton.Content = "Start board setup";
        BoardSetupStatusText.Text = "Board setup stopped.";
    }

    private void ClearBoardPreview()
    {
        Interlocked.Increment(ref _boardSetupGeneration);
        Volatile.Write(ref _latestBoardPreview, null);
        _animatedBoardCorners = null;
        _boardAnimationStarted = 0;
        _boardAnimationUpdated = 0;
        if (!_closing) CameraCanvas.Invalidate();
    }

    private void QueueBoardDetection(CameraFrame frame, long tick)
    {
        var previous = Interlocked.Read(ref _lastBoardDetectTick);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, tick) < TimeSpan.FromMilliseconds(100))
            return;
        if (Interlocked.CompareExchange(ref _detecting, 1, 0) != 0) return;
        Interlocked.Exchange(ref _lastBoardDetectTick, tick);
        var generation = Interlocked.Read(ref _boardSetupGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                var found = BoardDetector.Detect(frame.Width, frame.Height, frame.Stride, frame.Bgra);
                if (generation != Interlocked.Read(ref _boardSetupGeneration) ||
                    !Volatile.Read(ref _boardSetupActive)) return;
                if (found is not null)
                    Volatile.Write(ref _latestBoardPreview,
                        new BoardPreviewState(found, frame.Width, frame.Height, frame.Timestamp));
                _visionError = null;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closing && _frozenFrame is null) CameraCanvas.Invalidate();
                });
            }
            catch (Exception ex)
            {
                _visionError = "Board edge detection failed: " + ex.Message;
            }
            finally { Interlocked.Exchange(ref _detecting, 0); }
        });
    }

    private void UpdateBoardSetupStatus()
    {
        if (!Volatile.Read(ref _boardSetupActive)) return;
        if (!_camera.IsRunning)
        {
            BoardSetupStatusText.Text = "Grid is showing. Start the selected webcam to detect the white board.";
            return;
        }
        var state = Volatile.Read(ref _latestBoardPreview);
        BoardSetupStatusText.Text = state is not null &&
            DateTimeOffset.UtcNow - state.Timestamp < TimeSpan.FromMilliseconds(700)
            ? "White board found. Orange brackets in the camera preview show its four detected corners."
            : "Looking for all four white-board edges. Keep the whole board inside the camera view.";
    }

    private void DrawBoardPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect)
    {
        if (!Volatile.Read(ref _boardSetupActive)) return;
        var state = Volatile.Read(ref _latestBoardPreview);
        if (state is null || state.Width != frame.Width || state.Height != frame.Height ||
            DateTimeOffset.UtcNow - state.Timestamp > TimeSpan.FromMilliseconds(700))
        {
            _animatedBoardCorners = null;
            return;
        }

        Vector2 View(PixelPoint point) =>
            new((float)(rect.X + point.X / frame.Width * rect.Width),
                (float)(rect.Y + point.Y / frame.Height * rect.Height));
        var target = state.Detection.Corners.Select(View).ToArray();
        if (target.Length != 4) return;
        var now = Stopwatch.GetTimestamp();
        if (_animatedBoardCorners is null)
        {
            var middle = (target[0] + target[1] + target[2] + target[3]) / 4;
            _animatedBoardCorners = [middle, middle, middle, middle];
            _boardAnimationStarted = now;
            _boardAnimationUpdated = now;
        }
        var elapsed = Stopwatch.GetElapsedTime(_boardAnimationUpdated, now).TotalSeconds;
        var fraction = (float)(1 - Math.Exp(-Math.Min(elapsed, 0.1) / 0.12));
        for (var i = 0; i < 4; i++)
            _animatedBoardCorners[i] = Vector2.Lerp(_animatedBoardCorners[i], target[i], fraction);
        _boardAnimationUpdated = now;

        var arrival = (float)Math.Clamp(Stopwatch.GetElapsedTime(_boardAnimationStarted, now).TotalSeconds / 0.5, 0, 1);
        var arm = Math.Clamp((float)Math.Min(rect.Width, rect.Height) * 0.055f, 15f, 44f) * arrival;
        var orange = Color.FromArgb(255, 255, 131, 31);
        var edge = Color.FromArgb(140, 255, 131, 31);
        for (var i = 0; i < 4; i++)
        {
            var corner = _animatedBoardCorners[i];
            var next = _animatedBoardCorners[(i + 1) % 4];
            var previous = _animatedBoardCorners[(i + 3) % 4];
            ds.DrawLine(corner, next, edge, 2);
            var towardNext = Vector2.Normalize(next - corner);
            var towardPrevious = Vector2.Normalize(previous - corner);
            if (!float.IsFinite(towardNext.X) || !float.IsFinite(towardPrevious.X)) continue;
            ds.DrawLine(corner, corner + towardNext * arm, orange, 5);
            ds.DrawLine(corner, corner + towardPrevious * arm, orange, 5);
        }
    }
}
