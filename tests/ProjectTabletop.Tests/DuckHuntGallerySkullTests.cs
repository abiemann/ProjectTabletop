using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntGallerySkullTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double ms) => Origin.AddMilliseconds(ms);
    private static readonly BoardAim Play = new(.5, .46);

    [Fact]
    public void OnePlayStartsBothRowsAndSkullsCanBeHitWhileTheConveyorAdvances()
    {
        var game = Started();
        Assert.Equal(DuckHuntGalleryPhase.Opening, game.Snapshot.Phase);
        Assert.Equal(DuckHuntGalleryUpperPhase.Raising, game.Snapshot.UpperPhase);
        Assert.False(game.Snapshot.PlayVisible);
        Assert.Equal(0, game.Snapshot.UpperOpenProgress);
        game.Advance(At(700));
        Assert.Equal(.5, game.Snapshot.UpperOpenProgress, 10);
        game.Advance(At(2200));
        Assert.Equal(DuckHuntGalleryPhase.Advancing, game.Snapshot.Phase);
        Assert.Equal(DuckHuntGalleryUpperPhase.WaitingForHits, game.Snapshot.UpperPhase);
        Assert.True(game.Shoot(Skull(0), At(2210)));
        Assert.Equal(1, game.Snapshot.HitSkulls);
        Assert.Equal(0, game.Snapshot.HitTargets);
        Assert.Empty(game.Punctures);
        Assert.Equal(2.11, game.Snapshot.SkullHitSeconds(0), 10);
        Assert.False(game.Shoot(Skull(0), At(2210)));
        Assert.False(game.Shoot(Skull(0), At(2220)));
        Assert.Equal(1, game.Snapshot.HitSkulls);
        Assert.Equal(2.11, game.Snapshot.SkullHitSeconds(0), 10);
        Assert.Equal(2, game.Snapshot.Shots);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherRowCanFinishFirstButReplayWaitsForBoth(bool skullsFirst)
    {
        var game = Started();
        game.Advance(At(3900));
        for (int i = 0; i < 5; i++)
            Assert.True(game.Shoot(skullsFirst ? Skull(i) : Paper(game, i), At(4000 + i * 10)));
        game.Advance(At(8000));
        Assert.False(game.Snapshot.PlayVisible);
        Assert.False(game.Shoot(Play, At(8001)));
        Assert.Equal(1, game.Snapshot.Cycle);
        if (skullsFirst)
        {
            Assert.Equal(DuckHuntGalleryUpperPhase.Closed, game.Snapshot.UpperPhase);
            Assert.Equal(DuckHuntGalleryPhase.WaitingForHits, game.Snapshot.Phase);
            var stationary = game.Snapshot;
            game.Advance(At(8010));
            Assert.Equal(stationary, game.Snapshot);
        }
        else
        {
            Assert.Equal(DuckHuntGalleryPhase.Ready, game.Snapshot.Phase);
            Assert.Equal(DuckHuntGalleryUpperPhase.WaitingForHits, game.Snapshot.UpperPhase);
        }
        for (int i = 0; i < 5; i++)
            Assert.True(game.Shoot(skullsFirst ? Paper(game, i) : Skull(i), At(8100 + i * 10)));
        game.Advance(At(13000));
        Assert.True(game.Snapshot.PlayVisible);
        Assert.Equal(5, game.Snapshot.HitTargets);
        Assert.Equal(5, game.Snapshot.HitSkulls);
        Assert.False(game.Shoot(Play, At(8140)));
        Assert.True(game.Shoot(Play, At(13010)));
        Assert.Equal(2, game.Snapshot.Cycle);
        Assert.Empty(game.Punctures);
        Assert.Equal(0, game.Snapshot.SkullHitMask);
        Assert.Equal(0, game.Snapshot.Shots);
        Assert.Equal(0, game.Snapshot.AnimationSeconds);
        for (int i = 0; i < 5; i++) Assert.Equal(-1, game.Snapshot.SkullHitSeconds(i));
    }

    [Fact]
    public void AlternatingRowsKeepsIndependentProgressAndEachSkullShattersOnlyOnce()
    {
        var game = Started();
        game.Advance(At(3900));
        for (int i = 0; i < 5; i++)
        {
            Assert.True(game.Shoot(Skull(i), At(4000 + i * 30)));
            Assert.True(game.Shoot(Paper(game, i), At(4010 + i * 30)));
            Assert.False(game.Shoot(Skull(i), At(4020 + i * 30)));
            Assert.Equal(i + 1, game.Snapshot.HitSkulls);
            Assert.Equal(i + 1, game.Snapshot.HitTargets);
        }
        Assert.Equal(DuckHuntGalleryUpperPhase.ShatterWait, game.Snapshot.UpperPhase);
        Assert.Equal(DuckHuntGalleryPhase.SuccessWait, game.Snapshot.Phase);
        Assert.Equal(5, game.Punctures.Count);
        game.Advance(At(5119));
        Assert.Equal(DuckHuntGalleryUpperPhase.ShatterWait, game.Snapshot.UpperPhase);
        Assert.Equal(1, game.Snapshot.UpperOpenProgress);
        game.Advance(At(5120));
        Assert.Equal(DuckHuntGalleryUpperPhase.Lowering, game.Snapshot.UpperPhase);
        game.Advance(At(5720));
        Assert.Equal(.5, game.Snapshot.UpperOpenProgress, 10);
        Assert.Equal(5, game.Snapshot.HitSkulls);
        game.Advance(At(6320));
        Assert.Equal(DuckHuntGalleryUpperPhase.Closed, game.Snapshot.UpperPhase);
        Assert.Equal(0, game.Snapshot.UpperOpenProgress);
        Assert.Equal(5, game.Snapshot.HitSkulls);
        Assert.False(game.Snapshot.PlayVisible);
    }

    [Fact]
    public void EachRowUsesItsOwnPhotoBoundaryWhenTheOtherMechanismChangesPhase()
    {
        var lowerFirst = Started();
        lowerFirst.Advance(At(3900));
        for (int i = 0; i < 5; i++) Assert.True(lowerFirst.Shoot(Paper(lowerFirst, i), At(4000 + i * 10)));
        lowerFirst.Advance(At(7000));
        Assert.Equal(DuckHuntGalleryPhase.Closing, lowerFirst.Snapshot.Phase);
        Assert.True(lowerFirst.Shoot(Skull(0), At(6935)));
        lowerFirst.Advance(At(7980));
        Assert.Equal(DuckHuntGalleryPhase.Ready, lowerFirst.Snapshot.Phase);
        Assert.True(lowerFirst.Shoot(Skull(1), At(7935)));

        var upperFirst = Started();
        upperFirst.Advance(At(3900));
        for (int i = 0; i < 5; i++) Assert.True(upperFirst.Shoot(Skull(i), At(4000 + i * 10)));
        upperFirst.Advance(At(5100));
        Assert.Equal(DuckHuntGalleryUpperPhase.Lowering, upperFirst.Snapshot.UpperPhase);
        Assert.True(upperFirst.Shoot(Paper(upperFirst, 0), At(5035)));
    }

    [Fact]
    public void UpperShotsBeforeTheBackdropIsOpenCannotBecomeHitsLater()
    {
        var game = Started();
        game.Advance(At(1305));
        Assert.False(game.Shoot(Skull(0), At(1299)));
        Assert.Equal(0, game.Snapshot.SkullHitMask);
        Assert.True(game.Shoot(Skull(0), At(1300)));
        Assert.False(game.Shoot(null, At(1310)));
        game.Advance(At(1600));
        Assert.False(game.Shoot(Skull(1), At(1400)));
        Assert.Equal(1, game.Snapshot.HitSkulls);
    }

    [Fact]
    public void ReadinessPauseFreezesBackdropAndShatterClockWithoutReusingOldShots()
    {
        var game = Started();
        game.Advance(At(1300));
        Assert.True(game.Shoot(Skull(0), At(1400)));
        game.SetReady(false, At(1450));
        var paused = game.Snapshot;
        game.Advance(At(6450));
        Assert.Equal(paused, game.Snapshot);
        Assert.False(game.Shoot(Skull(1), At(6450)));
        game.SetReady(true, At(6450));
        Assert.Equal(paused with { Paused = false, Revision = game.Snapshot.Revision }, game.Snapshot);
        Assert.False(game.Shoot(Skull(1), At(6449)));
        game.Advance(At(6650));
        Assert.InRange(game.Snapshot.AnimationSeconds - paused.AnimationSeconds, .19, .21);
        Assert.Equal(paused.Skull0HitSeconds, game.Snapshot.Skull0HitSeconds);
        Assert.True(game.Shoot(Skull(1), At(6651)));
        Assert.Equal(1.551, game.Snapshot.Skull1HitSeconds, 10);
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void SkullHitDomainFollowsTheVisibleExteriorAndRejectsCornersAndGaps(double aspect)
    {
        for (int i = 0; i < 5; i++)
        {
            var pose = DuckHuntGalleryLayout.SkullPose(i, aspect);
            var bounds = pose.Bounds;
            Assert.Equal(1, bounds.Width * aspect / bounds.Height, 10);
            Assert.Equal(pose.BaseContact.U, bounds.X + bounds.Width * DuckHuntGalleryLayout.SkullBaseU, 10);
            Assert.Equal(pose.BaseContact.V, bounds.Y + bounds.Height * DuckHuntGalleryLayout.SkullBaseV, 10);
            Assert.InRange(bounds.X, 0, 1);
            Assert.InRange(bounds.X + bounds.Width, 0, 1);
            foreach (var source in new BoardAim[] { new(627, 50), new(550, 53), new(627, 500), new(520, 1200), new(627, 1207), new(210, 450), new(1040, 450) })
                Assert.True(DuckHuntGalleryLayout.ContainsSkull(i, Source(bounds, source), aspect));
            foreach (var source in new BoardAim[] { new(0, 0), new(1253, 0), new(0, 1253), new(1253, 1253), new(627, 40), new(627, 1215), new(200, 450), new(1055, 450), new(974, 821), new(920, 908) })
                Assert.False(DuckHuntGalleryLayout.ContainsSkull(i, Source(bounds, source), aspect));
            if (i < 4)
            {
                var next = DuckHuntGalleryLayout.SkullBounds(i + 1, aspect);
                var gap = new BoardAim((bounds.X + bounds.Width + next.X) / 2, bounds.Y + bounds.Height / 2);
                Assert.False(DuckHuntGalleryLayout.ContainsSkull(i, gap, aspect));
                Assert.False(DuckHuntGalleryLayout.ContainsSkull(i + 1, gap, aspect));
            }
        }
    }

    [Fact]
    public void SkullHitsDoNotChangeTheBoardRoundEpochOrDrawerState()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        board.SetDuckHuntReady(true, At(0));
        Assert.True(board.TryDuckHuntShot(Play, At(100)));
        long epoch = board.DuckHuntRoundRevision;
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(1400)));
        board.TickDuckHunt(At(1700));
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.True(board.TryDuckHuntShot(Skull(0), At(1800)));
        Assert.Equal(epoch, board.DuckHuntRoundRevision);
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.True(board.ActivateButton("duck-hunt-drawer-close", At(1900)));
        board.TickDuckHunt(At(2200));
        Assert.True(board.TryDuckHuntShot(Skull(1), At(2300)));
        Assert.Equal(epoch, board.DuckHuntRoundRevision);
        Assert.False(board.DuckHuntDrawerOpen);
    }

    private static DuckHuntGalleryGame Started()
    {
        var game = new DuckHuntGalleryGame();
        game.Reset(At(0));
        game.SetReady(true, At(0));
        Assert.True(game.Shoot(Play, At(100)));
        return game;
    }

    private static BoardAim Skull(int index)
    {
        var bounds = DuckHuntGalleryLayout.SkullBounds(index);
        return new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }

    private static BoardAim Paper(DuckHuntGalleryGame game, int index) =>
        DuckHuntGalleryLayout.TargetQuad(index, game.Snapshot).Map(DuckHuntGalleryLayout.PaperCenterU,
            DuckHuntGalleryLayout.PaperCenterV);

    private static BoardAim Source(BoardRect bounds, BoardAim source) =>
        new(bounds.X + bounds.Width * source.U / 1254, bounds.Y + bounds.Height * source.V / 1254);
}
