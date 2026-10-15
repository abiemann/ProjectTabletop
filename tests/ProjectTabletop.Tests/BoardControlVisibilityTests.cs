using System.Numerics;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class BoardControlVisibilityTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);

    [Fact]
    public void AllControlsShareOneFadeAndDuplicateRequestsDoNotRestartIt()
    {
        var board = new BoardSession();
        board.ShowSettings(At(0));
        var buttons = board.Buttons.ToArray();
        Assert.True(board.SetButtonsVisible(false, At(10)));
        long revision = board.Revision;
        Assert.False(board.ButtonsVisible);
        Assert.Empty(board.Buttons);
        Assert.Equal(buttons, board.GetVisibleButtons(At(10)));
        Assert.Equal(1, board.GetButtonsOpacity(At(10)));
        Assert.Equal(.5, board.GetButtonsOpacity(At(160)), 10);
        Assert.True(board.ControlVisibilityAnimating(At(160)));
        Assert.False(board.SetButtonsVisible(false, At(160)));
        Assert.Equal(revision, board.Revision);
        Assert.Equal(.5, board.GetButtonsOpacity(At(160)), 10);
        Assert.False(board.TickControlVisibility(At(309)));
        Assert.False(board.ControlVisibilityAnimating(At(310)));
        Assert.Empty(board.GetVisibleButtons(At(310)));
        Assert.Equal(0, board.GetButtonsOpacity(At(310)));
        Assert.True(board.TickControlVisibility(At(310)));
        Assert.False(board.TickControlVisibility(At(310)));
        Assert.Equal(revision + 1, board.Revision);

        Assert.True(board.SetButtonsVisible(true, At(400)));
        Assert.True(board.ButtonsVisible);
        Assert.Empty(board.Buttons);
        Assert.Equal(buttons, board.GetVisibleButtons(At(400)));
        Assert.Equal(0, board.GetButtonsOpacity(At(400)));
        Assert.Equal(.5, board.GetButtonsOpacity(At(550)), 10);
        Assert.Equal(1, board.GetButtonsOpacity(At(700)));
        Assert.True(board.TickControlVisibility(At(700)));
        Assert.Equal(buttons, board.Buttons);
        Assert.False(board.ControlVisibilityAnimating(At(700)));
    }

    [Fact]
    public void ReversingAnUnfinishedFadeContinuesFromItsCurrentOpacity()
    {
        var board = new BoardSession();
        board.ShowSettings(At(0));
        board.SetButtonsVisible(false, At(10));
        Assert.Equal(.5, board.GetButtonsOpacity(At(160)), 10);
        Assert.True(board.SetButtonsVisible(true, At(160)));
        Assert.Equal(.5, board.GetButtonsOpacity(At(160)), 10);
        Assert.Equal(.75, board.GetButtonsOpacity(At(310)), 10);
        Assert.Empty(board.Buttons);
        Assert.False(board.TickControlVisibility(At(459)));
        Assert.True(board.TickControlVisibility(At(460)));
        Assert.NotEmpty(board.Buttons);
        Assert.Equal(1, board.GetButtonsOpacity(At(460)));
    }

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
        Assert.True(board.SetButtonsVisible(true, At(400)));
        Assert.Empty(board.Buttons);
        Assert.Contains(board.GetVisibleButtons(At(550)), button => button.Id == card.Id);
        Assert.False(board.ActivateButton(card.Id, At(550)));
        // A late render/input tick settles at the exact deadline. Camera
        // observations and pinch anchors from the fade cannot execute afterward.
        var stale = new BoardHandSample(card.Bounds.X + .1, card.Bounds.Y + .08, At(1650), 2, At(650));
        Assert.Null(board.Update([stale], At(650), At(720)));
        Assert.Null(board.Update([stale with { ExecuteEventId = 3 }], At(721), At(721)));
        Assert.Contains(board.Buttons, button => button.Id == card.Id);
        Assert.False(board.ActivateButton(card.Id, At(699)));
        Assert.True(board.ActivateButton(card.Id, At(730)));
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
        Assert.Equal(drawer, board.GetVisibleButtons(At(1250)));
        Assert.Empty(board.Buttons);
        Assert.True(board.TickControlVisibility(At(1540)));
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
        board.SetButtonsVisible(true, At(1200));

        // A late clear result captured while hidden cannot arm the restored arrow.
        Assert.Empty(board.ObserveHeldButtons([], At(880), At(1210), [arrow]));
        Assert.Empty(board.ObserveHeldButtons([], At(1400), At(1520), [arrow]));
        Assert.Empty(board.ObserveHeldButtons([], At(1500), At(1530), [arrow]));
        for (int ms = 1600; ms <= 2600; ms += 200)
            Assert.Empty(board.ObserveHeldButtons([arrow], At(ms), At(ms), []));
        Assert.False(board.WaterGardenDrawerOpen);
        board.ObserveHeldButtons([], At(2700), At(2700), [arrow]);
        for (int ms = 2800; ms < 3800; ms += 200)
            Assert.Empty(board.ObserveHeldButtons([arrow], At(ms), At(ms), []));
        Assert.Equal([arrow], board.ObserveHeldButtons([arrow], At(3800), At(3800), []));
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
        Assert.NotEmpty(board.GetVisibleButtons(At(100)));
        Assert.All(board.GetVisibleButtons(At(100)), button => Assert.False(button.Enabled));
        board.TickControlVisibility(At(330));
        Assert.All(board.Buttons, button => Assert.False(button.Enabled));
        Assert.False(board.ActivateButton("water-drawer-open", At(340)));
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
        Assert.Empty(board.GetVisibleButtons(At(100)));
        board.TickControlVisibility(At(340));
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
        Assert.Equal(1, board.GetButtonsOpacity(At(20)));
        Assert.False(board.ControlVisibilityAnimating(At(20)));
        Assert.False(board.TickControlVisibility(At(1000)));
        Assert.Contains(board.Buttons, button => button.Id == "water-drawer-open");
        board.SetButtonsVisible(false, At(30));
        board.ShowMenu(At(40));
        Assert.True(board.ButtonsVisible);
        Assert.Equal(1, board.GetButtonsOpacity(At(40)));
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
        Assert.Equal(buttons, board.GetVisibleButtons(At(150)));
        Assert.Equal(1, board.GetButtonsOpacity(At(150)));
        Assert.False(board.ControlVisibilityAnimating(At(150)));
        Assert.Equal(revision, board.Revision);
    }

    [Fact]
    public void RenderingUsesCurrentControlStateDuringTheFade()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        board.SetButtonsVisible(false, At(10));
        Assert.All(board.GetVisibleButtons(At(50)), button => Assert.True(button.Enabled));
        board.SetWaterGardenStickPresent(true, At(100));
        Assert.NotEmpty(board.GetVisibleButtons(At(150)));
        Assert.All(board.GetVisibleButtons(At(150)), button => Assert.False(button.Enabled));
        Assert.Empty(board.GetVisibleButtons(At(310)));
    }

    [Fact]
    public void OpeningAnotherBoardDuringFadeInStartsFullyVisible()
    {
        var board = new BoardSession();
        board.ShowSettings(At(0));
        board.SetButtonsVisible(false, At(10));
        board.SetButtonsVisible(true, At(400));
        Assert.Equal(.5, board.GetButtonsOpacity(At(550)), 10);
        board.ShowWaterGarden(At(550));
        Assert.Equal(1, board.GetButtonsOpacity(At(550)));
        Assert.False(board.ControlVisibilityAnimating(At(550)));
        Assert.Contains(board.Buttons, button => button.Id == "water-drawer-open");
        Assert.False(board.TickControlVisibility(At(1000)));
    }
}
