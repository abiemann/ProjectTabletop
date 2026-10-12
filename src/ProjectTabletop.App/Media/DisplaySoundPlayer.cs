using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace ProjectTabletop.App.Media;

internal sealed record DisplaySoundClip(string Name, string? FilePath = null, Func<byte[]>? WaveFactory = null);

// The UI dispatcher requests sounds; native playback owns their timing. This
// service never discovers devices or performs work from a render callback.
internal sealed class DisplaySoundPlayer(string name, double volume) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<MediaSource> _sources = [];
    private readonly List<IRandomAccessStream> _streams = [];
    private MediaPlayer? _player;
    private MediaPlaybackList? _playlist;
    private IReadOnlyList<DisplaySoundClip>? _clips;
    private DeviceInformation? _device;
    private bool _enabled, _disposed, _playing, _looping;

    // Opening already occupies a voice. These managed flags avoid native state
    // reads and diagnostic allocations when choosing an idle playback channel.
    internal bool IsActive { get { lock (_gate) return _playing; } }
    internal bool IsEnabled { get { lock (_gate) return _enabled; } }

    internal sealed record AudioStatus(string? RequestedDisplay, string OutputDevice,
        bool UsesProjectorSpeakers, DateTimeOffset? PlayRequestedAt, string? Error,
        string PlaybackState = "None", bool Enabled = false, string? ActiveClip = null,
        string? Sequence = null, bool IsLooping = false);
    private AudioStatus _status = new(null, "Unavailable", false, null, null);
    internal AudioStatus Status
    {
        get
        {
            lock (_gate)
            {
                string state = "None";
                string? activeClip = null;
                try
                {
                    state = _player?.PlaybackSession.PlaybackState.ToString() ?? "None";
                    if (_playing && _playlist is { } playlist && _clips is { } clips &&
                        playlist.CurrentItemIndex < clips.Count)
                        activeClip = clips[(int)playlist.CurrentItemIndex].Name;
                }
                catch (Exception) { state = "Unavailable"; }
                return _status with { PlaybackState = state, ActiveClip = activeClip, IsLooping = _looping };
            }
        }
    }

    // An explicit, enabled display endpoint is mandatory. Changing or muting
    // that endpoint disposes the whole playlist, including its queued clips.
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

    // Callers keep immutable clip arrays so repeated cues reuse their prepared
    // player and sources. One-shot sequences restart only on an explicit request.
    internal void Play(IReadOnlyList<DisplaySoundClip> clips) => Start(clips, looping: false);

    // Native repeat keeps the background continuous. Repeated requests with the
    // same immutable array must not seek, recreate sources, or restart opening.
    internal void EnsureLooping(IReadOnlyList<DisplaySoundClip> clips) => Start(clips, looping: true);

    private void Start(IReadOnlyList<DisplaySoundClip> clips, bool looping)
    {
        lock (_gate)
        {
            if (_disposed || !_enabled || _device is null || clips.Count == 0) return;
            if (looping && _looping && _playing && _player is not null &&
                _status.Error is null && ReferenceEquals(_clips, clips)) return;
            try
            {
                if (_status.Error is not null || _looping != looping || !ReferenceEquals(_clips, clips)) ReleasePlayer();
                if (_player is null) Prepare(clips, looping);
                else if (_playlist!.CurrentItemIndex < clips.Count &&
                    _player.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused)
                {
                    // Fresh sources open asynchronously. Seeking before they
                    // are ready fails with MF_E_INVALIDREQUEST on real devices.
                    if (_playlist.CurrentItemIndex != 0)
                    {
                        _player.Pause();
                        _playlist.MoveTo(0);
                    }
                    else
                    {
                        _player.Pause();
                        _player.PlaybackSession.Position = TimeSpan.Zero;
                    }
                }
                _playing = true;
                _status = _status with { PlayRequestedAt = DateTimeOffset.UtcNow, Error = null };
                _player!.Play();
            }
            catch (Exception ex) { Fail(ex); }
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (!_disposed) ReleasePlayer();
        }
    }

    private void Prepare(IReadOnlyList<DisplaySoundClip> clips, bool looping)
    {
        _clips = clips;
        _looping = looping;
        _playlist = new MediaPlaybackList { AutoRepeatEnabled = looping, ShuffleEnabled = false };
        _playlist.ItemFailed += OnItemFailed;
        foreach (var clip in clips)
        {
            MediaSource source;
            if (clip.WaveFactory is { } createWave)
            {
                var stream = new MemoryStream(createWave(), writable: false).AsRandomAccessStream();
                _streams.Add(stream);
                source = MediaSource.CreateFromStream(stream, "audio/wav");
            }
            else
            {
                string path = Path.GetFullPath(clip.FilePath ?? throw new InvalidOperationException("Sound has no source."));
                if (!File.Exists(path)) throw new FileNotFoundException("Sound file was not found.", path);
                source = MediaSource.CreateFromUri(new Uri(path));
            }
            _sources.Add(source);
            _playlist.Items.Add(new MediaPlaybackItem(source));
        }
        _player = new MediaPlayer { AudioDevice = _device, Volume = volume, IsLoopingEnabled = false };
        _player.CommandManager.IsEnabled = false;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
        _player.Source = _playlist;
        _status = _status with { Sequence = string.Join(" → ", clips.Select(clip => clip.Name)) };
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _player)) return;
            Fail(new InvalidOperationException(args.ErrorMessage));
        }
    }

    private void OnItemFailed(MediaPlaybackList sender, MediaPlaybackItemFailedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _playlist)) return;
            // A failed opening whistle must not silently skip into a queued cue.
            Fail(args.Error.ExtendedError ?? new InvalidOperationException(args.Error.ErrorCode.ToString()));
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _player)) return;
            if (!_looping) _playing = false;
        }
    }

    private void Fail(Exception error)
    {
        AppLog.Write(name, error);
        _status = _status with { Error = error.Message };
        ReleasePlayer();
    }

    // Caller holds _gate. Clear identities first so callbacks queued by an old
    // player or playlist cannot affect a replacement after stop/mute/reconnect.
    private void ReleasePlayer()
    {
        var player = _player;
        var playlist = _playlist;
        _player = null;
        _playlist = null;
        _clips = null;
        _playing = false;
        _looping = false;
        if (playlist is not null) playlist.ItemFailed -= OnItemFailed;
        if (player is not null)
        {
            player.MediaFailed -= OnMediaFailed;
            player.MediaEnded -= OnMediaEnded;
            Release(player);
        }
        foreach (var source in _sources) Release(source);
        foreach (var stream in _streams) Release(stream);
        _sources.Clear();
        _streams.Clear();
    }

    private void Release(IDisposable resource)
    {
        try { resource.Dispose(); }
        catch (Exception ex) { AppLog.Write(name + " cleanup", ex); }
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
