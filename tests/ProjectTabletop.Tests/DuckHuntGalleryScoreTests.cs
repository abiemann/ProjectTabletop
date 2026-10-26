using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntGalleryScoreTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double ms) => Origin.AddMilliseconds(ms);

    [Fact]
    public void MeasuredPrintedRingsDefineInclusiveScoringBoundariesWithoutChangingPhysicalHits()
    {
        Assert.Equal(100, DuckHuntGalleryLayout.PaperScore(Source(585, 535)));
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI / 8;
            var inner = RingPoint(true, angle, 0);
            var outer = RingPoint(false, angle, 0);
            Assert.True(DuckHuntGalleryLayout.ContainsPaper(inner));
            Assert.True(DuckHuntGalleryLayout.ContainsPaper(outer));
            Assert.Equal(100, DuckHuntGalleryLayout.PaperScore(inner));
            Assert.Equal(50, DuckHuntGalleryLayout.PaperScore(RingPoint(true, angle, .1)));
            Assert.Equal(50, DuckHuntGalleryLayout.PaperScore(outer));
            Assert.Equal(0, DuckHuntGalleryLayout.PaperScore(RingPoint(false, angle, .1)));
        }
        Assert.Equal(0, DuckHuntGalleryLayout.PaperScore(new(.01, .01)));
        Assert.Equal(0, DuckHuntGalleryLayout.PaperScore(new(double.NaN, .5)));
        foreach (var clip in DuckHuntGalleryLayout.PaperClipBounds)
            Assert.Equal(0, DuckHuntGalleryLayout.PaperScore(new(clip.X + clip.Width / 2, clip.Y + clip.Height / 2)));
    }

    [Theory]
    [InlineData(.55, 100)]
    [InlineData(1, 50)]
    [InlineData(1.8, 0)]
    [InlineData(2.4, 100)]
    public void LowerPaperScoresOnlyTheFirstPhysicalHitIncludingAnOuterZero(double aspect, int points)
    {
        var game = Waiting();
        var paper = DuckHuntGalleryLayout.TargetQuad(2, game.Snapshot, aspect);
        Assert.True(game.Shoot(paper.Map(Zone(points).U, Zone(points).V), At(4000), aspect));
        Assert.Equal(points, game.Snapshot.Score);
        Assert.Equal(0, game.Snapshot.ScoreAnimationSeconds);
        Assert.Equal(1, game.Snapshot.HitTargets);
        var center = Zone(100);
        Assert.True(game.Shoot(paper.Map(center.U, center.V), At(4010), aspect));
        Assert.Equal(points, game.Snapshot.Score);
        Assert.Equal(2, game.Punctures.Count);
        Assert.Equal(1, game.Snapshot.HitTargets);
    }

    [Theory]
    [InlineData(.55, 0)]
    [InlineData(1, 100)]
    [InlineData(1.8, 50)]
    [InlineData(2.4, 0)]
    public void MiddlePaperAlsoScoresOnlyItsFirstHitAndKeepsTheFullExposure(double aspect, int points)
    {
        var game = Middle();
        game.Advance(At(8800));
        var paper = DuckHuntGalleryLayout.MiddleTargetQuad(game.Snapshot, aspect);
        var local = Zone(points);
        Assert.True(game.Shoot(paper.Map(local.U, local.V), At(8810), aspect));
        Assert.Equal(points, game.Snapshot.Score);
        var center = Zone(100);
        Assert.True(game.Shoot(paper.Map(center.U, center.V), At(8820), aspect));
        Assert.Equal(points, game.Snapshot.Score);
        Assert.Equal(1, game.Snapshot.MiddleHitMask);
        Assert.Equal(2, game.Punctures.Count);
        Assert.Equal(DuckHuntGalleryMiddlePhase.WaitingForHit, game.Snapshot.MiddlePhase);
        game.Advance(At(11799));
        Assert.Equal(DuckHuntGalleryMiddlePhase.WaitingForHit, game.Snapshot.MiddlePhase);
        game.Advance(At(11800));
        Assert.Equal(DuckHuntGalleryMiddlePhase.TurningAway, game.Snapshot.MiddlePhase);
    }

    [Fact]
    public void SkullAwardsOneHundredOnceAndInvalidStaleOrDuplicateShotsCannotScore()
    {
        var game = Waiting();
        var skull = Skull(0);
        Assert.False(game.Shoot(null, At(4000)));
        Assert.False(game.Shoot(new(double.NaN, .5), At(4000)));
        Assert.False(game.Shoot(skull, At(3700)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.True(game.Shoot(skull, At(4000)));
        Assert.Equal(100, game.Snapshot.Score);
        Assert.False(game.Shoot(skull, At(4000)));
        Assert.False(game.Shoot(skull, At(4010)));
        Assert.Equal(100, game.Snapshot.Score);
        Assert.Equal(1, game.Snapshot.HitSkulls);
    }

    [Fact]
    public void LateConfirmedMiddlePhotoScoresBeforeDeadlineButNeverAtOrAfterIt()
    {
        var game = Middle();
        game.Advance(At(8800));
        var local = Zone(50);
        var point = DuckHuntGalleryLayout.MiddleTargetQuad(game.Snapshot).Map(local.U, local.V);
        game.Advance(At(11820));
        Assert.True(game.Shoot(point, At(11799)));
        Assert.Equal(50, game.Snapshot.Score);
        Assert.InRange(game.Snapshot.ScoreAnimationSeconds, .016, .017);
        Assert.False(game.Shoot(point, At(11800)));
        Assert.False(game.Shoot(point, At(11801)));
        Assert.Equal(50, game.Snapshot.Score);
        var late = Middle();
        late.Advance(At(11820));
        Assert.False(late.Shoot(point, At(11800)));
        Assert.Equal(0, late.Snapshot.Score);
    }

    [Fact]
    public void ScoreAnimationPausesResumesAndSettlesWithoutAnEndlessSnapshotClock()
    {
        var game = Middle();
        game.Advance(At(8800));
        var local = Zone(100);
        var point = DuckHuntGalleryLayout.MiddleTargetQuad(game.Snapshot).Map(local.U, local.V);
        Assert.True(game.Shoot(point, At(8800)));
        game.Advance(At(8900));
        Assert.Equal(.1, game.Snapshot.ScoreAnimationSeconds, 10);
        game.SetReady(false, At(8900));
        var paused = game.Snapshot;
        game.Advance(At(13900));
        Assert.Equal(paused, game.Snapshot);
        game.SetReady(true, At(13900));
        Assert.Equal(.1, game.Snapshot.ScoreAnimationSeconds, 10);
        game.Advance(At(13950));
        Assert.Equal(.15, game.Snapshot.ScoreAnimationSeconds, 10);
        game.Advance(At(15000));
        Assert.Equal(1, game.Snapshot.ScoreAnimationSeconds);
        var settled = game.Snapshot;
        game.Advance(At(15100));
        Assert.Equal(settled, game.Snapshot);
    }

    [Fact]
    public void FifteenFirstBullseyesReachExactlyFifteenHundredAndFinalScorePersistsUntilReplay()
    {
        var game = Waiting();
        for (int i = 0; i < 5; i++)
        {
            Assert.True(game.Shoot(Skull(i), At(4000 + i * 10)));
            Assert.Equal((i + 1) * 100, game.Snapshot.Score);
        }
        var center = Zone(100);
        for (int i = 0; i < 5; i++)
        {
            var point = DuckHuntGalleryLayout.TargetQuad(i, game.Snapshot).Map(center.U, center.V);
            Assert.True(game.Shoot(point, At(4100 + i * 10)));
            Assert.Equal(600 + i * 100, game.Snapshot.Score);
        }
        for (int i = 0; i < 5; i++)
        {
            double face = 9940 + i * 4800;
            game.Advance(At(face));
            var point = DuckHuntGalleryLayout.MiddleTargetQuad(game.Snapshot).Map(center.U, center.V);
            Assert.True(game.Shoot(point, At(face + 1)));
            Assert.Equal(1100 + i * 100, game.Snapshot.Score);
            Assert.Equal(0, game.Snapshot.ScoreAnimationSeconds);
            Assert.True(game.Shoot(point, At(face + 10)));
            Assert.Equal(1100 + i * 100, game.Snapshot.Score);
        }
        game.Advance(At(34040));
        Assert.True(game.Snapshot.PlayVisible);
        Assert.Equal(DuckHuntGalleryGame.MaximumScore, game.Snapshot.Score);
        var finished = game.Snapshot;
        game.Advance(At(90000));
        Assert.Equal(finished, game.Snapshot);
        Assert.True(game.Start(At(90001)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.Equal(0, game.Snapshot.ScoreAnimationSeconds);
    }

    [Fact]
    public void StartingAndReplayingCloseTheDrawerAndResetTheRoundScore()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(-500)); board.SetDuckHuntReady(true, At(-500));
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.True(board.ActivateButton("duck-hunt-play", At(100)));
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(0, board.GalleryState.Score);
        board.TickDuckHunt(At(3900));
        var local = Zone(100);
        Assert.True(board.TryDuckHuntShot(DuckHuntGalleryLayout.TargetQuad(2, board.GalleryState).Map(local.U, local.V), At(4000)));
        Assert.False(board.TryDuckHuntShot(DuckHuntGalleryLayout.ScenePoint(.5, .78, 1), At(4010)));
        Assert.False(board.TryDuckHuntShot(DuckHuntGalleryLayout.ScenePoint(.5, .18, 1), At(4020)));
        board.TickDuckHunt(At(33000));
        Assert.True(board.GalleryState.PlayVisible);
        Assert.Equal(100, board.GalleryState.Score);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(33001)));
        Assert.True(board.DuckHuntDrawerOpen);
        Assert.True(board.ActivateButton("duck-hunt-play", At(33400)));
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(0, board.GalleryState.Score);
    }

    private static DuckHuntGalleryGame Waiting()
    {
        var game = new DuckHuntGalleryGame(); game.Reset(At(0)); game.SetReady(true, At(0));
        Assert.True(game.Start(At(100))); game.Advance(At(3900)); return game;
    }
    private static DuckHuntGalleryGame Middle()
    {
        var game = Waiting();
        Assert.False(game.Shoot(DuckHuntGalleryLayout.ScenePoint(.5, .78, 1), At(4000)));
        Assert.False(game.Shoot(DuckHuntGalleryLayout.ScenePoint(.5, .18, 1), At(4010)));
        game.Advance(At(6900)); return game;
    }
    private static BoardAim Skull(int index)
    {
        var bounds = DuckHuntGalleryLayout.SkullBounds(index);
        return new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }
    private static BoardAim Zone(int points) => points switch
    {
        100 => Source(585, 535),
        50 => Source(835, 535),
        _ => Source(585.5 + 400 / Math.Sqrt(2), 536.5 + 400 / Math.Sqrt(2))
    };
    private static BoardAim Source(double x, double y) => new(x / 1177, .5 + (y - 1337.0 / 2) * DuckHuntGalleryLayout.TargetAspect / 1177);
    private static BoardAim RingPoint(bool inner, double angle, double outside)
    {
        double x = inner ? DuckHuntGalleryLayout.InnerRingSourceX : DuckHuntGalleryLayout.OuterRingSourceX;
        double y = inner ? DuckHuntGalleryLayout.InnerRingSourceY : DuckHuntGalleryLayout.OuterRingSourceY;
        double radius = (inner ? DuckHuntGalleryLayout.InnerRingSourceRadius : DuckHuntGalleryLayout.OuterRingSourceRadius) + outside;
        return Source(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle));
    }
}
