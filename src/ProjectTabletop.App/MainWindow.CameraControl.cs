using System.Text.Json;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Live camera tuning uses the app's owned capture. Never open another camera
    // graph while preview/detection are running.
    private async Task<object> SetCameraColorControlsForControlAsync(JsonElement parameters)
    {
        double? Read(string name)
        {
            if (!parameters.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
                !double.IsFinite(number)) throw new ArgumentException($"Provide a finite numeric {name}.");
            return number;
        }
        foreach (var property in parameters.EnumerateObject())
            if (property.Name is not ("exposureLog2Seconds" or "whiteBalanceKelvin"))
                throw new ArgumentException("Only exposureLog2Seconds and whiteBalanceKelvin are supported.");
        double? exposure = Read("exposureLog2Seconds"), whiteBalance = Read("whiteBalanceKelvin");
        if (exposure is null && whiteBalance is null) throw new ArgumentException("Provide a camera control to adjust.");
        string? expectedDevice = _camera.ActiveDeviceId;
        long expectedEpoch = _camera.CaptureEpoch;
        bool CurrentSession() => !_closing && _cameraWanted && _camera.IsRunning &&
            !_cameraManualRestartRequired && expectedDevice is not null &&
            expectedDevice == _cameraWantedDeviceId && expectedDevice == _camera.ActiveDeviceId &&
            expectedEpoch == _camera.CaptureEpoch;
        if (!CurrentSession()) throw new InvalidOperationException("The camera is not available for adjustment.");
        StopBoardSetup();
        await FinishCameraColorCalibrationAsync();
        CancelCameraPictureRequest();
        await _cameraOperation.WaitAsync();
        try
        {
            if (!CurrentSession()) throw new InvalidOperationException("The camera changed before adjustment.");
            var snapshot = await _camera.GetControlsAsync();
            if (!CurrentSession() || snapshot.Epoch != expectedEpoch || snapshot.Status != "queried")
                throw new InvalidOperationException("The camera is not available for adjustment.");
            bool Changes(string name, double? target) => target is { } value &&
                (!snapshot.Controls.TryGetValue(name, out var control) || control.Value != value);
            if (Changes("legacyExposure", exposure) || Changes("legacyWhiteBalance", whiteBalance))
                ResetCameraPictureInput();
            var result = await _camera.SetManualControlsAsync(expectedDevice!, expectedEpoch,
                exposureLog2Seconds: exposure, whiteBalanceKelvin: whiteBalance);
            if (result.Changed)
            {
                await Task.Delay(350);
                if (CurrentSession())
                {
                    ResetCameraPictureInput();
                    if (exposure is not null) ResetCameraLatency("Estimated Visual Latency: scan again after changing camera exposure.");
                }
            }
            return result;
        }
        finally { _cameraOperation.Release(); }
    }
}
