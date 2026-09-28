using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace ProjectTabletop.App.Media;

internal sealed class PhotocopierSound : IDisposable
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private MediaSource? _source;
    private IRandomAccessStream? _stream;
    private DeviceInformation? _device;
    private bool _enabled;
    private bool _disposed;

    internal sealed record AudioStatus(string? RequestedDisplay, string OutputDevice,
        bool UsesProjectorSpeakers, DateTimeOffset? PlayRequestedAt, string? Error,
        string PlaybackState = "None", bool Enabled = false);
    private AudioStatus _status = new(null, "Unavailable", false, null, null);
    internal AudioStatus Status
    {
        get
        {
            lock (_gate)
            {
                string state = "None";
                try { state = _player?.PlaybackSession.PlaybackState.ToString() ?? "None"; }
                catch (Exception) { state = "Unavailable"; }
                return _status with { PlaybackState = state };
            }
        }
    }

    // Discovery belongs to the connected display lifecycle, not each shutter press.
    // Never leave an old device playing when output changes or audio is disabled.
    internal void ConfigureOutput(string? displayName, DeviceInformation? device, bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (device is not null && (!device.IsEnabled ||
                !DisplayAudioDevices.MatchesProjector(device.Name, displayName))) device = null;
            bool available = enabled && device is not null;
            bool changed = _enabled != available || _device?.Id != device?.Id ||
                _device?.Name != device?.Name || _status.RequestedDisplay != displayName;
            if (changed) ReleasePlayer();
            _device = device;
            _enabled = available;
            if (changed)
                _status = new(displayName, device?.Name ?? "Unavailable", available, null, null,
                    Enabled: available);
        }
    }

    // Called once a Copy request has passed capture validation. Playback starts
    // asynchronously and never determines whether the image can save.
    internal void Play()
    {
        lock (_gate)
        {
            if (_disposed || !_enabled || _device is null) return;
            try
            {
                if (_status.Error is not null) ReleasePlayer();
                if (_player is null)
                {
                    _stream = new MemoryStream(PhotocopierWave.Create(), writable: false).AsRandomAccessStream();
                    _source = MediaSource.CreateFromStream(_stream, "audio/wav");
                    // An explicit display endpoint is mandatory; no default-output fallback.
                    _player = new MediaPlayer { AudioDevice = _device, Volume = .7, IsLoopingEnabled = false };
                    _player.CommandManager.IsEnabled = false;
                    _player.MediaFailed += OnMediaFailed;
                    _player.Source = _source;
                }
                _player.Pause();
                _player.PlaybackSession.Position = TimeSpan.Zero;
                _status = _status with { PlayRequestedAt = DateTimeOffset.UtcNow, Error = null };
                _player.Play();
            }
            catch (Exception ex)
            {
                AppLog.Write("Photo Copy sound", ex);
                _status = _status with { Error = ex.Message };
                ReleasePlayer();
            }
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        lock (_gate)
        {
            // A queued failure from a released player cannot alter the new output.
            if (_disposed || !ReferenceEquals(sender, _player)) return;
            _status = _status with { Error = args.ErrorMessage };
            AppLog.Write("Photo Copy sound", new InvalidOperationException(args.ErrorMessage));
        }
    }

    // Caller holds _gate. Clear references first so late events are harmless.
    private void ReleasePlayer()
    {
        var player = _player;
        var source = _source;
        var stream = _stream;
        _player = null;
        _source = null;
        _stream = null;
        if (player is not null)
        {
            player.MediaFailed -= OnMediaFailed;
            Release(player);
        }
        if (source is not null) Release(source);
        if (stream is not null) Release(stream);
    }

    private static void Release(IDisposable resource)
    {
        try { resource.Dispose(); }
        catch (Exception ex) { AppLog.Write("Photo Copy audio cleanup", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _device = null;
            _status = _status with { Enabled = false, UsesProjectorSpeakers = false };
            ReleasePlayer();
        }
    }
}
