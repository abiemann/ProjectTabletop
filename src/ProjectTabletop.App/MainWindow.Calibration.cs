using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using ProjectTabletop.Calibration;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly AsyncResultGate _calibrationResults = new();
    private double _pieceTopHeightMillimeters = 5;
    private string CalibrationStatus { get; set; } = "Registration needed before overlays can align.";

    private string CalibrationPath => Path.Combine(_appDataDirectory, "calibration.json");

    private void BoardCorners_Click(object sender, RoutedEventArgs e)
    {
        if (!CanBeginCalibration()) return;
        if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
        InvalidateCalibration("Collecting four board-plane point pairs.");
        _annotationMode = AnnotationMode.BoardCalibration;
        _frozenFrame = null;
        _scene.ShowCalibrationTarget(0, pieceTop: false);
        CalibrationStatus = "Board target 1 of 4. Click its center in the live camera view. " +
            "Targets advance clockwise from top left.";
    }

    private void TopPoints_Click(object sender, RoutedEventArgs e)
    {
        if (!CanBeginCalibration()) return;
        if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
        if (_boardCameraPoints.Count != 4)
        {
            CalibrationStatus = "Measure the four board-plane points first.";
            return;
        }
        _calibrationResults.Invalidate();
        _calibration = null;
        _scene.SetTopPlaneMap(null);
        _topCameraPoints.Clear();
        _annotationMode = AnnotationMode.TopCalibration;
        _frozenFrame = null;
        _scene.ShowCalibrationTarget(0, pieceTop: true);
        CalibrationStatus = "Raised top target 1 of 4. Move a piece until the projected crosshair " +
            "lands on its top center, then click that center in the camera view.";
    }

    private bool CanBeginCalibration()
    {
        if (!_camera.IsRunning || _camera.LatestFrame is null || SelectedDisplay is null)
        {
            CalibrationStatus = "Start the webcam, choose the projector display, and wait for a frame.";
            return false;
        }
        if (SelectedCamera is not { } selected || _camera.ActiveDeviceId != selected.Device.Id)
        {
            CalibrationStatus = "Start the selected webcam before calibrating.";
            return false;
        }
        if (_output is null || !_output.IsFullScreen || _outputDisplayId != SelectedDisplay?.Id)
        {
            CalibrationStatus = "Open the selected projection display in full screen before calibrating.";
            return false;
        }
        if (!_scene.HasBoardMediaClip)
        {
            CalibrationStatus = "Complete a board scan before projecting calibration targets.";
            return false;
        }
        return true;
    }

    private void AdvanceCalibrationTarget(bool pieceTop)
    {
        var count = pieceTop ? _topCameraPoints.Count : _boardCameraPoints.Count;
        if (count < 4)
        {
            _scene.ShowCalibrationTarget(count, pieceTop);
            CalibrationStatus = $"{(pieceTop ? "Raised top" : "Board")} target {count + 1} of 4. " +
                (pieceTop ? "Move the piece until the next target hits its top center, then click that center." :
                            "Click the next projected board target in the camera view.");
            return;
        }

        _scene.ShowCalibrationTarget(-1, pieceTop);
        _annotationMode = AnnotationMode.None;
        if (_boardCameraPoints.Count == 4 && _topCameraPoints.Count == 4) CreateCalibration();
        else CalibrationStatus = "Board points recorded. Now measure four points on the raised piece top.";
    }

    private void CreateCalibration()
    {
        if (SelectedCamera is not { } camera || SelectedDisplay is not { } display ||
            _camera.ActiveDeviceId != camera.Device.Id || _camera.LatestFrame is not { } frame) return;
        try
        {
            var boardTarget = _scene.GetCalibrationTargets(display.Width, display.Height, pieceTop: false);
            var topTarget = _scene.GetCalibrationTargets(display.Width, display.Height, pieceTop: true);
            static Point2[] CameraPoints(IEnumerable<PixelPoint> points) =>
                points.Select(point => new Point2(point.X, point.Y)).ToArray();
            static Point2[] OutputPoints(IEnumerable<Vector2> points) =>
                points.Select(point => new Point2(point.X, point.Y)).ToArray();

            var session = new CalibrationSession(camera.Device.Id, frame.Width, frame.Height,
                display.Id, display.Width, display.Height,
                new PlaneCalibration(CameraPoints(_boardCameraPoints), OutputPoints(boardTarget)),
                new PlaneCalibration(CameraPoints(_topCameraPoints), OutputPoints(topTarget)),
                _pieceTopHeightMillimeters);
            ApplyCalibration(session);
            CalibrationStatus = "Board and raised top calibrated at " +
                $"{session.PieceTopHeightMillimeters:F1} mm. Save calibration for later launches.";
        }
        catch (Exception ex)
        {
            _calibration = null;
            _scene.SetTopPlaneMap(null);
            CalibrationStatus = "Calibration measurements rejected: " + ex.Message;
        }
    }

    private void ApplyCalibration(CalibrationSession session)
    {
        _calibrationResults.Invalidate();
        _calibration = session;
        _scene.SetTopPlaneMap(point =>
        {
            var projected = session.MapCameraToProjector(new Point2(point.X, point.Y), CalibrationPlane.PieceTop);
            return new Vector2((float)(projected.X / session.ProjectorWidth),
                               (float)(projected.Y / session.ProjectorHeight));
        });
    }

    private void InvalidateCalibration(string reason)
    {
        _calibrationResults.Invalidate();
        _calibration = null;
        _annotationMode = AnnotationMode.None;
        _boardCameraPoints.Clear();
        _topCameraPoints.Clear();
        _scene.SetTopPlaneMap(null);
        _scene.ShowCalibrationTarget(-1, pieceTop: false);
        if (_initialized) CalibrationStatus = reason;
    }

    private async void SaveCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_calibration is null)
        {
            CalibrationStatus = "Measure both the board and raised top before saving calibration.";
            return;
        }
        var generation = _calibrationResults.Capture();
        var calibration = _calibration;
        try
        {
            await CalibrationSessionStore.SaveAsync(CalibrationPath, calibration);
            CompleteStatus("Calibration saved to " + CalibrationPath);
        }
        catch (Exception ex) { CompleteStatus("Calibration save failed: " + ex.Message); }

        void CompleteStatus(string status) => _calibrationResults.TryApply(generation, () =>
        {
            if (_closing || !ReferenceEquals(calibration, _calibration)) return false;
            CalibrationStatus = status;
            return true;
        });
    }

    private async void LoadCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_output is null || !_output.IsFullScreen || _outputDisplayId != SelectedDisplay?.Id)
        {
            CalibrationStatus = "Open the selected projection display in full screen before loading calibration.";
            return;
        }
        if (SelectedCamera is not { } camera || SelectedDisplay is not { } display ||
            _camera.ActiveDeviceId != camera.Device.Id || _camera.LatestFrame is not { } frame)
        {
            CalibrationStatus = "Start the matching webcam and select the projector display before loading calibration.";
            return;
        }
        _calibrationResults.Invalidate();
        var generation = _calibrationResults.Capture();
        var cameraVersion = Interlocked.Read(ref _cameraOperationVersion);
        var output = _output;
        try
        {
            var session = await CalibrationSessionStore.LoadAsync(CalibrationPath);
            // DisplayChoice retains picker bounds; query Windows again to detect
            // a resolution change on the same display while the file was read.
            var liveDisplay = SelectedDisplay is { } selectedDisplay
                ? DisplayArea.GetFromDisplayId(selectedDisplay.DisplayId) : null;
            _calibrationResults.TryApply(generation, () =>
            {
                // A newer calibration action or close owns the status. Otherwise read
                // live hardware after the await: a camera restart (even to the same
                // device), output replacement or display change makes this read obsolete.
                if (_closing) return false;
                if (liveDisplay is null || !_cameraWanted || !_camera.IsRunning || _cameraOperation.CurrentCount == 0 ||
                    cameraVersion != Interlocked.Read(ref _cameraOperationVersion) ||
                    !ReferenceEquals(output, _output) || output is null || !output.IsFullScreen ||
                    SelectedCamera is not { } currentCamera || SelectedDisplay is not { } currentDisplay ||
                    currentDisplay.Id != display.Id ||
                    _camera.ActiveDeviceId != currentCamera.Device.Id || _camera.LatestFrame is not { } currentFrame ||
                    _outputDisplayId != currentDisplay.Id || output.ActualDisplayId != currentDisplay.Id)
                {
                    CalibrationStatus = "Calibration was not loaded because the webcam or projector output " +
                        "changed while it was being read. Load it again.";
                    return false;
                }
                if (!session.MatchesHardware(currentCamera.Device.Id, currentFrame.Width, currentFrame.Height,
                        currentDisplay.Id, liveDisplay.OuterBounds.Width, liveDisplay.OuterBounds.Height))
                {
                    CalibrationStatus = "Saved calibration does not match camera or projector mode. Recalibrate.";
                    return false;
                }
                _pieceTopHeightMillimeters = session.PieceTopHeightMillimeters;
                ApplyCalibration(session);
                _boardCameraPoints.Clear();
                _boardCameraPoints.AddRange(session.BoardPlane.CameraPoints.Select(point => new PixelPoint(point.X, point.Y)));
                _topCameraPoints.Clear();
                _topCameraPoints.AddRange(session.PieceTopPlane.CameraPoints.Select(point => new PixelPoint(point.X, point.Y)));
                CalibrationStatus = "Saved calibration loaded. Recalibrate if the camera, board, " +
                    "projector, lens correction, or piece height moved.";
                return true;
            });
        }
        catch (Exception ex)
        {
            _calibrationResults.TryApply(generation, () =>
            {
                if (_closing) return false;
                CalibrationStatus = "Calibration load failed: " + ex.Message;
                return true;
            });
        }
    }
}
