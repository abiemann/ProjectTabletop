using Windows.Devices.Enumeration;

namespace ProjectTabletop.App.Media;

internal sealed class PhotocopierSound : IDisposable
{
    private static readonly DisplaySoundClip[] Clips = [new("Photo Copy", WaveFactory: PhotocopierWave.Create)];
    private readonly DisplaySoundPlayer _player = new("Photo Copy sound", .7);

    internal DisplaySoundPlayer.AudioStatus Status => _player.Status;

    internal void ConfigureOutput(string? displayName, DeviceInformation? device, bool enabled) =>
        _player.ConfigureOutput(displayName, device, enabled);

    // Sound remains independent of whether the captured image can save.
    internal void Play() => _player.Play(Clips);

    public void Dispose() => _player.Dispose();
}
