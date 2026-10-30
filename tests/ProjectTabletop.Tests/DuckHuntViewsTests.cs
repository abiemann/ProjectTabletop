using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntViewsTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void MainMenuTileKeepsExistingPositionsAndOpensTheModeMenu()
    {
        var board = new BoardSession();
        var cards = board.GetMenuCards(At(0));
        Assert.Equal(new BoardRect(.08, .25, .40, .16), Assert.Single(cards, b => b.Id == "slots").Bounds);
        Assert.Equal(new BoardRect(.52, 1.05, .40, .16), Assert.Single(cards, b => b.Id == "blackjack").Bounds);
        Assert.Equal(new BoardRect(.08, 1.25, .40, .16), Assert.Single(cards, b => b.Id == "duck-hunt").Bounds);
        Assert.DoesNotContain(board.Buttons, b => b.Id == "duck-hunt");
        Assert.True(board.ActivateButton("menu-scroll-down", At(10)));
        board.TickMenu(At(660));
        Assert.True(board.ActivateButton("duck-hunt", At(800)));
        Assert.Equal(DuckHuntView.Menu, board.CurrentDuckHuntView);
        var practice = Assert.Single(board.Buttons, b => b.Id == "duck-hunt-target-practice");
        var hunt = Assert.Single(board.Buttons, b => b.Id == "duck-hunt-hunt");
        var clay = Assert.Single(board.Buttons, b => b.Id == "duck-hunt-clay-pigeon");
        Assert.Equal("TARGET PRACTICE", practice.Label);
        Assert.Equal("HUNT", hunt.Label);
        Assert.Equal("CLAY PIGEON", clay.Label);
        Assert.Equal(BoardButtonHold.Once, practice.Hold);
        Assert.Equal(BoardButtonHold.Once, hunt.Hold);
        Assert.Equal(practice.Bounds.Width, hunt.Bounds.Width);
        Assert.Equal(practice.Bounds.Height, hunt.Bounds.Height);
        Assert.Equal(.5, practice.Bounds.X + practice.Bounds.Width / 2);
        Assert.Equal(.5, hunt.Bounds.X + hunt.Bounds.Width / 2);
        Assert.True(practice.Bounds.Y + practice.Bounds.Height < hunt.Bounds.Y);
        Assert.Equal(hunt.Bounds.Width, clay.Bounds.Width);
        Assert.Equal(hunt.Bounds.Height, clay.Bounds.Height);
        Assert.Equal(.5, clay.Bounds.X + clay.Bounds.Width / 2);
        Assert.True(hunt.Bounds.Y + hunt.Bounds.Height < clay.Bounds.Y);
    }

    [Theory]
    [InlineData("duck-hunt-target-practice", DuckHuntView.TargetPractice)]
    [InlineData("duck-hunt-hunt", DuckHuntView.Hunt)]
    [InlineData("duck-hunt-clay-pigeon", DuckHuntView.ClayPigeon)]
    public void ModeChoiceNeedsFreshClearCaptionThenAFullHold(string id, DuckHuntView expected)
    {
        var board = new BoardSession();
        board.ShowDuckHunt(At(0));
        long epoch = board.DuckHuntRoundRevision;
        long revision = board.Revision;
        for (int ms = 100; ms <= 1200; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([id], At(ms), At(ms), []));
        Assert.Equal(DuckHuntView.Menu, board.CurrentDuckHuntView);
        board.ObserveHeldButtons([], At(1300), At(1300), [id]);
        for (int ms = 1400; ms < 2400; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([id], At(ms), At(ms), []));
        Assert.Equal([id], board.ObserveHeldButtons([id], At(2400), At(2400), []));
        Assert.Equal(expected, board.CurrentDuckHuntView);
        Assert.Equal(epoch + 1, board.DuckHuntRoundRevision);
        Assert.True(board.Revision > revision);
        bool practice = expected == DuckHuntView.TargetPractice;
        Assert.Equal(practice, board.DuckHuntDrawerOpen);
        string handle = practice ? "duck-hunt-drawer-close" : "duck-hunt-drawer-open";
        Assert.Contains(board.Buttons, button => button.Id == handle);
        if (practice)
            Assert.All(board.Buttons.Where(button => button.Id != handle), button => Assert.False(button.Enabled));
        else Assert.Single(board.Buttons);
        Assert.Empty(board.HoldProgress(At(2400)));
        // Frames from the old menu cannot prime the new view's controls.
        Assert.Empty(board.ObserveHeldButtons([], At(2399), At(2450), [handle]));
        for (int ms = 2500; ms <= 3600; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([handle], At(ms), At(ms), []));
        Assert.Equal(practice, board.DuckHuntDrawerOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MenuAndHuntKeepThePracticeGameIdleAndExitUsable(bool hunt)
    {
        var board = new BoardSession();
        board.ShowDuckHunt(At(0));
        if (hunt) Assert.True(board.ActivateButton("duck-hunt-hunt", At(10)));
        var idle = board.GalleryState;
        board.SetDuckHuntReady(true, At(100));
        board.TickDuckHunt(At(100000));
        board.SetDuckHuntReady(false, At(100100));
        Assert.False(board.TryDuckHuntShot(new(.5, .48), At(100200)));
        Assert.Equal(idle, board.GalleryState);
        Assert.Empty(board.GalleryPunctures);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(100300)));
        board.TickDuckHunt(At(100600));
        Assert.DoesNotContain(board.Buttons, b => b.Id == "duck-hunt-replay");
        Assert.DoesNotContain(board.Buttons, b => b.Id == "duck-hunt-mic");
        Assert.True(board.ActivateButton("duck-hunt-exit", At(100700)));
        Assert.Equal(BoardScreen.Menu, board.Screen);
    }

    [Fact]
    public void ViewChangesDiscardThePreviousRoundAndOnlyPracticeOpensItsDrawer()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        board.SetDuckHuntReady(true, At(100));
        Assert.True(board.ActivateButton("duck-hunt-play", At(400)));
        board.TickDuckHunt(At(11100));
        var target = DuckHuntGalleryLayout.TargetQuad(0, board.GalleryState, 1);
        Assert.True(board.TryDuckHuntShot(target.Map(.5, .5), At(11100)));
        Assert.NotEmpty(board.GalleryPunctures);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(11200)));
        long epoch = board.DuckHuntRoundRevision;
        board.ShowDuckHuntHunt(At(11500));
        Assert.Equal(epoch + 1, board.DuckHuntRoundRevision);
        Assert.Equal(DuckHuntView.Hunt, board.CurrentDuckHuntView);
        Assert.Equal(DuckHuntGalleryPhase.Ready, board.GalleryState.Phase);
        Assert.Equal(0, board.GalleryState.Shots);
        Assert.Empty(board.GalleryPunctures);
        Assert.False(board.DuckHuntDrawerOpen);
        board.ShowDuckHunt(At(12000));
        Assert.Equal(DuckHuntView.Menu, board.CurrentDuckHuntView);
        Assert.Equal(epoch + 2, board.DuckHuntRoundRevision);
        Assert.False(board.ActivateButton("duck-hunt-replay", At(12010)));
        board.ShowDuckHuntTargetPractice(At(12100));
        board.SetDuckHuntReady(true, At(12200));
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.Equal(DuckHuntGalleryPhase.Ready, board.GalleryState.Phase);
        Assert.Equal(0, board.GalleryState.HitTargets);
    }

    [Theory]
    [InlineData(DuckHuntView.Hunt)]
    [InlineData(DuckHuntView.TargetPractice)]
    [InlineData(DuckHuntView.ClayPigeon)]
    public void BackReturnsToWelcomeAndClearsTheRound(DuckHuntView view)
    {
        var board = new BoardSession();
        board.ShowDuckHunt(At(0));
        board.ActivateButton(view switch
        {
            DuckHuntView.Hunt => "duck-hunt-hunt",
            DuckHuntView.ClayPigeon => "duck-hunt-clay-pigeon",
            _ => "duck-hunt-target-practice"
        }, At(10));
        if (view == DuckHuntView.TargetPractice)
        {
            board.SetDuckHuntReady(true, At(100));
            Assert.True(board.ActivateButton("duck-hunt-play", At(400)));
            board.TickDuckHunt(At(11100));
            var target = DuckHuntGalleryLayout.TargetQuad(0, board.GalleryState, 1);
            Assert.True(board.TryDuckHuntShot(target.Map(.5, .5), At(11100)));
        }
        int openAt = 11200;
        if (!board.DuckHuntDrawerOpen)
            Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(openAt)));
        board.TickDuckHunt(At(openAt + 300));
        var back = Assert.Single(board.Buttons, button => button.Id == "duck-hunt-back");
        var exit = Assert.Single(board.Buttons, button => button.Id == "duck-hunt-exit");
        Assert.Equal(BoardButtonHold.Once, back.Hold);
        Assert.Equal(exit.Bounds.Width, back.Bounds.Width);
        Assert.Equal(.01, back.Bounds.X - exit.Bounds.X - exit.Bounds.Width, 10);
        Assert.DoesNotContain(board.Buttons, button => button.Id == "duck-hunt-replay");
        long epoch = board.DuckHuntRoundRevision;
        Assert.True(board.ActivateButton(back.Id, At(openAt + 400)));
        Assert.Equal(BoardScreen.DuckHunt, board.Screen);
        Assert.Equal(DuckHuntView.Menu, board.CurrentDuckHuntView);
        Assert.Equal(epoch + 1, board.DuckHuntRoundRevision);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(DuckHuntGalleryPhase.Ready, board.GalleryState.Phase);
        Assert.Equal(0, board.GalleryState.HitTargets);
        Assert.Empty(board.GalleryPunctures);
        Assert.Equal(["duck-hunt-target-practice", "duck-hunt-hunt", "duck-hunt-clay-pigeon", "duck-hunt-drawer-open"],
            board.Buttons.Select(button => button.Id));
        Assert.False(board.ActivateButton("duck-hunt-back", At(openAt + 500)));
    }
}
