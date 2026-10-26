using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntGalleryMiddleTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double milliseconds) => Origin.AddMilliseconds(milliseconds);
    private static readonly BoardAim Play = new(.5, .46);
    private static BoardAim Scene(double u, double v, double aspect = 1) => DuckHuntGalleryLayout.ScenePoint(u, v, aspect);

    [Fact]
    public void ABackgroundMissClosesOnlyItsReadyRowAndTheOtherRowStillAcceptsHits()
    {
        var game = Waiting();
        Assert.False(game.Shoot(Scene(.5, .18), At(4000)));
        Assert.Equal(DuckHuntGalleryUpperPhase.Lowering, game.Snapshot.UpperPhase);
        Assert.Equal(DuckHuntGalleryPhase.WaitingForHits, game.Snapshot.Phase);
        Assert.True(game.Shoot(Paper(game), At(4010)));
        Assert.Equal(1, game.Snapshot.HitTargets);
        Assert.False(game.Shoot(Scene(.5, .78), At(4020)));
        Assert.Equal(DuckHuntGalleryPhase.Departing, game.Snapshot.Phase);
        Assert.Equal(1, game.Snapshot.ConveyorPosition);
        game.Advance(At(5219));
        Assert.Equal(DuckHuntGalleryMiddlePhase.Closed, game.Snapshot.MiddlePhase);
        game.Advance(At(6920));
        Assert.Equal(DuckHuntGalleryPhase.Ready, game.Snapshot.Phase);
        Assert.Equal(DuckHuntGalleryUpperPhase.Closed, game.Snapshot.UpperPhase);
        Assert.Equal(DuckHuntGalleryMiddlePhase.Raising, game.Snapshot.MiddlePhase);
        Assert.False(game.Snapshot.PlayVisible);
    }

    [Fact]
    public void LowerMissDoesNotCloseUpperAndOldOrMovingRowPhotosCannotCauseMissClosures()
    {
        var game = Started();
        game.Advance(At(1305));
        Assert.False(game.Shoot(Scene(.5, .18), At(1299)));
        Assert.Equal(DuckHuntGalleryUpperPhase.WaitingForHits, game.Snapshot.UpperPhase);
        Assert.False(game.Shoot(Scene(.5, .78), At(1400)));
        Assert.Equal(DuckHuntGalleryPhase.RevealWait, game.Snapshot.Phase);
        game.Advance(At(3905));
        Assert.False(game.Shoot(Scene(.5, .78), At(3899)));
        Assert.Equal(DuckHuntGalleryPhase.WaitingForHits, game.Snapshot.Phase);
        Assert.False(game.Shoot(Scene(.5, .78), At(4000)));
        Assert.Equal(DuckHuntGalleryPhase.Departing, game.Snapshot.Phase);
        Assert.Equal(DuckHuntGalleryUpperPhase.WaitingForHits, game.Snapshot.UpperPhase);
        var skull = DuckHuntGalleryLayout.SkullBounds(0);
        Assert.True(game.Shoot(new(skull.X + skull.Width / 2, skull.Y + skull.Height / 2), At(4010)));
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void MissRegionsExcludeWoodControlsAndMetalButIncludeActualRecessCornersAndBelt(double aspect)
    {
        foreach (var point in new[] { Scene(.12, .18, aspect), Scene(.5, .355, aspect), Scene(.5, .56, aspect), new BoardAim(.03, .94) })
        {
            Assert.False(DuckHuntGalleryLayout.ContainsUpperMiss(point, aspect));
            Assert.False(DuckHuntGalleryLayout.ContainsLowerMiss(point, aspect));
        }
        Assert.True(DuckHuntGalleryLayout.ContainsUpperMiss(Scene(.5, .18, aspect), aspect));
        Assert.True(DuckHuntGalleryLayout.ContainsUpperMiss(Scene(.13, .31, aspect), aspect));
        Assert.True(DuckHuntGalleryLayout.ContainsLowerMiss(Scene(.5, .78, aspect), aspect));
        var game = Waiting();
        var paper = DuckHuntGalleryLayout.TargetQuad(2, game.Snapshot, aspect);
        int shot = 0;
        foreach (var local in new[] { new BoardAim(.5, .80), new BoardAim(.5, .95), new BoardAim(.5, .035) })
        {
            Assert.True(DuckHuntGalleryLayout.ContainsAssembly(local));
            Assert.False(game.Shoot(paper.Map(local.U, local.V), At(4000 + shot++ * 10), aspect));
            Assert.Equal(DuckHuntGalleryPhase.WaitingForHits, game.Snapshot.Phase);
        }
        Assert.False(DuckHuntGalleryLayout.ContainsAssembly(new(.01, .01)));
        Assert.False(game.Shoot(paper.Map(.01, .01), At(4200), aspect));
        Assert.Equal(DuckHuntGalleryPhase.Departing, game.Snapshot.Phase);
    }

    [Fact]
    public void FiveSequentialTargetsEnterTurnWaitExactlyThreeSecondsAndLeaveBeforeReplay()
    {
        var game = Middle();
        Assert.Equal(DuckHuntGalleryMiddlePhase.Raising, game.Snapshot.MiddlePhase);
        Assert.False(game.Snapshot.PlayVisible);
        for (int i = 0; i < 5; i++)
        {
            double entered = 7900 + i * 4800;
            game.Advance(At(entered));
            Assert.Equal(DuckHuntGalleryMiddlePhase.Entering, game.Snapshot.MiddlePhase);
            Assert.Equal(i, game.Snapshot.MiddleTargetIndex);
            Assert.Equal(90, DuckHuntGalleryLayout.MiddleTargetPose(game.Snapshot).YawDegrees);
            game.Advance(At(entered + 650));
            Assert.Equal(DuckHuntGalleryMiddlePhase.TurningToFace, game.Snapshot.MiddlePhase);
            game.Advance(At(entered + 900));
            Assert.Equal(DuckHuntGalleryMiddlePhase.WaitingForHit, game.Snapshot.MiddlePhase);
            Assert.Equal(0, DuckHuntGalleryLayout.MiddleTargetPose(game.Snapshot).YawDegrees);
            Assert.True(game.Shoot(MiddlePaper(game), At(entered + 950)));
            Assert.Equal(DuckHuntGalleryMiddlePhase.WaitingForHit, game.Snapshot.MiddlePhase);
            Assert.True(game.Shoot(MiddlePaper(game), At(entered + 1000)));
            Assert.Equal(5 + i, game.Punctures[^1].TargetIndex);
            game.Advance(At(entered + 3899));
            Assert.Equal(DuckHuntGalleryMiddlePhase.WaitingForHit, game.Snapshot.MiddlePhase);
            game.Advance(At(entered + 3900));
            Assert.Equal(DuckHuntGalleryMiddlePhase.TurningAway, game.Snapshot.MiddlePhase);
            game.Advance(At(entered + 4150));
            Assert.Equal(DuckHuntGalleryMiddlePhase.Leaving, game.Snapshot.MiddlePhase);
            Assert.Equal(-90, DuckHuntGalleryLayout.MiddleTargetPose(game.Snapshot).YawDegrees);
            Assert.False(game.Snapshot.PlayVisible);
        }
        game.Advance(At(31900));
        Assert.Equal(DuckHuntGalleryMiddlePhase.Lowering, game.Snapshot.MiddlePhase);
        Assert.False(DuckHuntGalleryLayout.MiddleTargetPose(game.Snapshot).Visible);
        Assert.False(game.Snapshot.PlayVisible);
        game.Advance(At(32900));
        Assert.Equal(DuckHuntGalleryMiddlePhase.Complete, game.Snapshot.MiddlePhase);
        Assert.True(game.Snapshot.PlayVisible);
        Assert.Equal(31, game.Snapshot.MiddleHitMask);
        Assert.Equal(10, game.Punctures.Count);
        Assert.False(game.Shoot(Play, At(32899)));
        Assert.False(game.Start(At(32899)));
        Assert.False(game.Shoot(Play, At(32901)));
        Assert.True(game.Start(At(32901)));
        Assert.Empty(game.Punctures);
        Assert.Equal(0, game.Snapshot.MiddleHitMask);
        Assert.Equal(DuckHuntGalleryMiddlePhase.Closed, game.Snapshot.MiddlePhase);
    }

    [Fact]
    public void PhotoTimeAcceptsLastVisibleFrameAfterRenderTurnsAwayButNeverTheDeadlineOrLater()
    {
        var game = Middle();
        game.Advance(At(8800));
        var paper = MiddlePaper(game);
        Assert.False(game.Shoot(paper, At(8799)));
        game.Advance(At(11820));
        Assert.Equal(DuckHuntGalleryMiddlePhase.TurningAway, game.Snapshot.MiddlePhase);
        Assert.True(game.Shoot(paper, At(11799)));
        Assert.Equal(At(11799), Assert.Single(game.Punctures).ShotTime);
        Assert.False(game.Shoot(paper, At(11799)));
        Assert.False(game.Shoot(paper, At(11800)));
        Assert.False(game.Shoot(paper, At(11801)));
        Assert.Single(game.Punctures);
        var stale = Middle();
        stale.Advance(At(12000));
        Assert.False(stale.Shoot(paper, At(11799)));
    }

    [Theory]
    [InlineData(7400)]
    [InlineData(8100)]
    [InlineData(8650)]
    [InlineData(9000)]
    [InlineData(11900)]
    [InlineData(12200)]
    [InlineData(32400)]
    public void PausePreservesEveryMiddlePoseAndTheRemainingExposureWindow(int at)
    {
        var game = Middle();
        game.Advance(At(at));
        game.SetReady(false, At(at));
        var paused = game.Snapshot;
        game.Advance(At(at + 5000));
        Assert.Equal(paused, game.Snapshot);
        Assert.False(game.Shoot(Play, At(at + 5000)));
        game.SetReady(true, At(at + 5000));
        Assert.Equal(paused with { Paused = false, Revision = game.Snapshot.Revision }, game.Snapshot);
        Assert.False(game.Shoot(Play, At(at + 4999)));
        if (at == 9000)
        {
            game.Advance(At(16800));
            Assert.Equal(DuckHuntGalleryMiddlePhase.TurningAway, game.Snapshot.MiddlePhase);
            var face = game.Snapshot with { MiddlePhase = DuckHuntGalleryMiddlePhase.WaitingForHit };
            var point = DuckHuntGalleryLayout.MiddleTargetQuad(face).Map(DuckHuntGalleryLayout.PaperCenterU, DuckHuntGalleryLayout.PaperCenterV);
            Assert.True(game.Shoot(point, At(16799)));
            Assert.False(game.Shoot(point, At(16800)));
        }
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void MiddlePaperHasTheExistingCircleClipsAndExactPunctureCoordinates(double aspect)
    {
        var game = Middle();
        game.Advance(At(8800));
        var pose = DuckHuntGalleryLayout.MiddleTargetPose(game.Snapshot, aspect);
        Assert.Equal(DuckHuntGalleryLayout.TargetAspect, pose.Quad.Bounds.Width * aspect / pose.Quad.Bounds.Height, 10);
        Assert.Equal(pose.BaseContact.U, pose.Quad.Map(DuckHuntGalleryLayout.TargetBaseU, DuckHuntGalleryLayout.TargetBaseV).U, 10);
        Assert.Equal(pose.BaseContact.V, pose.Quad.Map(DuckHuntGalleryLayout.TargetBaseU, DuckHuntGalleryLayout.TargetBaseV).V, 10);
        Assert.False(game.Shoot(pose.Quad.Map(.01, .01), At(8810), aspect));
        Assert.False(game.Shoot(pose.Quad.Map(.5, .95), At(8820), aspect));
        var local = new BoardAim(DuckHuntGalleryLayout.PaperCenterU + .4 * DuckHuntGalleryLayout.PaperRadiusU,
            DuckHuntGalleryLayout.PaperCenterV - .3 * DuckHuntGalleryLayout.PaperRadiusV);
        Assert.True(game.Shoot(pose.Quad.Map(local.U, local.V), At(8830), aspect));
        var hole = Assert.Single(game.Punctures);
        Assert.Equal(local.U, hole.U, 10);
        Assert.Equal(local.V, hole.V, 10);
        Assert.Equal(5, hole.TargetIndex);
    }

    private static DuckHuntGalleryGame Started()
    {
        var game = new DuckHuntGalleryGame(); game.Reset(At(0)); game.SetReady(true, At(0));
        Assert.True(game.Start(At(100)));
        return game;
    }
    private static DuckHuntGalleryGame Waiting() { var game = Started(); game.Advance(At(3900)); return game; }
    private static DuckHuntGalleryGame Middle()
    {
        var game = Waiting();
        Assert.False(game.Shoot(Scene(.5, .78), At(4000)));
        Assert.False(game.Shoot(Scene(.5, .18), At(4010)));
        game.Advance(At(6900));
        return game;
    }
    private static BoardAim Paper(DuckHuntGalleryGame game) => DuckHuntGalleryLayout.TargetQuad(2, game.Snapshot)
        .Map(DuckHuntGalleryLayout.PaperCenterU, DuckHuntGalleryLayout.PaperCenterV);
    private static BoardAim MiddlePaper(DuckHuntGalleryGame game) => DuckHuntGalleryLayout.MiddleTargetQuad(game.Snapshot)
        .Map(DuckHuntGalleryLayout.PaperCenterU, DuckHuntGalleryLayout.PaperCenterV);
}
