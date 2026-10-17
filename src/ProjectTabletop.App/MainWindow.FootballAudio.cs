using ProjectTabletop.App.Media;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly DisplaySoundPlayer _footballCrowdSound = new("Football crowd", .275);
    private readonly DisplaySoundVoices _footballGoalSounds = new("Football goal", .55);
    private readonly DisplaySoundPlayer _footballMatchSound = new("Football match", .78);
    private readonly DisplaySoundPlayer _footballAmbienceSound = new("Football stadium ambience", .45);
    private bool _footballAmbienceActive;
    private int _footballChantIndex;
    private static DisplaySoundClip FootballClip(string name) => new(name,
        Path.Combine(AppContext.BaseDirectory, "Assets", "Football", "Audio", name + ".mp3"));
    private static readonly DisplaySoundClip[] FootballKickoffSound = [FootballClip("start-whistle")];
    private static readonly DisplaySoundClip[] FootballAmbienceSound =
        [new("stadium-ambience", Path.Combine(AppContext.BaseDirectory, "Assets", "Football", "Audio", "stadium-ambience.wav"))];
    private static readonly DisplaySoundClip[][] FootballGoalSounds =
    [
        [FootballClip("goal-red")],
        [FootballClip("goal-blue")]
    ];
    private static readonly DisplaySoundClip[][] FootballChants =
    [
        [FootballClip("crowd-chant-1")],
        [FootballClip("crowd-chant-2")]
    ];
    // One playback list provides actual end-to-start sequencing, including when
    // device decoding takes longer than expected. No wall-clock delay can race it.
    private static readonly DisplaySoundClip[] FootballVictorySound =
        [FootballClip("end-whistle"), FootballClip("victory")];

    private object FootballAudioDiagnostics => new
    {
        mutedForMarkerSetup = FootballAudioBlockedByMarkerSetup,
        crowd = _footballCrowdSound.Status,
        goals = _footballGoalSounds.Status,
        match = _footballMatchSound.Status,
        ambience = _footballAmbienceSound.Status
    };

    private bool FootballAudioBlockedByMarkerSetup => _scene.CurrentBoardScreen == BoardScreen.Football &&
        MarkerSetupFirstUse.BlocksFootballAudio(FirstUseMarkerContext(_selectedCameraId ?? string.Empty, ready: true));

    private void UpdateFootballAmbience()
    {
        bool training = FootballAudioBlockedByMarkerSetup;
        _scene.SetFootballAudioTrainingBlocked(training);
        if (training) StopFootballCues();
        if (!_closing && !training && _footballAmbienceActive && _scene.CurrentBoardScreen == BoardScreen.Football)
            _footballAmbienceSound.EnsureLooping(FootballAmbienceSound);
        else
            _footballAmbienceSound.Stop();
    }

    private void StopFootballCues()
    {
        _footballCrowdSound.Stop();
        _footballGoalSounds.Stop();
        _footballMatchSound.Stop();
        _footballChantIndex = 0;
    }

    private void FootballAudioRequested(FootballAudioUpdate update)
    {
        // The scene may raise a cue under its render lock. Only enqueue here;
        // file loading and MediaPlayer work belong to the UI dispatcher.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            // First-use setup starts on this dispatcher too. Check its required
            // profiles before playing an earlier board-entry cue.
            bool training = FootballAudioBlockedByMarkerSetup;
            _scene.SetFootballAudioTrainingBlocked(training);
            if (update.Generation != _scene.FootballAudioGeneration) return;
            if ((update.Cues & FootballAudioCue.Stop) != 0)
                StopFootballCues();
            _footballAmbienceActive = update.AmbienceActive;
            UpdateFootballAmbience();
            if (training || _scene.CurrentBoardScreen != BoardScreen.Football) return;
            if ((update.Cues & FootballAudioCue.StartWhistle) != 0)
                _footballMatchSound.Play(FootballKickoffSound);
            if ((update.Cues & FootballAudioCue.GoalCheer) != 0 && update.ScoringPlayer is 1 or 2)
            {
                // The scorer travels with the cue, so queued playback cannot
                // accidentally use a newer score or a freshly reset match.
                _footballCrowdSound.Stop();
                _footballGoalSounds.Play(FootballGoalSounds[update.ScoringPlayer - 1]);
            }
            else if ((update.Cues & FootballAudioCue.CrowdChant) != 0)
                _footballCrowdSound.Play(FootballChants[_footballChantIndex++ % FootballChants.Length]);
            if ((update.Cues & FootballAudioCue.MatchEnd) != 0)
                _footballMatchSound.Play(FootballVictorySound);
        });
    }
}
