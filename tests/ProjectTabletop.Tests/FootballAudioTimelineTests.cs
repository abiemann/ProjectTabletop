using System.Numerics;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class FootballAudioTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KickoffWhistleOnlyFiresOnceDespiteTrackingRecoveryAndRepeatedRenders()
    {
        var timeline = new FootballAudioTimeline();
        Assert.Equal(FootballAudioCue.Stop, Update(timeline, FootballPhase.WaitingForPlayers, 0).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Countdown, 1).Cues);
        Assert.Equal(FootballAudioCue.StartWhistle, Update(timeline, FootballPhase.Playing, 2).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 2).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 3, recovering: true).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.WaitingForPlayers, 4).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Countdown, 5).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 6).Cues);
    }

    [Fact]
    public void ScorelessMatchChantsEveryFifteenSecondsFromKickoff()
    {
        var timeline = StartedTimeline();
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 14.999).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 15).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 15).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 29.999).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 30).Cues);
    }

    [Fact]
    public void EitherPlayersGoalCheersOnceAndRestartsTheChantInterval()
    {
        var timeline = StartedTimeline();
        Assert.Equal(FootballAudioCue.GoalCheer, Update(timeline, FootballPhase.Goal, 10, 1).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Goal, 10, 1).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 15, 1).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 24.999, 1).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 25, 1).Cues);
        Assert.Equal(FootballAudioCue.GoalCheer, Update(timeline, FootballPhase.Goal, 30, 1, 1).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 40, 1, 1).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 45, 1, 1).Cues);
    }

    [Fact]
    public void PausedOrRecoveringPlayNeverChantsAndResumeHasNoBacklog()
    {
        var timeline = StartedTimeline();
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 15, recovering: true).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.WaitingForPlayers, 30).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Countdown, 45).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 70).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 70.001).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 84.999).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 85).Cues);
    }

    [Fact]
    public void WinningGoalEmitsImmediateEndCueAndNeverStartsAnotherChant()
    {
        var timeline = StartedTimeline();
        Update(timeline, FootballPhase.Goal, 10, 4);
        Assert.Equal(FootballAudioCue.GoalCheer | FootballAudioCue.MatchEnd,
            Update(timeline, FootballPhase.Finished, 25, 5).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Finished, 25, 5).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Finished, 26, 5).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Finished, 60, 5).Cues);
    }

    [Fact]
    public void AppearanceAndOrdinarySnapshotRevisionsCannotRestartCelebrations()
    {
        var timeline = StartedTimeline();
        var final = State(FootballPhase.Finished, 5);
        Assert.Equal(FootballAudioCue.GoalCheer | FootballAudioCue.MatchEnd,
            timeline.Update(final, Start.AddSeconds(5), 1, 1, true).Cues);
        Assert.Equal(FootballAudioCue.None,
            timeline.Update(final with { Revision = 999 }, Start.AddSeconds(6), 1, 1, true).Cues);
    }

    [Fact]
    public void NewMatchOrModeStopsOldAudioAndAllowsOneNewKickoff()
    {
        var timeline = StartedTimeline();
        long previous = timeline.Generation;
        var reset = Update(timeline, FootballPhase.WaitingForPlayers, 10, match: 2);
        Assert.Equal(FootballAudioCue.Stop, reset.Cues);
        Assert.True(reset.Generation > previous);
        Assert.Equal(FootballAudioCue.StartWhistle,
            Update(timeline, FootballPhase.Playing, 12, match: 2).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 15, match: 2).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 27, match: 2).Cues);
    }

    [Fact]
    public void LeavingTheBoardStopsAudioAndInvalidatesQueuedCues()
    {
        var timeline = StartedTimeline();
        var queued = Update(timeline, FootballPhase.Goal, 5, 1);
        var exit = Update(timeline, FootballPhase.Goal, 6, 1, navigation: 2, visible: false);
        Assert.Equal(FootballAudioCue.Stop, exit.Cues);
        Assert.True(exit.Generation > queued.Generation);
        Assert.Equal(FootballAudioCue.None,
            Update(timeline, FootballPhase.Goal, 25, 1, navigation: 2, visible: false).Cues);
        Assert.Equal(FootballAudioCue.Stop,
            Update(timeline, FootballPhase.WaitingForPlayers, 30, navigation: 3, match: 2).Cues);
        Assert.Equal(FootballAudioCue.StartWhistle,
            Update(timeline, FootballPhase.Playing, 32, navigation: 3, match: 2).Cues);
    }

    [Fact]
    public void HidingAndRestoringOutputDoesNotReplayKickoffOrWin()
    {
        var timeline = StartedTimeline();
        long previous = timeline.Generation;
        Assert.Equal(FootballAudioCue.Stop, Update(timeline, FootballPhase.Playing, 2, visible: false).Cues);
        Assert.True(timeline.Generation > previous);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 20).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 35).Cues);
        Assert.Equal(FootballAudioCue.GoalCheer | FootballAudioCue.MatchEnd,
            Update(timeline, FootballPhase.Finished, 40, 5).Cues);
        Assert.Equal(FootballAudioCue.Stop, Update(timeline, FootballPhase.Finished, 41, 5, visible: false).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Finished, 42, 5).Cues);
    }

    [Fact]
    public void HiddenGoalsAndWinsAreNotPlayedWhenTheBoardReturns()
    {
        var timeline = StartedTimeline();
        Update(timeline, FootballPhase.Playing, 2, visible: false);
        Assert.Equal(FootballAudioCue.None,
            Update(timeline, FootballPhase.Finished, 5, 5, visible: false).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Finished, 6, 5).Cues);
    }

    private static FootballAudioTimeline StartedTimeline()
    {
        var timeline = new FootballAudioTimeline();
        Update(timeline, FootballPhase.WaitingForPlayers, 0);
        Update(timeline, FootballPhase.Playing, 0);
        return timeline;
    }

    private static FootballAudioUpdate Update(FootballAudioTimeline timeline, FootballPhase phase, double seconds,
        int score1 = 0, int score2 = 0, bool recovering = false, long navigation = 1, long match = 1, bool visible = true)
        => timeline.Update(State(phase, score1, score2) with { RecoveringInput = recovering },
            Start.AddSeconds(seconds), navigation, match, visible);

    private static FootballSnapshot State(FootballPhase phase, int score1 = 0, int score2 = 0)
        => new(Vector2.Zero, Vector2.Zero, FootballGame.BallRadius, 0, Quaternion.Identity, [],
            score1, score2, FootballMode.HumanVsAi, phase, string.Empty, 0, 1);
}
