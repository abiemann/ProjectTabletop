using ProjectTabletop.App.Media;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly DisplaySoundPlayer _footballCrowdSound = new("Football crowd", .55);
    private readonly DisplaySoundPlayer _footballMatchSound = new("Football match", .78);
    private int _footballChantIndex;
    private int _footballGoalCheerIndex;
    private static DisplaySoundClip FootballClip(string name) => new(name,
        Path.Combine(AppContext.BaseDirectory, "Assets", "Football", "Audio", name + ".mp3"));
    private static readonly DisplaySoundClip[] FootballKickoffSound = [FootballClip("start-whistle")];
    private static readonly DisplaySoundClip[][] FootballGoalSounds =
    [
        [FootballClip("goal-cheer")],
        [FootballClip("goal-cheer-2")],
        [FootballClip("goal-cheer-3")]
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
        crowd = _footballCrowdSound.Status,
        match = _footballMatchSound.Status
    };

    private void FootballAudioRequested(FootballAudioUpdate update)
    {
        // The scene may raise a cue under its render lock. Only enqueue here;
        // file loading and MediaPlayer work belong to the UI dispatcher.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || update.Generation != _scene.FootballAudioGeneration) return;
            if ((update.Cues & FootballAudioCue.Stop) != 0)
            {
                _footballCrowdSound.Stop();
                _footballMatchSound.Stop();
                _footballChantIndex = 0;
            }
            if (_scene.CurrentBoardScreen != BoardScreen.Football) return;
            if ((update.Cues & FootballAudioCue.StartWhistle) != 0)
                _footballMatchSound.Play(FootballKickoffSound);
            if ((update.Cues & FootballAudioCue.GoalCheer) != 0)
            {
                _footballCrowdSound.Play(FootballGoalSounds[_footballGoalCheerIndex]);
                // Keep the rotation across resets so a new match cannot repeat
                // the previous match's final cheer as its first goal sound.
                _footballGoalCheerIndex = (_footballGoalCheerIndex + 1) % FootballGoalSounds.Length;
            }
            else if ((update.Cues & FootballAudioCue.CrowdChant) != 0)
                _footballCrowdSound.Play(FootballChants[_footballChantIndex++ % FootballChants.Length]);
            if ((update.Cues & FootballAudioCue.MatchEnd) != 0)
                _footballMatchSound.Play(FootballVictorySound);
        });
    }
}
