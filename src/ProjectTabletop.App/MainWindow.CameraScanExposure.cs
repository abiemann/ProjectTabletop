using System.Diagnostics;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private bool _whiteScanExposureChecked;
    private string _whiteScanExposureStatus = "Not checked.";

    private bool TryPrepareWhiteScanExposure(CameraFrame frame, long generation)
    {
        if (_whiteScanExposureChecked) return false;
        _whiteScanExposureChecked = true;
        if (_camera.ActiveDeviceId is not { } device) return false;
        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
        _whiteScanExposureStatus = "Checking camera exposure against the white scan.";
        BoardSetupStatusText.Text = _whiteScanExposureStatus;
        _ = PrepareWhiteScanExposureAsync(frame.Retain(), device, _camera.CaptureEpoch, generation);
        return true;
    }

    private async Task PrepareWhiteScanExposureAsync(CameraFrameLease frameLease, string device, long epoch, long generation)
    {
        using (frameLease)
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
        {
            bool Current() => !_closing && _boardSetupActive && _cameraWanted && _camera.IsRunning &&
                !_cameraManualRestartRequired && _camera.ActiveDeviceId == device && _cameraWantedDeviceId == device &&
                _camera.CaptureEpoch == epoch && generation == Interlocked.Read(ref _boardSetupGeneration) &&
                (BoardSetupPhase)Volatile.Read(ref _boardSetupPhase) == BoardSetupPhase.Switching;
            bool changed = false, safe = true;
            Task<CameraCaptureService.CameraManualControlResult>? write = null;
            try
            {
                var controls = await _camera.GetControlsAsync().WaitAsync(cancellation.Token);
                if (!Current()) return;
                var exposure = controls.Status == "queried" && controls.Epoch == epoch
                    ? PictureControl(controls, "legacyExposure") : null;
                if (exposure is null)
                {
                    _whiteScanExposureStatus = "Manual exposure is unavailable; camera settings retained.";
                    return;
                }
                var frame = frameLease.Frame;
                var measurement = CameraWhiteScanExposure.Evaluate(frame.Width, frame.Height, frame.Stride, frame.Bgra,
                    exposure.Value, exposure.Minimum, exposure.Maximum, exposure.Step,
                    _camera.NegotiatedFormat?.FramesPerSecond ?? 60);
                if (measurement.SuggestedExposureLog2Seconds is not { } target || target == exposure.Value)
                {
                    _whiteScanExposureStatus = "White scan exposure checked; current setting retained.";
                    return;
                }
                await _cameraOperation.WaitAsync(cancellation.Token);
                try
                {
                    if (!Current()) return;
                    write = _camera.SetManualControlsAsync(device, epoch, exposureLog2Seconds: target,
                        cancellationToken: cancellation.Token);
                    var result = await write.WaitAsync(cancellation.Token);
                    if (!Current()) return;
                    changed = result.Changed;
                    safe = !result.Changed || result.Status is "applied" or "unchanged" || result.RollbackSucceeded;
                    _whiteScanExposureStatus = result.Status == "applied"
                        ? "White scan exposure adjusted; rechecking physical edges."
                        : "Camera exposure adjustment: " + result.Status + ".";
                }
                finally
                {
                    if (write is { IsCompleted: false })
                        _ = write.ContinueWith(_ => _cameraOperation.Release(), TaskScheduler.Default);
                    else _cameraOperation.Release();
                }
                if (changed && safe) await Task.Delay(450, cancellation.Token);
            }
            catch (Exception ex)
            {
                safe = write is null;
                _whiteScanExposureStatus = "Exposure check unavailable: " + ex.Message;
            }
            finally
            {
                if (Current())
                {
                    if (!safe)
                    {
                        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Failed);
                        _boardSetupDiagnostic = "Camera exposure could not be verified. Stop the camera and retry the scan.";
                        BoardSetupStatusText.Text = _boardSetupDiagnostic;
                    }
                    else if (changed)
                    {
                        // Recheck illuminated edges using only the new camera settings.
                        ClearBoardPreview();
                        _whiteScanExposureChecked = true;
                    }
                    else
                    {
                        _lastAnalyzedBoardFrames.Clear();
                        Interlocked.Exchange(ref _lastBoardDetectTick, 0);
                        Interlocked.Exchange(ref _boardPhaseStartedTick, Stopwatch.GetTimestamp());
                        Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.ScanWhite);
                    }
                }
            }
        }
    }
}
