using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 22, 18, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void VisibleAimOnSilhouetteHitsOnceFallsAndReturns()
    {
        var game = new DuckHuntGame();
        game.Reset(At(0));
        Assert.True(game.Shoot(new(.5, .5), At(100)));
        Assert.Equal(DuckHuntPhase.Falling, game.Snapshot.Phase);
        Assert.Equal(1, game.Snapshot.Hits);
        Assert.False(game.Shoot(new(.5, .5), At(200)));
        Assert.Equal(1, game.Snapshot.Shots);
        game.Advance(At(700));
        Assert.True(game.Snapshot.V > .48);
        Assert.True(game.Snapshot.Rotation > 0);
        game.Advance(At(1250));
        Assert.False(game.Snapshot.DuckVisible);
        Assert.Equal(DuckHuntPhase.Waiting, game.Snapshot.Phase);
        game.Advance(At(1700));
        Assert.Equal(DuckHuntPhase.Aiming, game.Snapshot.Phase);
        Assert.Equal(.5, game.Snapshot.U);
        Assert.Equal(.48, game.Snapshot.V);
        Assert.True(game.Shoot(new(.5, .5), At(1800)));
        Assert.Equal(2, game.Snapshot.Hits);
    }

    [Fact]
    public void MissingAimAndBackgroundCornersNeverHit()
    {
        var game = new DuckHuntGame();
        game.Reset(At(0));
        Assert.False(game.Shoot(null, At(100)));
        Assert.False(game.Shoot(new(double.NaN, .5), At(200)));
        Assert.False(game.Shoot(new(2, .5), At(300)));
        Assert.Equal(0, game.Snapshot.Shots);
        // Inside the rectangular extent, but outside every duck shape.
        Assert.False(game.Shoot(new(.32, .37), At(400)));
        Assert.Equal(1, game.Snapshot.Shots);
        Assert.Equal(0, game.Snapshot.Hits);
        Assert.Equal(DuckHuntPhase.Aiming, game.Snapshot.Phase);
        Assert.False(game.Shoot(new(.5, .5), At(400)));
        Assert.Equal(1, game.Snapshot.Shots);
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    public void HeadAndBillUseTheSameAspectCorrectSilhouetteAsTheRenderer(double aspect)
    {
        var duck = new DuckHuntGame().Snapshot;
        Assert.True(DuckHuntLayout.ContainsDuck(new(.595, .48 - .065 * aspect), duck, aspect));
        Assert.True(DuckHuntLayout.ContainsDuck(new(.65, .48 - .055 * aspect), duck, aspect));
        Assert.True(DuckHuntLayout.ContainsDuck(new(.35, .48 + .02 * aspect), duck, aspect));
        Assert.False(DuckHuntLayout.ContainsDuck(new(.68, .48 + .08 * aspect), duck, aspect));
    }

    [Fact]
    public void DelayedClickCanFollowRenderTickButCannotHitAReplacementTarget()
    {
        var game = new DuckHuntGame();
        game.Reset(At(0));
        game.Advance(At(200));
        Assert.True(game.Shoot(new(.5, .5), At(180)));
        game.Advance(At(1800));
        Assert.Equal(DuckHuntPhase.Aiming, game.Snapshot.Phase);
        Assert.False(game.Shoot(new(.5, .5), At(1600)));
        Assert.Equal(1, game.Snapshot.Hits);
        var before = game.Snapshot;
        game.Advance(At(100));
        Assert.Equal(before, game.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotBeforeRespawnCannotHitReplacementWhenAudioConfirmsAfterward(bool renderAdvanced)
    {
        var game = new DuckHuntGame();
        game.Reset(At(0));
        Assert.True(game.Shoot(new(.5, .5), At(100)));
        // The replacement appears at 1700 ms. The camera image still shows the
        // waiting interval, although audio confirmation is 20 ms after respawn.
        game.Advance(At(renderAdvanced ? 1720 : 1680));
        var gate = new DuckHuntAimGate();
        var shot = gate.EvaluateSnapshot(new(.5, .5), At(1680), At(1660), At(1720), At(1725));
        Assert.NotNull(shot);
        Assert.False(game.Shoot(shot.Value.Aim, shot.Value.ClickedAt));
        Assert.Equal(1, game.Snapshot.Hits);
        Assert.Equal(1, game.Snapshot.Shots);

        var next = gate.EvaluateSnapshot(new(.5, .5), At(1780), At(1740), At(1800), At(1805));
        Assert.NotNull(next);
        Assert.True(game.Shoot(next.Value.Aim, next.Value.ClickedAt));
        Assert.Equal(2, game.Snapshot.Hits);
    }

    [Fact]
    public void MenuOpensDuckHuntAndCaptionHoldExitRestoresSecondPage()
    {
        var board = new BoardSession();
        Assert.True(board.ActivateButton("menu-scroll-down", At(10)));
        Assert.True(board.TickMenu(At(660)));
        Assert.True(board.ActivateButton("duck-hunt", At(800)));
        Assert.Equal(BoardScreen.DuckHunt, board.Screen);
        Assert.Equal("Duck Hunt", board.Title);
        Assert.Equal("duck-hunt-drawer-open", Assert.Single(board.Buttons).Id);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(810)));
        board.TickDuckHunt(At(1110));
        var exit = Assert.Single(board.Buttons, button => button.Id == "duck-hunt-exit");
        Assert.Equal("EXIT", exit.Label);
        Assert.Equal(BoardButtonHold.Once, exit.Hold);
        Assert.False(board.ControlSwipesEnabled);
        Assert.False(board.SetButtonsVisible(false, At(1200)));
        Assert.True(board.TryDuckHuntShot(new(.5, .5), At(1250)));
        board.ObserveHeldButtons([], At(1300), At(1300), [exit.Id]);
        for (int ms = 1400; ms < 2400; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([exit.Id], At(ms), At(ms), []));
        Assert.Equal([exit.Id], board.ObserveHeldButtons([exit.Id], At(2400), At(2400), []));
        Assert.Equal(BoardScreen.Menu, board.Screen);
        Assert.True(board.MenuScrolled);
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(2500)));
        Assert.True(board.ActivateButton("duck-hunt", At(2600)));
        Assert.Equal(0, board.DuckHuntState.Hits);
        Assert.Equal(DuckHuntPhase.Aiming, board.DuckHuntState.Phase);
    }

    [Fact]
    public void MicrophonePickerRetainsExpansionAcrossDrawerAndSelectsOneDevice()
    {
        var board = OpenDrawer();
        int requests = 0;
        var selections = new List<string?>();
        board.DuckHuntMicrophonesRequested += () => requests++;
        board.DuckHuntMicrophoneSelected += selections.Add;
        board.SetDuckHuntMicrophones([new("usb", "USB microphone"), new("array", "Desk microphone array")],
            null, false, null, At(350));
        Assert.True(board.ActivateButton("duck-hunt-mic", At(400)));
        Assert.True(board.DuckHuntMicrophoneMenuOpen);
        Assert.Equal(1, requests);
        Assert.Equal("SYSTEM DEFAULT", board.Buttons.Single(button => button.Id == "duck-hunt-microphone-0").Label);
        Assert.All(board.Buttons.Where(button => !button.Id.Contains("drawer")),
            button => Assert.Equal(button.Label.ToUpperInvariant(), button.Label));
        Assert.False(board.TryDuckHuntShot(new(.5, .48), At(410)));
        Assert.Equal(0, board.DuckHuntState.Shots);
        Assert.True(board.ActivateButton("duck-hunt-drawer-close", At(450)));
        Assert.False(board.DuckHuntMicrophoneMenuOpen);
        Assert.Equal("duck-hunt-drawer-open", Assert.Single(board.Buttons).Id);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(500)));
        Assert.True(board.DuckHuntMicrophoneMenuOpen);
        Assert.Equal(2, requests);
        Assert.False(board.ActivateButton("duck-hunt-microphone-1", At(600)));
        board.TickDuckHunt(At(800));
        Assert.True(board.ActivateButton("duck-hunt-microphone-1", At(810)));
        Assert.Equal(["usb"], selections);
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.False(board.DuckHuntMicrophoneMenuOpen);
        Assert.Equal("USB microphone", board.DuckHuntSelectedMicrophoneLabel);
        Assert.True(board.ActivateButton("duck-hunt-mic", At(850)));
        Assert.True(board.Buttons.Single(button => button.Id == "duck-hunt-mic").Enabled);
        Assert.True(board.ActivateButton("duck-hunt-microphone-0", At(900)));
        Assert.Equal(new string?[] { "usb", null }, selections);
    }

    [Fact]
    public void MicrophonePagesReachEveryDeviceAndLoadingLeavesExitAndMicUsable()
    {
        var board = OpenDrawer();
        var devices = Enumerable.Range(0, 9).Select(index =>
            new DuckHuntMicrophoneChoice("id" + index, "A very long microphone endpoint name " + index)).ToArray();
        board.SetDuckHuntMicrophones(devices, "id8", true, null, At(350));
        board.ActivateButton("duck-hunt-mic", At(400));
        Assert.Equal(2, board.DuckHuntMicrophonePageCount);
        Assert.True(board.Buttons.Single(button => button.Id == "duck-hunt-exit").Enabled);
        Assert.True(board.Buttons.Single(button => button.Id == "duck-hunt-mic").Enabled);
        Assert.All(board.Buttons.Where(button => button.Id.StartsWith("duck-hunt-microphone-")),
            button => Assert.False(button.Enabled));
        Assert.False(board.ActivateButton("duck-hunt-microphones-next", At(410)));
        board.SetDuckHuntMicrophones(devices, "id8", false, null, At(450));
        var firstPage = board.Buttons.Where(button => button.Id.StartsWith("duck-hunt-microphone-")).ToArray();
        Assert.Equal(6, firstPage.Length);
        Assert.All(firstPage, button =>
        {
            Assert.Equal(BoardButtonHold.Once, button.Hold);
            Assert.All(button.Label.Split('\n'), line => Assert.InRange(line.Length, 1, 14));
            Assert.True(button.Bounds.Y + button.Bounds.Height < DuckHuntLayout.MicrophoneBounds.Y);
        });
        Assert.True(board.ActivateButton("duck-hunt-microphones-next", At(500)));
        var secondPage = board.Buttons.Where(button => button.Id.StartsWith("duck-hunt-microphone-")).ToArray();
        Assert.Equal(4, secondPage.Length);
        Assert.Equal(10, firstPage.Concat(secondPage).Select(button => button.Id).Distinct().Count());
        Assert.Equal(10, firstPage.Concat(secondPage).Select(button => button.Label).Distinct().Count());
        string? selected = null;
        board.DuckHuntMicrophoneSelected += id => selected = id;
        Assert.True(board.ActivateButton("duck-hunt-microphone-9", At(550)));
        Assert.Equal("id8", selected);
    }

    [Fact]
    public void RefreshedMicrophonesAndUpperChoiceEvidenceCancelPartialMicHold()
    {
        var board = OpenDrawer();
        const string mic = "duck-hunt-mic";
        board.ObserveHeldButtons([], At(350), At(350), [mic]);
        for (int ms = 400; ms <= 1000; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([mic], At(ms), At(ms), []));
        Assert.NotEmpty(board.HoldProgress(At(1000)));
        board.SetDuckHuntMicrophones([new("usb", "USB input")], null, false, null, At(1050));
        Assert.Empty(board.HoldProgress(At(1050)));
        for (int ms = 1100; ms <= 2200; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([mic], At(ms), At(ms), []));
        Assert.False(board.DuckHuntMicrophoneMenuOpen);
        Assert.True(board.ActivateButton(mic, At(2300)));
        board.ObserveHeldButtons([], At(2350), At(2350), [mic]);
        for (int ms = 2400; ms <= 3000; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([mic], At(ms), At(ms), []));
        Assert.NotEmpty(board.HoldProgress(At(3000)));
        Assert.Empty(board.ObserveHeldButtons([mic], At(3100), At(3100), [], [mic]));
        Assert.Empty(board.HoldProgress(At(3100)));
        for (int ms = 3200; ms <= 4500; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([mic], At(ms), At(ms), []));
        Assert.True(board.DuckHuntMicrophoneMenuOpen);
    }

    private static BoardSession OpenDrawer()
    {
        var board = new BoardSession();
        board.ShowDuckHunt(At(0));
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(10)));
        Assert.False(board.ActivateButton("duck-hunt-mic", At(200)));
        board.TickDuckHunt(At(310));
        return board;
    }
}
