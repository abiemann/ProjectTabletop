using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class DuckHuntProjectionTimingTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    public void DelayedClayImageHitsItsVisiblePoseWithoutEnlargingTheTarget(double aspect)
    {
        var game = Clay();
        game.Advance(At(1350));
        var visible = game.Targets[0];
        var aim = new BoardAim(visible.U, visible.V);
        game.Advance(At(1435));
        Assert.False(DuckHuntClayLayout.ContainsClay(aim, game.Targets[0], aspect));
        var uncorrected = Clay();
        Assert.False(uncorrected.Shoot(aim, At(1435), aspect));
        Assert.True(game.Shoot(aim, At(1435), aspect, At(1350)));
        var hit = game.Targets[0];
        Assert.True(hit.Hit);
        Assert.Equal(visible.U, hit.U);
        Assert.Equal(visible.V, hit.V);
        Assert.Equal(visible.Scale, hit.Scale);
        Assert.Equal(visible.BankAngle, hit.BankAngle);
        Assert.Equal(visible.FaceRatio, hit.FaceRatio);
        Assert.Equal(visible.FlightSeconds, hit.FlightSeconds);
        Assert.Equal(0, hit.HitElapsed);
        Assert.Equal(1, game.Snapshot.Score);
        Assert.Equal(1, game.Snapshot.Shots);
        Assert.False(game.Shoot(aim, At(1435), aspect, At(1351)));
        game.Advance(At(1535));
        Assert.Equal(.1, game.Targets[0].HitElapsed, 10);
        game.Advance(At(1435 + DuckHuntClayGame.HitDuration.TotalMilliseconds - 1));
        Assert.True(game.Targets[0].Visible);
        game.Advance(At(1435 + DuckHuntClayGame.HitDuration.TotalMilliseconds));
        Assert.False(game.Targets[0].Visible);
    }

    [Fact]
    public void CorrectedClayPoseStillRejectsOutsideItsOriginalEllipse()
    {
        var game = Clay();
        game.Advance(At(1350));
        var visible = game.Targets[0];
        var bounds = DuckHuntClayLayout.DiscBounds(visible, 1);
        var outside = new BoardAim(visible.U + Math.Cos(visible.BankAngle) * bounds.Width * .51,
            visible.V + Math.Sin(visible.BankAngle) * bounds.Width * .51);
        Assert.False(DuckHuntClayLayout.ContainsClay(outside, visible));
        Assert.False(game.Shoot(outside, At(1435), 1, At(1350)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.Equal(1, game.Snapshot.Shots);
    }

    [Fact]
    public void ClayPoseCannotCrossLaunchResumeResetOrRawFreshnessBarriers()
    {
        var game = Clay();
        Assert.False(game.Shoot(new(.5, .95), At(634), 1, At(549)));
        Assert.Equal(0, game.Snapshot.Shots);
        game.Advance(At(1350));
        var aim = new BoardAim(game.Targets[0].U, game.Targets[0].V);
        Assert.False(game.Shoot(aim, At(1435), 1, At(1436)));
        game.Advance(At(1586));
        Assert.False(game.Shoot(aim, At(1435), 1, At(1350)));
        Assert.Equal(0, game.Snapshot.Shots);
        game.SetReady(false, At(1600));
        game.SetReady(true, At(2000));
        Assert.False(game.Shoot(aim, At(2000), 1, At(1915)));
        Assert.Equal(0, game.Snapshot.Shots);
        game.Reset(At(2100));
        game.SetReady(true, At(2100));
        Assert.True(game.Start(At(2100)));
        Assert.False(game.Shoot(aim, At(2180), 1, At(2095)));
        Assert.Equal(0, game.Snapshot.Shots);
    }

    [Fact]
    public void ClayHitPoseAndEffectClockBothFreezeAcrossReadinessPauses()
    {
        var game = Clay();
        game.Advance(At(1350));
        var visible = game.Targets[0];
        Assert.True(game.Shoot(new(visible.U, visible.V), At(1435), 1, At(1350)));
        game.SetReady(false, At(1535));
        var frozen = game.Targets[0];
        game.Advance(At(10000));
        Assert.Equal(frozen, game.Targets[0]);
        game.SetReady(true, At(10000));
        Assert.Equal(frozen, game.Targets[0]);
        game.Advance(At(10100));
        Assert.Equal(visible.U, game.Targets[0].U);
        Assert.Equal(.2, game.Targets[0].HitElapsed, 10);
    }

    [Fact]
    public void DelayedBirdHitFreezesVisibleWingPoseAndStartsBlinkAtAcceptedTime()
    {
        var game = Flight();
        game.Advance(At(2650));
        var visible = game.Snapshot;
        game.Advance(At(2735));
        var aim = OldOnlyBirdPoint(visible, game.Snapshot);
        var uncorrected = Flight();
        Assert.False(uncorrected.Shoot(aim, At(2735)));
        Assert.True(game.Shoot(aim, At(2735), 1, At(2650)));
        Assert.Equal(visible.U, game.Snapshot.U);
        Assert.Equal(visible.V, game.Snapshot.V);
        Assert.Equal(visible.FlapPhase, game.Snapshot.FlapPhase);
        Assert.Equal(1, game.Snapshot.Score);
        Assert.True(game.Snapshot.Visible);
        game.Advance(At(2755));
        Assert.True(game.Snapshot.Visible); // 20 ms after acceptance, not 105 ms after scene pose.
        Assert.False(game.Shoot(aim, At(2735), 1, At(2651)));
        Assert.False(game.Shoot(aim, At(2756), 1, At(2671)));
        Assert.Equal(1, game.Snapshot.Score);
        game.Advance(At(2735 + DuckHuntFlightGame.HitDuration.TotalMilliseconds - 1));
        Assert.Equal(DuckHuntFlightPhase.Hit, game.Snapshot.Phase);
        game.Advance(At(2735 + DuckHuntFlightGame.HitDuration.TotalMilliseconds));
        Assert.Equal(DuckHuntFlightPhase.BetweenBirds, game.Snapshot.Phase);
    }

    [Fact]
    public void FlightPoseCannotReachBeforeLaunchIntoNewBirdOrAcrossReadinessBarrier()
    {
        var game = Flight();
        game.Advance(At(700));
        var aim = BirdCentre(game.Snapshot);
        Assert.False(game.Shoot(aim, At(700), 1, At(615)));
        Assert.Equal(0, game.Snapshot.Shots);
        var secondLaunch = At(650) + DuckHuntFlightGame.FlightDuration + DuckHuntFlightGame.BetweenBirdsDuration;
        game.Advance(secondLaunch);
        Assert.Equal(2, game.Snapshot.BirdsLaunched);
        Assert.False(game.Shoot(BirdCentre(game.Snapshot), secondLaunch, 1, secondLaunch.AddMilliseconds(-85)));
        Assert.Equal(0, game.Snapshot.Score);
        game.SetReady(false, secondLaunch.AddMilliseconds(50));
        game.SetReady(true, At(10000));
        Assert.False(game.Shoot(BirdCentre(game.Snapshot), At(10000), 1, At(9915)));
        Assert.Equal(0, game.Snapshot.Score);
    }

    [Fact]
    public void FlightHitPoseAndBlinkRemainStableAcrossPause()
    {
        var game = Flight();
        game.Advance(At(2650));
        var visible = game.Snapshot;
        Assert.True(game.Shoot(BirdCentre(visible), At(2735), 1, At(2650)));
        game.SetReady(false, At(2755));
        var paused = game.Snapshot;
        game.Advance(At(10000));
        Assert.Equal(paused, game.Snapshot);
        game.SetReady(true, At(10000));
        Assert.Equal(paused.FlapPhase, game.Snapshot.FlapPhase);
        Assert.Equal(paused.U, game.Snapshot.U);
        Assert.Equal(paused.Visible, game.Snapshot.Visible);
        game.Advance(At(10070));
        Assert.False(game.Snapshot.Visible);
    }

    [Fact]
    public void GalleryRejectsTargetsThatHaveNotYetOpenedInTheProjectedPose()
    {
        var game = Gallery();
        game.Advance(At(1305));
        var skull = DuckHuntGalleryLayout.SkullBounds(0);
        Assert.False(game.Shoot(new(skull.X + skull.Width / 2, skull.Y + skull.Height / 2),
            At(1305), 1, At(1220)));
        Assert.Equal(0, game.Snapshot.Shots);
        game.Advance(At(3905));
        var paper = PaperCentre(game);
        Assert.False(game.Shoot(paper, At(3905), 1, At(3820)));
        Assert.Equal(0, game.Snapshot.HitTargets);
        Assert.True(game.Shoot(paper, At(4000), 1, At(3915)));
        Assert.Equal(At(4000), Assert.Single(game.Punctures).ShotTime);
        Assert.Equal(0, game.Snapshot.ScoreAnimationSeconds);
    }

    [Fact]
    public void MiddleTargetUsesProjectedExposureDeadlineButKeepsAcceptedEffectTime()
    {
        var game = Middle();
        game.Advance(At(8800));
        var paper = MiddlePaperCentre(game);
        var uncorrected = Middle();
        Assert.False(uncorrected.Shoot(paper, At(11840)));
        Assert.True(game.Shoot(paper, At(11840), 1, At(11755)));
        Assert.Equal(At(11840), Assert.Single(game.Punctures).ShotTime);
        Assert.Equal(100, game.Snapshot.Score);
        Assert.Equal(1, game.Snapshot.MiddleHitMask);
        Assert.Equal(0, game.Snapshot.ScoreAnimationSeconds);
        Assert.False(game.Shoot(paper, At(11840), 1, At(11756)));
        Assert.False(game.Shoot(paper, At(11885), 1, At(11800)));
        Assert.Single(game.Punctures);
    }

    [Fact]
    public void MiddleHalfSecondLingerStartsAtAcceptedShotAndDoesNotAwardTwice()
    {
        var game = Middle();
        game.Advance(At(8900));
        var paper = MiddlePaperCentre(game);
        Assert.True(game.Shoot(paper, At(8985), 1, At(8900)));
        Assert.True(game.Shoot(paper, At(9085), 1, At(9000)));
        Assert.Equal(100, game.Snapshot.Score);
        game.Advance(At(9484));
        Assert.Equal(DuckHuntGalleryMiddlePhase.WaitingForHit, game.Snapshot.MiddlePhase);
        game.Advance(At(9485));
        Assert.Equal(DuckHuntGalleryMiddlePhase.TurningAway, game.Snapshot.MiddlePhase);
    }

    [Fact]
    public void GalleryCorrectedPoseCannotCrossInputEpochOrReviveStaleCameraData()
    {
        var game = Middle();
        game.Advance(At(8900));
        var paper = MiddlePaperCentre(game);
        Assert.False(game.Shoot(paper, At(8985), 1, At(8986)));
        game.Advance(At(9136));
        Assert.False(game.Shoot(paper, At(8985), 1, At(8900)));
        game.SetReady(false, At(9200));
        game.SetReady(true, At(10000));
        Assert.False(game.Shoot(paper, At(10000), 1, At(9915)));
        Assert.Equal(0, game.Snapshot.Score);
        Assert.Empty(game.Punctures);
        game.Reset(At(11000));
        game.SetReady(true, At(11000));
        Assert.False(game.Shoot(paper, At(11085), 1, At(10999)));
        Assert.Empty(game.Punctures);
    }

    [Fact]
    public void BoardSessionForwardsTheCorrectedClayPoseAndKeepsDefaultsCompatible()
    {
        var board = new BoardSession();
        board.ShowDuckHuntClayPigeon(At(0));
        board.SetDuckHuntReady(true, At(0));
        board.TickDuckHunt(At(300));
        Assert.True(board.ActivateButton("duck-hunt-play", At(400)));
        board.TickDuckHunt(At(1350));
        var target = board.ClayTargets[0];
        board.TickDuckHunt(At(1435));
        Assert.True(board.TryDuckHuntShot(new(target.U, target.V), At(1435), 1, At(1350)));
        Assert.Equal(1, board.ClayState.Score);
        var plain = Clay(); var explicitTime = Clay();
        plain.Advance(At(2000));
        var aim = new BoardAim(plain.Targets[0].U, plain.Targets[0].V);
        Assert.Equal(plain.Shoot(aim, At(2000)), explicitTime.Shoot(aim, At(2000), 1, At(2000)));
        Assert.Equal(plain.Targets.ToArray(), explicitTime.Targets.ToArray());
    }

    private static DuckHuntClayGame Clay()
    {
        var game = new DuckHuntClayGame(); game.Reset(At(0)); game.SetReady(true, At(0));
        Assert.True(game.Start(At(0))); return game;
    }

    private static DuckHuntFlightGame Flight()
    {
        var game = new DuckHuntFlightGame(new Random(1));
        game.Reset(At(0)); game.SetReady(true, At(0));
        Assert.True(game.Start(At(100))); return game;
    }

    private static BoardAim BirdCentre(DuckHuntFlightSnapshot bird)
    {
        var bounds = DuckHuntFlightLayout.SpriteBounds(bird, 1);
        return new(bounds.X + bounds.Width * .5, bounds.Y + bounds.Height * .65);
    }

    private static BoardAim OldOnlyBirdPoint(DuckHuntFlightSnapshot oldPose, DuckHuntFlightSnapshot newPose)
    {
        var bounds = DuckHuntFlightLayout.SpriteBounds(oldPose, 1);
        for (int y = 1; y < 100; y++)
            for (int x = 1; x < 100; x++)
            {
                var point = new BoardAim(bounds.X + bounds.Width * x / 100, bounds.Y + bounds.Height * y / 100);
                if (DuckHuntFlightLayout.ContainsBird(point, oldPose) &&
                    !DuckHuntFlightLayout.ContainsBird(point, newPose)) return point;
            }
        throw new InvalidOperationException("The distinct flight poses must expose an old-only silhouette point.");
    }

    private static DuckHuntGalleryGame Gallery()
    {
        var game = new DuckHuntGalleryGame(); game.Reset(At(0)); game.SetReady(true, At(0));
        Assert.True(game.Start(At(100))); return game;
    }

    private static DuckHuntGalleryGame Middle()
    {
        var game = Gallery(); game.Advance(At(3900));
        Assert.False(game.Shoot(DuckHuntGalleryLayout.ScenePoint(.5, .78, 1), At(4000)));
        Assert.False(game.Shoot(DuckHuntGalleryLayout.ScenePoint(.5, .18, 1), At(4010)));
        game.Advance(At(6900)); return game;
    }

    private static BoardAim PaperCentre(DuckHuntGalleryGame game) =>
        DuckHuntGalleryLayout.TargetQuad(0, game.Snapshot, 1).Map(.5, .4);
    private static BoardAim MiddlePaperCentre(DuckHuntGalleryGame game) =>
        DuckHuntGalleryLayout.MiddleTargetQuad(game.Snapshot, 1).Map(.5, .4);
}
