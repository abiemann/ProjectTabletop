namespace ProjectTabletop.Interaction;

[Flags]
public enum FootballAudioCue
{
    None = 0,
    Stop = 1,
    StartWhistle = 2,
    GoalCheer = 4,
    CrowdChant = 8,
    MatchEnd = 16,
    AmbienceChanged = 32
}

/// <summary>Captures board ambience visibility and the scoring player at cue creation:
/// 1 or 2 for a goal, otherwise 0. Waiting and finished matches retain their ambience.</summary>
public readonly record struct FootballAudioUpdate(long Generation, FootballAudioCue Cues, int ScoringPlayer = 0,
    bool AmbienceActive = false);

/// <summary>Produces sound cues from match changes, independently of render frequency and
/// tracking pauses. Playback belongs to the UI audio service, never the simulation thread.</summary>
public sealed class FootballAudioTimeline
{
    public static readonly TimeSpan ChantInterval = TimeSpan.FromSeconds(15);
    private long _navigationRevision = -1, _matchRevision = -1;
    private int _score1, _score2;
    private bool _visible, _started, _finished;
    private DateTimeOffset? _nextChantAt;

    public long Generation { get; private set; }

    public FootballAudioUpdate Update(FootballSnapshot state, DateTimeOffset now,
        long navigationRevision, long matchRevision, bool visible)
    {
        var cues = _visible != visible ? FootballAudioCue.AmbienceChanged : FootballAudioCue.None;
        if (_navigationRevision != navigationRevision || _matchRevision != matchRevision)
        {
            _navigationRevision = navigationRevision;
            _matchRevision = matchRevision;
            _score1 = state.Score1;
            _score2 = state.Score2;
            _started = _finished = false;
            _nextChantAt = null;
            Generation++;
            cues |= FootballAudioCue.Stop;
        }

        if (!visible)
        {
            if (_visible)
            {
                Generation++;
                cues |= FootballAudioCue.Stop;
            }
            _visible = false;
            _nextChantAt = null;
            _score1 = state.Score1;
            _score2 = state.Score2;
            // Hiding the projector must not replay an old kickoff or win later.
            _started |= state.Phase is FootballPhase.Playing or FootballPhase.Goal or FootballPhase.Finished;
            _finished |= state.Phase == FootballPhase.Finished;
            return new(Generation, cues, AmbienceActive: false);
        }
        _visible = true;

        int scoringPlayer = state.Score1 > _score1 ? 1 : state.Score2 > _score2 ? 2 : 0;
        if (scoringPlayer != 0)
        {
            cues |= FootballAudioCue.GoalCheer;
            _nextChantAt = now + ChantInterval;
        }
        _score1 = state.Score1;
        _score2 = state.Score2;

        if (state.Phase == FootballPhase.Finished)
        {
            if (!_finished) cues |= FootballAudioCue.MatchEnd;
            _finished = true;
            _nextChantAt = null;
            return new(Generation, cues, scoringPlayer, AmbienceActive: true);
        }

        if (state.Phase == FootballPhase.Playing && !state.RecoveringInput)
        {
            if (!_started)
            {
                _started = true;
                cues |= FootballAudioCue.StartWhistle;
            }
            _nextChantAt ??= now + ChantInterval;
            if (now >= _nextChantAt)
            {
                cues |= FootballAudioCue.CrowdChant;
                // Emit one chant after a long pause, never a backlog of missed cues.
                _nextChantAt = now + ChantInterval;
            }
        }
        return new(Generation, cues, scoringPlayer, AmbienceActive: true);
    }
}
