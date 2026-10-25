using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntFlightTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void OnlyAReadyFreshLaserClickOnStartBeginsTheRound()
    {
        var game = new DuckHuntFlightGame();
        game.Reset(At(0));
        Assert.True(game.Snapshot.Paused);
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
        Assert.True(game.Shoot(new(.5, .5), At(301)));
        Assert.Equal(DuckHuntFlightPhase.Launching, game.Snapshot.Phase);
        Assert.False(game.Snapshot.StartVisible);
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
        Assert.False(hit.Visible);
        Assert.False(game.Shoot(aim, At(2650)));
        Assert.False(game.Shoot(aim, At(2800)));
        game.Advance(At(2850));
        Assert.True(game.Snapshot.Visible);
        Assert.Equal(hit.U, game.Snapshot.U);
        Assert.Equal(hit.FlapPhase, game.Snapshot.FlapPhase);
        game.Advance(At(3300));
        Assert.Equal(DuckHuntFlightPhase.BetweenBirds, game.Snapshot.Phase);
        Assert.False(game.Snapshot.Visible);
        game.Advance(At(4000));
        Assert.Equal(2, game.Snapshot.BirdsLaunched);
        Assert.Equal(1, game.Snapshot.Score);
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
    }

    [Fact]
    public void TenMixedBirdsFinishAndOnlyAFreshStartShotBeginsAnotherRound()
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
            else launch += DuckHuntFlightGame.FlightDuration.TotalMilliseconds +
                DuckHuntFlightGame.BetweenBirdsDuration.TotalMilliseconds;
        }
        game.Advance(At(launch));
        Assert.Equal(DuckHuntFlightPhase.Finished, game.Snapshot.Phase);
        Assert.Equal(10, game.Snapshot.BirdsLaunched);
        Assert.Equal(5, game.Snapshot.Score);
        Assert.True(game.Snapshot.StartVisible);
        Assert.False(game.Shoot(new(.5, .5), At(launch - 1)));
        Assert.True(game.Shoot(new(.5, .5), At(launch + 1)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.Equal(0, game.Snapshot.BirdsLaunched);
        Assert.Equal(DuckHuntFlightPhase.Launching, game.Snapshot.Phase);
    }

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
        var wing = DuckHuntFlightLayout.NearWingPose(bird);
        double scale = wing.Length / (DuckHuntFlightLayout.WingRootV - DuckHuntFlightLayout.WingTopV);
        double x = (620 - DuckHuntFlightLayout.WingRootU) * scale;
        double y = (900 - DuckHuntFlightLayout.WingRootV) * scale;
        double localU = wing.PivotU + x * Math.Cos(wing.Angle) - y * Math.Sin(wing.Angle);
        double localV = wing.PivotV + x * Math.Sin(wing.Angle) + y * Math.Cos(wing.Angle);
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
    public void HuntStartCannotBeActivatedByHandOrOrdinaryButtonAndBackClearsTheRound()
    {
        var board = new BoardSession();
        board.ShowDuckHuntHunt(At(0));
        board.SetDuckHuntReady(true, At(100));
        Assert.DoesNotContain(board.Buttons, b => b.Label == "START");
        Assert.False(board.ActivateButton("duck-hunt-start", At(200)));
        board.ObserveHeldButtons([], At(200), At(200), ["duck-hunt-start"]);
        for (int ms = 300; ms <= 1400; ms += 100)
            Assert.Empty(board.ObserveHeldButtons(["duck-hunt-start"], At(ms), At(ms), []));
        Assert.Equal(DuckHuntFlightPhase.Ready, board.HuntFlightState.Phase);
        long revision = board.DuckHuntRoundRevision;
        Assert.True(board.TryDuckHuntShot(new(.5, .5), At(1500)));
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
        Assert.True(board.TryDuckHuntShot(new(.5, .5), At(2400)));
    }

    [Fact]
    public void ReplayShotThatAdvancesPastTheFinalBirdStillCreatesAFreshInputEpoch()
    {
        var board = new BoardSession();
        board.ShowDuckHuntHunt(At(0));
        board.SetDuckHuntReady(true, At(0));
        Assert.True(board.TryDuckHuntShot(new(.5, .5), At(100)));
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(200)));
        board.TickDuckHunt(At(500));
        long epoch = board.DuckHuntRoundRevision;
        // No render ticks occur until this fresh shot advances all ten escapes
        // and starts another round on the newly displayed START target.
        Assert.True(board.TryDuckHuntShot(new(.5, .5), At(68000)));
        Assert.Equal(DuckHuntFlightPhase.Launching, board.HuntFlightState.Phase);
        Assert.Equal(epoch + 1, board.DuckHuntRoundRevision);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(0, board.HuntFlightState.BirdsLaunched);
    }

    private static DuckHuntFlightGame Started(Random? random = null, double aspect = 1)
    {
        var game = new DuckHuntFlightGame(random ?? new SequenceRandom(0));
        game.Reset(At(0));
        game.SetReady(true, At(0));
        Assert.True(game.Shoot(new(.5, .5), At(100), aspect));
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
