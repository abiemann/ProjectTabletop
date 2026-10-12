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
            .Concat(FootballVictorySound).Concat(FootballAmbienceSound).ToArray();
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

        player.EnsureLooping(FootballAmbienceSound);
        var loopRequestedAt = player.Status.PlayRequestedAt;
        for (int repeat = 0; repeat < 10; repeat++) player.EnsureLooping(FootballAmbienceSound);
        await WaitFor(() => player.Status.ActiveClip == "stadium-ambience" && player.Status.PlaybackState == "Playing",
            "The stadium ambience did not start");
        // Exercise native overlapping goals during the ambience's first loop.
        var loopCheckStarted = System.Diagnostics.Stopwatch.StartNew();
        using (var goals = new DisplaySoundVoices("Football overlapping goal verification", 0))
        {
            goals.ConfigureOutput(display, device, false);
            int idleGoalChannels = goals.Status.Length;
            goals.Play(FootballGoalSounds[0]);
            Require(goals.Status.Length == idleGoalChannels &&
                goals.Status.All(s => s is { PlayRequestedAt: null, PlaybackState: "None", Enabled: false }),
                "Muted goals allocated or started a channel");
            goals.ConfigureOutput(display, device, true);
            goals.Play(FootballGoalSounds[0]);
            var firstGoalRequestedAt = goals.Status[0].PlayRequestedAt;
            // These requests deliberately arrive while the first source is
            // still opening. Opening channels must never be considered idle.
            goals.Play(FootballGoalSounds[0]);
            goals.Play(FootballGoalSounds[1]);
            await WaitFor(() => goals.Status is { Length: 3 } statuses &&
                statuses.All(s => s is { PlaybackState: "Playing", Error: null }) &&
                statuses.Count(s => s.ActiveClip == "goal-red") == 2 &&
                statuses.Count(s => s.ActiveClip == "goal-blue") == 1,
                "Three consecutive goals did not play on independent channels");
            Require(goals.Status[0].PlayRequestedAt == firstGoalRequestedAt,
                "A later goal restarted the first cheer");
            goals.ConfigureOutput(display, device, true);
            Require(goals.Status.All(s => s.ActiveClip is not null), "Refreshing the output stopped active cheers");
            await WaitFor(() => goals.Status.All(s => s.ActiveClip is null), "Goal cheers did not finish naturally");
            goals.Play(FootballGoalSounds[0]);
            goals.Play(FootballGoalSounds[1]);
            await WaitFor(() => goals.Status.Count(s => s is { PlaybackState: "Playing", ActiveClip: not null }) == 2,
                "Completed goal channels could not be reused");
            Require(goals.Status.Length == 3, "Completed goal channels were not reused");
            goals.Stop();
            Require(goals.Status.All(s => s is { ActiveClip: null, PlaybackState: "None" }),
                "Reset or exit left a goal channel playing");
            goals.Play(FootballGoalSounds[0]);
            goals.Play(FootballGoalSounds[1]);
            goals.ConfigureOutput(display, device, false);
            goals.Play(FootballGoalSounds[0]);
            Require(goals.Status.All(s => s is { ActiveClip: null, Enabled: false }), "Muting left a goal channel active");
            goals.ConfigureOutput(display, device, true);
            Require(goals.Status.All(s => s.ActiveClip is null), "Unmuting replayed old goal cheers");
            goals.Play(FootballGoalSounds[0]);
            goals.Play(FootballGoalSounds[1]);
            goals.ConfigureOutput(display, null, true);
            Require(goals.Status.All(s => s is { ActiveClip: null, Enabled: false }),
                "Disconnecting the output left a goal channel active");
        }
        var remainingLoop = TimeSpan.FromSeconds(durations["stadium-ambience"] + .3) - loopCheckStarted.Elapsed;
        if (remainingLoop > TimeSpan.Zero) await Task.Delay(remainingLoop);
        player.EnsureLooping(FootballAmbienceSound);
        Require(player.Status is { IsLooping: true, PlaybackState: "Playing", ActiveClip: "stadium-ambience", Error: null } &&
            player.Status.PlayRequestedAt == loopRequestedAt, "The ambience stopped or restarted at its loop boundary");
        player.ConfigureOutput(display, device, false);
        player.EnsureLooping(FootballAmbienceSound);
        Require(player.Status is { ActiveClip: null, IsLooping: false }, "Muted ambience restarted");
        player.ConfigureOutput(display, device, true);
        Require(player.Status.ActiveClip is null, "Enabling output replayed ambience without an active-board request");
        player.EnsureLooping(FootballAmbienceSound);
        await WaitFor(() => player.Status.ActiveClip == "stadium-ambience" && player.Status.PlaybackState == "Playing",
            "The active board could not resume ambience after unmuting");
        player.Play(FootballKickoffSound);
        await WaitFor(() => player.Status.ActiveClip == "start-whistle" && player.Status.PlaybackState == "Playing",
            "Switching from a loop to a one-shot failed");
        await WaitFor(() => player.Status.ActiveClip is null, "A one-shot inherited looping playback");
        player.Stop();
        Require(!player.Status.IsLooping, "Stopping left ambience looping");
        return new { passed = true, clips = durations, finalWhistleBeforeVictory = true,
            muteCancelsSequence = true, unmuteDoesNotReplay = true, cachedReplay = true,
            photoCopyWave = true, ambienceLoops = true, ambienceDoesNotRestart = true,
            layeredGoalCheer = true, consecutiveSameSideGoalsOverlap = true, goalChannelsReused = true,
            stopAndMuteCancelAllGoals = true, ambienceMuteAndResume = true, output = device.Name };

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
