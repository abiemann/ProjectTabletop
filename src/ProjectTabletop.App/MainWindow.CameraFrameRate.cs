using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Settings;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private Dictionary<string, int> _cameraFrameRates = new(StringComparer.Ordinal);
    private int _requestedCameraFramesPerSecond = 30;
    private bool _cameraFrameRatesInitialized, _loadingCameraFrameRate, _cameraFrameRatesLoadFailed;
    private string? _cameraFrameRatesError;
    private string CameraFrameRateSettingsPath => Path.Combine(_appDataDirectory, "camera-capture.json");

    private void InitializeCameraFrameRates()
    {
        try { _cameraFrameRates = CameraFrameRateSettingsStore.Read(CameraFrameRateSettingsPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            // Keep an unreadable file intact; an in-session choice still works.
            _cameraFrameRatesLoadFailed = true;
            _cameraFrameRatesError = "Could not load saved camera rates. Changes apply to this session. " + ex.Message;
        }
        _cameraFrameRatesInitialized = true;
        LoadSelectedCameraFrameRate();
    }

    private void LoadSelectedCameraFrameRate()
    {
        if (!_cameraFrameRatesInitialized) return;
        int rate = SelectedCamera is { } choice ? _cameraFrameRates.GetValueOrDefault(choice.Device.Id, 30) : 30;
        _loadingCameraFrameRate = true;
        try
        {
            _requestedCameraFramesPerSecond = rate;
            CameraFrameRateComboBox.SelectedIndex = rate switch { 60 => 1, 100 => 2, _ => 0 };
        }
        finally { _loadingCameraFrameRate = false; }
        UpdateCameraFrameRateStatus();
    }

    private async void CameraFrameRate_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_cameraFrameRatesInitialized || _loadingCameraFrameRate || _closing ||
            CameraFrameRateComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !int.TryParse(tag, out int rate) || rate is not (30 or 60 or 100)) return;
        await SetCameraFrameRateAsync(rate);
    }

    private async Task SetCameraFrameRateAsync(int rate)
    {
        if (rate is not (30 or 60 or 100)) throw new ArgumentException("Choose 30, 60 or 100 fps.");
        if (rate == _requestedCameraFramesPerSecond) return;
        _requestedCameraFramesPerSecond = rate;
        _loadingCameraFrameRate = true;
        try { CameraFrameRateComboBox.SelectedIndex = rate switch { 60 => 1, 100 => 2, _ => 0 }; }
        finally { _loadingCameraFrameRate = false; }
        SaveSelectedCameraFrameRate();
        UpdateCameraFrameRateStatus();
        // The normal start path serializes stop/start and invalidates old frames,
        // calibration and latency samples. Rapid changes supersede older starts.
        if (SelectedCamera is { } choice && _cameraWanted && _cameraWantedDeviceId == choice.Device.Id)
            await StartSelectedCameraAsync();
    }

    private void SaveSelectedCameraFrameRate()
    {
        if (SelectedCamera is not { } choice) return;
        _cameraFrameRates[choice.Device.Id] = _requestedCameraFramesPerSecond;
        if (_cameraFrameRatesLoadFailed) return;
        try
        {
            CameraFrameRateSettingsStore.Write(CameraFrameRateSettingsPath, _cameraFrameRates);
            _cameraFrameRatesError = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _cameraFrameRatesError = "Camera rate applies to this session but could not be saved. " + ex.Message; }
    }

    private void UpdateCameraFrameRateStatus()
    {
        if (_closing || !_cameraFrameRatesInitialized) return;
        string status;
        if (_camera.IsRunning && _camera.ActiveDeviceId == SelectedCamera?.Device.Id &&
            _camera.NegotiatedFormat is { } format)
        {
            bool matches = CameraFrameRatePolicy.MatchesRate(format.FramesPerSecond, _requestedCameraFramesPerSecond);
            status = matches
                ? $"Using {format.FramesPerSecond:0.##} fps. Changing the rate restarts the camera; run board setup again."
                : $"Requested {_requestedCameraFramesPerSecond} fps; camera is using {format.FramesPerSecond:0.##} fps. " +
                  (_camera.HasExclusiveFormatControl ? "The requested mode was unavailable or rejected." : "Another app controls the camera format.");
        }
        else status = $"Request {_requestedCameraFramesPerSecond} fps when the camera starts. Unsupported rates use an available mode.";
        CameraFrameRateStatusText.Text = status + (_cameraFrameRatesError is null ? "" : " " + _cameraFrameRatesError);
    }
}
