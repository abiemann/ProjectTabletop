using ProjectTabletop.Calibration;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly CameraLatencyMeasurement _cameraLatency = new() { ProjectorMilliseconds = 0 };
    private double? _projectorFrameMilliseconds, _projectorRefreshHertz;
    private readonly HashSet<int> _latencyObservedSpots = [];
    private bool _measuringCameraLatency, _cameraLatencyCompleted;
    // Published on the UI thread; detector workers must not enumerate the mutable scan.
    private long _projectionReferenceMinimumAgeTicks;
    private long _projectionSceneDelayTicks;

    private void UpdateProjectorFrameAllowance()
    {
        double hertz = SelectedDisplay?.PhysicalMode?.RefreshHertz ?? 0;
        _projectorFrameMilliseconds = CameraLatencyMeasurement.ProjectorFrameMilliseconds(hertz);
        _projectorRefreshHertz = _projectorFrameMilliseconds is null ? null : hertz;
        // Unknown refresh still permits raw setup timing for tracking, but no
        // camera-only estimate is presented until Windows supplies a valid rate.
        _cameraLatency.ProjectorMilliseconds = _projectorFrameMilliseconds ?? 0;
    }

    private void ResetCameraLatency(string? message = null)
    {
        _camera.CaptureEveryFrame = false;
        _cameraLatency.Reset();
        UpdateProjectorFrameAllowance();
        Volatile.Write(ref _projectionReferenceMinimumAgeTicks, 0);
        Volatile.Write(ref _projectionSceneDelayTicks, 0);
        _latencyObservedSpots.Clear();
        _measuringCameraLatency = _cameraLatencyCompleted = false;
        if (!_closing && message is not null) CameraLatencyText.Text = message;
        else UpdateCameraLatencyStatus();
    }

    private void BeginCameraLatency()
    {
        _cameraLatency.Reset();
        UpdateProjectorFrameAllowance();
        Volatile.Write(ref _projectionReferenceMinimumAgeTicks, 0);
        Volatile.Write(ref _projectionSceneDelayTicks, 0);
        _latencyObservedSpots.Clear();
        _cameraLatencyCompleted = false;
        _measuringCameraLatency = true;
        _camera.CaptureEveryFrame = true;
        UpdateCameraLatencyStatus();
    }

    private void CompleteCameraLatency()
    {
        _camera.CaptureEveryFrame = false;
        _measuringCameraLatency = false;
        _cameraLatencyCompleted = true;
        Volatile.Write(ref _projectionReferenceMinimumAgeTicks,
            _cameraLatency.ProjectionReferenceMinimumAge(_camera.NegotiatedFormat?.FramesPerSecond ?? 0).Ticks);
        Volatile.Write(ref _projectionSceneDelayTicks,
            TimeSpan.FromMilliseconds(_cameraLatency.TotalMedianMilliseconds ?? 0).Ticks);
        UpdateCameraLatencyStatus();
    }

    private void UpdateCameraLatencyStatus()
    {
        if (_closing) return;
        if (_projectorFrameMilliseconds is not { } frameMilliseconds)
        {
            CameraLatencyText.Text = "Estimated Visual Latency: unavailable. Select a projector and refresh displays to read its refresh rate.";
            return;
        }
        string allowance = $"Automatic projector allowance: {frameMilliseconds:0.#} ms at {_projectorRefreshHertz:0.##} Hz (one frame).";
        if (_cameraLatency.MedianMilliseconds is { } median)
            CameraLatencyText.Text = $"Estimated Visual Latency: {median:F0} ms" +
                (_measuringCameraLatency ? " (measuring). " : ". ") +
                $"Median of {_cameraLatency.Samples.Count}/{BoardSetupSpotCount} dots; " +
                $"range {_cameraLatency.MinimumMilliseconds:F0}–{_cameraLatency.MaximumMilliseconds:F0} ms. " +
                allowance;
        else
            CameraLatencyText.Text = _measuringCameraLatency
                ? $"Estimated Visual Latency: measuring setup dots. {allowance}"
                : _cameraLatencyCompleted
                    ? $"Estimated Visual Latency: no usable setup-dot timings. Scan again. {allowance}"
                    : $"Estimated Visual Latency: measured during board setup. {allowance}";
    }

    // Local diagnostics only; none of this text or data is rendered on the board.
    private object CameraLatencyStatus => new
    {
        measuring = _measuringCameraLatency,
        completed = _cameraLatencyCompleted,
        estimatedMilliseconds = _projectorFrameMilliseconds is null ? null : _cameraLatency.MedianMilliseconds,
        minimumMilliseconds = _projectorFrameMilliseconds is null ? null : _cameraLatency.MinimumMilliseconds,
        maximumMilliseconds = _projectorFrameMilliseconds is null ? null : _cameraLatency.MaximumMilliseconds,
        projectorMilliseconds = _projectorFrameMilliseconds,
        projectorRefreshHertz = _projectorRefreshHertz,
        projectorAllowanceSource = "activeDisplayFramePeriod",
        totalEstimatedMilliseconds = _cameraLatency.TotalMedianMilliseconds,
        projectionSceneDelayMilliseconds = TimeSpan.FromTicks(
            Volatile.Read(ref _projectionSceneDelayTicks)).TotalMilliseconds,
        projectionReferenceMinimumAgeMilliseconds = TimeSpan.FromTicks(
            Volatile.Read(ref _projectionReferenceMinimumAgeTicks)).TotalMilliseconds,
        samples = _projectorFrameMilliseconds is null ? null : _cameraLatency.Samples,
        status = CameraLatencyText.Text
    };
}
