using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class DuckHuntGalleryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double milliseconds) => Origin.AddMilliseconds(milliseconds);
    private static readonly BoardAim Play = new(.5, .46);

    [Fact]
    public void ReadinessNeverStartsGalleryAndPlayRequiresOneFreshValidAim()
    {
        var game = new DuckHuntGalleryGame();
        game.Reset(At(0));
        Assert.True(game.Snapshot.Paused);
        Assert.False(game.Shoot(Play, At(100)));
        game.SetReady(true, At(200));
        game.Advance(At(30000));
        Assert.Equal(DuckHuntGalleryPhase.Ready, game.Snapshot.Phase);
        Assert.False(game.Snapshot.Paused);
        Assert.False(game.Shoot(null, At(30000)));
        Assert.False(game.Shoot(new(double.NaN, .5), At(30000)));
        Assert.False(game.Shoot(Play, At(200)));
        Assert.False(game.Shoot(Play, At(30000), double.NaN));
        Assert.False(game.Shoot(new(.01, .01), At(30000)));
        Assert.False(game.Shoot(Play, At(30000)));
        Assert.True(game.Shoot(Play, At(30001)));
        Assert.Equal(DuckHuntGalleryPhase.Opening, game.Snapshot.Phase);
        Assert.False(game.Snapshot.PlayVisible);
        Assert.Equal(1, game.Snapshot.Cycle);
        Assert.Empty(game.Punctures);
    }

    [Fact]
    public void FlapOpensToNinetyFiveDegreesThenWaitsOneSecondBeforeAdvancing()
    {
        var game = Started();
        game.Advance(At(600));
        Assert.Equal(47.5, game.Snapshot.FlapAngleDegrees, 10);
        Assert.Equal(0, game.Snapshot.ConveyorPosition);
        game.Advance(At(1100));
        Assert.Equal(DuckHuntGalleryPhase.RevealWait, game.Snapshot.Phase);
        Assert.Equal(95, game.Snapshot.FlapAngleDegrees);
        game.Advance(At(2099));
        Assert.Equal(0, game.Snapshot.ConveyorPosition);
        Assert.Equal(DuckHuntGalleryPhase.RevealWait, game.Snapshot.Phase);
        game.Advance(At(2100));
        Assert.Equal(DuckHuntGalleryPhase.Advancing, game.Snapshot.Phase);
        game.Advance(At(3000));
        Assert.Equal(.5, game.Snapshot.ConveyorPosition, 10);
        game.Advance(At(3900));
        Assert.Equal(DuckHuntGalleryPhase.WaitingForHits, game.Snapshot.Phase);
        Assert.Equal(1, game.Snapshot.ConveyorPosition);
        var stopped = game.Snapshot;
        game.Advance(At(90000));
        Assert.Equal(stopped with { Revision = game.Snapshot.Revision, AnimationSeconds = game.Snapshot.AnimationSeconds }, game.Snapshot);
    }

    [Fact]
    public void PhotosBeforeTheRowStopsCannotBecomeHitsWhenConfirmationArrivesLater()
    {
        var game = Started();
        game.Advance(At(3910));
        var aim = PaperPoint(Target(game, 0));
        Assert.False(game.Shoot(aim, At(3899)));
        Assert.Empty(game.Punctures);
        Assert.True(game.Shoot(aim, At(3900)));
        Assert.Equal(1, game.Snapshot.HitTargets);
        Assert.Equal(At(3900), Assert.Single(game.Punctures).ShotTime);
    }

    [Fact]
    public void ExactPuncturesStayOnTheirPaperAndRepeatedHitsCountTheTargetOnce()
    {
        var game = Waiting();
        var paper = Target(game, 2);
        var firstLocal = PaperLocal(-.48, .62);
        var secondLocal = PaperLocal(.63, -.42);
        Assert.True(game.Shoot(paper.Map(firstLocal.U, firstLocal.V), At(4000)));
        Assert.False(game.Shoot(paper.Map(firstLocal.U, firstLocal.V), At(4000)));
        Assert.True(game.Shoot(paper.Map(secondLocal.U, secondLocal.V), At(4010)));
        Assert.Equal(1, game.Snapshot.HitTargets);
        Assert.Equal(2, game.Snapshot.Shots);
        Assert.Equal(2, game.Punctures.Count);
        Assert.Equal(2, game.Punctures[0].TargetIndex);
        Assert.Equal(firstLocal.U, game.Punctures[0].U, 10);
        Assert.Equal(firstLocal.V, game.Punctures[0].V, 10);
        Assert.Equal(secondLocal.U, game.Punctures[1].U, 10);
        Assert.Equal(secondLocal.V, game.Punctures[1].V, 10);
        var first = game.Punctures[0];
        for (int i = 0; i < 5; i++)
            if (i != 2) Assert.True(game.Shoot(PaperPoint(Target(game, i)), At(4100 + i * 10)));
        game.Advance(At(5700));
        Assert.Equal(DuckHuntGalleryPhase.Departing, game.Snapshot.Phase);
        Assert.Equal(first, game.Punctures[0]);
        var carried = Target(game, first.TargetIndex).Map(first.U, first.V);
        Assert.True(carried.V > paper.Map(first.U, first.V).V);
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void CircularPaperCenterAndEdgesAcceptHitsWithoutChangingPunctureCoordinates(double aspect)
    {
        var game = Waiting();
        var target = Target(game, 2, aspect);
        var paperCenter = PaperLocal();
        Assert.True(game.Shoot(target.Map(paperCenter.U, paperCenter.V), At(4000), aspect));
        for (int i = 0; i < 8; i++)
        {
            double angle = Math.PI / 8 + i * Math.PI / 4;
            var local = PaperLocal(Math.Cos(angle), Math.Sin(angle));
            Assert.True(DuckHuntGalleryLayout.ContainsPaper(local));
            Assert.True(game.Shoot(target.Map(local.U, local.V), At(4010 + i * 10), aspect));
            var puncture = game.Punctures[^1];
            Assert.Equal(local.U, puncture.U, 10);
            Assert.Equal(local.V, puncture.V, 10);
        }
        Assert.Equal(1, game.Snapshot.HitTargets);
        Assert.Equal(9, game.Punctures.Count);
        Assert.Equal(DuckHuntGalleryLayout.PaperRadiusU * target.Bounds.Width * aspect,
            DuckHuntGalleryLayout.PaperRadiusV * target.Bounds.Height, 10);
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void HolderAndCornersNeverCountAsPaperHits(double aspect)
    {
        var game = Waiting();
        var target = Target(game, 2, aspect);
        var outside = new List<BoardAim>
        {
            new(.01, .01), new(.99, .01), new(.01, .99), new(.99, .99),
            PaperLocal(.9, .9), PaperLocal(-.9, .9),
            PaperLocal(.9, -.9), PaperLocal(-.9, -.9)
        };
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            outside.Add(PaperLocal(1.02 * Math.Cos(angle), 1.02 * Math.Sin(angle)));
        }
        for (int i = 0; i < outside.Count; i++)
        {
            var local = outside[i];
            Assert.False(DuckHuntGalleryLayout.ContainsPaper(local));
            Assert.True(target.TryMapToLocal(target.Map(local.U, local.V), out _));
            Assert.False(game.Shoot(target.Map(local.U, local.V), At(4000 + i * 10), aspect));
        }
        Assert.Equal(outside.Count, game.Snapshot.Shots);
        Assert.Equal(0, game.Snapshot.HitTargets);
        Assert.Empty(game.Punctures);
        Assert.False(DuckHuntGalleryLayout.ContainsPaper(new(double.NaN, .5)));
        Assert.False(DuckHuntGalleryLayout.ContainsPaper(new(.5, double.PositiveInfinity)));
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void MetalClipsDoNotCountEvenWhereTheyOverlapThePaperCircle(double aspect)
    {
        var game = Waiting();
        var target = Target(game, 2, aspect);
        for (int i = 0; i < 4; i++)
        {
            double angle = i * Math.PI / 2;
            var local = PaperLocal(.97 * Math.Cos(angle), .97 * Math.Sin(angle));
            Assert.Contains(DuckHuntGalleryLayout.PaperClipBounds, clip => clip.Contains(local.U, local.V));
            Assert.False(DuckHuntGalleryLayout.ContainsPaper(local));
            Assert.False(game.Shoot(target.Map(local.U, local.V), At(4000 + i * 10), aspect));
        }
        Assert.Empty(game.Punctures);
        Assert.Equal(0, game.Snapshot.HitTargets);
        Assert.Equal(4, game.Snapshot.Shots);
    }

    [Fact]
    public void MissingAimStalePhotosAndGapsBetweenPapersNeverSatisfyATarget()
    {
        var game = Waiting();
        var paper = Target(game, 0);
        var next = Target(game, 1);
        var gap = new BoardAim((paper.TopRight.U + next.TopLeft.U) / 2, PaperPoint(paper).V);
        Assert.False(game.Shoot(null, At(4000)));
        Assert.False(game.Shoot(new(2, .5), At(4000)));
        Assert.False(game.Shoot(gap, At(4000)));
        Assert.False(game.Shoot(PaperPoint(paper), At(4000)));
        game.Advance(At(4200));
        Assert.False(game.Shoot(PaperPoint(paper), At(4001)));
        Assert.Empty(game.Punctures);
        Assert.Equal(0, game.Snapshot.HitTargets);
        Assert.Equal(1, game.Snapshot.Shots);
    }

    [Fact]
    public void FourDifferentTargetsAndManyRepeatHitsStillWaitForTheFifth()
    {
        var game = Waiting();
        for (int i = 0; i < 4; i++) Assert.True(game.Shoot(PaperPoint(Target(game, i)), At(4000 + i * 10)));
        for (int i = 0; i < 12; i++) Assert.True(game.Shoot(PaperPoint(Target(game, 0), -.2, .3), At(4100 + i * 10)));
        game.Advance(At(90000));
        Assert.Equal(DuckHuntGalleryPhase.WaitingForHits, game.Snapshot.Phase);
        Assert.Equal(4, game.Snapshot.HitTargets);
        Assert.True(game.Shoot(PaperPoint(Target(game, 4)), At(90010)));
        Assert.Equal(DuckHuntGalleryPhase.SuccessWait, game.Snapshot.Phase);
        Assert.Equal(5, game.Snapshot.HitTargets);
    }

    [Fact]
    public void CompletedRowWaitsOneSecondThenDepartsFallsClosesAndWaitsForAnotherPlay()
    {
        var game = Completed();
        game.Advance(At(5039));
        Assert.Equal(DuckHuntGalleryPhase.SuccessWait, game.Snapshot.Phase);
        Assert.Equal(1, game.Snapshot.ConveyorPosition);
        Assert.False(game.Shoot(PaperPoint(Target(game, 0)), At(5039)));
        game.Advance(At(5040));
        Assert.Equal(DuckHuntGalleryPhase.Departing, game.Snapshot.Phase);
        game.Advance(At(5640));
        Assert.Equal(1.5, game.Snapshot.ConveyorPosition, 10);
        game.Advance(At(6240));
        Assert.Equal(DuckHuntGalleryPhase.Falling, game.Snapshot.Phase);
        game.Advance(At(6590));
        Assert.Equal(.5, game.Snapshot.FallProgress, 10);
        game.Advance(At(6940));
        Assert.Equal(DuckHuntGalleryPhase.Closing, game.Snapshot.Phase);
        Assert.False(game.Snapshot.TargetsVisible);
        game.Advance(At(7440));
        Assert.Equal(47.5, game.Snapshot.FlapAngleDegrees, 10);
        game.Advance(At(7940));
        Assert.Equal(DuckHuntGalleryPhase.Ready, game.Snapshot.Phase);
        Assert.True(game.Snapshot.PlayVisible);
        Assert.Equal(0, game.Snapshot.FlapAngleDegrees);
        Assert.Equal(5, game.Punctures.Count);
        Assert.False(game.Shoot(Play, At(7939)));
        Assert.False(game.Shoot(new(.01, .01), At(7941)));
        Assert.Equal(5, game.Punctures.Count);
        Assert.True(game.Shoot(Play, At(7950)));
        Assert.Empty(game.Punctures);
        Assert.Equal(2, game.Snapshot.Cycle);
        Assert.Equal(0, game.Snapshot.HitTargets);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(1600)]
    [InlineData(3000)]
    [InlineData(4000)]
    [InlineData(4500)]
    [InlineData(5600)]
    [InlineData(6600)]
    [InlineData(7400)]
    public void EveryAnimationPhasePausesWithoutMovingPapersOrReusingPreResumeShots(int ms)
    {
        var game = ms > 4000 ? Completed() : Started();
        game.Advance(At(ms));
        game.SetReady(false, At(ms));
        var paused = game.Snapshot;
        var holes = game.Punctures.ToArray();
        game.Advance(At(ms + 5000));
        Assert.Equal(paused, game.Snapshot);
        Assert.False(game.Shoot(Play, At(ms + 5000)));
        game.SetReady(true, At(ms + 5000));
        var resumed = game.Snapshot;
        Assert.Equal(paused with { Paused = false, Revision = resumed.Revision }, resumed);
        Assert.Equal(holes, game.Punctures);
        Assert.False(game.Shoot(Play, At(ms + 4999)));
        game.Advance(At(ms + 5100));
        Assert.Equal(holes, game.Punctures);
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void AllFivePapersRemainVisibleEvenlySpacedAndPhysicallyProportioned(double aspect)
    {
        var game = Waiting();
        double previousGap = -1;
        for (int i = 0; i < 5; i++)
        {
            var paper = Target(game, i, aspect);
            Assert.InRange(paper.TopLeft.U, 0, 1);
            Assert.InRange(paper.TopLeft.V, 0, 1);
            Assert.InRange(paper.BottomRight.U, 0, 1);
            Assert.InRange(paper.BottomRight.V, 0, 1);
            Assert.Equal(DuckHuntGalleryLayout.TargetAspect, paper.Bounds.Width * aspect / paper.Bounds.Height, 10);
            if (i > 0)
            {
                var previous = Target(game, i - 1, aspect);
                double gap = paper.TopLeft.U - previous.TopRight.U;
                Assert.True(gap > 0);
                if (previousGap > 0) Assert.Equal(previousGap, gap, 10);
                previousGap = gap;
            }
            var point = paper.Map(.23, .71);
            Assert.True(paper.TryMapToLocal(point, out var local));
            Assert.Equal(.23, local.U, 10);
            Assert.Equal(.71, local.V, 10);
        }
    }

    [Theory]
    [InlineData(.55, .3)]
    [InlineData(1, .6)]
    [InlineData(1.8, 1)]
    public void FallingPapersCarryTheirProjectiveSurfaceAndPunctures(double aspect, double fall)
    {
        var state = Waiting().Snapshot with
        {
            Phase = DuckHuntGalleryPhase.Falling, ConveyorPosition = 2, FallProgress = fall
        };
        for (int i = 0; i < 5; i++)
        {
            var paper = DuckHuntGalleryLayout.TargetQuad(i, state, aspect);
            foreach (var local in new BoardAim[] { PaperLocal(-.48, .62), PaperLocal(), PaperLocal(.63, -.42) })
            {
                var point = paper.Map(local.U, local.V);
                Assert.True(paper.TryMapToLocal(point, out var restored));
                Assert.Equal(local.U, restored.U, 10);
                Assert.Equal(local.V, restored.V, 10);
            }
        }
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void OpaquePedestalRestsOnTheTopDeckAndStaysPinnedToTheSupportedRoller(double aspect)
    {
        var waiting = Waiting().Snapshot;
        for (int index = 0; index < 5; index++)
        {
            var stopped = DuckHuntGalleryLayout.TargetPose(index, waiting, aspect);
            Assert.True(stopped.Supported);
            Assert.False(stopped.InFrontOfLip);
            Assert.Equal(0, stopped.TiltDegrees);
            Assert.Equal(DuckHuntGalleryLayout.ScenePoint(0, .732, aspect).V, stopped.BaseContact.V, 10);
            Assert.True(stopped.BaseContact.V < DuckHuntGalleryLayout.ScenePoint(0, .738, aspect).V);
            Assert.True(stopped.Quad.BottomLeft.V > stopped.BaseContact.V);
            double previousY = stopped.BaseContact.V, previousAngle = 0;
            foreach (double position in new[] { 1.6, 1.7, 1.8, 1.9, 2.0 })
            {
                var pose = DuckHuntGalleryLayout.TargetPose(index, waiting with
                {
                    Phase = DuckHuntGalleryPhase.Departing, ConveyorPosition = position
                }, aspect);
                Assert.True(pose.Supported);
                Assert.True(pose.BaseContact.V >= previousY);
                Assert.True(pose.TiltDegrees >= previousAngle);
                var foot = pose.Quad.Map(DuckHuntGalleryLayout.TargetBaseU, DuckHuntGalleryLayout.TargetBaseV);
                Assert.Equal(pose.BaseContact.U, foot.U, 10);
                Assert.Equal(pose.BaseContact.V, foot.V, 10);
                Assert.Equal(position > 1.7, pose.InFrontOfLip);
                if (position > 1.7)
                {
                    Assert.True(pose.TiltDegrees > 0);
                    Assert.True(pose.Quad.TopRight.U - pose.Quad.TopLeft.U >
                        pose.Quad.BottomRight.U - pose.Quad.BottomLeft.U);
                }
                previousY = pose.BaseContact.V;
                previousAngle = pose.TiltDegrees;
            }
            Assert.Equal(60, previousAngle, 10);
            Assert.InRange(previousY, DuckHuntGalleryLayout.ScenePoint(0, .791, aspect).V,
                DuckHuntGalleryLayout.ScenePoint(0, .795, aspect).V);
        }
    }

    [Theory]
    [InlineData(.55)]
    [InlineData(1)]
    [InlineData(1.8)]
    [InlineData(2.4)]
    public void RollerReleaseKeepsTheSamePoseAndVelocityBeforeTheAirborneFall(double aspect)
    {
        var waiting = Waiting().Snapshot;
        const double seconds = .00001;
        for (int index = 0; index < 5; index++)
        {
            var departure = waiting with { Phase = DuckHuntGalleryPhase.Departing, ConveyorPosition = 2 };
            var falling = departure with { Phase = DuckHuntGalleryPhase.Falling };
            var onRoller = DuckHuntGalleryLayout.TargetPose(index, departure, aspect);
            var released = DuckHuntGalleryLayout.TargetPose(index, falling, aspect);
            Assert.True(onRoller.Supported);
            Assert.False(released.Supported);
            Assert.True(released.InFrontOfLip);
            Assert.Equal(onRoller.Quad, released.Quad);
            Assert.Equal(onRoller.BaseContact, released.BaseContact);
            Assert.Equal(onRoller.TiltDegrees, released.TiltDegrees);
            var before = DuckHuntGalleryLayout.TargetPose(index, departure with
            {
                ConveyorPosition = 2 - seconds / DuckHuntGalleryGame.DepartDuration.TotalSeconds
            }, aspect);
            var after = DuckHuntGalleryLayout.TargetPose(index, falling with
            {
                FallProgress = seconds / DuckHuntGalleryGame.FallDuration.TotalSeconds
            }, aspect);
            Assert.InRange(Math.Abs((released.BaseContact.V - before.BaseContact.V) / seconds -
                (after.BaseContact.V - released.BaseContact.V) / seconds), 0, .001);
            Assert.InRange(Math.Abs((released.TiltDegrees - before.TiltDegrees) / seconds -
                (after.TiltDegrees - released.TiltDegrees) / seconds), 0, .01);
            var first = DuckHuntGalleryLayout.TargetPose(index, falling with { FallProgress = .25 }, aspect);
            var second = DuckHuntGalleryLayout.TargetPose(index, falling with { FallProgress = .50 }, aspect);
            Assert.False(first.Supported);
            Assert.False(second.Supported);
            Assert.True(first.BaseContact.V > released.BaseContact.V);
            Assert.True(second.BaseContact.V - first.BaseContact.V > first.BaseContact.V - released.BaseContact.V);
            Assert.True(second.TiltDegrees > first.TiltDegrees && first.TiltDegrees > released.TiltDegrees);
        }
    }

    [Fact]
    public void SparseTicksAndRegularTicksProduceTheSameCycleState()
    {
        var sparse = Completed();
        var regular = Completed();
        for (int ms = 4041; ms <= 10000; ms += 1) regular.Advance(At(ms));
        sparse.Advance(At(10000));
        Assert.Equal(regular.Snapshot with { Revision = sparse.Snapshot.Revision }, sparse.Snapshot);
        Assert.Equal(regular.Punctures, sparse.Punctures);
        Assert.Equal(1, sparse.Snapshot.Cycle);
    }

    [Fact]
    public void PlayIsAbsentFromHandButtonsAndStartingClosesOnlyTheGalleryDrawer()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(10)));
        board.TickDuckHunt(At(310));
        Assert.Equal(["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-back"],
            board.Buttons.Select(b => b.Id));
        Assert.DoesNotContain(board.Buttons, b => b.Label is "PLAY" or "PLAY AGAIN");
        Assert.False(board.ActivateButton("duck-hunt-play", At(350)));
        board.SetDuckHuntReady(true, At(400));
        Assert.True(board.DuckHuntDrawerOpen);
        for (int ms = 500; ms <= 1600; ms += 100)
            Assert.Empty(board.ObserveHeldButtons(["duck-hunt-play"], At(ms), At(ms), []));
        Assert.Equal(DuckHuntGalleryPhase.Ready, board.GalleryState.Phase);
        long epoch = board.DuckHuntRoundRevision;
        Assert.True(board.TryDuckHuntShot(Play, At(1700)));
        Assert.Equal(epoch + 1, board.DuckHuntRoundRevision);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal(DuckHuntFlightPhase.Ready, board.HuntFlightState.Phase);
        board.TickDuckHunt(At(6000));
        Assert.Equal(DuckHuntGalleryPhase.WaitingForHits, board.GalleryState.Phase);
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(6100)));
        board.TickDuckHunt(At(6400));
        Assert.True(board.ActivateButton("duck-hunt-drawer-close", At(6500)));
        Assert.Equal(DuckHuntGalleryPhase.WaitingForHits, board.GalleryState.Phase);
        Assert.False(board.ActivateButton("duck-hunt-replay", At(6600)));
    }

    [Fact]
    public void CaptionHoldExitRestoresTheOriginatingSecondMenuPage()
    {
        var board = new BoardSession();
        Assert.True(board.ActivateButton("menu-scroll-down", At(10)));
        board.TickMenu(At(660));
        Assert.True(board.ActivateButton("duck-hunt", At(800)));
        Assert.True(board.ActivateButton("duck-hunt-target-practice", At(805)));
        Assert.True(board.ActivateButton("duck-hunt-drawer-open", At(810)));
        board.TickDuckHunt(At(1110));
        var exit = Assert.Single(board.Buttons, b => b.Id == "duck-hunt-exit");
        Assert.Equal(BoardButtonHold.Once, exit.Hold);
        Assert.False(board.ControlSwipesEnabled);
        Assert.False(board.SetButtonsVisible(false, At(1200)));
        board.ObserveHeldButtons([], At(1300), At(1300), [exit.Id]);
        for (int ms = 1400; ms < 2400; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([exit.Id], At(ms), At(ms), []));
        Assert.Equal([exit.Id], board.ObserveHeldButtons([exit.Id], At(2400), At(2400), []));
        Assert.Equal(BoardScreen.Menu, board.Screen);
        Assert.True(board.MenuScrolled);
        Assert.False(board.TryDuckHuntShot(Play, At(2500)));
    }

    [Fact]
    public void AutomaticCycleCompletionLeavesDrawerClosedAndReplayShotChangesInputEpoch()
    {
        var board = new BoardSession();
        board.ShowDuckHuntTargetPractice(At(0));
        board.SetDuckHuntReady(true, At(0));
        Assert.True(board.TryDuckHuntShot(Play, At(100)));
        board.TickDuckHunt(At(4000));
        for (int i = 0; i < 5; i++)
            Assert.True(board.TryDuckHuntShot(SkullCenter(i), At(4000 + i * 10)));
        for (int i = 0; i < 5; i++)
            Assert.True(board.TryDuckHuntShot(PaperPoint(DuckHuntGalleryLayout.TargetQuad(i,
                board.GalleryState, 1)), At(4100 + i * 10)));
        long epoch = board.DuckHuntRoundRevision;
        board.TickDuckHunt(At(10000));
        Assert.Equal(DuckHuntGalleryPhase.Ready, board.GalleryState.Phase);
        Assert.False(board.DuckHuntDrawerOpen);
        Assert.Equal("duck-hunt-drawer-open", Assert.Single(board.Buttons).Id);
        Assert.True(board.TryDuckHuntShot(Play, At(10010)));
        Assert.Equal(epoch + 1, board.DuckHuntRoundRevision);
        Assert.Empty(board.GalleryPunctures);
        Assert.False(board.TryDuckHuntShot(Play, At(4000)));
    }

    private static DuckHuntGalleryGame Started()
    {
        var game = new DuckHuntGalleryGame();
        game.Reset(At(0));
        game.SetReady(true, At(0));
        Assert.True(game.Shoot(Play, At(100)));
        return game;
    }

    private static DuckHuntGalleryGame Waiting()
    {
        var game = Started();
        game.Advance(At(3900));
        return game;
    }

    private static DuckHuntGalleryGame Completed()
    {
        var game = Started();
        game.Advance(At(1300));
        for (int i = 0; i < 5; i++) Assert.True(game.Shoot(SkullCenter(i), At(1400 + i * 10)));
        game.Advance(At(3900));
        for (int i = 0; i < 5; i++) Assert.True(game.Shoot(PaperPoint(Target(game, i)), At(4000 + i * 10)));
        return game;
    }

    private static BoardAim SkullCenter(int index, double aspect = 1)
    {
        var bounds = DuckHuntGalleryLayout.SkullBounds(index, aspect);
        return new(bounds.X + bounds.Width * .5, bounds.Y + bounds.Height * .5);
    }

    private static DuckHuntGalleryQuad Target(DuckHuntGalleryGame game, int index, double aspect = 1) =>
        DuckHuntGalleryLayout.TargetQuad(index, game.Snapshot, aspect);

    private static BoardAim PaperLocal(double x = 0, double y = 0) => new(
        DuckHuntGalleryLayout.PaperCenterU + x * DuckHuntGalleryLayout.PaperRadiusU,
        DuckHuntGalleryLayout.PaperCenterV + y * DuckHuntGalleryLayout.PaperRadiusV);

    private static BoardAim PaperPoint(DuckHuntGalleryQuad paper, double x = 0, double y = 0)
    {
        var local = PaperLocal(x, y);
        return paper.Map(local.U, local.V);
    }
}
