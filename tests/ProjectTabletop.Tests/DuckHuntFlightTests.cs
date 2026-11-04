using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntFlightTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void OnlyAReadyCurrentPlayActionBeginsTheRoundAndShotsCannotStartIt()
    {
        var game = new DuckHuntFlightGame();
        game.Reset(At(0));
        Assert.True(game.Snapshot.Paused);
        Assert.False(game.Start(At(100)));
        Assert.False(game.Shoot(new(.5, .5), At(100)));
        game.SetReady(true, At(200));
        game.Advance(At(300));
        Assert.Equal(DuckHuntFlightPhase.Ready, game.Snapshot.Phase);
        Assert.False(game.Shoot(null, At(300)));
        Assert.False(game.Shoot(new(double.NaN, .5), At(300)));
        Assert.False(game.Shoot(new(.5, .5), At(199)));
        Assert.False(game.Shoot(new(.5, .5), At(300), double.NaN));
        Assert.False(game.Shoot(new(.01, .01), At(300)));
        Assert.False(game.Shoot(new(.5, .5), At(300)));
        Assert.False(game.Start(At(299)));
        Assert.False(game.Start(At(300), double.NaN));
        Assert.True(game.Start(At(301)));
        Assert.False(game.Start(At(302)));
        Assert.Equal(DuckHuntFlightPhase.Launching, game.Snapshot.Phase);
        Assert.False(game.Snapshot.PlayVisible);
        Assert.Equal(0, game.Snapshot.Shots);
        game.Advance(At(851));
        Assert.Equal(DuckHuntFlightPhase.Flying, game.Snapshot.Phase);
        Assert.Equal(1, game.Snapshot.BirdsLaunched);
    }

    [Theory]
    [InlineData(0, 0, .15, 1)]
    [InlineData(1, 0, .5, -1)]
    [InlineData(1, 1, .5, 1)]
    [InlineData(2, 0, .85, -1)]
    public void AllThreeLaunchPositionsFlyDiagonallyTowardTheOppositeTrees(
        int side, int centreDirection, double startU, int direction)
    {
        var game = Started(new SequenceRandom(side, centreDirection));
        game.Advance(At(650));
        var start = game.Snapshot;
        Assert.Equal(startU, start.U);
        Assert.Equal(direction, start.Direction);
        game.Advance(At(2650));
        var first = game.Snapshot;
        game.Advance(At(4650));
        var second = game.Snapshot;
        Assert.True((first.U - start.U) * direction > 0);
        Assert.True(first.V < start.V);
        Assert.Equal(first.U - start.U, second.U - first.U, 10);
        Assert.Equal(first.V - start.V, second.V - first.V, 10);
        Assert.NotEqual(start.FlapPhase, first.FlapPhase);
    }

    [Fact]
    public void HitUsesHistoricalPhotoPoseThenFreezesBlinksAndAwardsExactlyOnePoint()
    {
        var game = Started();
        game.Advance(At(2650));
        var photographed = game.Snapshot;
        var aim = BodyCentre(photographed);
        game.Advance(At(2770));
        Assert.True(game.Shoot(aim, At(2650)));
        var hit = game.Snapshot;
        Assert.Equal(DuckHuntFlightPhase.Hit, hit.Phase);
        Assert.Equal(1, hit.Score);
        Assert.Equal(photographed.U, hit.U);
        Assert.Equal(photographed.V, hit.V);
        Assert.Equal(photographed.FlapPhase, hit.FlapPhase);
        Assert.Equal(aim, hit.Impact);
        Assert.Equal(.12, hit.HitElapsed, 10);
        Assert.False(hit.Visible);
        Assert.False(game.Shoot(aim, At(2650)));
        Assert.False(game.Shoot(aim, At(2800)));
        Assert.Equal(aim, game.Snapshot.Impact);
        game.Advance(At(2850));
        Assert.True(game.Snapshot.Visible);
        Assert.Equal(hit.U, game.Snapshot.U);
        Assert.Equal(hit.FlapPhase, game.Snapshot.FlapPhase);
        game.Advance(At(3300));
        Assert.Equal(DuckHuntFlightPhase.BetweenBirds, game.Snapshot.Phase);
        Assert.False(game.Snapshot.Visible);
        Assert.Equal(aim, game.Snapshot.Impact);
        Assert.Equal(.65, game.Snapshot.HitElapsed, 10);
        game.Advance(At(4000));
        Assert.Equal(2, game.Snapshot.BirdsLaunched);
        Assert.Equal(1, game.Snapshot.Score);
        Assert.Null(game.Snapshot.Impact);
        Assert.Equal(0, game.Snapshot.HitElapsed);
    }

    [Fact]
    public void ImpactKeepsMeasuredOffCentrePointAndAgesFromClickRatherThanProjectedPose()
    {
        var game = Started();
        game.Advance(At(2650));
        var photographed = game.Snapshot;
        var centre = BodyCentre(photographed);
        var aim = new BoardAim(centre.U + .006, centre.V + .003);
        Assert.True(DuckHuntFlightLayout.ContainsBird(aim, photographed, 1));
        game.Advance(At(2820));
        Assert.True(game.Shoot(aim, At(2770), sceneTime: At(2650)));
        Assert.Equal(aim, game.Snapshot.Impact);
        Assert.NotEqual(new BoardAim(photographed.U, photographed.V), game.Snapshot.Impact);
        Assert.Equal(.05, game.Snapshot.HitElapsed, 10);
        Assert.Equal(photographed.U, game.Snapshot.U);
        Assert.Equal(photographed.V, game.Snapshot.V);
        Assert.False(game.Shoot(centre, At(2821)));
        Assert.Equal(aim, game.Snapshot.Impact);
        Assert.Equal(1, game.Snapshot.Score);
    }

    [Fact]
    public void MissesDoNotCreateImpactAndResetClearsTheLastSuccessfulHit()
    {
        var game = Started();
        game.Advance(At(2650));
        Assert.False(game.Shoot(new(.01, .01), At(2650)));
        Assert.Null(game.Snapshot.Impact);
        Assert.Equal(0, game.Snapshot.HitElapsed);
        game.Advance(At(2700));
        Assert.True(game.Shoot(BodyCentre(game.Snapshot), At(2700)));
        Assert.NotNull(game.Snapshot.Impact);
        game.Reset(At(2800));
        Assert.Null(game.Snapshot.Impact);
        Assert.Equal(0, game.Snapshot.HitElapsed);
        game.SetReady(true, At(2900));
        Assert.True(game.Start(At(3000)));
        Assert.Null(game.Snapshot.Impact);
    }

    [Fact]
    public void LastHitPopupEndsBeforeCompletedRoundAndReplayStayClear()
    {
        var game = Started();
        var launch = At(650);
        for (int bird = 1; bird < 10; bird++)
            launch += DurationForBird(bird) + DuckHuntFlightGame.BetweenBirdsDuration;
        var shotTime = launch.AddMilliseconds(500);
        game.Advance(shotTime);
        Assert.True(game.Shoot(BodyCentre(game.Snapshot), shotTime));
        var gap = shotTime + DuckHuntFlightGame.HitDuration;
        game.Advance(gap.AddMilliseconds(699));
        Assert.Equal(DuckHuntFlightPhase.BetweenBirds, game.Snapshot.Phase);
        Assert.NotNull(game.Snapshot.Impact);
        Assert.Equal(1.349, game.Snapshot.HitElapsed, 10);
        var end = gap + DuckHuntFlightGame.BetweenBirdsDuration;
        game.Advance(end);
        Assert.True(game.Snapshot.RoundFinished);
        Assert.Null(game.Snapshot.Impact);
        var completed = game.Snapshot;
        game.Advance(end.AddSeconds(10));
        Assert.Equal(completed, game.Snapshot);
        Assert.True(game.Start(end.AddSeconds(11)));
        Assert.Null(game.Snapshot.Impact);
        Assert.Equal(0, game.Snapshot.HitElapsed);
        Assert.Equal(0, game.Snapshot.Score);
    }

    [Fact]
    public void AmbiguousPhotoFallbackHitsAndFreezesTheSelectedEarlierFlightPose()
    {
        var game = Started();
        var tracker = new DuckHuntMeasurementTracker();
        game.Advance(At(2650));
        var photographed = game.Snapshot;
        tracker.Observe(BodyCentre(photographed), At(2650), At(2655));
        game.Advance(At(2690));
        Assert.NotEqual(photographed.U, game.Snapshot.U);
        Assert.True(tracker.Capture(null, At(2675), At(2620), At(2680), At(2690), true, out var shot));
        Assert.True(game.Shoot(shot!.Value.Aim, shot.Value.ClickedAt));
        Assert.Equal(photographed.U, game.Snapshot.U);
        Assert.Equal(photographed.V, game.Snapshot.V);
        Assert.Equal(photographed.FlapPhase, game.Snapshot.FlapPhase);
        Assert.Equal(1, game.Snapshot.Score);
        Assert.Equal(1, game.Snapshot.Shots);
        Assert.False(tracker.Capture(null, At(2675), At(2620), At(2680), At(2690), true, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1.8)]
    public void ConfirmationJustAfterEscapeStillTestsTheVisibleHistoricalBird(int side, double aspect)
    {
        var game = Started(new SequenceRandom(side), aspect);
        game.Advance(At(6530));
        var photographed = game.Snapshot;
        var bounds = DuckHuntFlightLayout.SpriteBounds(photographed, aspect);
        var aim = new BoardAim(side == 0 ? .998 : .002,
            bounds.Y + bounds.Height * (DuckHuntFlightLayout.BodyArtworkBounds.Y +
                DuckHuntFlightLayout.BodyArtworkBounds.Height * .61));
        Assert.True(DuckHuntFlightLayout.ContainsBird(aim, photographed, aspect));
        game.Advance(At(6660));
        Assert.Equal(DuckHuntFlightPhase.BetweenBirds, game.Snapshot.Phase);
        Assert.True(game.Shoot(aim, At(6530), aspect));
        Assert.Equal(DuckHuntFlightPhase.Hit, game.Snapshot.Phase);
        Assert.Equal(1, game.Snapshot.Score);
        Assert.Equal(1, game.Snapshot.BirdsLaunched);
    }

    [Fact]
    public void PausesFreezeFlightAndInvalidateEarlierPhotosOnResume()
    {
        var game = Started();
        game.Advance(At(2650));
        game.SetReady(false, At(2650));
        var frozen = game.Snapshot;
        game.Advance(At(10000));
        Assert.Equal(frozen, game.Snapshot);
        Assert.False(game.Shoot(BodyCentre(frozen), At(10000)));
        game.SetReady(true, At(10000));
        Assert.Equal(frozen.U, game.Snapshot.U);
        Assert.False(game.Snapshot.Paused);
        Assert.False(game.Shoot(BodyCentre(frozen), At(9999)));
        Assert.True(game.Shoot(BodyCentre(frozen), At(10000)));
        Assert.Equal(1, game.Snapshot.Score);
        game.SetReady(false, At(10100));
        var blink = game.Snapshot;
        game.Advance(At(20000));
        Assert.Equal(blink, game.Snapshot);
        game.SetReady(true, At(20000));
        Assert.Equal(blink.Visible, game.Snapshot.Visible);
        Assert.Equal(blink.FlapPhase, game.Snapshot.FlapPhase);
        Assert.Equal(blink.Impact, game.Snapshot.Impact);
        Assert.Equal(.1, game.Snapshot.HitElapsed, 10);
        game.Advance(At(20100));
        Assert.Equal(.2, game.Snapshot.HitElapsed, 10);
    }

    [Fact]
    public void TenMixedBirdsFinishAndOnlyAFreshPlayActionBeginsAnotherRound()
    {
        var game = Started();
        double launch = 650;
        for (int bird = 1; bird <= 10; bird++)
        {
            game.Advance(At(launch));
            Assert.Equal(bird, game.Snapshot.BirdsLaunched);
            if (bird % 2 == 1)
            {
                game.Advance(At(launch + 2000));
                Assert.True(game.Shoot(BodyCentre(game.Snapshot), At(launch + 2000)));
                launch += 2000 + DuckHuntFlightGame.HitDuration.TotalMilliseconds +
                    DuckHuntFlightGame.BetweenBirdsDuration.TotalMilliseconds;
            }
            else launch += DurationForBird(bird).TotalMilliseconds +
                DuckHuntFlightGame.BetweenBirdsDuration.TotalMilliseconds;
        }
        game.Advance(At(launch));
        Assert.Equal(DuckHuntFlightPhase.Finished, game.Snapshot.Phase);
        Assert.Equal(10, game.Snapshot.BirdsLaunched);
        Assert.Equal(5, game.Snapshot.Score);
        Assert.True(game.Snapshot.PlayVisible);
        Assert.False(game.Shoot(new(.5, .5), At(launch - 1)));
        Assert.False(game.Shoot(new(.5, .5), At(launch + 1)));
        Assert.False(game.Start(At(launch)));
        Assert.True(game.Start(At(launch + 1)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.Equal(0, game.Snapshot.BirdsLaunched);
        Assert.Equal(DuckHuntFlightPhase.Launching, game.Snapshot.Phase);
    }

    [Fact]
    public void EachSuccessiveBirdFliesTenPercentFasterAndReplayRestoresInitialSpeed()
    {
        var game = Started();
        var launch = At(650);
        double previousSpeed = 0;
        for (int bird = 1; bird <= 10; bird++)
        {
            var duration = DurationForBird(bird);
            game.Advance(launch);
            Assert.Equal(bird, game.Snapshot.BirdsLaunched);
            var start = game.Snapshot;
            game.Advance(launch.AddMilliseconds(200));
            double speed = (start.V - game.Snapshot.V) / .2;
            if (bird > 1) Assert.Equal(1.1, speed / previousSpeed, 6);
            previousSpeed = speed;
            game.Advance(launch + duration - TimeSpan.FromTicks(1));
            Assert.Equal(DuckHuntFlightPhase.Flying, game.Snapshot.Phase);
            game.Advance(launch + duration);
            Assert.Equal(DuckHuntFlightPhase.BetweenBirds, game.Snapshot.Phase);
            launch += duration + DuckHuntFlightGame.BetweenBirdsDuration;
        }
        game.Advance(launch);
        Assert.True(game.Snapshot.RoundFinished);
        Assert.Equal(0, game.Snapshot.Score);
        Assert.True(game.Start(launch.AddTicks(1)));
        launch += TimeSpan.FromTicks(1) + DuckHuntFlightGame.LaunchDelay;
        game.Advance(launch.AddSeconds(3));
        Assert.Equal(1, game.Snapshot.BirdsLaunched);
        Assert.Equal(.455, game.Snapshot.V, 10);
        game.Reset(launch.AddSeconds(4));
        game.SetReady(true, launch.AddSeconds(4));
        Assert.True(game.Start(launch.AddSeconds(4).AddTicks(1)));
        game.Advance(launch.AddSeconds(7).AddTicks(1) + DuckHuntFlightGame.LaunchDelay);
        Assert.Equal(1, game.Snapshot.BirdsLaunched);
        Assert.Equal(.455, game.Snapshot.V, 10);
    }

    [Fact]
    public void FasterLastBirdStillUsesItsPhotographedPoseAfterEscape()
    {
        var game = Started();
        var launch = At(650);
        for (int bird = 1; bird < 10; bird++)
            launch += DurationForBird(bird) + DuckHuntFlightGame.BetweenBirdsDuration;
        var escape = launch + DurationForBird(10);
        var photoTime = escape.AddMilliseconds(-120);
        game.Advance(photoTime);
        var photographed = game.Snapshot;
        Assert.Equal(10, photographed.BirdsLaunched);
        var bounds = DuckHuntFlightLayout.SpriteBounds(photographed, 1);
        var aim = new BoardAim(.998, bounds.Y + bounds.Height *
            (DuckHuntFlightLayout.BodyArtworkBounds.Y + DuckHuntFlightLayout.BodyArtworkBounds.Height * .61));
        Assert.True(DuckHuntFlightLayout.ContainsBird(aim, photographed, 1));
        game.Advance(escape.AddMilliseconds(10));
        Assert.Equal(DuckHuntFlightPhase.BetweenBirds, game.Snapshot.Phase);
        Assert.True(game.Shoot(aim, photoTime));
        Assert.Equal(photographed.U, game.Snapshot.U);
        Assert.Equal(photographed.V, game.Snapshot.V);
        Assert.Equal(photographed.FlapPhase, game.Snapshot.FlapPhase);
        Assert.Equal(1, game.Snapshot.Score);
    }

    private static TimeSpan DurationForBird(int number) =>
        TimeSpan.FromTicks((long)Math.Round(TimeSpan.FromSeconds(6).Ticks / Math.Pow(1.1, number - 1)));

    [Fact]
    public void SparseTicksStopAfterExactlyTenEscapesAndStaleAimCannotScoreOrRestart()
    {
        var game = Started();
        game.Advance(At(2650));
        var old = BodyCentre(game.Snapshot);
        game.Advance(At(2851));
        Assert.False(game.Shoot(old, At(2650)));
        Assert.False(game.Shoot(new(.01, .01), At(2851)));
        Assert.Equal(1, game.Snapshot.Shots);
        game.Advance(At(90000));
        Assert.Equal(DuckHuntFlightPhase.Finished, game.Snapshot.Phase);
        Assert.Equal(10, game.Snapshot.BirdsLaunched);
        Assert.Equal(0, game.Snapshot.Score);
        var finished = game.Snapshot;
        game.Advance(At(100000));
        Assert.Equal(finished, game.Snapshot);
        Assert.False(game.Shoot(new(.5, .5), At(90000)));
    }

    [Theory]
    [InlineData(.55, -1)]
    [InlineData(1, 1)]
    [InlineData(1.8, -1)]
    public void FlightGeometryMatchesMirroringAspectAndWingPose(double aspect, int direction)
    {
        var bird = new DuckHuntFlightSnapshot(DuckHuntFlightPhase.Flying, 0, 1, 0,
            .5, .4, direction, Math.PI / 2, 1, true, 1);
        var bounds = DuckHuntFlightLayout.SpriteBounds(bird, aspect);
        Assert.Equal(bounds.Width * aspect, bounds.Height, 12);
        Assert.True(DuckHuntFlightLayout.ContainsBird(BodyCentre(bird, aspect), bird, aspect));
        foreach (var pixel in new BoardAim[] { new(1650, 260), new(1820, 370), new(180, 510) })
        {
            var body = DuckHuntFlightLayout.BodyArtworkBounds;
            var source = DuckHuntFlightLayout.BodySourceBounds;
            double bodyU = body.X + (pixel.U - source.X) / source.Width * body.Width;
            double bodyV = body.Y + (pixel.V - source.Y) / source.Height * body.Height;
            if (direction < 0) bodyU = 1 - bodyU;
            Assert.True(DuckHuntFlightLayout.ContainsBird(
                new(bounds.X + bodyU * bounds.Width, bounds.Y + bodyV * bounds.Height), bird, aspect));
        }
        var wingPoint = DuckHuntFlightLayout.NearWingPose(bird).Inner.Transform(620, 900);
        double localU = wingPoint.U;
        double localV = wingPoint.V;
        if (direction < 0) localU = 1 - localU;
        Assert.True(DuckHuntFlightLayout.ContainsBird(
            new(bounds.X + localU * bounds.Width, bounds.Y + localV * bounds.Height), bird, aspect));
        Assert.False(DuckHuntFlightLayout.ContainsBird(new(bounds.X, bounds.Y), bird, aspect));
        Assert.False(DuckHuntFlightLayout.ContainsBird(new(bounds.X + bounds.Width, bounds.Y + bounds.Height), bird, aspect));
        // Empty sky beneath the head is inside the sprite rectangle, not the bird.
        double emptyU = direction > 0 ? .86 : .14;
        Assert.False(DuckHuntFlightLayout.ContainsBird(
            new(bounds.X + emptyU * bounds.Width, bounds.Y + .75 * bounds.Height), bird, aspect));
        Assert.False(DuckHuntFlightLayout.ContainsBird(BodyCentre(bird, aspect), bird with { Visible = false }, aspect));
    }

    [Fact]
    public void HuntDrawerPlayUsesNormalButtonAndBackClearsTheRound()
    {
        var board = new BoardSession();
        board.ShowDuckHuntHunt(At(0));
        board.SetDuckHuntReady(true, At(100));
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.DoesNotContain(board.Buttons, b => b.Label == "START");
        Assert.False(board.ActivateButton("duck-hunt-start", At(200)));
        board.ObserveHeldButtons([], At(200), At(200), ["duck-hunt-start"]);
        for (int ms = 300; ms <= 1400; ms += 100)
            Assert.Empty(board.ObserveHeldButtons(["duck-hunt-start"], At(ms), At(ms), []));
        Assert.Equal(DuckHuntFlightPhase.Ready, board.HuntFlightState.Phase);
        long revision = board.DuckHuntRoundRevision;
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(1500)));
        Assert.True(board.ActivateButton("duck-hunt-play", At(1501)));
        Assert.Equal(revision + 1, board.DuckHuntRoundRevision);
        Assert.Equal(DuckHuntGalleryPhase.Ready, board.GalleryState.Phase);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(1600)));
        board.TickDuckHunt(At(1900));
        Assert.True(board.ActivateButton("duck-hunt-back", At(2000)));
        Assert.Equal(DuckHuntView.Menu, board.CurrentDuckHuntView);
        Assert.Equal(DuckHuntFlightPhase.Ready, board.HuntFlightState.Phase);
        Assert.True(board.HuntFlightState.Paused);
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(2100)));
        board.ShowDuckHuntHunt(At(2200));
        board.SetDuckHuntReady(true, At(2300));
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(1500)));
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(2400)));
        Assert.True(board.ActivateButton("duck-hunt-play", At(2600)));
    }

    [Fact]
    public void CompletingTheRoundOpensReplayAndPlayCreatesAFreshInputEpoch()
    {
        var board = new BoardSession();
        board.ShowDuckHuntHunt(At(0));
        board.SetDuckHuntReady(true, At(0));
        Assert.True(board.ActivateButton("duck-hunt-play", At(400)));
        long epoch = board.DuckHuntRoundRevision;
        board.TickDuckHunt(At(68000));
        Assert.True(board.HuntFlightState.RoundFinished);
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(68001)));
        Assert.False(board.ActivateButton("duck-hunt-play", At(68299)));
        Assert.True(board.ActivateButton("duck-hunt-play", At(68301)));
        Assert.Equal(DuckHuntFlightPhase.Launching, board.HuntFlightState.Phase);
        Assert.Equal(epoch + 1, board.DuckHuntRoundRevision);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(0, board.HuntFlightState.BirdsLaunched);
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1.8)]
    public void DrawerPlayNeedsFreshCaptionAndAFullHoldAndRetainsTheBoardAspect(double aspect)
    {
        const string play = "duck-hunt-play";
        var board = new BoardSession();
        board.ShowDuckHuntHunt(At(0));
        board.TickDuckHunt(At(310));
        Assert.Equal(["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-back", play],
            board.Buttons.Select(button => button.Id));
        var button = Assert.Single(board.Buttons, button => button.Id == play);
        var back = Assert.Single(board.Buttons, button => button.Id == "duck-hunt-back");
        Assert.Equal(BoardButtonHold.Once, button.Hold);
        Assert.Equal(back.Bounds.Width, button.Bounds.Width);
        Assert.Equal(back.Bounds.Height, button.Bounds.Height);
        Assert.Equal(.01, button.Bounds.X - back.Bounds.X - back.Bounds.Width, 10);
        Assert.False(button.Enabled);
        Assert.False(board.ActivateButton(play, At(350)));
        board.SetDuckHuntReady(true, At(400), aspect);
        for (int ms = 500; ms <= 1600; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([play], At(ms), At(ms), []));
        Assert.True(board.HuntFlightState.PlayVisible);
        board.ObserveHeldButtons([], At(1700), At(1700), [play]);
        for (int ms = 1800; ms < 2800; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([play], At(ms), At(ms), []));
        long epoch = board.DuckHuntRoundRevision;
        Assert.Equal([play], board.ObserveHeldButtons([play], At(2800), At(2800), []));
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(epoch + 1, board.DuckHuntRoundRevision);
        board.TickDuckHunt(At(9349)); // One millisecond before the first bird escapes.
        var bird = board.HuntFlightState;
        var bounds = DuckHuntFlightLayout.SpriteBounds(bird, aspect);
        Assert.True(bird.Direction > 0 ? bounds.X > 1 : bounds.X + bounds.Width < 0);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(9350)));
        board.TickDuckHunt(At(9700));
        Assert.DoesNotContain(board.Buttons, button => button.Id == play);
        Assert.False(board.ActivateButton(play, At(9710)));
    }

    [Fact]
    public void AlreadyOpenDrawerRequiresNewCaptionEvidenceWhenRoundFinishes()
    {
        const string play = "duck-hunt-play";
        var board = new BoardSession();
        board.ShowDuckHuntHunt(At(0)); board.SetDuckHuntReady(true, At(0));
        Assert.True(board.ActivateButton(play, At(400)));
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(500)));
        board.TickDuckHunt(At(900));
        board.ObserveHeldButtons([], At(1000), At(1000), [play]);
        board.TickDuckHunt(At(68000));
        Assert.True(board.HuntFlightState.RoundFinished);
        Assert.True(board.DuckHuntDrawerOpen);
        for (int ms = 68100; ms <= 69200; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([play], At(ms), At(ms), []));
        Assert.True(board.HuntFlightState.RoundFinished);
        board.ObserveHeldButtons([], At(69300), At(69300), [play]);
        for (int ms = 69400; ms < 70400; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([play], At(ms), At(ms), []));
        Assert.Equal([play], board.ObserveHeldButtons([play], At(70400), At(70400), []));
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(DuckHuntFlightPhase.Launching, board.HuntFlightState.Phase);
    }

    private static DuckHuntFlightGame Started(Random? random = null, double aspect = 1)
    {
        var game = new DuckHuntFlightGame(random ?? new SequenceRandom(0));
        game.Reset(At(0));
        game.SetReady(true, At(0));
        Assert.True(game.Start(At(100), aspect));
        return game;
    }

    private static BoardAim BodyCentre(DuckHuntFlightSnapshot bird, double aspect = 1)
    {
        var bounds = DuckHuntFlightLayout.SpriteBounds(bird, aspect);
        return new(bounds.X + bounds.Width * DuckHuntFlightLayout.BodyCenterU,
            bounds.Y + bounds.Height * DuckHuntFlightLayout.BodyCenterV);
    }

    private sealed class SequenceRandom(params int[] choices) : Random
    {
        private int _next;
        public override int Next(int maxValue) => choices[_next++ % choices.Length] % maxValue;
    }
}
