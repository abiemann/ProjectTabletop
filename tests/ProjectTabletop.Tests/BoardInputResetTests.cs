using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class BoardInputResetTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PictureResetPreservesBlackjackPresentationBarrier(bool preserve)
    {
        var board = new BoardSession();
        board.ShowBlackjack(At(0));
        board.HoldBlackjackPresentationUntil(At(5000));
        board.ResetInput(At(1000), preserveBoardAnimations: preserve);
        Assert.Equal(!preserve, board.ActivateButton("bj-deal", At(1100)));
        if (preserve) Assert.True(board.ActivateButton("bj-deal", At(5001)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PictureResetPreservesCrownDeedPresentationDeadline(bool preserve)
    {
        var board = new BoardSession();
        board.ShowCrownDeed(At(0));
        board.HoldCrownDeedPresentationUntil(At(5000));
        board.ResetInput(At(1000), preserveBoardAnimations: preserve);
        Assert.Equal(preserve, board.IsCrownDeedPresentationActive(At(1100)));
        Assert.False(board.IsCrownDeedPresentationActive(At(5001)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PictureResetPreservesDrawerButDiscardsPartialAndOldCaptionInput(bool preserve)
    {
        var board = new BoardSession();
        board.ShowPaint(At(0));
        Assert.True(board.ActivateButton("paint-drawer-open", At(10)));
        board.TickPaint(At(400));
        board.ObserveHeldButtons([], At(500), At(500), ["paint-reset"]);
        for (int time = 600; time <= 900; time += 100)
            Assert.Empty(board.ObserveHeldButtons(["paint-reset"], At(time), At(time), []));
        Assert.NotEmpty(board.HoldProgress(At(900)));

        board.ResetInput(At(1000), preserveBoardAnimations: preserve);
        Assert.Equal(preserve, board.PaintDrawerOpen);
        Assert.Empty(board.HoldProgress(At(1000)));
        Assert.Empty(board.ObserveHeldButtons([], At(950), At(1010), ["paint-reset"]));
        for (int time = 1100; time <= 2200; time += 100)
            Assert.Empty(board.ObserveHeldButtons(["paint-reset"], At(time), At(time), []));
        Assert.Equal(0, board.PaintResetRevision);
        Assert.Equal(BoardScreen.Paint, board.Screen);
    }
}
