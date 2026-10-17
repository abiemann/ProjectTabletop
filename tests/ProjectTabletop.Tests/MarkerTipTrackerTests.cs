using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class MarkerTipTrackerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly MarkerTipObservation Marker = new(new(100, 100), 4, 50, .9);

    [Fact]
    public void StationaryMarkerNeedsTwoFreshFramesAndFollowsMeasuredMotion()
    {
        var tracker = new MarkerTipTracker();
        Assert.False(Observe(tracker, One(Marker), 0).Confirmed);
        var replayed = tracker.Update(One(Marker), Epoch, Epoch.AddMilliseconds(30));
        Assert.False(replayed.Confirmed);
        Assert.Null(replayed.Observation);
        Assert.Equal("old-camera-frame", replayed.Reason);
        Assert.True(Observe(tracker, One(Marker), 70).Confirmed);
        var moved = Marker with { Center = new(155, 100) };
        var measured = Observe(tracker, One(moved), 140);
        Assert.True(measured.Confirmed);
        Assert.Equal(moved, measured.Observation);
    }

    [Fact]
    public void MissingMarkerPublishesNoPredictionAndRequiresFreshConfirmationOnReturn()
    {
        var tracker = new MarkerTipTracker();
        tracker.Acquire(Marker, Epoch);
        Assert.True(Observe(tracker, One(Marker), 70).Confirmed);
        var missing = Observe(tracker, new([], "covered"), 140);
        Assert.False(missing.Confirmed);
        Assert.Null(missing.Observation);
        Assert.False(Observe(tracker, One(Marker), 210).Confirmed);
        Assert.True(Observe(tracker, One(Marker), 280).Confirmed);
        tracker.Reset();
        Assert.False(Observe(tracker, One(Marker), 350).Confirmed);
    }

    [Fact]
    public void AmbiguousMarkersAreNotGuessedDuringAcquisitionOrTracking()
    {
        var tracker = new MarkerTipTracker();
        var two = new MarkerTipDetectionResult([Marker, Marker with { Center = new(150, 100) }], "two");
        Assert.Null(Observe(tracker, two, 0).Observation);
        tracker.Acquire(Marker, Epoch);
        Assert.True(Observe(tracker, One(Marker), 70).Confirmed);
        var near = new MarkerTipDetectionResult([
            Marker with { Center = new(97, 100) }, Marker with { Center = new(103, 100) }], "two");
        var ambiguous = Observe(tracker, near, 140);
        Assert.Null(ambiguous.Observation);
        Assert.Equal("ambiguous-marker-tip", ambiguous.Reason);
        Assert.False(Observe(tracker, One(Marker), 210).Confirmed);
    }

    [Theory]
    [InlineData(70, 1000)]
    [InlineData(2000, 1000)]
    public void StaleAndFutureFramesClearConfirmation(int frameMilliseconds, int nowMilliseconds)
    {
        var tracker = new MarkerTipTracker();
        tracker.Acquire(Marker, Epoch);
        var rejected = tracker.Update(One(Marker), Epoch.AddMilliseconds(frameMilliseconds),
            Epoch.AddMilliseconds(nowMilliseconds));
        Assert.False(rejected.Confirmed);
        Assert.Null(rejected.Observation);
        Assert.Equal("stale-camera-frame", rejected.Reason);
        Assert.False(Observe(tracker, One(Marker), nowMilliseconds + 70).Confirmed);
    }

    private static MarkerTipDetectionResult One(MarkerTipObservation observation) => new([observation], "test");
    private static MarkerTipTrackResult Observe(MarkerTipTracker tracker, MarkerTipDetectionResult detection, int ms) =>
        tracker.Update(detection, Epoch.AddMilliseconds(ms), Epoch.AddMilliseconds(ms));
}
