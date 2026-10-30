using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntClayTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) =>
        Origin.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    private static double Launch(int id) => DuckHuntClayGame.LaunchDelay.TotalSeconds +
        (id / 2) * DuckHuntClayGame.PairInterval.TotalSeconds +
        (id % 2) * DuckHuntClayGame.SecondClayDelay.TotalSeconds;

    [Fact]
    public void StandaloneGameCanBecomeReadyWithoutAnExplicitReset()
    {
        var game = new DuckHuntClayGame();
        game.SetReady(true, At(0));
        Assert.Equal(DuckHuntClayPhase.Playing, game.Snapshot.Phase);
        Assert.False(game.Snapshot.Paused);
        Assert.Equal(0, game.Snapshot.PairsLaunched);
        game.Advance(At(Launch(0)));
        Assert.Equal(1, game.Snapshot.PairsLaunched);
        Assert.True(game.Targets[0].Visible);
        Assert.False(game.Targets[1].Visible);
    }

    [Fact]
    public void EntryWaitsForReadinessThenAutomaticallyLaunchesStaggeredPairs()
    {
        var game = new DuckHuntClayGame();
        game.Reset(At(0));
        game.Advance(At(10));
        Assert.Equal(DuckHuntClayPhase.Ready, game.Snapshot.Phase);
        Assert.True(game.Snapshot.Paused);
        Assert.All(game.Targets, target => Assert.False(target.Visible));
        game.SetReady(true, At(10));
        Assert.Equal(DuckHuntClayPhase.Playing, game.Snapshot.Phase);
        Assert.Equal(0, game.Snapshot.Shots);
        game.Advance(At(10 + Launch(0)));
        Assert.Equal(1, game.Snapshot.PairsLaunched);
        Assert.True(game.Targets[0].Visible);
        Assert.True(game.Targets[0].V > 1);
        Assert.False(game.Targets[1].Visible);
        game.Advance(At(10 + Launch(1) - .001));
        Assert.False(game.Targets[1].Visible);
        game.Advance(At(10 + Launch(1)));
        Assert.Equal(2, game.Targets.Count(target => target.Visible));
        Assert.Equal(0, game.Targets[1].FlightSeconds);
        Assert.Equal(.5, game.Targets[0].FlightSeconds, 10);
    }

    [Fact]
    public void ClaysArcUpAndAwayShrinkContinuouslyAndLeaveTheImageBeforeEnding()
    {
        var game = Started();
        game.Advance(At(Launch(1) + .5));
        var early = game.Targets.Take(2).ToArray();
        game.Advance(At(Launch(1) + 2));
        var middle = game.Targets.Take(2).ToArray();
        game.Advance(At(Launch(1) + 3));
        var apex = game.Targets.Take(2).ToArray();
        game.Advance(At(Launch(0) + 4.999));
        var departing = game.Targets[0];
        for (int id = 0; id < 2; id++)
        {
            Assert.True(middle[id].V < early[id].V);
            Assert.True(middle[id].Scale < early[id].Scale);
            Assert.True(apex[id].Scale < middle[id].Scale);
            Assert.NotEqual(early[id].BankAngle, middle[id].BankAngle);
        }
        Assert.True(middle[0].U < early[0].U);
        Assert.True(middle[1].U > early[1].U);
        Assert.True(departing.V > apex[0].V);
        foreach (double aspect in new[] { .55, 1, 1.8 })
        {
            var bounds = DuckHuntClayLayout.DiscBounds(departing, aspect);
            Assert.True(departing.U + bounds.Width / 2 < 0);
        }
        game.Advance(At(Launch(0) + 5));
        Assert.False(game.Targets[0].Visible);
        Assert.True(game.Targets[1].Visible);
    }

    [Fact]
    public void HistoricalHitShattersExactPhotoPoseAndOnlyScoresOnce()
    {
        var game = Started();
        double photoAt = Launch(0) + 1.8;
        game.Advance(At(photoAt));
        var photo = game.Targets[0];
        game.Advance(At(photoAt + .12));
        Assert.True(game.Shoot(new(photo.U, photo.V), At(photoAt)));
        var hit = game.Targets[0];
        Assert.True(hit.Hit);
        Assert.True(hit.Visible);
        Assert.Equal(photo.U, hit.U);
        Assert.Equal(photo.V, hit.V);
        Assert.Equal(photo.Scale, hit.Scale);
        Assert.Equal(photo.BankAngle, hit.BankAngle);
        Assert.Equal(photo.FlightSeconds, hit.FlightSeconds);
        Assert.Equal(.12, hit.HitElapsed, 10);
        Assert.Equal(1, game.Snapshot.Score);
        Assert.False(game.Targets[1].Hit);
        Assert.False(game.Shoot(new(photo.U, photo.V), At(photoAt)));
        Assert.False(game.Shoot(new(photo.U, photo.V), At(photoAt + .13)));
        Assert.Equal(1, game.Snapshot.Score);
        Assert.Equal(2, game.Snapshot.Shots);
        game.Advance(At(photoAt + DuckHuntClayGame.HitDuration.TotalSeconds));
        Assert.True(game.Targets[0].Hit);
        Assert.False(game.Targets[0].Visible);
    }

    [Fact]
    public void FreshPhotoBeforeASecondLaunchCannotHitTheLaterClay()
    {
        var game = Started();
        // A second clay is still below the image at launch, so use the next
        // pair's centre as a deliberately wrong delayed photograph instead.
        double nextLaunch = Launch(2);
        game.Advance(At(nextLaunch + .1));
        var newClay = game.Targets[2];
        Assert.True(newClay.Visible);
        Assert.False(game.Shoot(new(.5, .9), At(nextLaunch - .01)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.False(game.Targets[2].Hit);
        // The old pair's retained identities cannot be retargeted to this pair.
        Assert.Equal(0, game.Targets[0].Id);
        Assert.Equal(2, game.Targets[2].Id);
        Assert.False(game.Targets[0].Visible);
    }

    [Fact]
    public void InvalidMissingStaleAndDuplicateShotsCannotScore()
    {
        var game = Started();
        double photoAt = Launch(0) + 2;
        game.Advance(At(photoAt));
        var clay = game.Targets[0];
        var point = new BoardAim(clay.U, clay.V);
        Assert.False(game.Shoot(null, At(photoAt)));
        Assert.False(game.Shoot(new(double.NaN, clay.V), At(photoAt)));
        Assert.False(game.Shoot(new(-.01, clay.V), At(photoAt)));
        Assert.False(game.Shoot(point, At(photoAt), double.PositiveInfinity));
        Assert.False(game.Shoot(point, At(photoAt), 0));
        game.Advance(At(photoAt + .151));
        Assert.False(game.Shoot(point, At(photoAt)));
        Assert.Equal(0, game.Snapshot.Shots);
        var current = game.Targets[0];
        Assert.False(game.Shoot(new(.99, .99), At(photoAt + .151)));
        Assert.False(game.Shoot(new(current.U, current.V), At(photoAt + .151)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.Equal(1, game.Snapshot.Shots);
    }

    [Fact]
    public void TrainedClickGateRejectsFutureFramesAndMissingDotForClayShots()
    {
        var game = Started();
        var gate = new DuckHuntAimGate();
        gate.Reset(At(0));
        game.Advance(At(2));
        var clay = game.Targets[0];
        var aim = new BoardAim(clay.U, clay.V);
        Assert.Null(gate.EvaluateSnapshot(aim, At(2.03), At(2), At(2.02), At(2.02)));
        Assert.Null(gate.EvaluateSnapshot(aim, At(2.10), At(2.10), At(2.12), At(2.11)));
        Assert.Null(gate.EvaluateSnapshot(null, At(2.2), At(2.2), At(2.22), At(2.22)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.Equal(0, game.Snapshot.Shots);
    }

    [Fact]
    public void PauseFreezesBothFlightsAndFragmentsAndResumeRejectsOldPhotos()
    {
        var game = Started();
        game.Advance(At(2));
        var clay = game.Targets[0];
        Assert.True(game.Shoot(new(clay.U, clay.V), At(2)));
        game.SetReady(false, At(2.2));
        var frozenState = game.Snapshot;
        var frozenTargets = game.Targets.ToArray();
        game.Advance(At(30));
        Assert.Equal(frozenState, game.Snapshot);
        Assert.Equal(frozenTargets, game.Targets);
        Assert.False(game.Shoot(new(game.Targets[1].U, game.Targets[1].V), At(30)));
        game.SetReady(true, At(30));
        Assert.Equal(frozenTargets, game.Targets);
        Assert.False(game.Snapshot.Paused);
        var second = game.Targets[1];
        Assert.False(game.Shoot(new(second.U, second.V), At(29.999)));
        Assert.True(game.Shoot(new(second.U, second.V), At(30)));
        Assert.Equal(2, game.Snapshot.Score);
        game.Advance(At(30.2));
        Assert.Equal(.4, game.Targets[0].HitElapsed, 10);
        Assert.Equal(.2, game.Targets[1].HitElapsed, 10);
    }

    [Fact]
    public void TenPairsFinishExactlyAndSparseTicksDoNotCreateExtraTargets()
    {
        var game = Started();
        for (int pair = 0; pair < DuckHuntClayGame.PairsPerRound; pair++)
        {
            double launch = Launch(pair * 2);
            game.Advance(At(launch + 1.5));
            Assert.Equal(pair + 1, game.Snapshot.PairsLaunched);
            Assert.Equal(2, game.Targets.Count(target => target.Visible));
            var first = game.Targets[pair * 2];
            Assert.True(game.Shoot(new(first.U, first.V), At(launch + 1.5)));
            if (pair % 2 != 0) continue;
            game.Advance(At(launch + 1.6));
            var second = game.Targets[pair * 2 + 1];
            Assert.True(game.Shoot(new(second.U, second.V), At(launch + 1.6)));
        }
        game.Advance(At(DuckHuntClayGame.RoundDuration.TotalSeconds));
        Assert.True(game.Snapshot.RoundFinished);
        Assert.Equal(10, game.Snapshot.PairsLaunched);
        Assert.Equal(15, game.Snapshot.Score);
        Assert.Equal(15, game.Snapshot.Shots);
        Assert.Equal(20, game.Targets.Count);
        Assert.All(game.Targets, target => Assert.False(target.Visible));
        var finished = game.Snapshot;
        game.Advance(At(1000));
        Assert.Equal(finished, game.Snapshot);
        Assert.False(game.Shoot(new(.5, .5), At(1000)));
        Assert.Equal(finished, game.Snapshot);

        var sparse = Started();
        sparse.Advance(At(1000));
        Assert.True(sparse.Snapshot.RoundFinished);
        Assert.Equal(10, sparse.Snapshot.PairsLaunched);
        Assert.Equal(0, sparse.Snapshot.Score);
    }

    [Theory]
    [InlineData(.55, -.5)]
    [InlineData(1, .3)]
    [InlineData(1.8, .7)]
    public void SharedHitEllipseMatchesPhysicalBankAtEveryAspect(double aspect, double angle)
    {
        var clay = new DuckHuntClayTarget(0, 0, .5, .4, .8, angle, .48, 2, true, false, 0);
        var bounds = DuckHuntClayLayout.DiscBounds(clay, aspect);
        Assert.Equal(bounds.Width * aspect * clay.FaceRatio, bounds.Height, 12);
        BoardAim Point(double x, double y) => new(
            clay.U + (x * Math.Cos(angle) - y * Math.Sin(angle)) / aspect,
            clay.V + x * Math.Sin(angle) + y * Math.Cos(angle));
        double major = bounds.Width * aspect / 2, minor = bounds.Height / 2;
        Assert.True(DuckHuntClayLayout.ContainsClay(Point(.99 * major, 0), clay, aspect));
        Assert.True(DuckHuntClayLayout.ContainsClay(Point(0, .99 * minor), clay, aspect));
        Assert.False(DuckHuntClayLayout.ContainsClay(Point(1.01 * major, 0), clay, aspect));
        Assert.False(DuckHuntClayLayout.ContainsClay(Point(.8 * major, .8 * minor), clay, aspect));
        Assert.False(DuckHuntClayLayout.ContainsClay(new(clay.U, clay.V), clay with { Hit = true }, aspect));
        Assert.False(DuckHuntClayLayout.ContainsClay(new(clay.U, clay.V), clay with { Visible = false }, aspect));
    }

    [Fact]
    public void FinishOpensDrawerAndPlayAgainClosesItWithFreshAutomaticRound()
    {
        var board = new BoardSession();
        board.ShowDuckHuntClayPigeon(At(0));
        Assert.Equal(DuckHuntView.ClayPigeon, board.CurrentDuckHuntView);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.DoesNotContain(board.Buttons, button => button.Label is "START" or "PLAY AGAIN");
        Assert.False(board.ActivateButton("duck-hunt-start", At(.1)));
        board.SetDuckHuntReady(true, At(.2));
        Assert.Equal(DuckHuntClayPhase.Playing, board.ClayState.Phase);
        Assert.Equal(DuckHuntFlightPhase.Ready, board.HuntFlightState.Phase);
        Assert.Equal(DuckHuntGalleryPhase.Ready, board.GalleryState.Phase);
        board.TickDuckHunt(At(100));
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.True(board.ClayState.RoundFinished);
        Assert.Equal(["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-back", "duck-hunt-play-again"],
            board.Buttons.Select(button => button.Id));
        Assert.False(board.ActivateButton("duck-hunt-play-again", At(100.1)));
        board.TickDuckHunt(At(100.3));
        long revision = board.DuckHuntRoundRevision;
        Assert.True(board.ActivateButton("duck-hunt-play-again", At(100.4)));
        Assert.Equal(revision + 1, board.DuckHuntRoundRevision);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.False(board.ClayState.Paused);
        Assert.Equal(DuckHuntClayPhase.Playing, board.ClayState.Phase);
        Assert.Equal(0, board.ClayState.Score);
        Assert.Equal(0, board.ClayState.PairsLaunched);
        Assert.False(board.TryDuckHuntShot(new(.5, .5), At(100.399)));
        board.TickDuckHunt(At(100.4 + Launch(1)));
        Assert.Equal(2, board.ClayTargets.Count(target => target.Visible));
    }

    [Fact]
    public void FinishedReplayRequiresClearLetteringAndFullHoldAfterDrawerSettles()
    {
        var board = new BoardSession();
        board.ShowDuckHuntClayPigeon(At(0));
        board.SetDuckHuntReady(true, At(0));
        board.TickDuckHunt(At(100));
        board.TickDuckHunt(At(100.3));
        const string replay = "duck-hunt-play-again";
        for (int step = 4; step <= 16; step++)
            Assert.Empty(board.ObserveHeldButtons([replay], At(100 + step / 10d), At(100 + step / 10d), []));
        Assert.True(board.ClayState.RoundFinished);
        board.ObserveHeldButtons([], At(101.7), At(101.7), [replay]);
        for (int step = 18; step < 28; step++)
            Assert.Empty(board.ObserveHeldButtons([replay], At(100 + step / 10d), At(100 + step / 10d), []));
        Assert.Equal([replay], board.ObserveHeldButtons([replay], At(102.8), At(102.8), []));
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(DuckHuntClayPhase.Playing, board.ClayState.Phase);
    }

    private static DuckHuntClayGame Started()
    {
        var game = new DuckHuntClayGame();
        game.Reset(At(0));
        game.SetReady(true, At(0));
        return game;
    }
}
