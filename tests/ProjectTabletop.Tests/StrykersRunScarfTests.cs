using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class StrykersRunScarfTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForwardRunHoldsLastSideAndHorizontalTravelTrailsOpposite()
    {
        var game = Playing(out int milliseconds);
        Advance(game, ref milliseconds, 150, .5f);
        Assert.Equal(-1, game.Snapshot.ScarfSide);
        Advance(game, ref milliseconds, 350, .2f);
        Assert.Equal(1, game.Snapshot.ScarfSide);
        float afterTurn = game.Snapshot.RunSeconds;
        Advance(game, ref milliseconds, 500, .2f);
        Assert.Equal(1, game.Snapshot.ScarfSide);
        Assert.True(game.Snapshot.RunSeconds > afterTurn);
        Advance(game, ref milliseconds, 350, .8f);
        Assert.Equal(-1, game.Snapshot.ScarfSide);
        Advance(game, ref milliseconds, 350, .8f);
        Assert.Equal(-1, game.Snapshot.ScarfSide);
    }

    [Fact]
    public void SmallInputJitterDoesNotFlipScarfButSlowTravelDoes()
    {
        var game = Playing(out int milliseconds);
        for (int i = 0; i < 30; i++)
        {
            Advance(game, ref milliseconds, 20, i % 2 == 0 ? .503f : .497f);
            Assert.Equal(-1, game.Snapshot.ScarfSide);
        }
        // Each step is smaller than the direction deadband; sustained travel
        // still changes the side without requiring a fast flick.
        for (int i = 1; i <= 30; i++)
            Advance(game, ref milliseconds, 20, .497f - i * .001f);
        Assert.Equal(1, game.Snapshot.ScarfSide);
    }

    [Fact]
    public void ReversalTurnsSmoothlyAndMissingInputFreezesTheExactPose()
    {
        var game = Playing(out int milliseconds);
        Advance(game, ref milliseconds, 60, .2f);
        var turning = game.Snapshot;
        Assert.InRange(turning.ScarfSide, -.95f, .9f);
        game.ClearInput(Epoch.AddMilliseconds(++milliseconds));
        for (int i = 0; i < 100; i++)
        {
            milliseconds += 10;
            game.Advance(Epoch.AddMilliseconds(milliseconds));
            Assert.Equal(turning.ScarfSide, game.Snapshot.ScarfSide);
            Assert.Equal(turning.RunSeconds, game.Snapshot.RunSeconds);
            Assert.Equal(turning.SquadX, game.Snapshot.SquadX);
        }
        Advance(game, ref milliseconds, 10, .2f);
        Assert.Equal(turning.ScarfSide, game.Snapshot.ScarfSide);
        Advance(game, ref milliseconds, 300, .2f);
        Assert.Equal(1, game.Snapshot.ScarfSide);
    }

    [Fact]
    public void ResetRestoresDefaultSideAndOldSnapshotsCannotChange()
    {
        var game = Playing(out int milliseconds);
        Advance(game, ref milliseconds, 350, .2f);
        var leftTravel = game.Snapshot;
        Assert.Equal(1, leftTravel.ScarfSide);
        game.Reset(Epoch.AddMilliseconds(++milliseconds));
        Assert.Equal(-1, game.Snapshot.ScarfSide);
        Assert.Equal(0, game.Snapshot.RunSeconds);
        Assert.Equal(1, leftTravel.ScarfSide);
    }

    [Fact]
    public void TurnAndRunPoseAreIndependentOfRenderingCadence()
    {
        var frequent = Playing(out int frequentTime);
        var sparse = Playing(out int sparseTime);
        Advance(frequent, ref frequentTime, 90, .2f, 10);
        Advance(sparse, ref sparseTime, 90, .2f, 30);
        Assert.Equal(frequent.Snapshot.ScarfSide, sparse.Snapshot.ScarfSide);
        Assert.Equal(frequent.Snapshot.RunSeconds, sparse.Snapshot.RunSeconds);
        Assert.Equal(frequent.Snapshot.SquadX, sparse.Snapshot.SquadX);
    }

    private static StrykersRunGame Playing(out int milliseconds)
    {
        var game = new StrykersRunGame();
        game.Start(Epoch);
        milliseconds = 0;
        Advance(game, ref milliseconds, 500, .5f);
        Assert.Equal(StrykersRunPhase.Playing, game.Phase);
        return game;
    }

    private static void Advance(StrykersRunGame game, ref int milliseconds, int duration, float position, int step = 10)
    {
        int end = milliseconds + duration;
        while (milliseconds < end)
        {
            milliseconds = Math.Min(end, milliseconds + step);
            var now = Epoch.AddMilliseconds(milliseconds);
            game.SetInput(position, now);
            game.Advance(now);
        }
    }
}
