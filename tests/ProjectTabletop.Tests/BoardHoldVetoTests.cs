using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class BoardHoldVetoTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MenuArrowVetoAtActivationCancelsUntilFreshClearAndAFullNewHold(bool secondPage)
    {
        var board = Menu(secondPage);
        string arrow = secondPage ? "menu-scroll-up" : "menu-scroll-down";
        Clear(board, arrow, 0);
        for (int ms = 100; ms <= 1000; ms += 100) Assert.Empty(Hold(board, arrow, ms));
        Assert.Equal(.9, Assert.Single(board.HoldProgress(At(1000))).Progress, 10);

        Assert.Empty(board.ObserveHeldButtons([arrow], At(1100), At(1100), [], [arrow]));
        Assert.Empty(board.HoldProgress(At(1100)));
        Assert.False(board.MenuScrolling);
        Assert.Equal(secondPage, board.MenuScrolled);
        for (int ms = 1200; ms <= 2500; ms += 100)
        {
            Assert.Empty(Hold(board, arrow, ms));
            Assert.Empty(board.HoldProgress(At(ms)));
        }

        Clear(board, arrow, 2600);
        for (int ms = 2700; ms < 3700; ms += 100) Assert.Empty(Hold(board, arrow, ms));
        Assert.Equal([arrow], Hold(board, arrow, 3700));
        Assert.True(board.MenuScrolling);
        Assert.Equal(!secondPage, board.MenuScrolled);
    }

    [Theory]
    [InlineData(1000, 1001)] // Duplicate.
    [InlineData(999, 1001)] // Out of order.
    [InlineData(1100, 1451)] // Stale delivery.
    [InlineData(1101, 1100)] // Future timestamp.
    public void RejectedFramesCannotVetoFreshHold(int frameMilliseconds, int nowMilliseconds)
    {
        var board = Menu(false);
        const string arrow = "menu-scroll-down";
        Clear(board, arrow, 0);
        for (int ms = 100; ms <= 1000; ms += 100) Assert.Empty(Hold(board, arrow, ms));
        Assert.Empty(board.ObserveHeldButtons([arrow], At(frameMilliseconds), At(nowMilliseconds), [], [arrow]));
        Assert.Equal(.9, Assert.Single(board.HoldProgress(At(1000))).Progress, 10);
        Assert.Equal([arrow], Hold(board, arrow, 1100));
    }

    [Fact]
    public void BlockedClearObservationCannotRearmTheSameCaption()
    {
        var board = Menu(false);
        const string arrow = "menu-scroll-down";
        Assert.Empty(board.ObserveHeldButtons([], At(0), At(0), [arrow], [arrow]));
        for (int ms = 100; ms <= 1500; ms += 100)
        {
            Assert.Empty(Hold(board, arrow, ms));
            Assert.Empty(board.HoldProgress(At(ms)));
        }
        Clear(board, arrow, 1600);
        for (int ms = 1700; ms < 2700; ms += 100) Assert.Empty(Hold(board, arrow, ms));
        Assert.Equal([arrow], Hold(board, arrow, 2700));
    }

    [Fact]
    public void VetoOfAnotherCurrentCaptionDoesNotInterruptTheHeldSibling()
    {
        var board = new BoardSession();
        board.ShowPaint(At(-1000));
        board.PaintSaveEnabled = true;
        Assert.True(board.ActivateButton("paint-drawer-open", At(-500)));
        Assert.True(board.TickPaint(At(-200)));
        Clear(board, "paint-save", 0);
        for (int ms = 100; ms <= 1000; ms += 100) Assert.Empty(Hold(board, "paint-save", ms));
        Assert.Equal(["paint-save"], board.ObserveHeldButtons(["paint-save"], At(1100), At(1100), [], ["paint-reset"]));
        Assert.Equal(0, board.PaintResetRevision);
        Assert.True(board.PaintDrawerOpen);
    }

    [Fact]
    public void UnknownOrNonHoldCaptionsDoNotInterruptTheMenuArrow()
    {
        var board = Menu(false);
        const string arrow = "menu-scroll-down";
        Clear(board, arrow, 0);
        for (int ms = 100; ms <= 1000; ms += 100) Assert.Empty(Hold(board, arrow, ms));
        Assert.Equal([arrow], board.ObserveHeldButtons([arrow], At(1100), At(1100), [],
            ["football", "menu-scroll-up", "not-a-button"]));
    }

    [Fact]
    public void VetoKeepsCoveredReplacementArrowSpentUntilGenuineRelease()
    {
        var board = Menu(false);
        Clear(board, "menu-scroll-down", 0);
        for (int ms = 100; ms < 1100; ms += 100) Assert.Empty(Hold(board, "menu-scroll-down", ms));
        Assert.Equal(["menu-scroll-down"], Hold(board, "menu-scroll-down", 1100));
        Assert.True(board.TickMenu(At(1750)));

        const string arrow = "menu-scroll-up";
        for (int ms = 1800; ms <= 2400; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([arrow], At(ms), At(ms), [], [arrow]));
        Clear(board, arrow, 2500);
        for (int ms = 2600; ms <= 3700; ms += 100)
        {
            Assert.Empty(Hold(board, arrow, ms));
            Assert.Empty(board.HoldProgress(At(ms)));
        }
        for (int ms = 3800; ms <= 4200; ms += 100) Clear(board, arrow, ms);
        for (int ms = 4300; ms < 5300; ms += 100) Assert.Empty(Hold(board, arrow, ms));
        Assert.Equal([arrow], Hold(board, arrow, 5300));
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

    private static IReadOnlyList<string> Hold(BoardSession board, string id, int milliseconds) =>
        board.ObserveHeldButtons([id], At(milliseconds), At(milliseconds), []);
    private static void Clear(BoardSession board, string id, int milliseconds) =>
        Assert.Empty(board.ObserveHeldButtons([], At(milliseconds), At(milliseconds), [id]));
}
