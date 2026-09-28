using Microsoft.UI.Xaml;
using ProjectTabletop.App.Media;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private DeviceWatcher? _displayAudioWatcher;
    private DeviceInformation? _displayAudioDevice;
    private long _displayAudioVersion;
    private bool _displayAudioRequested = true, _updatingDisplayAudio, _audioEnumerationComplete, _displayAudioVideoReady;

    private bool DisplayAudioOutputConnected => !_closing && _displayAudioVideoReady && _output is { } output &&
        output.AppWindow.IsVisible && SelectedDisplay is { } display &&
        _outputDisplayId == display.Id && output.ActualDisplayId == display.Id;

    private object DisplayAudioDiagnostics => new
    {
        outputConnected = DisplayAudioOutputConnected,
        available = EnableDisplayAudioCheckBox.IsEnabled,
        enabled = EnableDisplayAudioCheckBox.IsChecked == true,
        preferenceEnabled = _displayAudioRequested,
        deviceName = _displayAudioDevice?.Name,
        status = DisplayAudioStatusText.Text
    };

    private void InitializeDisplayAudio()
    {
        try
        {
            _displayAudioWatcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector());
            _displayAudioWatcher.Added += DisplayAudioDeviceAdded;
            _displayAudioWatcher.Removed += DisplayAudioDeviceRemoved;
            _displayAudioWatcher.Updated += DisplayAudioDeviceChanged;
            _displayAudioWatcher.EnumerationCompleted += DisplayAudioEnumerationCompleted;
            _displayAudioWatcher.Start();
        }
        catch (Exception ex) { AppLog.Write("Display audio watcher", ex); }
    }

    private void DisplayAudioDeviceAdded(DeviceWatcher sender, DeviceInformation args) => QueueDisplayAudioRefresh();
    private void DisplayAudioDeviceChanged(DeviceWatcher sender, DeviceInformationUpdate args) => QueueDisplayAudioRefresh();
    private void DisplayAudioDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing) return;
        if (_displayAudioDevice?.Id == args.Id) InvalidateDisplayAudio();
        if (_audioEnumerationComplete) _ = RefreshDisplayAudioAsync();
    });
    private void DisplayAudioEnumerationCompleted(DeviceWatcher sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            _audioEnumerationComplete = true;
            _ = RefreshDisplayAudioAsync();
        });
    }

    private void QueueDisplayAudioRefresh() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closing && _audioEnumerationComplete) _ = RefreshDisplayAudioAsync();
    });

    private void InvalidateDisplayAudio(bool disconnectOutput = false)
    {
        if (disconnectOutput) _displayAudioVideoReady = false;
        _displayAudioVersion++;
        _displayAudioDevice = null;
        ApplyDisplayAudioState("Open output to check display audio.");
    }

    private async Task RefreshDisplayAudioAsync()
    {
        if (!DisplayAudioOutputConnected) { InvalidateDisplayAudio(); return; }
        long version = ++_displayAudioVersion;
        var output = _output;
        string? displayId = SelectedDisplay?.Id;
        string? displayName = SelectedDisplay?.PhysicalMode?.FriendlyName;
        string? monitorPath = SelectedDisplay?.PhysicalMode?.MonitorDevicePath;
        if (!string.IsNullOrWhiteSpace(displayName) && DisplayComboBox.Items.OfType<DisplayChoice>().Count(
                display => string.Equals(display.PhysicalMode?.FriendlyName?.Trim(), displayName.Trim(),
                    StringComparison.OrdinalIgnoreCase)) > 1)
        {
            _displayAudioDevice = null;
            ApplyDisplayAudioState("Multiple displays share this name; their speakers cannot be matched reliably.");
            return;
        }
        if (_displayAudioDevice is null) DisplayAudioStatusText.Text = "Checking display speakers…";
        try
        {
            var device = await DisplayAudioDevices.FindAsync(displayName);
            if (_closing || version != _displayAudioVersion || !ReferenceEquals(output, _output) ||
                displayId != SelectedDisplay?.Id || !DisplayAudioOutputConnected ||
                displayName != SelectedDisplay?.PhysicalMode?.FriendlyName ||
                monitorPath != SelectedDisplay?.PhysicalMode?.MonitorDevicePath) return;
            _displayAudioDevice = device;
            ApplyDisplayAudioState();
        }
        catch (Exception ex)
        {
            if (_closing || version != _displayAudioVersion) return;
            _displayAudioDevice = null;
            ApplyDisplayAudioState("Could not check display audio. Refresh displays to retry.");
            AppLog.Write("Display audio discovery", ex);
        }
    }

    private void ApplyDisplayAudioState(string? unavailableStatus = null)
    {
        bool available = DisplayAudioOutputConnected && _displayAudioDevice is not null;
        _updatingDisplayAudio = true;
        EnableDisplayAudioCheckBox.IsEnabled = available;
        EnableDisplayAudioCheckBox.IsChecked = available && _displayAudioRequested;
        _updatingDisplayAudio = false;
        DisplayAudioStatusText.Text = available
            ? (_displayAudioRequested ? _displayAudioDevice!.Name : "Display audio is off.")
            : unavailableStatus ?? "No matching audio output is available for this display.";
        _photocopierSound.ConfigureOutput(SelectedDisplay?.PhysicalMode?.FriendlyName,
            _displayAudioDevice, available && _displayAudioRequested);
    }

    private void EnableDisplayAudio_Changed(object sender, RoutedEventArgs args)
    {
        if (_updatingDisplayAudio || !_projectionSetupInitialized || _loadingProjectionProfile) return;
        SetDisplayAudioEnabled(EnableDisplayAudioCheckBox.IsChecked == true);
    }

    private void SetDisplayAudioEnabled(bool enabled)
    {
        if (!EnableDisplayAudioCheckBox.IsEnabled)
            throw new InvalidOperationException("The connected display has no available audio output.");
        _displayAudioRequested = enabled;
        ApplyDisplayAudioState();
        if (_projectionProfileKey is not { } key) return;
        // An unfinished size edit must not erase the last valid optical values
        // or prevent this independent audio choice from being saved.
        var profile = EnteredProjectionProfile();
        if (!profile.IsValid)
            profile = (_sessionProjectionProfiles.GetValueOrDefault(key) ??
                _projectionSetup.Profiles.GetValueOrDefault(key) ?? new()) with { EnableDisplayAudio = enabled };
        _sessionProjectionProfiles[key] = profile;
        if (Projection.ProjectionSizeProfile.PersistentKey(SelectedDisplay?.PhysicalMode) is { } persistentKey)
        {
            _projectionSetup.Profiles[persistentKey] = profile;
            SaveProjectionSettings();
        }
    }

    private void DisposeDisplayAudio()
    {
        _displayAudioVersion++;
        if (_displayAudioWatcher is not { } watcher) return;
        watcher.Added -= DisplayAudioDeviceAdded;
        watcher.Removed -= DisplayAudioDeviceRemoved;
        watcher.Updated -= DisplayAudioDeviceChanged;
        watcher.EnumerationCompleted -= DisplayAudioEnumerationCompleted;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        _displayAudioWatcher = null;
    }
}
