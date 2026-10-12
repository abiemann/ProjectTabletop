using System.Numerics;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class FootballAudioTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadyPreviewKeepsAmbienceButWaitsForPlayBeforeMatchSounds()
    {
        var timeline = new FootballAudioTimeline();
        var entry = Update(timeline, FootballPhase.Ready, 0);
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged, entry.Cues);
        Assert.True(entry.AmbienceActive);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Ready, 60).Cues);
        Assert.Equal(FootballAudioCue.Stop, Update(timeline, FootballPhase.WaitingForPlayers, 61, match: 2).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Countdown, 62, match: 2).Cues);
        Assert.Equal(FootballAudioCue.StartWhistle, Update(timeline, FootballPhase.Playing, 64, match: 2).Cues);
        Assert.Equal(FootballAudioCue.None, Update(timeline, FootballPhase.Playing, 78, match: 2).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 79, match: 2).Cues);
    }

    [Fact]
    public void KickoffWhistleOnlyFiresOnceDespiteTrackingRecoveryAndRepeatedRenders()
    {
        var timeline = new FootballAudioTimeline();
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged,
            Update(timeline, FootballPhase.WaitingForPlayers, 0).Cues);
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConsecutiveGoalsKeepTheSameScoringSideAndRepeatedFramesHaveNoScorer(int player)
    {
        var timeline = StartedTimeline();
        for (int score = 1; score <= 3; score++)
        {
            int score1 = player == 1 ? score : 0, score2 = player == 2 ? score : 0;
            var goal = Update(timeline, FootballPhase.Goal, score * 5, score1, score2);
            Assert.Equal(FootballAudioCue.GoalCheer, goal.Cues);
            Assert.Equal(player, goal.ScoringPlayer);
            var repeated = Update(timeline, FootballPhase.Goal, score * 5, score1, score2);
            Assert.Equal(FootballAudioCue.None, repeated.Cues);
            Assert.Equal(0, repeated.ScoringPlayer);
            var resumed = Update(timeline, FootballPhase.Playing, score * 5 + 1, score1, score2);
            Assert.Equal(FootballAudioCue.None, resumed.Cues);
            Assert.Equal(0, resumed.ScoringPlayer);
        }
    }

    [Fact]
    public void AlternatingGoalsCaptureTheirOwnScorerWithoutChangingEarlierCues()
    {
        var timeline = StartedTimeline();
        var first = Update(timeline, FootballPhase.Goal, 5, 1, 0);
        var second = Update(timeline, FootballPhase.Goal, 10, 1, 1);
        var third = Update(timeline, FootballPhase.Goal, 15, 2, 1);
        var fourth = Update(timeline, FootballPhase.Goal, 20, 2, 2);
        Assert.Equal(new[] { 1, 2, 1, 2 }, new[] {
            first.ScoringPlayer, second.ScoringPlayer, third.ScoringPlayer, fourth.ScoringPlayer });
        Assert.All(new[] { first, second, third, fourth },
            cue => Assert.Equal(FootballAudioCue.GoalCheer, cue.Cues));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void WinningGoalEmitsItsScoringSideAndImmediateEndCueWithoutAnotherChant(int player)
    {
        var timeline = StartedTimeline();
        int score1 = player == 1 ? 4 : 0, score2 = player == 2 ? 4 : 0;
        Update(timeline, FootballPhase.Goal, 10, score1, score2);
        score1 += player == 1 ? 1 : 0;
        score2 += player == 2 ? 1 : 0;
        var winner = Update(timeline, FootballPhase.Finished, 25, score1, score2);
        Assert.Equal(FootballAudioCue.GoalCheer | FootballAudioCue.MatchEnd, winner.Cues);
        Assert.Equal(player, winner.ScoringPlayer);
        foreach (double seconds in new[] { 25.0, 26, 60 })
        {
            var repeated = Update(timeline, FootballPhase.Finished, seconds, score1, score2);
            Assert.Equal(FootballAudioCue.None, repeated.Cues);
            Assert.Equal(0, repeated.ScoringPlayer);
        }
    }

    [Fact]
    public void ResetClearsThePreviousScorerFromStopKickoffAndChantCues()
    {
        var timeline = StartedTimeline();
        var previousGoal = Update(timeline, FootballPhase.Goal, 5, 0, 1);
        Assert.Equal(2, previousGoal.ScoringPlayer);
        var reset = Update(timeline, FootballPhase.WaitingForPlayers, 6, match: 2);
        Assert.Equal(FootballAudioCue.Stop, reset.Cues);
        Assert.Equal(0, reset.ScoringPlayer);
        Assert.True(reset.Generation > previousGoal.Generation);
        var kickoff = Update(timeline, FootballPhase.Playing, 8, match: 2);
        Assert.Equal(FootballAudioCue.StartWhistle, kickoff.Cues);
        Assert.Equal(0, kickoff.ScoringPlayer);
        var chant = Update(timeline, FootballPhase.Playing, 23, match: 2);
        Assert.Equal(FootballAudioCue.CrowdChant, chant.Cues);
        Assert.Equal(0, chant.ScoringPlayer);
        Assert.Equal(1, Update(timeline, FootballPhase.Goal, 24, 1, 0, match: 2).ScoringPlayer);
        Assert.Equal(2, previousGoal.ScoringPlayer);
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
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged, exit.Cues);
        Assert.Equal(1, queued.ScoringPlayer);
        Assert.Equal(0, exit.ScoringPlayer);
        Assert.True(exit.Generation > queued.Generation);
        Assert.Equal(FootballAudioCue.None,
            Update(timeline, FootballPhase.Goal, 25, 1, navigation: 2, visible: false).Cues);
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged,
            Update(timeline, FootballPhase.WaitingForPlayers, 30, navigation: 3, match: 2).Cues);
        Assert.Equal(FootballAudioCue.StartWhistle,
            Update(timeline, FootballPhase.Playing, 32, navigation: 3, match: 2).Cues);
    }

    [Fact]
    public void HidingAndRestoringOutputDoesNotReplayKickoffOrWin()
    {
        var timeline = StartedTimeline();
        long previous = timeline.Generation;
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged,
            Update(timeline, FootballPhase.Playing, 2, visible: false).Cues);
        Assert.True(timeline.Generation > previous);
        Assert.Equal(FootballAudioCue.AmbienceChanged, Update(timeline, FootballPhase.Playing, 20).Cues);
        Assert.Equal(FootballAudioCue.CrowdChant, Update(timeline, FootballPhase.Playing, 35).Cues);
        Assert.Equal(FootballAudioCue.GoalCheer | FootballAudioCue.MatchEnd,
            Update(timeline, FootballPhase.Finished, 40, 5).Cues);
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged,
            Update(timeline, FootballPhase.Finished, 41, 5, visible: false).Cues);
        Assert.Equal(FootballAudioCue.AmbienceChanged, Update(timeline, FootballPhase.Finished, 42, 5).Cues);
    }

    [Fact]
    public void HiddenGoalsAndWinsAreNotPlayedWhenTheBoardReturns()
    {
        var timeline = StartedTimeline();
        Update(timeline, FootballPhase.Playing, 2, visible: false);
        Assert.Equal(FootballAudioCue.None,
            Update(timeline, FootballPhase.Finished, 5, 5, visible: false).Cues);
        Assert.Equal(FootballAudioCue.AmbienceChanged, Update(timeline, FootballPhase.Finished, 6, 5).Cues);
    }

    [Fact]
    public void AmbienceStartsWhileWaitingAndContinuesThroughPlayRecoveryWinAndRematch()
    {
        var timeline = new FootballAudioTimeline();
        var entry = Update(timeline, FootballPhase.WaitingForPlayers, 0);
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged, entry.Cues);
        Assert.True(entry.AmbienceActive);
        var continuing = new[]
        {
            Update(timeline, FootballPhase.Countdown, 1),
            Update(timeline, FootballPhase.Playing, 2),
            Update(timeline, FootballPhase.Playing, 3, recovering: true),
            Update(timeline, FootballPhase.WaitingForPlayers, 4),
            Update(timeline, FootballPhase.Playing, 5),
            Update(timeline, FootballPhase.Goal, 6, 1),
            Update(timeline, FootballPhase.Finished, 7, 5),
            Update(timeline, FootballPhase.WaitingForPlayers, 8, match: 2),
            Update(timeline, FootballPhase.Countdown, 9, match: 2),
            Update(timeline, FootballPhase.Playing, 10, match: 2)
        };
        Assert.All(continuing, cue =>
        {
            Assert.True(cue.AmbienceActive);
            Assert.Equal(FootballAudioCue.None, cue.Cues & FootballAudioCue.AmbienceChanged);
        });
        Assert.Equal(FootballAudioCue.Stop, continuing[7].Cues);
    }

    [Fact]
    public void AmbienceOnlyNotifiesOnVisibilityChangesAndKeepsCapturedStateImmutable()
    {
        var timeline = new FootballAudioTimeline();
        var initialHidden = Update(timeline, FootballPhase.WaitingForPlayers, 0, visible: false);
        Assert.Equal(FootballAudioCue.Stop, initialHidden.Cues);
        Assert.False(initialHidden.AmbienceActive);
        var stillHidden = Update(timeline, FootballPhase.WaitingForPlayers, 1, visible: false);
        Assert.Equal(FootballAudioCue.None, stillHidden.Cues);
        Assert.False(stillHidden.AmbienceActive);
        var entry = Update(timeline, FootballPhase.WaitingForPlayers, 2);
        Assert.Equal(FootballAudioCue.AmbienceChanged, entry.Cues);
        Assert.True(entry.AmbienceActive);
        for (int frame = 0; frame < 60; frame++)
        {
            var unchanged = Update(timeline, FootballPhase.WaitingForPlayers, 2 + frame / 60.0);
            Assert.Equal(FootballAudioCue.None, unchanged.Cues);
            Assert.True(unchanged.AmbienceActive);
        }
        var hidden = Update(timeline, FootballPhase.WaitingForPlayers, 4, visible: false);
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged, hidden.Cues);
        Assert.False(hidden.AmbienceActive);
        Assert.True(hidden.Generation > entry.Generation);
        var restored = Update(timeline, FootballPhase.WaitingForPlayers, 5);
        Assert.Equal(FootballAudioCue.AmbienceChanged, restored.Cues);
        Assert.True(restored.AmbienceActive);
        var exit = Update(timeline, FootballPhase.WaitingForPlayers, 6, navigation: 2, visible: false);
        Assert.Equal(FootballAudioCue.Stop | FootballAudioCue.AmbienceChanged, exit.Cues);
        Assert.False(exit.AmbienceActive);
        Assert.True(entry.AmbienceActive);
        Assert.False(hidden.AmbienceActive);
        Assert.True(restored.AmbienceActive);
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
