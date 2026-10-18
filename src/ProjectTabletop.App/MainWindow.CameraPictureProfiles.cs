using System.Text.Json;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Settings;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private CameraPictureSettings _cameraPictureSettings = new(true, new(StringComparer.Ordinal));
    private CameraCaptureService.CameraControlsSnapshot? _cameraPictureSession;
    private string? _cameraPictureDevice;
    private bool _cameraPictureInitialized, _cameraPictureLoadingUi, _cameraPictureStorageFailed;
    private bool _cameraPictureCalibration, _cameraPictureWorkerRunning, _cameraPicturePending;
    private bool _cameraPictureRestoreNeeded, _cameraPictureSessionFailed;
    private bool _cameraPictureTouchedBrightness, _cameraPictureTouchedContrast;
    private bool? _cameraPictureLastRequestedDark;
    private readonly object _cameraPictureCancelGate = new();
    private CancellationTokenSource? _cameraPictureCancellation;
    private long _cameraPictureRequestGeneration;
    private long _cameraPictureLastRequestedGeneration = -1;
    private string _cameraPictureStatus = "Camera stopped.";
    private string _cameraPictureActive = "Default";
    private CameraCaptureService.CameraImageControlResult? _cameraPictureLastResult;
    private string CameraPictureSettingsPath => Path.Combine(_appDataDirectory, "camera-picture.json");

    private void InitializeCameraPictureProfiles()
    {
        try { _cameraPictureSettings = CameraPictureSettingsStore.Read(CameraPictureSettingsPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _cameraPictureStorageFailed = true;
            _cameraPictureStatus = "Automatic profiles unavailable: " + ex.Message;
        }
        _cameraPictureInitialized = true;
        UpdateCameraPictureUi();
    }

    private bool CameraPictureWantsDark => _cameraPictureSettings.Enabled && !_closing &&
        !_cameraPictureCalibration && !_boardSetupActive && !_cameraManualRestartRequired &&
        _annotationMode is not (AnnotationMode.BoardCalibration or AnnotationMode.TopCalibration) &&
        _cameraWanted && _camera.ActiveDeviceId == _cameraWantedDeviceId &&
        _scene.HasBoardMediaClip && _scene.CurrentBoardScreen == BoardScreen.Globe;

    // May run under the scene lock on a detector thread: only cancel and post.
    private void BoardOpenedForCameraPicture(BoardScreen screen)
    {
        CancelCameraPictureRequest();
        DispatcherQueue.TryEnqueue(RequestCameraPictureProfile);
    }

    private void CancelCameraPictureRequest()
    {
        Interlocked.Increment(ref _cameraPictureRequestGeneration);
        lock (_cameraPictureCancelGate) _cameraPictureCancellation?.Cancel();
    }

    private void RequestCameraPictureProfile()
    {
        if (!_cameraPictureInitialized || _closing) return;
        UpdateCameraPictureUi();
        bool dark = CameraPictureWantsDark;
        if (_cameraPictureLastRequestedDark == dark && _cameraPictureLastRequestedGeneration ==
            Interlocked.Read(ref _cameraPictureRequestGeneration)) return;
        _cameraPictureLastRequestedDark = dark;
        CancelCameraPictureRequest();
        _cameraPictureLastRequestedGeneration = Interlocked.Read(ref _cameraPictureRequestGeneration);
        _cameraPicturePending = true;
        if (!_cameraPictureWorkerRunning)
        {
            // Navigation can clear setup and open another board in the same UI turn.
            // Reconcile its final state rather than briefly applying the old board's profile.
            _cameraPictureWorkerRunning = true;
            if (!DispatcherQueue.TryEnqueue(() => _ = ReconcileCameraPictureAsync()))
                _cameraPictureWorkerRunning = false;
        }
    }

    private async Task ReconcileCameraPictureAsync()
    {
        _cameraPictureWorkerRunning = true;
        try
        {
            while (_cameraPicturePending && !_closing)
            {
                _cameraPicturePending = false;
                long generation = Interlocked.Read(ref _cameraPictureRequestGeneration);
                using var cancellation = new CancellationTokenSource();
                lock (_cameraPictureCancelGate) _cameraPictureCancellation = cancellation;
                await _cameraOperation.WaitAsync();
                try
                {
                    if (_closing || generation != Interlocked.Read(ref _cameraPictureRequestGeneration) ||
                        !_camera.IsRunning || _cameraManualRestartRequired ||
                        _cameraPictureStorageFailed) continue;
                    bool dark = CameraPictureWantsDark;
                    if (_cameraPictureSessionFailed && (dark || !_cameraPictureRestoreNeeded)) continue;
                    if (!dark && _cameraPictureRestoreNeeded) await RestoreCameraPictureDefaultAsync(cancellation.Token);
                    else if (_cameraPictureSettings.Enabled) await ApplyCameraPictureAsync(dark, cancellation.Token);
                }
                finally
                {
                    _cameraOperation.Release();
                    lock (_cameraPictureCancelGate)
                        if (ReferenceEquals(_cameraPictureCancellation, cancellation)) _cameraPictureCancellation = null;
                }
            }
        }
        catch (Exception ex) { FailCameraPictureSession(ex.Message); }
        finally { _cameraPictureWorkerRunning = false; UpdateCameraPictureUi(); }
    }

    // The caller owns _cameraOperation. A persisted Default is established before any override.
    private async Task InitializeCameraPictureSessionAsync()
    {
        _cameraPictureSession = null;
        _cameraPictureDevice = _camera.ActiveDeviceId;
        _cameraPictureSessionFailed = false;
        _cameraPictureRestoreNeeded = false;
        _cameraPictureTouchedBrightness = _cameraPictureTouchedContrast = false;
        _cameraPictureLastRequestedDark = null;
        if (_cameraPictureStorageFailed || !_camera.IsRunning || _cameraPictureDevice is null) return;
        var snapshot = await _camera.GetControlsAsync();
        if (snapshot.Status != "queried" || _camera.ActiveDeviceId != _cameraPictureDevice)
        {
            FailCameraPictureSession("Camera picture controls are unavailable for this session.");
            return;
        }
        _cameraPictureSession = snapshot;
        if (!_cameraPictureSettings.Cameras.ContainsKey(_cameraPictureDevice))
        {
            var defaults = CameraPictureProfilePolicy.CaptureDefaults(
                PictureControl(snapshot, "brightness"), PictureControl(snapshot, "contrast"));
            if (defaults is null)
            {
                _cameraPictureStatus = "This camera does not expose compatible picture controls.";
                return;
            }
            var cameras = new Dictionary<string, CameraPictureDefaults>(_cameraPictureSettings.Cameras, StringComparer.Ordinal)
                { [_cameraPictureDevice] = defaults };
            if (!SaveCameraPictureSettings(_cameraPictureSettings with { Cameras = cameras })) return;
        }
        if (_cameraPictureSettings.Enabled)
        {
            var target = CameraPictureProfilePolicy.CreateTarget(_cameraPictureSettings.Cameras[_cameraPictureDevice],
                PictureControl(snapshot, "brightness"), PictureControl(snapshot, "contrast"), darkScene: false);
            if (target.Brightness is null && target.Contrast is null)
            {
                _cameraPictureStatus = "Saved Default is incompatible with this camera session. No picture settings changed.";
                return;
            }
            // Recovers the original Default even if a previous process ended while Dark was active.
            _cameraPictureRestoreNeeded = true;
            await RestoreCameraPictureDefaultAsync();
            if (_cameraPictureRestoreNeeded)
                throw new InvalidOperationException("The camera has not confirmed its Default picture settings. Stop and retry the camera before calibration.");
        }
        else _cameraPictureStatus = "Automatic profiles off. Camera settings are manual.";
        UpdateCameraPictureUi();
    }

    private static CameraPictureControl? PictureControl(CameraCaptureService.CameraControlsSnapshot snapshot, string name)
    {
        if (!snapshot.Controls.TryGetValue(name, out var control) || control.Supported != true ||
            control.Status != "available" || (control.AutoSupported == true && control.Automatic != false) ||
            control.Value is not { } value || control.Minimum is not { } min ||
            control.Maximum is not { } max || control.Step is not { } step) return null;
        var result = new CameraPictureControl(value, min, max, step);
        return result.IsValid ? result : null;
    }

    // Also called while closing/stopping: never tests _cameraWanted and never opens a camera.
    private async Task RestoreCameraPictureDefaultAsync(CancellationToken cancellation = default)
    {
        if (!_camera.IsRunning || !_cameraPictureRestoreNeeded) return;
        var snapshot = await _camera.GetControlsAsync();
        if (snapshot.Status != "queried" || _cameraPictureDevice != _camera.ActiveDeviceId)
        {
            FailCameraPictureSession("Could not read camera controls to restore Default.");
            return;
        }
        _cameraPictureSession = snapshot;
        await ApplyCameraPictureAsync(dark: false, cancellation);
    }

    private async Task ApplyCameraPictureAsync(bool dark, CancellationToken cancellation)
    {
        var snapshot = _cameraPictureSession;
        var device = _cameraPictureDevice;
        if (snapshot is null || device is null || _camera.ActiveDeviceId != device ||
            !_cameraPictureSettings.Cameras.TryGetValue(device, out var defaults)) return;
        var target = CameraPictureProfilePolicy.CreateTarget(defaults,
            PictureControl(snapshot, "brightness"), PictureControl(snapshot, "contrast"), dark);
        if (target.Brightness is null && target.Contrast is null)
        {
            _cameraPictureStatus = "Saved Default does not match this camera's current controls. Turn profiles off to capture a new Default.";
            return;
        }
        if (cancellation.IsCancellationRequested) return;
        // The operation gate suspends all detector admission; invalidate already-running work first.
        // Skip resets when our confirmed values already match, including unsupported/no-op transitions.
        bool expectedChange = target.Brightness is { } b && b != PictureControl(snapshot, "brightness")?.Value ||
            target.Contrast is { } c && c != PictureControl(snapshot, "contrast")?.Value;
        if (expectedChange && !_closing) ResetCameraPictureInput();
        var result = await _camera.SetImageControlsAsync(device, snapshot.Epoch,
            target.Brightness, target.Contrast, cancellation);
        _cameraPictureLastResult = result;
        if (result.Changed)
        {
            _cameraPictureRestoreNeeded = true;
            _cameraPictureTouchedBrightness |= target.Brightness is { } requestedBrightness && result.Before.Brightness != requestedBrightness;
            _cameraPictureTouchedContrast |= target.Contrast is { } requestedContrast && result.Before.Contrast != requestedContrast;
            // Setting changes only: let queued camera images drain, then discard all old evidence.
            // Normal hand/marker/swipe input has no additional delay.
            if (!_closing)
            {
                await Task.Delay(220);
                if (!_closing) ResetCameraPictureInput();
            }
        }
        var controls = new Dictionary<string, CameraCaptureService.CameraControlInfo>(snapshot.Controls);
        // Cancellation before driver admission has no readback and cannot invalidate known values.
        // A setter attempt or an actual preflight read must, however, retain unknowns as unknowns.
        if (target.Brightness is not null && (result.Changed || result.Before.Brightness is not null))
            controls["brightness"] = controls["brightness"] with { Value = result.After.Brightness };
        if (target.Contrast is not null && (result.Changed || result.Before.Contrast is not null))
            controls["contrast"] = controls["contrast"] with { Value = result.After.Contrast };
        _cameraPictureSession = snapshot with { Controls = controls };
        if (result.Status is "applied" or "unchanged")
        {
            if (!dark && ((_cameraPictureTouchedBrightness &&
                    (target.Brightness is null || result.After.Brightness != defaults.Brightness?.Value)) ||
                (_cameraPictureTouchedContrast &&
                    (target.Contrast is null || result.After.Contrast != defaults.Contrast?.Value))))
            {
                FailCameraPictureSession("The driver has not confirmed restoration of all changed controls.");
                return;
            }
            _cameraPictureRestoreNeeded = dark;
            if (!dark) _cameraPictureTouchedBrightness = _cameraPictureTouchedContrast = false;
            _cameraPictureActive = dark ? "Dark scene" : "Default";
            _cameraPictureStatus = dark
                ? "Dark scene · lower contrast for Globe. Exposure and white balance unchanged."
                : "Default · your saved camera picture settings.";
        }
        else if (result.Status != "cancelled") FailCameraPictureSession(result.Error ?? result.Status);
        UpdateCameraPictureUi();
    }

    private void ResetCameraPictureInput()
    {
        ClearHandTracking(reason: "camera-picture", preserveBoardAnimations: true, preserveRecording: true);
        ResetMarkerTracking("camera-picture");
        _cameraInputWorker.Clear();
        Interlocked.Exchange(ref _cameraInputNotBeforeTicks, MonotonicClock.UtcNow.UtcTicks);
    }

    private async Task PrepareCameraPictureCalibrationAsync()
    {
        _cameraPictureCalibration = true;
        CancelCameraPictureRequest();
        await _cameraOperation.WaitAsync();
        try
        {
            await RestoreCameraPictureDefaultAsync();
            if (_cameraPictureRestoreNeeded)
                throw new InvalidOperationException("Cannot begin calibration until the camera confirms its Default picture settings.");
        }
        finally { _cameraOperation.Release(); }
    }

    private void FinishCameraPictureCalibration()
    {
        _cameraPictureCalibration = false;
        _cameraPictureLastRequestedDark = null;
        RequestCameraPictureProfile();
    }

    private void FailCameraPictureSession(string error)
    {
        _cameraPictureSessionFailed = true;
        _cameraPictureStatus = "Automatic picture adjustment paused: " + error;
    }

    private bool SaveCameraPictureSettings(CameraPictureSettings settings)
    {
        try
        {
            CameraPictureSettingsStore.Write(CameraPictureSettingsPath, settings);
            _cameraPictureSettings = settings;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _cameraPictureStorageFailed = true;
            _cameraPictureStatus = "Could not save camera defaults; automatic changes are disabled. " + ex.Message;
            return false;
        }
    }

    private async void CameraPictureProfiles_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_cameraPictureInitialized || _cameraPictureLoadingUi || _closing) return;
        bool enabled = CameraPictureProfilesToggle.IsOn;
        CancelCameraPictureRequest();
        await _cameraOperation.WaitAsync();
        try
        {
            if (!enabled)
            {
                await RestoreCameraPictureDefaultAsync();
                if (_cameraPictureRestoreNeeded) return;
            }
            if (SaveCameraPictureSettings(_cameraPictureSettings with { Enabled = enabled }))
            {
                _cameraPictureSessionFailed = false;
                if (enabled && _camera.IsRunning) await InitializeCameraPictureSessionAsync();
                if (!enabled) _cameraPictureStatus = "Automatic profiles off. Camera settings are manual.";
            }
        }
        catch (Exception ex) { FailCameraPictureSession(ex.Message); }
        finally
        {
            _cameraOperation.Release();
            _cameraPictureLastRequestedDark = null;
            RequestCameraPictureProfile();
            UpdateCameraPictureUi();
        }
    }

    private async void CaptureCameraPictureDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraPictureSettings.Enabled || _closing || _boardSetupActive) return;
        await _cameraOperation.WaitAsync();
        try
        {
            if (_cameraPictureSettings.Enabled || !_camera.IsRunning || _camera.ActiveDeviceId is not { } device) return;
            var snapshot = await _camera.GetControlsAsync();
            if (snapshot.Status != "queried") return;
            var defaults = CameraPictureProfilePolicy.CaptureDefaults(PictureControl(snapshot, "brightness"), PictureControl(snapshot, "contrast"));
            if (defaults is null) { _cameraPictureStatus = "No compatible picture controls to save."; return; }
            var cameras = new Dictionary<string, CameraPictureDefaults>(_cameraPictureSettings.Cameras, StringComparer.Ordinal)
                { [device] = defaults };
            if (SaveCameraPictureSettings(_cameraPictureSettings with { Cameras = cameras }))
            {
                _cameraPictureDevice = device;
                _cameraPictureSession = snapshot;
                _cameraPictureSessionFailed = false;
                _cameraPictureStatus = "Current camera settings saved as Default. You can enable automatic profiles.";
            }
        }
        catch (Exception ex) { FailCameraPictureSession(ex.Message); }
        finally { _cameraOperation.Release(); UpdateCameraPictureUi(); }
    }

    private void UpdateCameraPictureUi()
    {
        if (!_cameraPictureInitialized || _closing) return;
        _cameraPictureLoadingUi = true;
        CameraPictureProfilesToggle.IsOn = _cameraPictureSettings.Enabled && !_cameraPictureStorageFailed;
        CameraPictureProfilesToggle.IsEnabled = !_cameraPictureStorageFailed;
        CaptureCameraPictureDefaultButton.IsEnabled = !_cameraPictureSettings.Enabled && !_boardSetupActive &&
            !_cameraPictureStorageFailed && _camera.IsRunning && !_cameraPictureRestoreNeeded;
        CameraPictureStatusText.Text = _cameraPictureStatus;
        _cameraPictureLoadingUi = false;
    }

    private object GetCameraPictureProfileStatus() => new
    {
        enabled = _cameraPictureSettings.Enabled && !_cameraPictureStorageFailed,
        active = _cameraPictureActive, desired = CameraPictureWantsDark ? "Dark scene" : "Default",
        status = _cameraPictureStatus, busy = _cameraPictureWorkerRunning,
        restorePending = _cameraPictureRestoreNeeded, settingsPath = CameraPictureSettingsPath,
        defaults = _cameraPictureDevice is { } id ? _cameraPictureSettings.Cameras.GetValueOrDefault(id) : null,
        lastResult = _cameraPictureLastResult
    };
}
