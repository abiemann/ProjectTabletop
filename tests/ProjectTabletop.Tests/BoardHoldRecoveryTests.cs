using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class BoardHoldRecoveryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentActivityCancelsProgressButAllowsAFullNewHoldWithoutLifting(bool secondPage)
    {
        var board = Menu(secondPage);
        string arrow = secondPage ? "menu-scroll-up" : "menu-scroll-down";
        Feed(board, [], 0, clear: [arrow]);
        for (int ms = 100; ms <= 1000; ms += 100) Assert.Empty(Feed(board, [arrow], ms));
        Assert.Equal(.9, Assert.Single(board.HoldProgress(At(1000))).Progress, 10);

        // A veto arriving on the activation frame must win. Continuous activity
        // cannot accumulate progress or reactivate the arrow, however long held.
        for (int ms = 1100; ms <= 2600; ms += 100)
        {
            Assert.Empty(Feed(board, [arrow], ms, blocked: [arrow], recoverable: [arrow]));
            Assert.Empty(board.HoldProgress(At(ms)));
            Assert.False(board.MenuScrolling);
        }

        // Preserve the verified clear caption, not the elapsed 900 ms of hold.
        for (int ms = 2700; ms < 3700; ms += 100) Assert.Empty(Feed(board, [arrow], ms));
        Assert.Equal([arrow], Feed(board, [arrow], 3700));
        Assert.True(board.MenuScrolling);
        Assert.Equal(!secondPage, board.MenuScrolled);
    }

    [Fact]
    public void RecoverableBlockCannotInventClearReadinessAtStartup()
    {
        var board = Menu(false);
        const string arrow = "menu-scroll-down";
        Assert.Empty(Feed(board, [], 0, clear: [arrow], blocked: [arrow], recoverable: [arrow]));
        for (int ms = 100; ms <= 1500; ms += 100)
        {
            Assert.Empty(Feed(board, [arrow], ms));
            Assert.Empty(board.HoldProgress(At(ms)));
        }
        Assert.Empty(Feed(board, [], 1600, clear: [arrow]));
        for (int ms = 1700; ms < 2700; ms += 100) Assert.Empty(Feed(board, [arrow], ms));
        Assert.Equal([arrow], Feed(board, [arrow], 2700));
    }

    [Fact]
    public void LaterRecoverableBlockCannotUndoAHardReachVeto()
    {
        var board = Menu(false);
        const string arrow = "menu-scroll-down";
        Feed(board, [], 0, clear: [arrow]);
        for (int ms = 100; ms <= 500; ms += 100) Assert.Empty(Feed(board, [arrow], ms));
        Assert.Empty(Feed(board, [arrow], 600, blocked: [arrow]));
        Assert.Empty(Feed(board, [arrow], 700, blocked: [arrow], recoverable: [arrow]));
        for (int ms = 800; ms <= 2400; ms += 100)
        {
            Assert.Empty(Feed(board, [arrow], ms));
            Assert.Empty(board.HoldProgress(At(ms)));
        }
        Feed(board, [], 2500, clear: [arrow]);
        for (int ms = 2600; ms < 3600; ms += 100) Assert.Empty(Feed(board, [arrow], ms));
        Assert.Equal([arrow], Feed(board, [arrow], 3600));
    }

    [Theory]
    [InlineData(1000, 1001)]
    [InlineData(999, 1001)]
    [InlineData(1100, 1451)]
    [InlineData(1101, 1100)]
    public void RejectedRecoverableVetoCannotCancelFreshProgress(int frameMilliseconds, int nowMilliseconds)
    {
        var board = Menu(false);
        const string arrow = "menu-scroll-down";
        Feed(board, [], 0, clear: [arrow]);
        for (int ms = 100; ms <= 1000; ms += 100) Assert.Empty(Feed(board, [arrow], ms));
        Assert.Empty(board.ObserveHeldButtons([arrow], At(frameMilliseconds), At(nowMilliseconds), [], [arrow], [arrow]));
        Assert.Equal(.9, Assert.Single(board.HoldProgress(At(1000))).Progress, 10);
        Assert.Equal([arrow], Feed(board, [arrow], 1100));
    }

    [Fact]
    public void RecoverableVetoDoesNotReleaseAnAlreadyUsedArrowPlace()
    {
        var board = Menu(false);
        Feed(board, [], 0, clear: ["menu-scroll-down"]);
        for (int ms = 100; ms < 1100; ms += 100) Assert.Empty(Feed(board, ["menu-scroll-down"], ms));
        Assert.Equal(["menu-scroll-down"], Feed(board, ["menu-scroll-down"], 1100));
        Assert.True(board.TickMenu(At(1750)));

        const string arrow = "menu-scroll-up";
        // One clear frame is enough to verify new artwork, but not the required
        // physical lift interval. Continued covering must not page back.
        Feed(board, [], 1800, clear: [arrow]);
        Assert.Empty(Feed(board, [arrow], 1900, blocked: [arrow], recoverable: [arrow]));
        for (int ms = 2000; ms <= 3600; ms += 100)
        {
            Assert.Empty(Feed(board, [arrow], ms));
            Assert.Empty(board.HoldProgress(At(ms)));
        }
        for (int ms = 3700; ms <= 4100; ms += 100) Feed(board, [], ms, clear: [arrow]);
        for (int ms = 4200; ms < 5200; ms += 100) Assert.Empty(Feed(board, [arrow], ms));
        Assert.Equal([arrow], Feed(board, [arrow], 5200));
    }

    private static BoardSession Menu(bool secondPage)
    {
        var board = new BoardSession();
        if (secondPage)
        {
            Assert.True(board.ActivateButton("menu-scroll-down", At(-1000)));
            Assert.True(board.TickMenu(At(-350)));
        }
        return board;
    }

    private static IReadOnlyList<string> Feed(BoardSession board, string[] held, int milliseconds,
        string[]? clear = null, string[]? blocked = null, string[]? recoverable = null) =>
        board.ObserveHeldButtons(held, At(milliseconds), At(milliseconds), clear ?? [], blocked, recoverable);
}
