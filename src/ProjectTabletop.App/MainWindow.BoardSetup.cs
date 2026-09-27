using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
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
    private int _boardSetupPhase;
    private long _boardPhaseStartedTick;
    private CameraFrame? _lastAnalyzedBoardFrame;
    private CameraFrame? _whiteBoardReference;
    private BoardDetection? _physicalBoard;
    private BoardDetection? _projectorField;
    private BoardDetection? _candidateBoard;
    private BoardDetection? _candidateField;
    private int _stableWhiteObservations;
    private int _spotIndex;
    private readonly List<PixelPoint> _spotCameraPoints = [];
    private string? _boardSetupDiagnostic;
    private double? _boardRegistrationError;
    private float? _boardGridInset;

    private enum BoardSetupPhase { Inactive, Switching, ScanWhite, MeasureSpots, GridReady, Failed }

    private sealed record BoardPreviewState(BoardDetection Detection, int Width, int Height,
                                            DateTimeOffset Timestamp);

    private async void BoardSetup_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _boardSetupActive))
        {
            StopBoardSetup();
            return;
        }

        await StartBoardSetupAsync();
    }

    internal async Task StartBoardSetupAsync()
    {
        if (Volatile.Read(ref _boardSetupActive)) return;

        if (SelectedDisplay is null)
        {
            BoardSetupStatusText.Text = "Select the projector display first.";
            return;
        }

        if (_output is null || !_output.IsFullScreen || _outputDisplayId != SelectedDisplay.Id)
            OpenOutput_Click(this, new RoutedEventArgs());
        if (_output is null || !_output.IsFullScreen || _outputDisplayId != SelectedDisplay.Id)
        {
            BoardSetupStatusText.Text = "Open the selected projector display in full screen first.";
            return;
        }

        Volatile.Write(ref _boardSetupActive, true);
        ClearBoardPreview();
        Volatile.Write(ref _latestDetections, Array.Empty<PieceDetection>());
        _scene.SetDetections(Array.Empty<PieceDetection>(), DateTimeOffset.MinValue);
        BoardSetupButton.Content = "End board setup";
        RescanBoardButton.IsEnabled = true;
        if (SelectedCamera is not null && (!_camera.IsRunning ||
            _camera.ActiveDeviceId != SelectedCamera.Device.Id))
            await StartSelectedCameraAsync();
        if (!Volatile.Read(ref _boardSetupActive) || _closing) return;
        BoardSetupStatusText.Text = _camera.IsRunning
            ? "White illumination is on. Finding the physical cardboard and projector field."
            : "White illumination is on. Select and start a webcam to find the cardboard.";
        SetStatus("Board scan is active. Keep the cardboard still inside the white projector field.");
    }

    private void EndBoardSetup()
    {
        Volatile.Write(ref _boardSetupActive, false);
        ClearBoardPreview();
        _scene.SetBoardSetup(false);
        BoardSetupButton.Content = "Start board setup";
        RescanBoardButton.IsEnabled = false;
        BoardSetupStatusText.Text = "Board setup stopped.";
    }

    internal void StopBoardSetup()
    {
        if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
    }

    internal void ShowBlackOutputForControl()
    {
        StopBoardSetup();
        _scene.SetBlackOutput(true);
        SetStatus("Projector output is black for an ambient webcam capture. Start board scan to restore white illumination.");
    }

    private void RescanBoard_Click(object sender, RoutedEventArgs e) => RescanBoardSetup();

    internal void RescanBoardSetup()
    {
        if (!Volatile.Read(ref _boardSetupActive)) return;
        ClearBoardPreview();
        BoardSetupStatusText.Text = "White illumination is on. Finding the physical cardboard again.";
    }

    internal string BoardSetupControlStatus => BoardSetupStatusText.Text;

    private void ClearBoardPreview()
    {
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        Interlocked.Increment(ref _boardSetupGeneration);
        Volatile.Write(ref _latestBoardPreview, null);
        _animatedBoardCorners = null;
        _boardAnimationStarted = 0;
        _boardAnimationUpdated = 0;
        Interlocked.Exchange(ref _lastAnalyzedBoardFrame, null);
        _whiteBoardReference = null;
        _physicalBoard = null;
        _projectorField = null;
        _candidateBoard = null;
        _candidateField = null;
        _stableWhiteObservations = 0;
        _spotIndex = 0;
        _spotCameraPoints.Clear();
        _boardSetupDiagnostic = null;
        _boardRegistrationError = null;
        _boardGridInset = null;
        Interlocked.Exchange(ref _lastBoardDetectTick, 0);
        if (Volatile.Read(ref _boardSetupActive))
        {
            _scene.SetBoardSetup(true);
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.ScanWhite);
            Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
        }
        else
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Inactive);
        if (!_closing) CameraCanvas.Invalidate();
    }

    private void QueueBoardDetection(CameraFrame frame, long tick)
    {
        var phase = (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase);
        if (phase is not (BoardSetupPhase.ScanWhite or BoardSetupPhase.MeasureSpots)) return;
        var phaseStarted = Interlocked.Read(ref _boardPhaseStartedTick);
        // Let the projector and Android Webcam exposure settle after each scene change.
        if (phaseStarted == 0 || Stopwatch.GetElapsedTime(phaseStarted, tick) < TimeSpan.FromMilliseconds(900))
            return;
        var previous = Interlocked.Read(ref _lastBoardDetectTick);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, tick) < TimeSpan.FromMilliseconds(200))
            return;
        if (Interlocked.CompareExchange(ref _detecting, 1, 0) != 0) return;
        Interlocked.Exchange(ref _lastBoardDetectTick, tick);
        var priorFrame = Interlocked.Exchange(ref _lastAnalyzedBoardFrame, frame);
        if (priorFrame is not null && priorFrame.Width == frame.Width && priorFrame.Height == frame.Height &&
            priorFrame.Stride == frame.Stride && priorFrame.Bgra.AsSpan().SequenceEqual(frame.Bgra))
        {
            Interlocked.Exchange(ref _detecting, 0);
            return;
        }
        var generation = Interlocked.Read(ref _boardSetupGeneration);
        var spotIndex = Volatile.Read(ref _spotIndex);
        var whiteReference = Volatile.Read(ref _whiteBoardReference);
        _ = Task.Run(() =>
        {
            try
            {
                if (phase == BoardSetupPhase.ScanWhite)
                {
                    var field = BoardDetector.DetectProjectedField(frame.Width, frame.Height, frame.Stride,
                        frame.Bgra);
                    var board = BoardDetector.DetectUniformIllumination(frame.Width, frame.Height, frame.Stride,
                        frame.Bgra);
                    DispatcherQueue.TryEnqueue(() => ProcessWhiteScan(frame, board, field, generation));
                }
                else if (whiteReference is not null && whiteReference.Width == frame.Width &&
                         whiteReference.Height == frame.Height && whiteReference.Stride == frame.Stride)
                {
                    var spot = BoardDetector.DetectDarkCalibrationSpot(frame.Width, frame.Height,
                        frame.Stride, whiteReference.Bgra, frame.Bgra);
                    DispatcherQueue.TryEnqueue(() => ProcessCalibrationSpot(frame, spot, spotIndex, generation));
                }
                _visionError = null;
            }
            catch (Exception ex)
            {
                _visionError = "Cardboard scan failed: " + ex.Message;
            }
            finally { Interlocked.Exchange(ref _detecting, 0); }
        });
    }

    private void ProcessWhiteScan(CameraFrame frame, BoardDetection? board,
                                  BoardDetection? field, long generation)
    {
        if (_closing || generation != Interlocked.Read(ref _boardSetupGeneration) ||
            (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) != BoardSetupPhase.ScanWhite) return;
        if (field is null)
        {
            _stableWhiteObservations = 0;
            _boardSetupDiagnostic = "Looking for the full white projector field. Keep it inside the camera view.";
            return;
        }
        if (board is null)
        {
            _stableWhiteObservations = 0;
            _boardSetupDiagnostic = "Projector field found. Looking for four separate cardboard edges.";
            return;
        }
        if (!BoardInsideField(board, field))
        {
            _stableWhiteObservations = 0;
            _boardSetupDiagnostic = "Cardboard extends beyond the white projector field. Move it inside the light.";
            return;
        }

        Volatile.Write(ref _latestBoardPreview,
            new BoardPreviewState(board, frame.Width, frame.Height, frame.Timestamp));
        if (_candidateBoard is not null && _candidateField is not null &&
            CornersClose(_candidateBoard, board, frame.Width, frame.Height) &&
            CornersClose(_candidateField, field, frame.Width, frame.Height))
            _stableWhiteObservations++;
        else
            _stableWhiteObservations = 1;
        _candidateBoard = board;
        _candidateField = field;
        _boardSetupDiagnostic = $"Cardboard edges found. Checking stability ({_stableWhiteObservations}/3).";
        if (_stableWhiteObservations < 3)
        {
            if (_frozenFrame is null) CameraCanvas.Invalidate();
            return;
        }

        _physicalBoard = board;
        _projectorField = field;
        Volatile.Write(ref _whiteBoardReference, frame);
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        _scene.ShowBoardCalibrationSpot(0);
        Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _lastBoardDetectTick, 0);
        Interlocked.Exchange(ref _lastAnalyzedBoardFrame, null);
        Volatile.Write(ref _spotIndex, 0);
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.MeasureSpots);
        _boardSetupDiagnostic = null;
        BoardSetupStatusText.Text = "Cardboard found. Aligning projector, spot 1 of 5. Keep the cardboard still.";
        if (_frozenFrame is null) CameraCanvas.Invalidate();
    }

    private void ProcessCalibrationSpot(CameraFrame frame, CalibrationSpotDetection? spot,
                                        int spotIndex, long generation)
    {
        if (_closing || generation != Interlocked.Read(ref _boardSetupGeneration) ||
            (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) != BoardSetupPhase.MeasureSpots ||
            spotIndex != Volatile.Read(ref _spotIndex)) return;
        if (spot is null)
        {
            _boardSetupDiagnostic = $"Alignment spot {spotIndex + 1} is not clear in the webcam view. " +
                "Keep the cardboard still or press Scan again.";
            return;
        }
        if (_physicalBoard is null || !InsideQuad(spot.Center, _physicalBoard.Corners))
        {
            _boardSetupDiagnostic = "The alignment spot is outside the cardboard. " +
                "Center the cardboard in the white light and press Scan again.";
            return;
        }
        if (_spotCameraPoints.Any(point => Distance(point, spot.Center) <
                Math.Min(frame.Width, frame.Height) * 0.08))
        {
            _boardSetupDiagnostic = "Waiting for the next alignment spot to appear in a fresh camera frame.";
            return;
        }
        _spotCameraPoints.Add(spot.Center);
        _boardSetupDiagnostic = null;
        if (spotIndex + 1 < SceneCompositor.BoardCalibrationSpotCount)
        {
            var next = spotIndex + 1;
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
            _scene.ShowBoardCalibrationSpot(next);
            Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _lastBoardDetectTick, 0);
            Interlocked.Exchange(ref _lastAnalyzedBoardFrame, null);
            Volatile.Write(ref _spotIndex, next);
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.MeasureSpots);
            BoardSetupStatusText.Text = $"Cardboard found. Aligning projector, spot {next + 1} of 5. " +
                "Keep the cardboard still.";
            return;
        }

        try
        {
            Point2[] cameraPoints = _spotCameraPoints.Take(4)
                .Select(point => new Point2(point.X, point.Y)).ToArray();
            Point2[] projectorPoints = Enumerable.Range(0, 4)
                .Select(index => SceneCompositor.BoardCalibrationSpotPosition(index))
                .Select(point => new Point2(point.X, point.Y)).ToArray();
            var map = Homography.FromFourPoints(cameraPoints, projectorPoints);
            var check = map.Transform(new Point2(spot.Center.X, spot.Center.Y));
            var expected = SceneCompositor.BoardCalibrationSpotPosition(4);
            var error = Math.Sqrt(Math.Pow(check.X - expected.X, 2) +
                                  Math.Pow(check.Y - expected.Y, 2));
            _boardRegistrationError = error;
            if (error > 0.015)
                throw new InvalidOperationException(
                    $"The center check error is {error:F4} normalized, above the 0.015 limit.");

            var board = _physicalBoard ?? throw new InvalidOperationException("Board edges were lost.");
            Vector2[] corners = board.Corners.Select(point =>
            {
                var projected = map.Transform(new Point2(point.X, point.Y));
                return new Vector2((float)projected.X, (float)projected.Y);
            }).ToArray();
            _boardGridInset = _scene.SetDetectedBoardGrid(corners);
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.GridReady);
            _boardSetupDiagnostic = null;
            BoardSetupStatusText.Text = "Physical cardboard found and aligned. Grid is projected inside " +
                $"its detected edges (center check error {error:F4} normalized; " +
                $"grid inset {_boardGridInset:P1}). " +
                "Press Scan again if the cardboard moves.";
            SetStatus("Cardboard scan complete. Grid follows the detected physical board corners.");
        }
        catch (Exception ex)
        {
            _scene.ShowBoardCalibrationSpot(-1);
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Failed);
            _boardSetupDiagnostic = "Projector alignment was rejected: " + ex.Message +
                " Press Scan again after checking the cardboard and webcam.";
            BoardSetupStatusText.Text = _boardSetupDiagnostic;
        }
    }

    private static bool BoardInsideField(BoardDetection board, BoardDetection field)
    {
        if (board.Corners.Length != 4 || field.Corners.Length != 4) return false;
        var boardArea = QuadArea(board.Corners);
        var fieldArea = QuadArea(field.Corners);
        if (fieldArea <= 0 || boardArea < fieldArea * 0.16 || boardArea > fieldArea * 0.84)
            return false;
        try
        {
            var source = field.Corners.Select(point => new Point2(point.X, point.Y)).ToArray();
            Point2[] target = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            var fieldMap = Homography.FromFourPoints(source, target);
            return board.Corners.All(point =>
            {
                var normalized = fieldMap.Transform(new Point2(point.X, point.Y));
                return normalized.X >= -0.025 && normalized.X <= 1.025 &&
                       normalized.Y >= -0.025 && normalized.Y <= 1.025;
            });
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static double QuadArea(IReadOnlyList<PixelPoint> points)
    {
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += points[index].X * next.Y - next.X * points[index].Y;
        }
        return Math.Abs(area) / 2;
    }

    private static bool InsideQuad(PixelPoint point, IReadOnlyList<PixelPoint> corners)
    {
        if (corners.Count != 4) return false;
        var sign = 0;
        for (var index = 0; index < 4; index++)
        {
            var first = corners[index];
            var second = corners[(index + 1) % 4];
            var cross = (second.X - first.X) * (point.Y - first.Y) -
                        (second.Y - first.Y) * (point.X - first.X);
            if (Math.Abs(cross) < 1e-7) continue;
            var current = Math.Sign(cross);
            if (sign != 0 && sign != current) return false;
            sign = current;
        }
        return true;
    }

    private static bool CornersClose(BoardDetection first, BoardDetection second, int width, int height)
    {
        if (first.Corners.Length != 4 || second.Corners.Length != 4) return false;
        var limit = Math.Sqrt(width * (double)width + height * (double)height) * 0.015;
        return Enumerable.Range(0, 4).All(index =>
            Distance(first.Corners[index], second.Corners[index]) <= limit);
    }

    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private void UpdateBoardSetupStatus()
    {
        if (!Volatile.Read(ref _boardSetupActive)) return;
        if (!_camera.IsRunning)
        {
            BoardSetupStatusText.Text = "White illumination is on. Start the webcam to find the cardboard.";
            return;
        }
        var phase = (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase);
        BoardSetupStatusText.Text = phase switch
        {
            BoardSetupPhase.ScanWhite => _boardSetupDiagnostic ??
                "White illumination is on. Looking for four physical cardboard edges inside the projector field.",
            BoardSetupPhase.MeasureSpots => _boardSetupDiagnostic ??
                $"Cardboard found. Aligning projector, spot {Volatile.Read(ref _spotIndex) + 1} of 5. " +
                "Keep the cardboard still.",
            BoardSetupPhase.GridReady => "Physical cardboard found and aligned. Grid is projected inside " +
                $"its detected edges (center check error {_boardRegistrationError:F4} normalized; " +
                $"grid inset {_boardGridInset:P1}). " +
                "Press Scan again if the cardboard moves.",
            BoardSetupPhase.Failed => _boardSetupDiagnostic ?? "Board scan stopped. Press Scan again.",
            _ => BoardSetupStatusText.Text
        };
    }

    private void DrawBoardPreview(CanvasDrawingSession ds, CameraFrame frame, Rect rect)
    {
        if (!Volatile.Read(ref _boardSetupActive)) return;
        var state = Volatile.Read(ref _latestBoardPreview);
        var phase = (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase);
        if (state is null || state.Width != frame.Width || state.Height != frame.Height ||
            (phase == BoardSetupPhase.ScanWhite &&
                DateTimeOffset.UtcNow - state.Timestamp > TimeSpan.FromMilliseconds(700)))
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
