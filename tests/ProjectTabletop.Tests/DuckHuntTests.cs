using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 22, 18, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void ReadyWaitsForInputAndEachSideStartsEntirelyOutsideTheBoard()
    {
        var game = new DuckHuntGame();
        game.Reset(At(0));
        game.Advance(At(100000));
        Assert.Equal(DuckHuntPhase.Ready, game.Snapshot.Phase);
        Assert.False(game.Shoot(new(.5, .5), At(100001)));
        Assert.False(game.Start(At(100002), double.NaN));
        foreach (bool fromLeft in new[] { true, false })
        {
            game.Reset(At(0));
            Assert.True(game.Start(At(0), enterFromLeft: fromLeft));
            var bounds = DuckHuntLayout.PhotoBounds(game.Snapshot, 1);
            Assert.True(fromLeft ? bounds.X + bounds.Width < 0 : bounds.X > 1);
            Assert.Equal(fromLeft ? 1 : -1, game.Snapshot.Direction);
            Assert.False(game.Start(At(1000), enterFromLeft: !fromLeft));
        }
    }

    [Theory]
    [InlineData(true, .55)]
    [InlineData(false, 1)]
    [InlineData(true, 1.8)]
    public void WalkingIsHorizontalAtConstantSpeedRegardlessOfRenderTickFrequency(bool fromLeft, double aspect)
    {
        var game = Started(fromLeft, aspect);
        double start = game.Snapshot.U;
        game.Advance(At(4000));
        double first = game.Snapshot.U;
        for (int ms = 4010; ms <= 8000; ms += 10) game.Advance(At(ms));
        Assert.Equal(first - start, game.Snapshot.U - first, 12);
        Assert.Equal(.48, game.Snapshot.V);
        Assert.Equal(0, game.Snapshot.Rotation);
        var sparse = Started(fromLeft, aspect);
        sparse.Advance(At(8000));
        Assert.Equal(sparse.Snapshot with { Revision = game.Snapshot.Revision }, game.Snapshot);
        game.Advance(At(11000));
        Assert.Equal(.5, game.Snapshot.U, 12);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuckWinsOnlyAfterWholeDuckEscapesAndThenStops(bool fromLeft)
    {
        var game = Started(fromLeft);
        game.Advance(At(21900));
        Assert.False(game.Snapshot.RoundFinished);
        game.Advance(At(22000));
        Assert.Equal(DuckHuntPhase.DuckWon, game.Snapshot.Phase);
        Assert.False(game.Snapshot.DuckVisible);
        var terminal = game.Snapshot;
        game.Advance(At(30000));
        Assert.Equal(terminal, game.Snapshot);
        Assert.False(game.Shoot(new(.5, .5), At(30001)));
        Assert.Equal(0, game.Snapshot.Shots);
    }

    [Fact]
    public void HitTurnsInPlaceThenWalksBackAtTheSameSpeed()
    {
        var game = Started();
        game.Advance(At(10000));
        double before = game.Snapshot.U;
        game.Advance(At(11000));
        var centre = game.Snapshot;
        Assert.True(game.Shoot(new(centre.U, centre.V), At(11000)));
        Assert.Equal(DuckHuntPhase.Turning, game.Snapshot.Phase);
        Assert.Equal(-1, game.Snapshot.Direction);
        Assert.Equal(1, game.Snapshot.Facing);
        Assert.False(game.Shoot(new(centre.U, centre.V), At(11100)));
        Assert.Equal(1, game.Snapshot.Shots);
        game.Advance(At(11140));
        Assert.Equal(centre.U, game.Snapshot.U);
        Assert.InRange(Math.Abs(game.Snapshot.Facing), 0, 1e-12);
        game.Advance(At(11280));
        Assert.Equal(DuckHuntPhase.Walking, game.Snapshot.Phase);
        Assert.Equal(-1, game.Snapshot.Facing);
        game.Advance(At(12280));
        Assert.Equal(centre.U - before, centre.U - game.Snapshot.U, 12);
        Assert.Equal(.48, game.Snapshot.V);
    }

    [Fact]
    public void TenHitsWinAndResetMakesAFreshRound()
    {
        var game = Started();
        for (int hit = 1; hit <= 10; hit++)
        {
            int ms = 11000 + (hit - 1) * 1000;
            game.Advance(At(ms));
            Assert.True(game.Shoot(new(game.Snapshot.U, game.Snapshot.V), At(ms)));
            Assert.Equal(hit, game.Snapshot.Hits);
            Assert.Equal(hit, game.Snapshot.Shots);
            Assert.Equal(hit == 10 ? DuckHuntPhase.PlayerWon : DuckHuntPhase.Turning, game.Snapshot.Phase);
        }
        var won = game.Snapshot;
        game.Advance(At(60000));
        Assert.Equal(won with { Revision = game.Snapshot.Revision }, game.Snapshot);
        Assert.False(game.Shoot(new(won.U, won.V), At(60001)));
        game.Reset(At(61000));
        Assert.Equal(DuckHuntPhase.Ready, game.Snapshot.Phase);
        Assert.Equal(0, game.Snapshot.Hits);
        Assert.Equal(0, game.Snapshot.Shots);
        Assert.True(game.Start(At(61000), enterFromLeft: false));
        Assert.False(game.Shoot(new(.5, .5), At(20000)));
    }

    [Fact]
    public void MissingAimBackgroundAndDuplicateClicksDoNotMakeHits()
    {
        var game = Started();
        game.Advance(At(11000));
        Assert.False(game.Shoot(null, At(11001)));
        Assert.False(game.Shoot(new(double.NaN, .5), At(11002)));
        Assert.False(game.Shoot(new(2, .5), At(11003)));
        Assert.Equal(0, game.Snapshot.Shots);
        Assert.False(game.Shoot(new(.01, .01), At(11004)));
        Assert.Equal(1, game.Snapshot.Shots);
        Assert.Equal(0, game.Snapshot.Hits);
        Assert.False(game.Shoot(new(.5, .5), At(11004)));
        Assert.False(game.Shoot(new(.5, .5), At(11000)));
        Assert.Equal(1, game.Snapshot.Shots);
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    public void PhotographicHeadBillBodyAndBlankCornersUseTheRenderedAspectAndMirroring(double aspect)
    {
        foreach (bool fromLeft in new[] { true, false })
        {
            var game = Started(fromLeft, aspect);
            game.Advance(At(11000));
            var duck = game.Snapshot;
            var bounds = DuckHuntLayout.PhotoBounds(duck, aspect);
            Assert.Equal(DuckHuntLayout.PhotoPixelWidth / DuckHuntLayout.PhotoPixelHeight,
                bounds.Width * aspect / bounds.Height, 12);
            Assert.True(DuckHuntLayout.ContainsDuck(PhotoPoint(duck, aspect, 400, 150), duck, aspect));
            Assert.True(DuckHuntLayout.ContainsDuck(PhotoPoint(duck, aspect, 165, 285), duck, aspect));
            Assert.True(DuckHuntLayout.ContainsDuck(PhotoPoint(duck, aspect, 800, 620), duck, aspect));
            Assert.False(DuckHuntLayout.ContainsDuck(PhotoPoint(duck, aspect, 100, 100), duck, aspect));
            Assert.False(DuckHuntLayout.ContainsDuck(PhotoPoint(duck, aspect, 240, 340), duck, aspect));
            Assert.False(DuckHuntLayout.ContainsDuck(PhotoPoint(duck, aspect, 1000, 1080), duck, aspect));
        }
    }

    [Fact]
    public void DelayedPhotoHitsHistoricalDuckRatherThanLaterRenderedPosition()
    {
        var game = Started();
        game.Advance(At(11000));
        var photographed = game.Snapshot;
        game.Advance(At(16000));
        Assert.False(DuckHuntLayout.ContainsDuck(new(photographed.U, photographed.V), game.Snapshot));
        Assert.True(game.Shoot(new(photographed.U, photographed.V), At(11000)));
        Assert.Equal(-1, game.Snapshot.Direction);
        Assert.True(game.Snapshot.U < photographed.U);
        Assert.Equal(DuckHuntPhase.Walking, game.Snapshot.Phase);
        var before = game.Snapshot;
        game.Advance(At(100));
        Assert.Equal(before, game.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausePreservesPositionAndTurnProgressAndRejectsPreResumePhotos(bool duringTurn)
    {
        var game = Started();
        game.Advance(At(11000));
        if (duringTurn) Assert.True(game.Shoot(new(.5, .48), At(11000)));
        game.SetPaused(true, At(11100));
        var frozen = game.Snapshot;
        Assert.True(frozen.Paused);
        game.Advance(At(60000));
        Assert.Equal(frozen, game.Snapshot);
        Assert.False(game.Shoot(new(frozen.U, frozen.V), At(60001)));
        game.SetPaused(false, At(61100));
        Assert.Equal(frozen with { Paused = false, Revision = game.Snapshot.Revision }, game.Snapshot);
        Assert.False(game.Shoot(new(frozen.U, frozen.V), At(11050)));
        game.Advance(At(62000));
        Assert.Equal(DuckHuntPhase.Walking, game.Snapshot.Phase);
        Assert.NotEqual(frozen.U, game.Snapshot.U);
    }

    [Fact]
    public void AValidPhotoJustBeforeEscapeCanStillReverseTheDuckAfterTheRenderPassedItsExit()
    {
        var game = Started();
        game.Advance(At(21870));
        var old = game.Snapshot;
        var aim = PhotoPoint(old, 1, 1300, 715);
        Assert.InRange(aim.U, 0, 1);
        game.Advance(At(22020));
        Assert.Equal(DuckHuntPhase.DuckWon, game.Snapshot.Phase);
        Assert.True(game.Shoot(aim, At(21870)));
        Assert.Equal(DuckHuntPhase.Turning, game.Snapshot.Phase);
        Assert.Equal(-1, game.Snapshot.Direction);
    }

    [Fact]
    public void AnimatedFeetUseTheSamePhysicalTransformsForHitTesting()
    {
        var duck = new DuckHuntSnapshot(DuckHuntPhase.Walking, 0, 0, .5, .48, 0, 0);
        foreach (double phase in new[] { 0.0, Math.PI / 2, Math.PI, 1.5 * Math.PI })
        foreach (bool rear in new[] { true, false })
        {
            duck = duck with { WalkPhase = phase };
            var pose = DuckHuntLayout.FootPose(duck, rear);
            double x = (rear ? 500 : 710) - pose.PivotU * DuckHuntLayout.PhotoPixelWidth;
            double y = (rear ? 1100 : 1147) - pose.PivotV * DuckHuntLayout.PhotoPixelHeight;
            double c = Math.Cos(pose.Angle), s = Math.Sin(pose.Angle);
            var point = PhotoPoint(duck, 1.8, x * c - y * s + pose.PivotU * DuckHuntLayout.PhotoPixelWidth,
                x * s + y * c + (pose.PivotV + pose.Lift) * DuckHuntLayout.PhotoPixelHeight);
            Assert.True(DuckHuntLayout.ContainsDuck(point, duck, 1.8));
        }
    }

    private static DuckHuntGame Started(bool fromLeft = true, double aspect = 1)
    {
        var game = new DuckHuntGame();
        game.Reset(At(0));
        Assert.True(game.Start(At(0), aspect, fromLeft));
        return game;
    }

    private static BoardAim PhotoPoint(DuckHuntSnapshot duck, double aspect, double x, double y)
    {
        var bounds = DuckHuntLayout.PhotoBounds(duck, aspect);
        double u = x / DuckHuntLayout.PhotoPixelWidth;
        if (duck.Direction > 0) u = 1 - u;
        return new(bounds.X + u * bounds.Width, bounds.Y + y / DuckHuntLayout.PhotoPixelHeight * bounds.Height);
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
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(1250)));
        board.ObserveHeldButtons([], At(1300), At(1300), [exit.Id]);
        for (int ms = 1400; ms < 2400; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([exit.Id], At(ms), At(ms), []));
        Assert.Equal([exit.Id], board.ObserveHeldButtons([exit.Id], At(2400), At(2400), []));
        Assert.Equal(BoardScreen.Menu, board.Screen);
        Assert.True(board.MenuScrolled);
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(2500)));
        Assert.True(board.ActivateButton("duck-hunt", At(2600)));
        Assert.Equal(0, board.DuckHuntState.Hits);
        Assert.Equal(DuckHuntPhase.Ready, board.DuckHuntState.Phase);
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
