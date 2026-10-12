#if DEBUG
using ProjectTabletop.App.Media;
using Windows.Storage;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Silent, isolated players exercise actual Windows decoding, playlist order,
    // and output cancellation without changing the live match or audio setting.
    private async Task<object> VerifyFootballAudioAsync()
    {
        await RefreshDisplayAudioAsync();
        var device = _displayAudioDevice ?? throw new InvalidOperationException("Open an output with display speakers first.");
        string? display = SelectedDisplay?.PhysicalMode?.FriendlyName;
        using var player = new DisplaySoundPlayer("Football audio verification", 0);
        var clips = FootballKickoffSound.Concat(FootballGoalSounds.SelectMany(c => c)).Concat(FootballChants.SelectMany(c => c))
            .Concat(FootballVictorySound).ToArray();
        var durations = new Dictionary<string, double>();
        player.ConfigureOutput(display, device, true);
        foreach (var clip in clips)
        {
            var file = await StorageFile.GetFileFromPathAsync(clip.FilePath!);
            var properties = await file.Properties.GetMusicPropertiesAsync();
            Require(properties.Duration.TotalSeconds > .1, $"Empty audio asset: {clip.Name}");
            durations[clip.Name] = properties.Duration.TotalSeconds;
            player.Play([clip]);
            await WaitFor(() => player.Status.ActiveClip == clip.Name && player.Status.PlaybackState == "Playing",
                $"Windows could not play {clip.Name}");
            player.Stop();
        }

        player.Play(FootballVictorySound);
        await WaitFor(() => player.Status.ActiveClip == "end-whistle" && player.Status.PlaybackState == "Playing",
            "The final whistle did not start first");
        await WaitFor(() => player.Status.ActiveClip == "victory" && player.Status.PlaybackState == "Playing",
            "The stadium celebration did not follow the final whistle");
        player.Stop();

        player.Play(FootballVictorySound);
        await WaitFor(() => player.Status.ActiveClip == "end-whistle" && player.Status.PlaybackState == "Playing",
            "The final sequence could not replay");
        player.ConfigureOutput(display, device, false);
        await Task.Delay(TimeSpan.FromSeconds(durations["end-whistle"] + .25));
        Require(!player.Status.Enabled && player.Status.ActiveClip is null,
            "Muting left a queued victory sound active");
        player.ConfigureOutput(display, device, true);
        Require(player.Status.ActiveClip is null, "Unmuting replayed an old sound");

        player.Play([new("photocopier", WaveFactory: PhotocopierWave.Create)]);
        await WaitFor(() => player.Status.ActiveClip == "photocopier" && player.Status.PlaybackState == "Playing",
            "Shared playback no longer supports the Photo Copy sound");
        player.Stop();
        Require(player.Status.ActiveClip is null, "Stopping a sound did not clear playback");
        player.Play(FootballKickoffSound);
        await WaitFor(() => player.Status.PlaybackState == "Playing", "Cached sound did not start");
        await WaitFor(() => player.Status.ActiveClip is null, "Sound did not finish");
        player.Play(FootballKickoffSound);
        await WaitFor(() => player.Status.ActiveClip == "start-whistle" && player.Status.PlaybackState == "Playing",
            "A completed sound could not replay from the cached playlist");
        player.Stop();
        return new { passed = true, clips = durations, finalWhistleBeforeVictory = true,
            muteCancelsSequence = true, unmuteDoesNotReplay = true, cachedReplay = true,
            photoCopyWave = true, output = device.Name };

        async Task WaitFor(Func<bool> ready, string message)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            while (!ready() && DateTimeOffset.UtcNow < deadline)
            {
                if (player.Status.Error is { } error) throw new InvalidOperationException(message + ": " + error);
                await Task.Delay(20);
            }
            Require(ready(), message);
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
