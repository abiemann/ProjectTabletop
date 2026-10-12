using ProjectTabletop.Calibration;
using Microsoft.UI.Xaml.Controls;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly CameraLatencyMeasurement _cameraLatency = new();
    private readonly HashSet<int> _latencyObservedSpots = [];
    private bool _measuringCameraLatency, _cameraLatencyCompleted;
    // Published on the UI thread; detector workers must not enumerate the mutable scan.
    private long _projectionReferenceMinimumAgeTicks;

    private void ProjectorLatency_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_projectionSetupInitialized || _loadingProjectionProfile) return;
        if (!double.IsFinite(sender.Value) || sender.Value is < 0 or > CameraLatencyMeasurement.MaximumTotalMilliseconds)
        {
            sender.Value = _cameraLatency.ProjectorMilliseconds;
            return;
        }
        _cameraLatency.ProjectorMilliseconds = sender.Value;
        UpdateCameraLatencyStatus();
        ProjectorSize_ValueChanged(sender, args);
    }

    private void ResetCameraLatency(string? message = null)
    {
        _camera.CaptureEveryFrame = false;
        _cameraLatency.Reset();
        Volatile.Write(ref _projectionReferenceMinimumAgeTicks, 0);
        _latencyObservedSpots.Clear();
        _measuringCameraLatency = _cameraLatencyCompleted = false;
        if (!_closing)
            CameraLatencyText.Text = message ?? "Estimated camera latency: measured during board setup.";
    }

    private void BeginCameraLatency()
    {
        _cameraLatency.Reset();
        Volatile.Write(ref _projectionReferenceMinimumAgeTicks, 0);
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
        UpdateCameraLatencyStatus();
    }

    private void UpdateCameraLatencyStatus()
    {
        if (_closing) return;
        if (_cameraLatency.MedianMilliseconds is { } median)
            CameraLatencyText.Text = $"Estimated camera latency: {median:F0} ms" +
                (_measuringCameraLatency ? " (measuring). " : ". ") +
                $"Median of {_cameraLatency.Samples.Count}/{BoardSetupSpotCount} dots; " +
                $"range {_cameraLatency.MinimumMilliseconds:F0}–{_cameraLatency.MaximumMilliseconds:F0} ms. " +
                $"{_cameraLatency.ProjectorMilliseconds:0.##} ms projector delay subtracted.";
        else
            CameraLatencyText.Text = _measuringCameraLatency
                ? $"Estimated camera latency: measuring setup dots ({_cameraLatency.ProjectorMilliseconds:0.##} ms projector allowance)."
                : _cameraLatencyCompleted
                    ? "Estimated camera latency: unavailable with this projector latency. Check the value or scan again."
                    : "Estimated camera latency: measured during board setup.";
    }

    // Local diagnostics only; none of this text or data is rendered on the board.
    private object CameraLatencyStatus => new
    {
        measuring = _measuringCameraLatency,
        completed = _cameraLatencyCompleted,
        estimatedMilliseconds = _cameraLatency.MedianMilliseconds,
        minimumMilliseconds = _cameraLatency.MinimumMilliseconds,
        maximumMilliseconds = _cameraLatency.MaximumMilliseconds,
        projectorMilliseconds = _cameraLatency.ProjectorMilliseconds,
        projectionReferenceMinimumAgeMilliseconds = TimeSpan.FromTicks(
            Volatile.Read(ref _projectionReferenceMinimumAgeTicks)).TotalMilliseconds,
        samples = _cameraLatency.Samples,
        status = CameraLatencyText.Text
    };
}
