using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class BoardControlSwipeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, BoardControlSwipe.Show)]
    [InlineData(true, BoardControlSwipe.Hide)]
    public void WholeBoardSweepRecognizesBothDirections(bool reverse, BoardControlSwipe expected)
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.12, .32, .53, .78];
        for (int i = 0; i < positions.Length; i++)
        {
            var result = Observe(tracker, i * 100, reverse ? 1 - positions[i] : positions[i]);
            Assert.Equal(i == positions.Length - 1 ? expected : (BoardControlSwipe?)null, result);
        }
    }

    [Fact]
    public void EndpointFramesDoNotRepeatAndReverseSwipeWorksAfterCooldown()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Equal(BoardControlSwipe.Show, Sweep(tracker, 0, false));
        for (int time = 400; time <= 1100; time += 100)
            Assert.Null(Observe(tracker, time, .9));
        Assert.Null(Observe(tracker, 1200, .65));
        Assert.Null(Observe(tracker, 1300, .42));
        Assert.Equal(BoardControlSwipe.Hide, Observe(tracker, 1400, .15));
    }

    [Fact]
    public void CooldownDiscardsAnImmediateReverseSweep()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Equal(BoardControlSwipe.Show, Sweep(tracker, 0, false));
        Assert.Null(Sweep(tracker, 400, true));
        Assert.Equal(BoardControlSwipe.Hide, Sweep(tracker, 1000, true));
    }

    [Fact]
    public void DifferentHandsCannotCompleteEachOthersPath()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1, id: 1));
        Assert.Null(Observe(tracker, 100, .35, id: 1));
        Assert.Null(Observe(tracker, 200, .55, id: 2));
        Assert.Null(Observe(tracker, 300, .8, id: 2));
        Assert.Null(Observe(tracker, 400, .9, id: 2));
    }

    [Fact]
    public void SecondHandCanSwipeWhileFirstStaysStill()
    {
        var tracker = new BoardControlSwipeTracker();
        double[] positions = [.9, .65, .4, .15];
        for (int i = 0; i < positions.Length; i++)
        {
            var time = Start.AddMilliseconds(i * 100);
            var result = tracker.Update([new(1, .1, .2), new(2, positions[i], .7)], time, time);
            Assert.Equal(i == positions.Length - 1 ? BoardControlSwipe.Hide : (BoardControlSwipe?)null, result);
        }
    }

    [Fact]
    public void DuplicateFramesAndTrackingIdsCannotManufactureObservations()
    {
        var tracker = new BoardControlSwipeTracker();
        Assert.Null(Observe(tracker, 0, .1));
        Assert.Null(Observe(tracker, 100, .35));
        Assert.Null(Observe(tracker, 100, .8));
        Assert.Null(Observe(tracker, 50, .8));
        var time = Start.AddMilliseconds(300);
        Assert.Null(tracker.Update([new(1, .8, .5), new(1, .8, .5)], time, time));
        Assert.Equal(BoardControlSwipe.Show, Observe(tracker, 400, .85));
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
        Assert.Null(Observe(tracker, 451, .6));
        Assert.Null(Observe(tracker, 550, .8));
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
        Assert.Equal(BoardControlSwipe.Show, Observe(tracker, 500, .85, .5));
    }

    [Theory]
    [InlineData(.3, .45, .6, .9)]
    [InlineData(.1, .2, .3, .4)]
    public void MiddleStartsAndShortMovementsAreNotCommands(double a, double b, double c, double d)
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
    public void CrossingBeyondFiveSecondsIsNotASwipe()
    {
        var tracker = new BoardControlSwipeTracker();
        for (int i = 0; i <= 30; i++)
            Assert.Null(Observe(tracker, i * 200, .75 - .51 * i / 30));
    }

    [Theory]
    [InlineData(false, BoardControlSwipe.Show)]
    [InlineData(true, BoardControlSwipe.Hide)]
    public void SteadyDeliberateCrossingCanTakeFourSeconds(bool reverse, BoardControlSwipe expected)
    {
        var tracker = new BoardControlSwipeTracker();
        for (int i = 0; i <= 40; i++)
        {
            double u = .25 + .50 * i / 40;
            var result = Observe(tracker, i * 100, reverse ? 1 - u : u, .86);
            Assert.Equal(i == 40 ? expected : (BoardControlSwipe?)null, result);
        }
    }

    [Fact]
    public void WaitingAtStartingEdgeDoesNotConsumeTheSwipeWindow()
    {
        var tracker = new BoardControlSwipeTracker();
        for (int time = 0; time <= 2000; time += 100)
            Assert.Null(Observe(tracker, time, .12));
        Assert.Null(Observe(tracker, 2100, .35));
        Assert.Null(Observe(tracker, 2200, .6));
        Assert.Equal(BoardControlSwipe.Show, Observe(tracker, 2300, .85));
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
        Assert.Equal(BoardControlSwipe.Show, Observe(tracker, 500, .9));
    }

    [Theory]
    [InlineData(0, .1, .5)]
    [InlineData(-1, .1, .5)]
    [InlineData(1, double.NaN, .5)]
    [InlineData(1, .1, double.PositiveInfinity)]
    [InlineData(1, -.1, .5)]
    [InlineData(1, .1, 1.1)]
    public void InvalidIdentityOrGeometryCannotStartASwipe(long id, double u, double v)
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
        Assert.Equal(BoardControlSwipe.Hide, Sweep(tracker, 400, true));
        tracker.Reset();
        Assert.Equal(BoardControlSwipe.Show, Sweep(tracker, 800, false));
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
        Observe(tracker, 700, .80);
        Assert.False(tracker.HasTraversalInProgress);
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
        BoardSwipeSample[] samples = [new(1, .1, .5), new(2, .9, .4)];
        for (int i = 0; i < 1000; i++)
        {
            var time = Start.AddMilliseconds(i * 100);
            tracker.Update(samples, time, time);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1000; i < 2000; i++)
        {
            var time = Start.AddMilliseconds(i * 100);
            samples[0] = new(1, (i % 10) / 10.0, .5);
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
