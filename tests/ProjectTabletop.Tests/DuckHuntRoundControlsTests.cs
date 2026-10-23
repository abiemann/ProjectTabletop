using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntRoundControlsTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void RoundWaitsForInputAndPausesWhileCaptureOrLearningIsUnavailable()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        board.SetDuckHuntReady(false, At(100));
        board.TickDuckHunt(At(30000));
        Assert.Equal(DuckHuntPhase.Ready, board.DuckHuntState.Phase);
        board.SetDuckHuntReady(true, At(30100));
        board.TickDuckHunt(At(34100));
        Assert.Equal(DuckHuntPhase.Walking, board.DuckHuntState.Phase);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(34200)));
        board.TickDuckHunt(At(34500));
        board.SetDuckHuntReady(false, At(34600));
        var paused = board.DuckHuntState;
        Assert.True(paused.Paused);
        board.TickDuckHunt(At(90000));
        Assert.Equal(paused.U, board.DuckHuntState.U);
        Assert.False(board.DuckHuntState.RoundFinished);
        Assert.False(board.TryDuckHuntShot(new(paused.U, paused.V), At(90000)));
        board.SetDuckHuntReady(true, At(90100));
        Assert.False(board.DuckHuntState.Paused);
        Assert.Equal(paused.U, board.DuckHuntState.U);
        board.TickDuckHunt(At(91100));
        Assert.NotEqual(paused.U, board.DuckHuntState.U);
        Assert.Equal(paused.V, board.DuckHuntState.V);
    }

    [Fact]
    public void EscapeOpensDrawerAndFreshFullCaptionHoldRestartsWithDrawerClosed()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        long firstRound = board.DuckHuntRoundRevision;
        board.SetDuckHuntReady(true, At(100));
        Assert.DoesNotContain(board.Buttons, button => button.Id == "duck-hunt-replay");
        board.TickDuckHunt(At(30000));
        Assert.Equal(DuckHuntPhase.DuckWon, board.DuckHuntState.Phase);
        var replay = Assert.Single(board.Buttons, button => button.Id == "duck-hunt-replay");
        Assert.Equal("PLAY AGAIN", replay.Label);
        Assert.Equal(BoardButtonHold.Once, replay.Hold);
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.False(replay.Enabled);
        Assert.False(board.ActivateButton(replay.Id, At(30200)));
        board.TickDuckHunt(At(30300));
        Assert.True(Assert.Single(board.Buttons, button => button.Id == replay.Id).Enabled);
        // Existing obstruction at the instant the result appears is not a press.
        for (int ms = 30400; ms <= 31300; ms += 100)
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
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal("duck-hunt-drawer-open", Assert.Single(board.Buttons).Id);
        board.SetDuckHuntReady(true, At(32600));
        Assert.Equal(DuckHuntPhase.Walking, board.DuckHuntState.Phase);
        Assert.False(board.DuckHuntDrawerOpen);
    }

    [Fact]
    public void FinishedRoundCanCloseDrawerAndReplayIsAvailableOnlyAfterReopening()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        board.SetDuckHuntReady(true, At(100));
        board.TickDuckHunt(At(30000));
        Assert.True(board.DuckHuntDrawerOpen);
        board.TickDuckHunt(At(30300));
        Assert.True(board.ActivateButton("duck-hunt-drawer-close", At(30400)));
        for (int ms = 30500; ms <= 31500; ms += 100)
        {
            board.TickDuckHunt(At(ms));
            board.SetDuckHuntReady(true, At(ms));
            Assert.False(board.DuckHuntDrawerOpen);
            Assert.Equal("duck-hunt-drawer-open", Assert.Single(board.Buttons).Id);
        }
        Assert.False(board.ActivateButton("duck-hunt-replay", At(31600)));
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(31700)));
        Assert.False(board.ActivateButton("duck-hunt-replay", At(31900)));
        board.TickDuckHunt(At(32000));
        Assert.Equal(["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-back", "duck-hunt-replay"],
            board.Buttons.Select(button => button.Id));
        Assert.True(board.ActivateButton("duck-hunt-replay", At(32100)));
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(DuckHuntPhase.Ready, board.DuckHuntState.Phase);
        Assert.Equal("duck-hunt-drawer-open", Assert.Single(board.Buttons).Id);
    }

    [Fact]
    public void TenthHitOpensDrawerAndStartingAfterReplayClosesIt()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        board.SetDuckHuntReady(true, At(100));
        for (int hit = 1; hit <= DuckHuntGame.HitsToWin; hit++)
        {
            int ms = 11100 + (hit - 1) * 1000;
            board.TickDuckHunt(At(ms));
            var duck = board.DuckHuntState;
            Assert.True(board.TryDuckHuntShot(new(duck.U, duck.V), At(ms)));
            Assert.Equal(hit == DuckHuntGame.HitsToWin, board.DuckHuntDrawerOpen);
        }
        Assert.Equal(DuckHuntPhase.PlayerWon, board.DuckHuntState.Phase);
        Assert.False(Assert.Single(board.Buttons, button => button.Id == "duck-hunt-replay").Enabled);
        board.TickDuckHunt(At(20400));
        Assert.True(board.ActivateButton("duck-hunt-replay", At(20500)));
        Assert.False(board.DuckHuntDrawerOpen);
        // A player can inspect the drawer while waiting for the microphone or
        // camera, but it closes once those inputs actually start the new round.
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(20600)));
        board.TickDuckHunt(At(20900));
        Assert.True(board.DuckHuntDrawerOpen);
        board.SetDuckHuntReady(true, At(21000));
        Assert.Equal(DuckHuntPhase.Walking, board.DuckHuntState.Phase);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal("duck-hunt-drawer-open", Assert.Single(board.Buttons).Id);
    }

    [Fact]
    public void DelayedPreEscapeHitResumesPlayAndClosesTheAutomaticResultDrawer()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        board.SetDuckHuntReady(true, At(100));
        long round = board.DuckHuntRoundRevision;
        board.TickDuckHunt(At(21970));
        var photographed = board.DuckHuntState;
        var bounds = DuckHuntLayout.PhotoBounds(photographed, 1);
        double u = 1300 / DuckHuntLayout.PhotoPixelWidth;
        if (photographed.Direction > 0) u = 1 - u;
        var aim = new BoardAim(bounds.X + u * bounds.Width,
            bounds.Y + 715 / DuckHuntLayout.PhotoPixelHeight * bounds.Height);
        Assert.InRange(aim.U, 0, 1);
        board.TickDuckHunt(At(22120));
        Assert.Equal(DuckHuntPhase.DuckWon, board.DuckHuntState.Phase);
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.True(board.TryDuckHuntShot(aim, At(21970)));
        Assert.Equal(DuckHuntPhase.Turning, board.DuckHuntState.Phase);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(round, board.DuckHuntRoundRevision);
        Assert.Equal("duck-hunt-drawer-open", Assert.Single(board.Buttons).Id);
    }
}
