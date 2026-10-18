using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class BoardControlSwipeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeBoardSweepTogglesInBothDirections(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.12, .32, .53, .9];
        for (int i = 0; i < positions.Length; i++)
        {
            var result = Observe(tracker, i * 100, reverse ? 1 - positions[i] : positions[i]);
            Assert.Equal(i == positions.Length - 1 ? BoardControlSwipe.Toggle : (BoardControlSwipe?)null, result);
        }
    }

    [Fact]
    public void EndpointFramesDoNotRepeatAndReverseSwipeWorksAfterCooldown()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Equal(BoardControlSwipe.Toggle, Sweep(tracker, 0, false));
        for (int time = 400; time <= 1100; time += 100)
            Assert.Null(Observe(tracker, time, .9));
        Assert.Null(Observe(tracker, 1200, .65));
        Assert.Null(Observe(tracker, 1300, .42));
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 1400, .15));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CooldownDiscardsImmediateSwipesAndRearmsForEitherDirection(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Equal(BoardControlSwipe.Toggle, Sweep(tracker, 0, false));
        Assert.Null(Sweep(tracker, 400, reverse));
        Assert.Equal(BoardControlSwipe.Toggle, Sweep(tracker, 1000, reverse));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpatialProgressDoesNotRequirePersistentOrPositiveIdentity(bool untracked)
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1, id: untracked ? 0 : 1));
        Assert.Null(Observe(tracker, 100, .35, id: untracked ? 0 : 83));
        Assert.Null(Observe(tracker, 200, .55, id: untracked ? 0 : -1));
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 300, .9, id: untracked ? 0 : 972));
    }

    [Fact]
    public void WidelySeparatedSimultaneousDisturbancesCannotManufactureASwipe()
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.9, .65, .4, .15];
        for (int i = 0; i < positions.Length; i++)
        {
            var time = Start.AddMilliseconds(i * 100);
            var result = tracker.Update([new(1, .1, .2), new(2, positions[i], .7)], time, time);
            Assert.Null(result);
            Assert.False(tracker.HasTraversalInProgress);
        }
    }

    [Fact]
    public void DuplicateFramesAndComponentsCannotManufactureObservations()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .35));
        Assert.Null(Observe(tracker, 100, .8));
        Assert.Null(Observe(tracker, 50, .8));
        var time = Start.AddMilliseconds(300);
        Assert.Null(tracker.Update([new(1, .6, .5), new(2, .6, .5)], time, time));
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 400, .85));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactComponentsShareOneSpatialObservation(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.1, .35, .6, .9];
        for (int i = 0; i < positions.Length; i++)
        {
            var time = Start.AddMilliseconds(i * 100);
            double u = reverse ? 1 - positions[i] : positions[i];
            var result = tracker.Update([new(0, u - .025, .48), new(57 + i, u - .01, .51),
                new(0, u + .01, .49), new(200 + i, u + .025, .52)], time, time);
            Assert.Equal(i == positions.Length - 1 ? BoardControlSwipe.Toggle : (BoardControlSwipe?)null, result);
        }
    }

    [Fact]
    public void SeparateEndpointFlashesNeverBecomeACrossing()
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.1, .1, .9, .9, .1, .1, .9, .9];
        for (int i = 0; i < positions.Length; i++)
        {
            Assert.Null(Observe(tracker, i * 100, positions[i], id: i));
            Assert.False(tracker.HasTraversalInProgress);
        }
    }

    [Fact]
    public void StationaryFramesDoNotSupplyIntermediateMovement()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        for (int i = 1; i <= 5; i++)
        {
            Assert.Null(Observe(tracker, i * 100, .4, id: i));
            Assert.False(tracker.HasTraversalInProgress);
        }
        Assert.Null(Observe(tracker, 600, .8));
        Assert.Null(Observe(tracker, 700, .8));
        Assert.False(tracker.HasTraversalInProgress);
    }

    [Fact]
    public void SimultaneousEndpointNoiseDiscardsAnEstablishedPath()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .35));
        Assert.Null(Observe(tracker, 200, .55));
        Assert.True(tracker.HasTraversalInProgress);
        var time = Start.AddMilliseconds(250);
        Assert.Null(tracker.Update([new(0, .1, .5), new(0, .9, .5)], time, time));
        Assert.False(tracker.HasTraversalInProgress);
        Assert.Null(Observe(tracker, 300, .8));
    }

    [Theory]
    [InlineData(351)]
    [InlineData(-1)]
    public void StaleOrFutureInputInvalidatesThePartialSwipe(int ageMilliseconds)
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .35));
        var time = Start.AddMilliseconds(200);
        Assert.Null(tracker.Update([new(1, .6, .5)], time, time.AddMilliseconds(ageMilliseconds)));
        Assert.Null(Observe(tracker, 300, .8));
        Assert.Null(Observe(tracker, 400, .9));
    }

    [Fact]
    public void LongGapCannotBridgeTwoSeparateHandAppearances()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .35));
        Assert.Null(Observe(tracker, 701, .6));
        Assert.Null(Observe(tracker, 800, .8));
    }

    [Fact]
    public void BriefPositionLossDoesNotWidenCameraFrameFreshness()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .35));
        var missing = Start.AddMilliseconds(400);
        Assert.Null(tracker.Update([], missing, missing));
        Assert.Null(Observe(tracker, 700, .6));
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 800, .9));

        var delayed = new BoardControlSwipeTracker();
        Assert.Null(Observe(delayed, 0, .1));
        Assert.Null(Observe(delayed, 100, .35));
        var time = Start.AddMilliseconds(200);
        Assert.Null(delayed.Update([new(0, .6, .5)], time, time.AddMilliseconds(351)));
        Assert.Null(Observe(delayed, 600, .9));
    }

    [Fact]
    public void BriefMissAndSmallPositionJitterStillAllowDeliberateSwipe()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .4, .51));
        var time = Start.AddMilliseconds(200);
        Assert.Null(tracker.Update([], time, time));
        Assert.Null(Observe(tracker, 300, .37, .49));
        Assert.Null(Observe(tracker, 400, .6, .52));
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 500, .85, .5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedCentralLossNeedsContinuedMotionBeforeJoiningTheSweep(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        // One continuous hand was captured on both sides of a dark-sky gap.
        int[] times = [0, 50, 99, 124, 149, 174, 200, 224, 249, 300, 349];
        double[] positions = [.9158, .8971, .8637, .8499, .8377, .8131, .8041, .7909, .7823, .7564, .6969];
        double[] heights = [.9482, .9444, .9424, .9387, .9396, .9389, .9383, .9387, .9386, .9235, .9472];
        double Direction(double u) => reverse ? 1 - u : u;
        for (int i = 0; i < times.Length; i++)
            Assert.Null(Observe(tracker, times[i], Direction(positions[i]), heights[i]));
        for (int time = 375; time < 749; time += 25) Missing(tracker, time);
        Assert.Null(Observe(tracker, 749, Direction(.1738), .9215));
        Assert.False(tracker.HasTraversalInProgress);
        Assert.Null(Observe(tracker, 775, Direction(.1623), .9251));
        // Reaching the edge alone cannot confirm the provisional reacquisition.
        Assert.Null(Observe(tracker, 799, Direction(.1496), .9261));
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 824, Direction(.1374), .9272));
        Assert.Null(Observe(tracker, 849, Direction(.1241), .9284));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedVerticalDriftAcrossGapStillNeedsLocalCorroboration(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        // Recorded continuous sweep: the star-loss centroid changes height
        // while the hand crosses the missing middle, then continues normally.
        int[] times = [0, 26, 50, 150, 175, 200, 226, 250, 275, 300, 325, 350, 375, 400, 425];
        double[] positions = [.005208, .030382, .032434, .100430, .143682, .156113, .183160,
            .220129, .273037, .277699, .291867, .373698, .373816, .391809, .421007];
        double[] heights = [.898750, .916250, .885966, .919375, .931766, .935164, .932292,
            .919596, .920529, .922443, .921106, .905781, .890398, .882216, .866042];
        double Direction(double u) => reverse ? 1 - u : u;
        for (int i = 0; i < times.Length; i++)
            Assert.Null(Observe(tracker, times[i], Direction(positions[i]), heights[i]));
        for (int time = 450; time < 700; time += 25) Missing(tracker, time);
        Assert.Null(Observe(tracker, 700, Direction(.842222), .939675));
        Assert.False(tracker.HasTraversalInProgress);
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 775, Direction(.917316), .948812));
        Assert.Null(Observe(tracker, 800, Direction(.944878), .928750));
    }

    [Theory]
    [InlineData(.90)] // steeper than the allowed cross-gap slope
    [InlineData(.73)] // shallow enough across the gap, but exceeds the total vertical span
    public void ReacquisitionCannotMoveToAnIncompatibleVerticalLocation(double height)
    {
        var tracker = EstablishedGap();
        Assert.Null(Observe(tracker, 750, .17, height));
        Assert.Null(Observe(tracker, 825, .12, height));
        Missing(tracker, 900);
    }

    [Theory]
    [InlineData(.13, .65)] // diagonal movement after a valid shifted reacquisition
    [InlineData(.23, .57)] // reversal after a valid shifted reacquisition
    public void ShiftedReacquisitionStillRequiresCompatibleLocalMotion(double nextU, double nextV)
    {
        var tracker = EstablishedGap();
        Assert.Null(Observe(tracker, 750, .17, .57));
        Assert.False(tracker.HasTraversalInProgress);
        Assert.Null(Observe(tracker, 800, nextU, nextV));
        Assert.Null(Observe(tracker, 850, .12, .57));
        Missing(tracker, 925);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReacquiredEndpointCannotCompleteByHoldingOrDisappearing(bool disappear)
    {
        var tracker = EstablishedGap();
        Assert.Null(Observe(tracker, 750, .12));
        Assert.False(tracker.HasTraversalInProgress);
        for (int time = 775; time <= 1500; time += 25)
        {
            if (disappear) Missing(tracker, time);
            else Assert.Null(Observe(tracker, time, .12));
            Assert.False(tracker.HasTraversalInProgress);
        }
    }

    [Theory]
    [InlineData(0)] // no actual empty frames
    [InlineData(1)] // instantaneous, implausibly fast jump
    [InlineData(2)] // opposite direction
    [InlineData(3)] // different vertical location
    [InlineData(4)] // expired gap
    [InlineData(5)] // insufficient established movement
    [InlineData(6)] // stationary before the gap
    public void UncorroboratedOrIncompatibleGapsDoNotJoinTwoAppearances(int failure)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = failure == 5 ? [.90, .86, .82, .78] : [.95, .88, .81, .74];
        for (int i = 0; i < positions.Length; i++) Assert.Null(Observe(tracker, i * 100, positions[i]));
        if (failure == 6) Assert.Null(Observe(tracker, 325, .74));
        if (failure != 0) Missing(tracker, failure == 1 ? 310 : 450);
        int returnTime = failure == 1 ? 325 : failure == 4 ? 901 : 750;
        double first = failure == 2 ? .98 : .12;
        double height = failure == 3 ? .8 : .5;
        Assert.Null(Observe(tracker, returnTime, first, height));
        Assert.Null(Observe(tracker, returnTime + 75, failure == 2 ? .94 : .08, height));
        Missing(tracker, returnTime + 150);
    }

    [Theory]
    [InlineData(0)] // reversal after reacquisition
    [InlineData(1)] // diagonal post-gap movement
    [InlineData(2)] // implausibly fast post-gap movement
    [InlineData(3)] // ambiguous simultaneous disturbances
    [InlineData(4)] // stale input
    [InlineData(5)] // explicit reset
    [InlineData(6)] // pending confirmation expires despite stationary samples
    public void PendingGapCannotSurviveIncompatibleEvidence(int failure)
    {
        var tracker = EstablishedGap();
        Assert.Null(Observe(tracker, 750, .17));
        switch (failure)
        {
            case 0: Assert.Null(Observe(tracker, 800, .23)); break;
            case 1: Assert.Null(Observe(tracker, 800, .13, .58)); break;
            case 2: Assert.Null(Observe(tracker, 751, .03)); break;
            case 3:
                var ambiguous = Start.AddMilliseconds(775);
                Assert.Null(tracker.Update([new(0, .1, .5), new(1, .9, .5)], ambiguous, ambiguous));
                break;
            case 4:
                var stale = Start.AddMilliseconds(775);
                Assert.Null(tracker.Update([new(0, .16, .5)], stale, stale.AddMilliseconds(351)));
                break;
            case 5: tracker.Reset(); break;
            case 6:
                for (int time = 775; time <= 1400; time += 25) Assert.Null(Observe(tracker, time, .17));
                break;
        }
        int next = failure == 6 ? 1450 : 850;
        Assert.Null(Observe(tracker, next, .12));
        Assert.Null(Observe(tracker, next + 75, .08));
        Missing(tracker, next + 150);
    }

    private static BoardControlSwipeTracker EstablishedGap()
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.95, .88, .81, .74];
        for (int i = 0; i < positions.Length; i++) Assert.Null(Observe(tracker, i * 100, positions[i]));
        Missing(tracker, 450);
        return tracker;
    }

    private static void Missing(BoardControlSwipeTracker tracker, int milliseconds)
    {
        var time = Start.AddMilliseconds(milliseconds);
        Assert.Null(tracker.Update([], time, time));
    }

    [Theory]
    [InlineData(.48, .58, .68, .8)]
    [InlineData(.1, .2, .3, .4)]
    public void ShortMovementsAreNotCommands(double a, double b, double c, double d)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [a, b, c, d];
        for (int i = 0; i < positions.Length; i++)
            Assert.Null(Observe(tracker, i * 100, positions[i]));
    }

    [Fact]
    public void FastJumpDoesNotBecomeASwipeByHoldingTheEndpoint()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 30, .35));
        Assert.Null(Observe(tracker, 60, .6));
        Assert.Null(Observe(tracker, 90, .85));
        for (int time = 200; time <= 1000; time += 100)
            Assert.Null(Observe(tracker, time, .85));
    }

    [Fact]
    public void SlowCrossingIsNotASwipe()
    {
        var tracker = new BoardControlSwipeTracker();
        for (int i = 0; i <= 30; i++)
            Assert.Null(Observe(tracker, i * 200, .75 - .51 * i / 30));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossingAtExactlyOneSecondQualifies(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        for (int i = 0; i <= 10; i++)
        {
            double u = .15 + .70 * i / 10;
            var result = Observe(tracker, i * 100, reverse ? 1 - u : u, .86);
            Assert.Equal(i == 10 ? BoardControlSwipe.Toggle : (BoardControlSwipe?)null, result);
        }
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(150, false)]
    [InlineData(150, true)]
    [InlineData(1500, false)]
    [InlineData(1500, true)]
    [InlineData(3000, false)]
    [InlineData(3000, true)]
    public void QuickAndRelaxedCrossingsBothQualify(int durationMilliseconds, bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        const int steps = 6;
        for (int i = 0; i <= steps; i++)
        {
            double u = .15 + .70 * i / steps;
            var result = Observe(tracker, durationMilliseconds * i / steps, reverse ? 1 - u : u, id: i);
            Assert.Equal(i == steps ? BoardControlSwipe.Toggle : (BoardControlSwipe?)null, result);
        }
    }

    [Theory]
    [InlineData(.305, .970)]
    [InlineData(.280, .944)]
    [InlineData(.682, .138)]
    [InlineData(.261, .958)]
    [InlineData(.591, .046)]
    [InlineData(.573, .040)]
    public void LateVisibleStartsFromCapturedPassesStillCompleteAFullCrossing(double first, double last)
    {
        var tracker = new BoardControlSwipeTracker();
        int commands = 0;
        for (int i = 0; i <= 10; i++)
        {
            double u = first + (last - first) * i / 10;
            var result = Observe(tracker, i * 55, u, .92, id: i);
            if (result is not null) commands++;
        }
        Assert.Equal(1, commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossingBeyondThreeSecondsExpiresEvenWithFreshFrames(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.15, .267, .383, .5, .617, .733, .85];
        for (int i = 0; i < positions.Length; i++)
            Assert.Null(Observe(tracker, i * 500 + (i == 6 ? 1 : 0), reverse ? 1 - positions[i] : positions[i]));
        Assert.Null(Observe(tracker, 3100, reverse ? .1 : .9));
    }

    [Fact]
    public void WaitingAtStartingEdgeDoesNotConsumeTheSwipeWindow()
    {
        var tracker = new BoardControlSwipeTracker();
        for (int time = 0; time <= 2000; time += 100)
            Assert.Null(Observe(tracker, time, .12));
        Assert.Null(Observe(tracker, 2100, .35));
        Assert.Null(Observe(tracker, 2200, .6));
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 2300, .85));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettlingVerticallyBeforeSweepingDoesNotPolluteTheMotionVector(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .5, .2));
        Assert.Null(Observe(tracker, 100, .5, .4));
        Assert.Null(Observe(tracker, 200, .5, .65));
        double[] positions = [.62, .74, .86];
        for (int i = 0; i < positions.Length; i++)
        {
            var result = Observe(tracker, 300 + i * 100, reverse ? 1 - positions[i] : positions[i], .65);
            Assert.Equal(i == positions.Length - 1 ? BoardControlSwipe.Toggle : (BoardControlSwipe?)null, result);
        }
    }

    [Fact]
    public void DiagonalOrReversingPathsAreRejected()
    {
        var diagonal = new BoardControlSwipeTracker();
        Assert.Null(Observe(diagonal, 0, .1, .3));
        Assert.Null(Observe(diagonal, 100, .35, .4));
        Assert.Null(Observe(diagonal, 200, .6, .6));
        Assert.Null(Observe(diagonal, 300, .85, .6));

        var reversal = new BoardControlSwipeTracker();
        Assert.Null(Observe(reversal, 0, .1));
        Assert.Null(Observe(reversal, 100, .5));
        Assert.Null(Observe(reversal, 200, .35));
        Assert.Null(Observe(reversal, 300, .6));
        Assert.Null(Observe(reversal, 400, .9));
    }

    [Fact]
    public void ReversalBackToTheEdgeCanStartANewSwipe()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .5));
        Assert.Null(Observe(tracker, 200, .15));
        Assert.Null(Observe(tracker, 300, .4));
        Assert.Null(Observe(tracker, 400, .6));
        Assert.Equal(BoardControlSwipe.Toggle, Observe(tracker, 500, .9));
    }

    [Theory]
    [InlineData(1, double.NaN, .5)]
    [InlineData(1, .1, double.PositiveInfinity)]
    [InlineData(1, -.1, .5)]
    [InlineData(1, .1, 1.1)]
    public void InvalidGeometryCannotStartASwipe(long id, double u, double v)
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, u, v, id));
        Assert.Null(Observe(tracker, 100, .35));
        Assert.Null(Observe(tracker, 200, .6));
        Assert.Null(Observe(tracker, 300, .9));
    }

    [Fact]
    public void ResetDiscardsPartialPathAndCooldown()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .35));
        tracker.Reset();
        Assert.Null(Observe(tracker, 200, .6));
        Assert.Null(Observe(tracker, 300, .9));
        Assert.Equal(BoardControlSwipe.Toggle, Sweep(tracker, 400, true));
        tracker.Reset();
        Assert.Equal(BoardControlSwipe.Toggle, Sweep(tracker, 800, false));
    }

    [Fact]
    public void TraversalActivityNeedsEstablishedFreshMovement()
    {
        var tracker = new BoardControlSwipeTracker();
        Observe(tracker, 0, .12);
        Observe(tracker, 100, .13);
        Observe(tracker, 200, .13);
        Assert.False(tracker.HasTraversalInProgress);
        Observe(tracker, 300, .32);
        Assert.False(tracker.HasTraversalInProgress);
        Observe(tracker, 400, .45);
        Assert.True(tracker.HasTraversalInProgress);
        var missing = Start.AddMilliseconds(500);
        tracker.Update([], missing, missing);
        Assert.False(tracker.HasTraversalInProgress);
        Observe(tracker, 600, .62);
        Assert.True(tracker.HasTraversalInProgress);
        Observe(tracker, 600, .63);
        Assert.False(tracker.HasTraversalInProgress);
        Observe(tracker, 700, .90);
        Assert.False(tracker.HasTraversalInProgress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamCanStartAtTheCenterAndInferItsDirection(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.5, .62, .74, .86];
        for (int i = 0; i < positions.Length; i++)
        {
            var result = Observe(tracker, i * 100, reverse ? 1 - positions[i] : positions[i], id: i);
            Assert.Equal(i == positions.Length - 1 ? BoardControlSwipe.Toggle : (BoardControlSwipe?)null, result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutwardVectorCompletesAfterBriefDisappearanceNearEdge(bool reverse)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.38, .52, .66, .80];
        for (int i = 0; i < positions.Length; i++)
            Assert.Null(Observe(tracker, i * 100, reverse ? 1 - positions[i] : positions[i], id: i));
        var tooSoon = Start.AddMilliseconds(350);
        Assert.Null(tracker.Update([], tooSoon, tooSoon));
        var departure = Start.AddMilliseconds(400);
        Assert.Equal(BoardControlSwipe.Toggle, tracker.Update([], departure, departure));
        Assert.False(tracker.HasTraversalInProgress);
        for (int time = 500; time <= 1500; time += 100)
        {
            var now = Start.AddMilliseconds(time);
            Assert.Null(tracker.Update([], now, now));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExtrapolationNeedsNearEdgeEvidenceRecentMotionAndABriefGap(int failure)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = failure == 0 ? [.2, .36, .52, .68] : [.38, .52, .66, .8];
        for (int i = 0; i < positions.Length; i++) Assert.Null(Observe(tracker, i * 100, positions[i]));
        if (failure == 1) Assert.Null(Observe(tracker, 350, .8));
        var missing = Start.AddMilliseconds(failure == 2 ? 551 : 450);
        Assert.Null(tracker.Update([], missing, missing));
    }

    [Fact]
    public void DiagonalVectorIsRejectedEvenWithinTheVerticalSpanLimit()
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.5, .616, .733, .85];
        double[] heights = [.5, .573, .646, .72];
        for (int i = 0; i < positions.Length; i++)
            Assert.Null(Observe(tracker, i * 100, positions[i], heights[i]));
    }

    [Fact]
    public void InvalidOrResetFramesCannotLeaveTraversalActivityLatched()
    {
        var tracker = new BoardControlSwipeTracker();
        Observe(tracker, 0, .1);
        Observe(tracker, 100, .35);
        Observe(tracker, 200, .55);
        Assert.True(tracker.HasTraversalInProgress);
        tracker.Reset();
        Assert.False(tracker.HasTraversalInProgress);
        Observe(tracker, 300, .1);
        Observe(tracker, 400, .35);
        Observe(tracker, 500, .55);
        Assert.True(tracker.HasTraversalInProgress);
        var stale = Start.AddMilliseconds(600);
        tracker.Update([new(1, .65, .5)], stale, stale.AddMilliseconds(351));
        Assert.False(tracker.HasTraversalInProgress);
    }

    [Fact]
    public void ProcessingFramesDoesNotAllocate()
    {
        var tracker = new BoardControlSwipeTracker();
        BoardSwipeSample[] samples = [new(0, .1, .5), new(0, .12, .52), new(0, .08, .48), new(0, .11, .5)];
        for (int i = 0; i < 1000; i++)
        {
            var time = Start.AddMilliseconds(i * 100);
            tracker.Update(samples, time, time);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1000; i < 2000; i++)
        {
            var time = Start.AddMilliseconds(i * 100);
            double u = (i % 4) switch { 0 => .1, 1 => .35, 2 => .6, _ => .9 };
            for (int j = 0; j < samples.Length; j++) samples[j] = new(i + j, u + j * .01, .5);
            tracker.Update(samples, time, time);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static BoardControlSwipe? Sweep(BoardControlSwipeTracker tracker, int start, bool reverse)
    {
        double[] positions = [.1, .35, .6, .9];
        BoardControlSwipe? result = null;
        for (int i = 0; i < positions.Length; i++)
            result = Observe(tracker, start + i * 100, reverse ? 1 - positions[i] : positions[i]);
        return result;
    }

    private static BoardControlSwipe? Observe(BoardControlSwipeTracker tracker, int milliseconds,
        double u, double v = .5, long id = 1)
    {
        var time = Start.AddMilliseconds(milliseconds);
        return tracker.Update([new(id, u, v)], time, time);
    }
}
