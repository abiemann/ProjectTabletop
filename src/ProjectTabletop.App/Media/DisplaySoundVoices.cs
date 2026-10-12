using Windows.Devices.Enumeration;

namespace ProjectTabletop.App.Media;

// UI-thread one-shot playback that overlaps existing cues rather than stealing
// their channel. Each voice retains the shared player's output and cleanup rules.
internal sealed class DisplaySoundVoices(string name, double volume) : IDisposable
{
    private readonly List<DisplaySoundPlayer> _voices = [new(name, volume)];
    private string? _displayName;
    private DeviceInformation? _device;
    private bool _enabled, _disposed;

    internal DisplaySoundPlayer.AudioStatus[] Status => _voices.Select(voice => voice.Status).ToArray();

    internal void ConfigureOutput(string? displayName, DeviceInformation? device, bool enabled)
    {
        if (_disposed) return;
        _displayName = displayName;
        _device = device;
        _enabled = enabled;
        foreach (var voice in _voices) voice.ConfigureOutput(displayName, device, enabled);
    }

    internal void Play(IReadOnlyList<DisplaySoundClip> clips)
    {
        // Use the first player's validated endpoint, not a second routing policy.
        if (_disposed || clips.Count == 0 || !_voices[0].IsEnabled) return;
        foreach (var voice in _voices)
        {
            if (voice.IsActive) continue;
            voice.Play(clips);
            return;
        }

        // Allocate only for overlapping cues; completed voices are reused.
        var added = new DisplaySoundPlayer(name, volume);
        added.ConfigureOutput(_displayName, _device, _enabled);
        _voices.Add(added);
        added.Play(clips);
    }

    internal void Stop()
    {
        if (_disposed) return;
        foreach (var voice in _voices) voice.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var voice in _voices) voice.Dispose();
        _voices.Clear();
        _device = null;
    }
}
