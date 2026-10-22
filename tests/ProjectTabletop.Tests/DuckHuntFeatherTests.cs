using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntFeatherTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 22, 18, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void OnlyAnAcceptedHitEmitsFeathers()
    {
        var game = Started();
        game.Advance(At(11000));
        Assert.False(game.Shoot(null, At(11000)));
        Assert.False(game.Shoot(new(double.NaN, .48), At(11001)));
        Assert.False(game.Shoot(new(.01, .01), At(11002)));
        Assert.Equal(0, game.Feathers.Count);
        Assert.True(game.Shoot(new(.5, .48), At(11003)));
        Assert.Equal(DuckHuntFeathers.FeathersPerHit, game.Feathers.Count);
        Assert.True(game.Feathers.IsAnimating);
        Assert.False(game.Shoot(new(.5, .48), At(11003))); // Duplicate.
        Assert.False(game.Shoot(new(.5, .48), At(11004))); // During the turn.
        game.SetPaused(true, At(12000));
        Assert.False(game.Shoot(new(game.Snapshot.U, game.Snapshot.V), At(12001)));
        Assert.Equal(DuckHuntFeathers.FeathersPerHit, game.Feathers.Count);
    }

    [Theory]
    [InlineData(.55, true)]
    [InlineData(1, false)]
    [InlineData(1.8, true)]
    public void FeathersLeaveThePhotographedBodyThenSettleNearItsFeet(double aspect, bool enterFromLeft)
    {
        var game = Started(aspect, enterFromLeft);
        game.Advance(At(11000));
        var duck = game.Snapshot;
        var bounds = DuckHuntLayout.PhotoBounds(duck, aspect);
        Assert.True(game.Shoot(new(duck.U, duck.V), At(11000), aspect));
        var originals = Poses(game);
        foreach (var pose in originals)
        {
            Assert.True(DuckHuntLayout.ContainsDuck(new(pose.U, pose.V), duck, aspect));
            Assert.InRange(pose.Length, .022, .035);
            Assert.False(pose.Settled);
        }
        game.Advance(At(12000));
        for (int i = 0; i < game.Feathers.Count; i++)
        {
            var pose = game.Feathers.GetPose(i);
            Assert.NotEqual(originals[i].U, pose.U);
            Assert.True(pose.V > originals[i].V);
            Assert.NotEqual(originals[i].Angle, pose.Angle);
            Assert.InRange(pose.WidthScale, .35, 1);
        }
        game.Advance(At(14000));
        Assert.False(game.Feathers.IsAnimating);
        for (int i = 0; i < game.Feathers.Count; i++)
        {
            var pose = game.Feathers.GetPose(i);
            Assert.True(pose.Settled);
            Assert.InRange(pose.U, pose.Length * .5 / aspect, 1 - pose.Length * .5 / aspect);
            Assert.InRange(pose.V, bounds.Y + bounds.Height - .03, .70 - pose.Length * .5);
        }
    }

    [Fact]
    public void TenthHitEmitsAndAnimatesEvenAfterPlayerWinsThenRemainsUntilReset()
    {
        var game = Started();
        for (int hit = 0; hit < DuckHuntGame.HitsToWin; hit++)
        {
            int ms = 11000 + hit * 1000;
            game.Advance(At(ms));
            Assert.True(game.Shoot(new(game.Snapshot.U, game.Snapshot.V), At(ms)));
            Assert.Equal((hit + 1) * DuckHuntFeathers.FeathersPerHit, game.Feathers.Count);
        }
        Assert.Equal(DuckHuntPhase.PlayerWon, game.Snapshot.Phase);
        Assert.Equal(DuckHuntFeathers.MaximumCount, game.Feathers.Count);
        Assert.True(game.Feathers.IsAnimating);
        long revision = game.Snapshot.Revision;
        var falling = game.Feathers.GetPose(game.Feathers.Count - 1);
        game.Advance(At(20500));
        Assert.True(game.Snapshot.Revision > revision);
        Assert.NotEqual(falling, game.Feathers.GetPose(game.Feathers.Count - 1));
        game.Advance(At(24000));
        Assert.False(game.Feathers.IsAnimating);
        var settled = Poses(game);
        var terminal = game.Snapshot;
        game.Advance(At(60000));
        Assert.Equal(terminal, game.Snapshot);
        Assert.Equal(settled, Poses(game));
        Assert.False(game.Shoot(new(terminal.U, terminal.V), At(60001)));
        Assert.Equal(DuckHuntFeathers.MaximumCount, game.Feathers.Count);
        game.Reset(At(61000));
        Assert.Equal(0, game.Feathers.Count);
        Assert.False(game.Feathers.IsAnimating);
        Assert.True(game.Start(At(61000)));
        Assert.Equal(0, game.Feathers.Count);
    }

    [Fact]
    public void MissAndDuckEscapeKeepSettledFeathersVisible()
    {
        var game = Started();
        game.Advance(At(11000));
        Assert.True(game.Shoot(new(.5, .48), At(11000)));
        game.Advance(At(15000));
        var settled = Poses(game);
        Assert.False(game.Shoot(new(.01, .01), At(15001)));
        game.Advance(At(60000));
        Assert.Equal(DuckHuntPhase.DuckWon, game.Snapshot.Phase);
        Assert.Equal(settled, Poses(game));
        Assert.False(game.Feathers.IsAnimating);
        var terminal = game.Snapshot;
        game.Advance(At(90000));
        Assert.Equal(terminal, game.Snapshot);
        Assert.Equal(settled, Poses(game));
    }

    [Fact]
    public void PausingFreezesEveryFeatherAndResumingContinuesWithoutAJump()
    {
        var game = Started();
        game.Advance(At(11000));
        Assert.True(game.Shoot(new(.5, .48), At(11000)));
        game.SetPaused(true, At(11500));
        var frozen = Poses(game);
        var paused = game.Snapshot;
        game.Advance(At(60000));
        Assert.Equal(frozen, Poses(game));
        Assert.Equal(paused, game.Snapshot);
        game.SetPaused(false, At(60000));
        Assert.Equal(frozen, Poses(game));
        game.Advance(At(60500));
        Assert.NotEqual(frozen[0], game.Feathers.GetPose(0));
        Assert.True(game.Feathers.IsAnimating);
        game.Advance(At(63000));
        Assert.False(game.Feathers.IsAnimating);
    }

    [Fact]
    public void LatePhotoUsesTheOriginalDuckAndCatchesUpToTheCurrentRenderTime()
    {
        var immediate = Started();
        immediate.Advance(At(11000));
        Assert.True(immediate.Shoot(new(.5, .48), At(11000)));
        immediate.Advance(At(11200));
        var delayed = Started();
        delayed.Advance(At(11200));
        Assert.True(delayed.Shoot(new(.5, .48), At(11000)));
        Assert.Equal(Poses(immediate), Poses(delayed));
        Assert.True(delayed.Feathers.IsAnimating);
    }

    [Fact]
    public void HitNearTheEdgeStillLandsAllFeathersOnTheBoard()
    {
        var game = Started();
        game.Advance(At(21870));
        var bounds = DuckHuntLayout.PhotoBounds(game.Snapshot, 1);
        var tail = new BoardAim(bounds.X + bounds.Width * (1 - 1300 / DuckHuntLayout.PhotoPixelWidth),
            bounds.Y + bounds.Height * 715 / DuckHuntLayout.PhotoPixelHeight);
        game.Advance(At(22020));
        Assert.Equal(DuckHuntPhase.DuckWon, game.Snapshot.Phase);
        Assert.True(game.Shoot(tail, At(21870)));
        game.Advance(At(26000));
        foreach (var pose in Poses(game))
        {
            Assert.True(pose.Settled);
            Assert.InRange(pose.U, pose.Length * .5, 1 - pose.Length * .5);
            Assert.InRange(pose.V, pose.Length * .5, .70 - pose.Length * .5);
        }
    }

    private static DuckHuntFeatherPose[] Poses(DuckHuntGame game) =>
        Enumerable.Range(0, game.Feathers.Count).Select(game.Feathers.GetPose).ToArray();

    private static DuckHuntGame Started(double aspect = 1, bool enterFromLeft = true)
    {
        var game = new DuckHuntGame();
        game.Reset(At(0));
        Assert.True(game.Start(At(0), aspect, enterFromLeft));
        return game;
    }
}
