using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntRoundControlsTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void RoundWaitsForInputAndPausesWhileMicrophonePickerIsOpen()
    {
        var board = new BoardSession();
        board.ShowDuckHunt(At(0));
        board.SetDuckHuntReady(false, At(100));
        board.TickDuckHunt(At(30000));
        Assert.Equal(DuckHuntPhase.Ready, board.DuckHuntState.Phase);
        board.SetDuckHuntReady(true, At(30100));
        board.TickDuckHunt(At(34100));
        Assert.Equal(DuckHuntPhase.Walking, board.DuckHuntState.Phase);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(34200)));
        board.TickDuckHunt(At(34500));
        Assert.True(board.ActivateButton("duck-hunt-mic", At(34600)));
        board.SetDuckHuntReady(true, At(34600));
        var paused = board.DuckHuntState;
        Assert.True(paused.Paused);
        board.TickDuckHunt(At(90000));
        Assert.Equal(paused.U, board.DuckHuntState.U);
        Assert.False(board.DuckHuntState.RoundFinished);
        Assert.False(board.TryDuckHuntShot(new(paused.U, paused.V), At(90000)));
        Assert.True(board.ActivateButton("duck-hunt-mic", At(90100)));
        board.SetDuckHuntReady(true, At(90100));
        Assert.False(board.DuckHuntState.Paused);
        Assert.Equal(paused.U, board.DuckHuntState.U);
        board.TickDuckHunt(At(91100));
        Assert.NotEqual(paused.U, board.DuckHuntState.U);
        Assert.Equal(paused.V, board.DuckHuntState.V);
    }

    [Fact]
    public void EscapeShowsReplayWithClosedDrawerAndFreshFullCaptionHoldRestarts()
    {
        var board = new BoardSession();
        board.ShowDuckHunt(At(0));
        long firstRound = board.DuckHuntRoundRevision;
        board.SetDuckHuntReady(true, At(100));
        Assert.DoesNotContain(board.Buttons, button => button.Id == "duck-hunt-replay");
        board.TickDuckHunt(At(30000));
        Assert.Equal(DuckHuntPhase.DuckWon, board.DuckHuntState.Phase);
        var replay = Assert.Single(board.Buttons, button => button.Id == "duck-hunt-replay");
        Assert.Equal("PLAY AGAIN", replay.Label);
        Assert.Equal(BoardButtonHold.Once, replay.Hold);
        Assert.False(board.DuckHuntDrawerOpen);
        // Existing obstruction at the instant the result appears is not a press.
        for (int ms = 30100; ms <= 31300; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([replay.Id], At(ms), At(ms), []));
        Assert.Equal(firstRound, board.DuckHuntRoundRevision);
        board.ObserveHeldButtons([], At(31400), At(31400), [replay.Id]);
        for (int ms = 31500; ms < 32500; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([replay.Id], At(ms), At(ms), []));
        Assert.Equal([replay.Id], board.ObserveHeldButtons([replay.Id], At(32500), At(32500), []));
        Assert.Equal(firstRound + 1, board.DuckHuntRoundRevision);
        Assert.Equal(DuckHuntPhase.Ready, board.DuckHuntState.Phase);
        Assert.Equal(0, board.DuckHuntState.Hits);
        Assert.Equal(0, board.DuckHuntState.Shots);
        Assert.DoesNotContain(board.Buttons, button => button.Id == replay.Id);
        board.SetDuckHuntReady(true, At(32600));
        Assert.Equal(DuckHuntPhase.Walking, board.DuckHuntState.Phase);
    }
}
