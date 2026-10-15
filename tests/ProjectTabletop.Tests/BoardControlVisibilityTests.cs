using System.Numerics;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class BoardControlVisibilityTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);

    [Fact]
    public void HiddenSettingsControlsCannotBeClickedOrSelected()
    {
        var board = new BoardSession();
        board.ShowSettings(At(0));
        var card = board.Buttons.Single(button => button.Id == "hand-tracking");
        long navigation = board.NavigationRevision;
        Assert.True(board.SetButtonsVisible(false, At(10)));
        Assert.Empty(board.Buttons);
        Assert.False(board.ActivateButton(card.Id, At(20)));
        var hand = new BoardHandSample(card.Bounds.X + .1, card.Bounds.Y + .08,
            At(1030), 1);
        Assert.Null(board.Update([hand], At(30), At(30)));
        Assert.Equal(BoardScreen.Settings, board.Screen);
        Assert.Equal(navigation, board.NavigationRevision);
        Assert.True(board.SetButtonsVisible(true, At(40)));
        Assert.Contains(board.Buttons, button => button.Id == card.Id);
        Assert.True(board.ActivateButton(card.Id, At(50)));
        Assert.Equal(BoardScreen.HandTracking, board.Screen);
    }

    [Fact]
    public void MenuPageStaysVisibleAndWaterDrawerStateSurvivesVisibilityChanges()
    {
        var board = new BoardSession();
        Assert.True(board.ActivateButton("menu-scroll-down", At(10)));
        board.TickMenu(At(700));
        Assert.True(board.MenuScrolled);
        var page = board.Buttons.ToArray();
        Assert.False(board.ControlSwipesEnabled);
        Assert.False(board.SetButtonsVisible(false, At(710)));
        Assert.False(board.SetButtonsVisible(true, At(720)));
        Assert.True(board.ButtonsVisible);
        Assert.NotEmpty(board.GetMenuCards(At(720)));
        Assert.True(board.MenuScrolled);
        Assert.Equal(page, board.Buttons);

        board.ShowWaterGarden(At(800));
        Assert.True(board.ActivateButton("water-drawer-open", At(810)));
        board.TickWaterGarden(At(1200));
        Assert.True(board.ActivateButton("water-garden-duck-add", At(1210)));
        long navigation = board.NavigationRevision, reset = board.WaterGardenResetRevision;
        var drawer = board.Buttons.ToArray();
        Assert.True(board.SetButtonsVisible(false, At(1220)));
        Assert.True(board.WaterGardenDrawerOpen);
        Assert.False(board.ActivateButton("water-garden-reset", At(1230)));
        Assert.True(board.SetButtonsVisible(true, At(1240)));
        Assert.Equal(drawer, board.Buttons);
        Assert.Equal(1, board.WaterGardenAddedDuckCount);
        Assert.Equal(reset, board.WaterGardenResetRevision);
        Assert.Equal(navigation, board.NavigationRevision);
    }

    [Fact]
    public void RestoredHoldRequiresFreshClearCaptionAndAFullHold()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        const string arrow = "water-drawer-open";
        board.ObserveHeldButtons([], At(10), At(10), [arrow]);
        for (int ms = 20; ms <= 820; ms += 200)
            Assert.Empty(board.ObserveHeldButtons([arrow], At(ms), At(ms), []));
        Assert.NotEmpty(board.HoldProgress(At(820)));
        board.SetButtonsVisible(false, At(850));
        Assert.Empty(board.HoldProgress(At(850)));
        board.SetButtonsVisible(true, At(900));

        // A late clear result captured while hidden cannot arm the restored arrow.
        Assert.Empty(board.ObserveHeldButtons([], At(880), At(910), [arrow]));
        for (int ms = 1000; ms <= 2200; ms += 200)
            Assert.Empty(board.ObserveHeldButtons([arrow], At(ms), At(ms), []));
        Assert.False(board.WaterGardenDrawerOpen);
        board.ObserveHeldButtons([], At(2300), At(2300), [arrow]);
        for (int ms = 2400; ms < 3400; ms += 200)
            Assert.Empty(board.ObserveHeldButtons([arrow], At(ms), At(ms), []));
        Assert.Equal([arrow], board.ObserveHeldButtons([arrow], At(3400), At(3400), []));
        Assert.True(board.WaterGardenDrawerOpen);
    }

    [Fact]
    public void ShowDoesNotEnableControlsDisarmedByWand()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        board.SetWaterGardenStickPresent(true, At(10));
        board.SetButtonsVisible(false, At(20));
        board.SetButtonsVisible(true, At(30));
        Assert.All(board.Buttons, button => Assert.False(button.Enabled));
        Assert.False(board.ActivateButton("water-drawer-open", At(40)));
        Assert.True(board.WaterGardenStickPresent);
    }

    [Fact]
    public void VisibilityDoesNotResetFootballOrRevealUnavailableControls()
    {
        var board = new BoardSession();
        board.ShowFootball(At(0));
        Assert.True(board.ActivateButton("football-play", At(10)));
        board.SetFootballInput(0, new Vector2(-.5f, .3f), At(20));
        board.TickFootball(At(20));
        Assert.False(board.FootballControlsVisible);
        var state = board.FootballState;
        long inputRevision = board.FootballInputRevision;
        board.SetButtonsVisible(false, At(30));
        board.SetButtonsVisible(true, At(40));
        Assert.Empty(board.Buttons);
        Assert.Equal(state.Phase, board.FootballState.Phase);
        Assert.Equal(state.Kickers, board.FootballState.Kickers);
        Assert.Equal(inputRevision, board.FootballInputRevision);
    }

    [Fact]
    public void OpeningAnotherBoardRestoresItsControls()
    {
        var board = new BoardSession();
        board.ShowSettings(At(0));
        Assert.True(board.SetButtonsVisible(false, At(10)));
        board.ShowWaterGarden(At(20));
        Assert.True(board.ButtonsVisible);
        Assert.Contains(board.Buttons, button => button.Id == "water-drawer-open");
        board.SetButtonsVisible(false, At(30));
        board.ShowMenu(At(40));
        Assert.True(board.ButtonsVisible);
        Assert.NotEmpty(board.GetMenuCards(At(40)));
    }

    [Theory]
    [InlineData(BoardScreen.Menu)]
    [InlineData(BoardScreen.StrykersRun)]
    public void ExcludedBoardsRetainTheirControls(BoardScreen screen)
    {
        var board = new BoardSession();
        if (screen == BoardScreen.StrykersRun) board.ShowStrykersRun(At(0));
        var buttons = board.Buttons.ToArray();
        long revision = board.Revision;
        Assert.False(board.ControlSwipesEnabled);
        Assert.False(board.SetButtonsVisible(false, At(10)));
        Assert.Equal(buttons, board.Buttons);
        Assert.Equal(revision, board.Revision);
    }
}
