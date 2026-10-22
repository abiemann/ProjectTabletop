using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class DuckHuntMeasurementTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
    private static readonly BoardAim Old = new(.3, .4), New = new(.6, .7);
    private static DateTimeOffset At(double milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void ContinuousCoordinatesFollowEveryValidFrameAndExpireWhenTheDotDisappears()
    {
        var tracker = new DuckHuntMeasurementTracker();
        Assert.True(tracker.Observe(Old, At(0), At(5)));
        Assert.Equal(Old, tracker.GetSnapshot(At(5)).LiveDetectedXY);
        Assert.True(tracker.Observe(New, At(16), At(20)));
        Assert.Equal(New, tracker.GetSnapshot(At(20)).LiveDetectedXY);
        Assert.Null(tracker.GetSnapshot(At(167)).LiveDetectedXY);
        Assert.True(tracker.Observe(null, At(170), At(175)));
        Assert.Null(tracker.GetSnapshot(At(175)).LiveDetectedXY);
    }

    [Fact]
    public void SnapshotUsesLatestPreOnsetCoordinatesEvenWhenPostClickTrackingFinishesFirst()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(new(.1, .2), At(0), At(5));
        tracker.Observe(Old, At(16), At(20));
        tracker.Observe(new(.8, .9), At(40), At(45));
        Assert.True(tracker.Capture(New, At(45), At(30), At(60), At(65)));
        var measurement = tracker.GetSnapshot(At(65));
        Assert.True(measurement.HasCompletedPair);
        Assert.Equal(Old, measurement.OldDetectedXY);
        Assert.Equal(At(16), measurement.OldFrameTimestamp);
        Assert.Equal(New, measurement.NewDetectedXY);
        Assert.Equal(At(45), measurement.NewFrameTimestamp);
        Assert.Equal(At(30), measurement.ClickOnset);
        Assert.Equal(new BoardAim(.8, .9), measurement.LiveDetectedXY);
    }

    [Fact]
    public void SampleAtExactClickOnsetIsNotAPreClickCoordinate()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(Old, At(0), At(5));
        tracker.Observe(new(.8, .9), At(30), At(35));
        Assert.True(tracker.Capture(New, At(45), At(30), At(60), At(65)));
        Assert.Equal(Old, tracker.GetSnapshot(At(65)).OldDetectedXY);
    }

    [Fact]
    public void LaterContinuousTrackingCannotMoveTheCompletedMeasurement()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(Old, At(0), At(5));
        tracker.Capture(New, At(45), At(30), At(60), At(65));
        tracker.Observe(new(.9, .1), At(80), At(85));
        var measurement = tracker.GetSnapshot(At(85));
        Assert.Equal(Old, measurement.OldDetectedXY);
        Assert.Equal(New, measurement.NewDetectedXY);
        Assert.Equal(new BoardAim(.9, .1), measurement.LiveDetectedXY);
        // The completed pair persists even after the live dot expires.
        Assert.True(tracker.GetSnapshot(At(1000)).HasCompletedPair);
        Assert.Null(tracker.GetSnapshot(At(1000)).LiveDetectedXY);
    }

    [Fact]
    public void EqualOldAndNewCoordinatesRemainAValidZeroDistanceMeasurement()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(Old, At(0), At(5));
        Assert.True(tracker.Capture(Old, At(45), At(30), At(60), At(65)));
        var measurement = tracker.GetSnapshot(At(65));
        Assert.True(measurement.HasCompletedPair);
        Assert.Equal(measurement.OldDetectedXY, measurement.NewDetectedXY);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidNewPointIsReportedWithoutInventingMissingOrStaleOldPoint(bool staleOld)
    {
        var tracker = new DuckHuntMeasurementTracker();
        if (staleOld) tracker.Observe(Old, At(-151), At(-150));
        Assert.True(tracker.Capture(New, At(45), At(0), At(60), At(65)));
        var measurement = tracker.GetSnapshot(At(65));
        Assert.False(measurement.HasCompletedPair);
        Assert.Null(measurement.OldDetectedXY);
        Assert.Null(measurement.OldFrameTimestamp);
        Assert.Equal(New, measurement.NewDetectedXY);
    }

    [Fact]
    public void FreshMissingObservationCannotBeUsedAsOldButRecentValidPositionRemainsAvailable()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(Old, At(0), At(5));
        tracker.Observe(null, At(16), At(20));
        Assert.True(tracker.Capture(New, At(45), At(30), At(60), At(65)));
        Assert.Equal(Old, tracker.GetSnapshot(At(65)).OldDetectedXY);
    }

    [Fact]
    public void NewMeasurementWithNoRecentOldPointClearsThePreviousCentre()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(Old, At(0), At(5));
        tracker.Capture(New, At(45), At(30), At(60), At(65));
        Assert.True(tracker.Capture(new(.9, .2), At(345), At(330), At(360), At(365)));
        var measurement = tracker.GetSnapshot(At(365));
        Assert.False(measurement.HasCompletedPair);
        Assert.Null(measurement.OldDetectedXY);
        Assert.Equal(new BoardAim(.9, .2), measurement.NewDetectedXY);
    }

    [Fact]
    public void MissingClickDotCannotReplaceMeasurementOrBeRetriedWithLaterPixels()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(Old, At(0), At(5));
        tracker.Capture(New, At(45), At(30), At(60), At(65));
        Assert.False(tracker.Capture(null, At(345), At(330), At(360), At(365)));
        Assert.False(tracker.Capture(new(.9, .2), At(380), At(330), At(390), At(395)));
        Assert.Equal(New, tracker.GetSnapshot(At(395)).NewDetectedXY);
        Assert.Equal(At(30), tracker.GetSnapshot(At(395)).ClickOnset);
    }

    [Fact]
    public void ResetClearsCoordinatesAndRejectsInFlightObservationsAndClicks()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(Old, At(0), At(5));
        tracker.Capture(New, At(45), At(30), At(60), At(65));
        tracker.Reset(At(80));
        Assert.Equal(default, tracker.GetSnapshot(At(85)));
        Assert.False(tracker.Observe(Old, At(70), At(85)));
        Assert.False(tracker.Capture(New, At(90), At(75), At(100), At(105)));
        tracker.Observe(Old, At(110), At(115));
        Assert.True(tracker.Capture(New, At(145), At(130), At(160), At(165)));
        Assert.True(tracker.GetSnapshot(At(165)).HasCompletedPair);
    }

    [Fact]
    public void BoundedHistoryWrapPreservesMostRecentPreClickSample()
    {
        var tracker = new DuckHuntMeasurementTracker();
        for (int i = 0; i < 100; i++)
            tracker.Observe(new(i / 100.0, .5), At(i * 10), At(i * 10 + 2));
        Assert.True(tracker.Capture(New, At(995), At(975), At(1000), At(1005)));
        Assert.Equal(new BoardAim(.97, .5), tracker.GetSnapshot(At(1005)).OldDetectedXY);
    }

    [Theory]
    [InlineData(double.NaN, .5)]
    [InlineData(.5, double.PositiveInfinity)]
    [InlineData(-.1, .5)]
    [InlineData(.5, 1.1)]
    public void InvalidContinuousCoordinatesCannotSupplyAnOldPosition(double u, double v)
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(new(u, v), At(0), At(5));
        Assert.Null(tracker.GetSnapshot(At(5)).LiveDetectedXY);
        Assert.True(tracker.Capture(New, At(45), At(30), At(60), At(65)));
        Assert.Null(tracker.GetSnapshot(At(65)).OldDetectedXY);
    }

    [Fact]
    public void StaleFutureAndOutOfOrderContinuousFramesDoNotReplaceLiveCoordinates()
    {
        var tracker = new DuckHuntMeasurementTracker();
        tracker.Observe(Old, At(100), At(105));
        Assert.False(tracker.Observe(New, At(90), At(110)));
        Assert.False(tracker.Observe(New, At(120), At(110)));
        Assert.False(tracker.Observe(New, At(110), At(261)));
        Assert.Equal(Old, tracker.GetSnapshot(At(115)).LiveDetectedXY);
    }
}
