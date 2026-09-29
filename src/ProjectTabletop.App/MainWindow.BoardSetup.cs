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
    private Task? _boardSetupStartTask;
    private long _boardSetupRequestVersion;
    private long _boardSetupStartRequest;
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
    private BoardDetection? _ambientBoard;
    private BoardDetection? _projectorField;
    private BoardDetection? _candidateAmbientBoard;
    private readonly List<BoardDetection> _ambientObservations = [];
    private readonly List<bool> _ambientRecoveredObservations = [];
    private bool _ambientRecoveredFromPrior;
    private TrustedBoardPrior? _trustedBoardPrior;
    private BoardDetection? _candidateBoard;
    private BoardDetection? _candidateField;
    private int _stableAmbientObservations;
    private int _stableWhiteObservations;
    private bool _whiteOnlyFallback;
    private bool _whiteEdgeUnavailable;
    private bool _candidateWhiteBoardPresent;
    private int _spotIndex;
    private readonly List<PixelPoint> _spotCameraPoints = [];
    private string? _boardSetupDiagnostic;
    private double? _boardRegistrationError;
    private double? _boardCrossCheckErrorPixels;
    private int _boardCornersAtLightEdge;
    private float? _boardGridInset;
    private string? _boardProjectionWarning;

    private const int BoardSetupSpotCount = BoardRegistration.SpotCount;
    private const int CenterCheckSpotIndex = BoardSetupSpotCount - 1;

    private enum BoardSetupPhase { Inactive, Switching, ScanAmbient, ScanWhite, MeasureSpots, GridReady, Failed }

    private sealed record BoardPreviewState(BoardDetection Detection, int Width, int Height,
                                            DateTimeOffset Timestamp);

    private sealed record TrustedBoardPrior(BoardDetection Detection, int Width, int Height,
                                            string CameraDeviceId, string DisplayId);

    private async void BoardSetup_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _boardSetupActive) ||
            (_boardSetupStartTask is { IsCompleted: false } &&
             _boardSetupStartRequest == _boardSetupRequestVersion))
        {
            StopBoardSetup();
            return;
        }

        await StartBoardSetupAsync();
    }

    internal Task StartBoardSetupAsync()
    {
        if (Volatile.Read(ref _boardSetupActive)) return Task.CompletedTask;
        if (_boardSetupStartTask is { IsCompleted: false } &&
            _boardSetupStartRequest == _boardSetupRequestVersion) return _boardSetupStartTask;
        return _boardSetupStartTask = StartBoardSetupCoreAsync();
    }

    private async Task StartBoardSetupCoreAsync()
    {
        var request = Interlocked.Increment(ref _boardSetupRequestVersion);
        _boardSetupStartRequest = request;
        BoardSetupButton.Content = "Cancel board setup";
        try
        {
            // Await the verified native fullscreen presenter before collecting
            // any camera/projector correspondence, including the ambient frame.
            if (!await OpenProjectionOutputAsync())
            {
                if (request == _boardSetupRequestVersion && !_closing)
                    BoardSetupStatusText.Text = StatusText.Text;
                return;
            }
            if (request != _boardSetupRequestVersion || _closing) return;
            RequireProjectionOutput();

            Volatile.Write(ref _boardSetupActive, true);
            ClearBoardPreview();
            Volatile.Write(ref _latestDetections, Array.Empty<PieceDetection>());
            _scene.SetDetections(Array.Empty<PieceDetection>(), DateTimeOffset.MinValue);
            BoardSetupButton.Content = "End board setup";
            RescanBoardButton.IsEnabled = true;
            if (SelectedCamera is not null && (!_camera.IsRunning ||
                _camera.ActiveDeviceId != SelectedCamera.Device.Id))
                await StartSelectedCameraAsync();
            if (request != _boardSetupRequestVersion || !Volatile.Read(ref _boardSetupActive) || _closing) return;
            BoardSetupStatusText.Text = _camera.IsRunning
                ? "Projector is black. Finding the cardboard by ambient light before the white scan."
                : "Projector is black. Select and start a webcam to find the cardboard.";
            SetStatus("Board scan is active. Keep the cardboard still while black and white views are checked.");
        }
        catch (Exception ex)
        {
            if (request != _boardSetupRequestVersion || _closing) return;
            EndBoardSetup();
            BoardSetupStatusText.Text = "Cannot start board setup: " + ex.Message;
            SetStatus(BoardSetupStatusText.Text);
        }
        finally
        {
            if (!_closing && !Volatile.Read(ref _boardSetupActive) &&
                request == _boardSetupRequestVersion)
                BoardSetupButton.Content = "Start board setup";
        }
    }

    private void EndBoardSetup()
    {
        Interlocked.Increment(ref _boardSetupRequestVersion);
        Volatile.Write(ref _boardSetupActive, false);
        Volatile.Write(ref _trustedBoardPrior, null);
        ClearBoardPreview();
        _scene.SetBoardSetup(false);
        BoardSetupButton.Content = "Start board setup";
        RescanBoardButton.IsEnabled = false;
        BoardSetupStatusText.Text = _scene.HasBoardMediaClip
            ? "Board scan complete. The selected app or media is projected inside the detected cardboard."
            : "Board setup stopped. Output stays black until a successful board scan.";
        SetStatus(BoardSetupStatusText.Text);
    }

    internal void StopBoardSetup()
    {
        if (Volatile.Read(ref _boardSetupActive) || _boardSetupStartTask is { IsCompleted: false })
            EndBoardSetup();
    }

    internal void ShowBlackOutputForControl()
    {
        RequireProjectionOutput();
        StopBoardSetup();
        ClearHandTracking();
        _scene.ClearBoardMediaClip();
        _scene.SetBlackOutput(true);
        SetStatus("Projector output is black for an ambient webcam capture. Start board scan to restore white illumination.");
    }

    private async void RescanBoard_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _boardSetupActive)) await RescanBoardSetupAsync();
        else await StartBoardSetupAsync();
    }

    internal async Task RescanBoardSetupAsync()
    {
        if (!Volatile.Read(ref _boardSetupActive)) return;
        var request = _boardSetupRequestVersion;
        try
        {
            if (!await OpenProjectionOutputAsync() || request != _boardSetupRequestVersion ||
                !Volatile.Read(ref _boardSetupActive) || _closing) return;
            RequireProjectionOutput();
        }
        catch (Exception ex)
        {
            _output?.Close();
            BoardSetupStatusText.Text = "Cannot rescan: " + ex.Message;
            SetStatus(BoardSetupStatusText.Text);
            return;
        }
        ClearBoardPreview();
        BoardSetupStatusText.Text = "Projector is black. Finding the physical cardboard again.";
    }

    internal string BoardSetupControlStatus => BoardSetupStatusText.Text;

    private void ClearBoardPreview()
    {
        ClearHandTracking();
        if (_trustedBoardPrior is { } trusted &&
            (SelectedCamera?.Device.Id != trusted.CameraDeviceId ||
             SelectedDisplay?.Id != trusted.DisplayId))
            Volatile.Write(ref _trustedBoardPrior, null);
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        Interlocked.Increment(ref _boardSetupGeneration);
        Volatile.Write(ref _latestBoardPreview, null);
        _animatedBoardCorners = null;
        _boardAnimationStarted = 0;
        _boardAnimationUpdated = 0;
        Interlocked.Exchange(ref _lastAnalyzedBoardFrame, null);
        _whiteBoardReference = null;
        _physicalBoard = null;
        _ambientBoard = null;
        _projectorField = null;
        _candidateAmbientBoard = null;
        _ambientObservations.Clear();
        _ambientRecoveredObservations.Clear();
        _ambientRecoveredFromPrior = false;
        _candidateBoard = null;
        _candidateField = null;
        _stableAmbientObservations = 0;
        _stableWhiteObservations = 0;
        _whiteOnlyFallback = false;
        _whiteEdgeUnavailable = false;
        _candidateWhiteBoardPresent = false;
        _spotIndex = 0;
        _spotCameraPoints.Clear();
        _boardSetupDiagnostic = null;
        _boardRegistrationError = null;
        _boardCrossCheckErrorPixels = null;
        _boardCornersAtLightEdge = 0;
        _boardGridInset = null;
        _boardProjectionWarning = null;
        Interlocked.Exchange(ref _lastBoardDetectTick, 0);
        if (Volatile.Read(ref _boardSetupActive))
        {
            _scene.SetBoardSetup(true);
            _scene.SetBlackOutput(true);
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.ScanAmbient);
            Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
        }
        else
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Inactive);
        if (!_closing) CameraCanvas.Invalidate();
    }

    private void QueueBoardDetection(CameraFrame frame, long tick)
    {
        if (_outputOperation.CurrentCount == 0) return;
        var phase = (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase);
        if (phase is not (BoardSetupPhase.ScanAmbient or BoardSetupPhase.ScanWhite or
                          BoardSetupPhase.MeasureSpots)) return;
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
                if (phase == BoardSetupPhase.ScanAmbient)
                {
                    var board = BoardDetector.DetectAmbientBoard(frame.Width, frame.Height, frame.Stride,
                        frame.Bgra);
                    bool recovered = false;
                    if (board is null && CompatibleTrustedBoardPrior(frame) is { } trusted)
                    {
                        board = BoardDetector.RecoverAmbientBoardWithPrior(frame.Width, frame.Height,
                            frame.Stride, frame.Bgra, trusted.Detection);
                        recovered = board is not null;
                    }
                    DispatcherQueue.TryEnqueue(() => ProcessAmbientScan(frame, board, recovered, generation));
                }
                else if (phase == BoardSetupPhase.ScanWhite)
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

    private TrustedBoardPrior? CompatibleTrustedBoardPrior(CameraFrame frame)
    {
        var trusted = Volatile.Read(ref _trustedBoardPrior);
        if (trusted is null) return null;
        if (trusted.Width == frame.Width && trusted.Height == frame.Height &&
            trusted.CameraDeviceId == _camera.ActiveDeviceId &&
            trusted.DisplayId == _outputDisplayId)
            return trusted;
        Interlocked.CompareExchange(ref _trustedBoardPrior, null, trusted);
        return null;
    }

    private void ProcessAmbientScan(CameraFrame frame, BoardDetection? board,
                                    bool recovered, long generation)
    {
        if (_closing || generation != Interlocked.Read(ref _boardSetupGeneration) ||
            (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) != BoardSetupPhase.ScanAmbient) return;
        if (board is null)
        {
            _candidateAmbientBoard = null;
            _ambientObservations.Clear();
            _ambientRecoveredObservations.Clear();
            _stableAmbientObservations = 0;
            _boardSetupDiagnostic = "Projector is black. Looking for the cardboard by room light.";
            return;
        }

        if (_candidateAmbientBoard is null ||
            !PhysicalCornersClose(_candidateAmbientBoard, board, frame.Width, frame.Height))
        {
            _ambientObservations.Clear();
            _ambientRecoveredObservations.Clear();
        }
        _ambientObservations.Add(board);
        _ambientRecoveredObservations.Add(recovered);
        _candidateAmbientBoard = board;
        _stableAmbientObservations = _ambientObservations.Count;
        Volatile.Write(ref _latestBoardPreview,
            new BoardPreviewState(board, frame.Width, frame.Height, frame.Timestamp));
        _boardSetupDiagnostic = (recovered
            ? "Three ambient cardboard edges recovered with the last trusted scan. "
            : "Four ambient cardboard edges found. ") + "Checking stability " +
            $"({_stableAmbientObservations}/3).";
        if (_frozenFrame is null) CameraCanvas.Invalidate();
        if (_stableAmbientObservations < 3) return;

        var corners = Enumerable.Range(0, 4).Select(index => new PixelPoint(
            _ambientObservations.Average(observation => observation.Corners[index].X),
            _ambientObservations.Average(observation => observation.Corners[index].Y))).ToArray();
        _ambientBoard = new BoardDetection(corners,
            _ambientObservations.Average(observation => observation.Confidence));
        _ambientRecoveredFromPrior = _ambientRecoveredObservations.Any(value => value);
        BeginWhiteScan(whiteOnlyFallback: false);
    }

    private void BeginWhiteScan(bool whiteOnlyFallback)
    {
        if (!Volatile.Read(ref _boardSetupActive)) return;
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        _whiteOnlyFallback = whiteOnlyFallback;
        _scene.SetBlackOutput(false);
        _candidateBoard = null;
        _candidateField = null;
        _stableWhiteObservations = 0;
        _whiteEdgeUnavailable = false;
        _candidateWhiteBoardPresent = false;
        Interlocked.Exchange(ref _lastBoardDetectTick, 0);
        Interlocked.Exchange(ref _lastAnalyzedBoardFrame, null);
        Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.ScanWhite);
        _boardSetupDiagnostic = whiteOnlyFallback
            ? "Ambient cardboard edges were not found. Continuing with the white-only scan."
            : (_ambientRecoveredFromPrior
                ? "Three ambient edges inferred the fourth from the last trusted scan. "
                : "Four ambient cardboard edges saved. ") +
              "White light is checking the same edges and projector field.";
        BoardSetupStatusText.Text = _boardSetupDiagnostic;
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
        if (board is null && _ambientBoard is null)
        {
            _stableWhiteObservations = 0;
            _boardSetupDiagnostic = "Projector field found. Looking for four separate cardboard edges.";
            return;
        }
        var candidateBoard = board ?? _ambientBoard!;
        if (!BoardInsideField(candidateBoard, field))
        {
            _stableWhiteObservations = 0;
            _boardSetupDiagnostic = "Cardboard extends beyond the white projector field. Move it inside the light.";
            return;
        }

        Volatile.Write(ref _latestBoardPreview,
            new BoardPreviewState(candidateBoard, frame.Width, frame.Height, frame.Timestamp));
        if (_candidateBoard is not null && _candidateField is not null &&
            _candidateWhiteBoardPresent == (board is not null) &&
            CornersClose(_candidateBoard, candidateBoard, frame.Width, frame.Height) &&
            CornersClose(_candidateField, field, frame.Width, frame.Height))
            _stableWhiteObservations++;
        else
            _stableWhiteObservations = 1;
        _candidateBoard = candidateBoard;
        _candidateField = field;
        _candidateWhiteBoardPresent = board is not null;
        _boardSetupDiagnostic = board is null
            ? $"White board edge unavailable; using trusted ambient corners. " +
              $"Checking field stability ({_stableWhiteObservations}/3)."
            : $"Cardboard edges found. Checking stability ({_stableWhiteObservations}/3).";
        if (_stableWhiteObservations < 3)
        {
            if (_frozenFrame is null) CameraCanvas.Invalidate();
            return;
        }

        if (_ambientBoard is not null)
        {
            if (board is not null)
            {
                // The physical corners come from the ambient scan. Where the
                // cardboard nearly fills the light, its white-lit edge merges
                // with the light's edge, so those corners are compared only
                // along that edge (movement there is still caught).
                double tolerance = PhysicalCornerTolerance(frame.Width, frame.Height);
                _boardCrossCheckErrorPixels = BoardDetector.CrossCheckError(_ambientBoard, board, field,
                    tolerance * 2, out _boardCornersAtLightEdge);
                if (_boardCrossCheckErrorPixels > tolerance)
                {
                    Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Failed);
                    _boardSetupDiagnostic = $"Black and white cardboard edges differ by " +
                        $"{_boardCrossCheckErrorPixels:F1} camera pixels. The board or camera may have moved. " +
                        "Press Scan again.";
                    BoardSetupStatusText.Text = _boardSetupDiagnostic;
                    return;
                }
            }
            else _whiteEdgeUnavailable = true;
            if (!BoardInsideField(_ambientBoard, field))
            {
                Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Failed);
                _boardSetupDiagnostic = "The ambient cardboard edges extend beyond the white projector field. " +
                    "Move the cardboard inside the light and press Scan again.";
                BoardSetupStatusText.Text = _boardSetupDiagnostic;
                return;
            }
        }

        _physicalBoard = _ambientBoard ?? board ??
            throw new InvalidOperationException("The cardboard edges were lost.");
        _projectorField = field;
        Volatile.Write(ref _latestBoardPreview,
            new BoardPreviewState(_physicalBoard, frame.Width, frame.Height, frame.Timestamp));
        Volatile.Write(ref _whiteBoardReference, frame);
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        _scene.ShowBoardCalibrationSpot(0);
        Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _lastBoardDetectTick, 0);
        Interlocked.Exchange(ref _lastAnalyzedBoardFrame, null);
        Volatile.Write(ref _spotIndex, 0);
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.MeasureSpots);
        _boardSetupDiagnostic = null;
        BoardSetupStatusText.Text = _whiteOnlyFallback
            ? "White-only board scan complete. Aligning projector, spot 1 of 5. Keep the cardboard still."
            : _whiteEdgeUnavailable
            ? "White edge unavailable; using trusted ambient cardboard corners. " +
              "Aligning projector, spot 1 of 5. Keep the cardboard still."
            : $"{(_ambientRecoveredFromPrior ? "Three-edge recovery" : "Four-edge ambient scan")} " +
              $"and white board edges agree within {_boardCrossCheckErrorPixels:F1} px" +
              (_boardCornersAtLightEdge > 0
                  ? $" ({_boardCornersAtLightEdge} corner{(_boardCornersAtLightEdge == 1 ? "" : "s")} at the light's edge " +
                    "checked from the black scan). "
                  : ". ") +
              "Aligning projector, spot 1 of 5. Keep the cardboard still.";
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
            _boardSetupDiagnostic = $"{RegistrationStage(spotIndex)} spot {spotIndex + 1} of 5 " +
                "is not clear in the webcam view. " +
                "Keep the cardboard still or press Scan again.";
            return;
        }
        if (_physicalBoard is null || !InsideQuad(spot.Center, _physicalBoard.Corners))
        {
            _boardSetupDiagnostic = "The alignment spot is outside the cardboard. " +
                "Center the cardboard in the white light and press Scan again.";
            return;
        }
        if (_spotCameraPoints.Count > 0 &&
            Distance(_spotCameraPoints[^1], spot.Center) <
                Math.Min(frame.Width, frame.Height) * 0.08)
        {
            _boardSetupDiagnostic = "Waiting for the next alignment spot to appear in a fresh camera frame.";
            return;
        }
        _spotCameraPoints.Add(spot.Center);
        _boardSetupDiagnostic = null;
        try
        {
            if (spotIndex + 1 < BoardSetupSpotCount)
            {
                ShowNextRegistrationSpot(spotIndex + 1);
                return;
            }

            Point2[] cameraPoints = _spotCameraPoints
                .Select(point => new Point2(point.X, point.Y)).ToArray();
            var map = BoardRegistration.FitAndValidate(cameraPoints, out var error);
            _boardRegistrationError = error;

            var board = _physicalBoard ?? throw new InvalidOperationException("Board edges were lost.");
            Vector2[] corners = board.Corners.Select(point =>
            {
                var projected = map.Transform(new Point2(point.X, point.Y));
                return new Vector2((float)projected.X, (float)projected.Y);
            }).ToArray();
            _boardProjectionWarning = ProjectionBoundaryWarning(corners);
            _boardGridInset = _scene.CompleteBoardSetup(corners, map);
            RememberBoardFacing();
            Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.GridReady);
            if (_ambientBoard is not null && !_ambientRecoveredFromPrior &&
                _camera.ActiveDeviceId is { } cameraId && _outputDisplayId is { } displayId)
                Volatile.Write(ref _trustedBoardPrior,
                    new TrustedBoardPrior(_ambientBoard, frame.Width, frame.Height,
                                          cameraId, displayId));
            _boardSetupDiagnostic = null;
            BoardSetupStatusText.Text = (_whiteOnlyFallback
                ? "Physical cardboard found with white-only fallback. "
                : _whiteEdgeUnavailable
                ? "White edge unavailable; using trusted ambient cardboard corners. "
                : $"{(_ambientRecoveredFromPrior ? "Three-edge inferred" : "Four-edge ambient")} " +
                  $"and white cardboard edges agreed within {_boardCrossCheckErrorPixels:F1} px. ") +
                "Five-spot registration complete. Content is projected inside " +
                $"its detected edges (center check error {error:F4} normalized; " +
                $"grid inset {_boardGridInset:P1}). " +
                (_boardProjectionWarning ?? "Press Scan again if the cardboard moves.");
            // Keep the final clip/map and diagnostics, then reveal the selected board app.
            // EndBoardSetup clears scan history, which is still useful for a later rescan.
            Volatile.Write(ref _boardSetupActive, false);
            BoardSetupButton.Content = "Start board setup";
            RescanBoardButton.IsEnabled = true;
            UpdateBoardAppStatus();
            SetStatus(_boardProjectionWarning ??
                $"Cardboard scan complete. {_scene.CurrentBoardTitle} is ready on the board.");
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

    private void ShowNextRegistrationSpot(int index)
    {
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        _scene.ShowBoardCalibrationSpot(index);
        Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _lastBoardDetectTick, 0);
        Interlocked.Exchange(ref _lastAnalyzedBoardFrame, null);
        Volatile.Write(ref _spotIndex, index);
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.MeasureSpots);
        BoardSetupStatusText.Text = $"Cardboard found. {RegistrationStage(index)} spot " +
            $"{index + 1} of 5. Keep the cardboard still.";
    }

    private static string RegistrationStage(int spotIndex) => spotIndex switch
    {
        < CenterCheckSpotIndex => "Alignment",
        _ => "Center validation"
    };

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

    private static double PhysicalCornerTolerance(int width, int height) =>
        Math.Max(5, Math.Min(width, height) * 0.0075);

    private static bool PhysicalCornersClose(BoardDetection first, BoardDetection second,
                                             int width, int height)
    {
        if (first.Corners.Length != 4 || second.Corners.Length != 4) return false;
        var limit = PhysicalCornerTolerance(width, height);
        return Enumerable.Range(0, 4).All(index =>
            Distance(first.Corners[index], second.Corners[index]) <= limit);
    }

    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static string? ProjectionBoundaryWarning(IReadOnlyList<Vector2> corners)
    {
        // The orange stroke has thickness, so a corner very near the canvas
        // boundary can lose an entire arm even when its center is just inside.
        const float minimumMargin = 0.005f;
        string[] names = ["top-left", "top-right", "bottom-right", "bottom-left"];
        static bool NearBoundary(Vector2 point) =>
            point.X < minimumMargin || point.X > 1 - minimumMargin ||
            point.Y < minimumMargin || point.Y > 1 - minimumMargin;
        var affected = Enumerable.Range(0, 4).Where(index =>
        {
            var point = corners[index];
            // Check both orange arms as well as their joint. The top bar can
            // leave the projector even when the joint itself is still visible.
            return NearBoundary(point) ||
                   NearBoundary(Vector2.Lerp(point, corners[(index + 1) % 4], 0.07f)) ||
                   NearBoundary(Vector2.Lerp(point, corners[(index + 3) % 4], 0.07f));
        }).Select(index => names[index]).ToArray();
        return affected.Length == 0 ? null :
            "Orange bracket at " + string.Join(", ", affected) +
            " reaches the projector boundary and may be clipped. " +
            "Move the cardboard a few millimeters toward the projected center and press Scan again.";
    }

    private void UpdateBoardSetupStatus()
    {
        if (!Volatile.Read(ref _boardSetupActive)) return;
        if (_boardScanSuspendedForCameraOutage)
        {
            BoardSetupStatusText.Text = "Webcam image is stale. Projector is black until a changing frame arrives.";
            return;
        }
        if (!_camera.IsRunning)
        {
            BoardSetupStatusText.Text = "Projector is black. Start the webcam to find the cardboard.";
            return;
        }
        var phase = (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase);
        if (phase == BoardSetupPhase.ScanAmbient &&
            Volatile.Read(ref _latestCameraFrame) is { } freshFrame &&
            DateTimeOffset.UtcNow - freshFrame.Timestamp < TimeSpan.FromSeconds(1) &&
            DateTimeOffset.UtcNow - _cameraImageChangedAtUtc < TimeSpan.FromSeconds(2) &&
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _boardPhaseStartedTick)) >= TimeSpan.FromSeconds(6))
        {
            BeginWhiteScan(whiteOnlyFallback: true);
            phase = BoardSetupPhase.ScanWhite;
        }
        BoardSetupStatusText.Text = phase switch
        {
            BoardSetupPhase.ScanAmbient => _boardSetupDiagnostic ??
                "Projector is black. Looking for four physical cardboard edges by room light.",
            BoardSetupPhase.ScanWhite => _boardSetupDiagnostic ??
                (_whiteOnlyFallback
                    ? "Ambient edges unavailable; white-only scan is finding the cardboard and projector field."
                    : $"White light is cross-checking {(_ambientRecoveredFromPrior ? "three inferred" : "four")} " +
                      "ambient cardboard edges and projector field."),
            BoardSetupPhase.MeasureSpots => _boardSetupDiagnostic ??
                $"{(_whiteOnlyFallback ? "White-only scan" : _whiteEdgeUnavailable
                    ? "White edge unavailable; using trusted ambient corners"
                    : $"Black/white edges agree within {_boardCrossCheckErrorPixels:F1} px")}. " +
                $"{RegistrationStage(Volatile.Read(ref _spotIndex))} spot " +
                $"{Volatile.Read(ref _spotIndex) + 1} of 5. " +
                "Keep the cardboard still.",
            BoardSetupPhase.GridReady => (_whiteOnlyFallback
                ? "White-only board scan complete. "
                : _whiteEdgeUnavailable
                ? "White edge unavailable; using trusted ambient cardboard corners. "
                : $"{(_ambientRecoveredFromPrior ? "Three-edge inferred" : "Four-edge ambient")} " +
                  $"and white board edges agree within {_boardCrossCheckErrorPixels:F1} px. ") +
                "Five-spot registration complete. Grid is projected inside " +
                $"its detected edges (center check error {_boardRegistrationError:F4} normalized; " +
                $"grid inset {_boardGridInset:P1}). " +
                (_boardProjectionWarning ?? "Press Scan again if the cardboard moves."),
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
            (phase is BoardSetupPhase.ScanAmbient or BoardSetupPhase.ScanWhite &&
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
