using ProjectTabletop.Calibration;

namespace ProjectTabletop.Tests;

public class CameraLatencyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SubtractsProjectorDelayFromFrameArrival()
    {
        var measurement = new CameraLatencyMeasurement();
        Assert.Equal(17, measurement.ProjectorMilliseconds);

        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(97.5)));

        var sample = Assert.Single(measurement.Samples);
        Assert.Equal(0, sample.SpotIndex);
        Assert.Equal(Start, sample.ShownAt);
        Assert.Equal(Start.AddMilliseconds(97.5), sample.ArrivedAt);
        Assert.Equal(97.5, sample.TotalMilliseconds);
        Assert.Equal(80.5, sample.Milliseconds);
        Assert.Equal(80.5, measurement.MedianMilliseconds);
        Assert.Equal(80.5, measurement.MinimumMilliseconds);
        Assert.Equal(80.5, measurement.MaximumMilliseconds);
    }

    [Fact]
    public void SubtractsManuallyConfiguredProjectorDelay()
    {
        var measurement = new CameraLatencyMeasurement { ProjectorMilliseconds = 32.5 };

        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(97.5)));

        var sample = Assert.Single(measurement.Samples);
        Assert.Equal(97.5, sample.TotalMilliseconds);
        Assert.Equal(65, sample.Milliseconds);
        Assert.Equal(65, measurement.MedianMilliseconds);
    }

    [Fact]
    public void ChangingProjectorDelayRecalculatesAndRestoresFirstObservations()
    {
        var measurement = new CameraLatencyMeasurement();
        var samples = measurement.Samples;
        Assert.True(measurement.Record(2, Start, Start.AddMilliseconds(97)));
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(37)));
        Assert.True(measurement.Record(1, Start, Start.AddMilliseconds(77)));

        measurement.ProjectorMilliseconds = 50;

        Assert.Same(samples, measurement.Samples);
        Assert.Equal([2, 1], samples.Select(sample => sample.SpotIndex));
        Assert.Equal([47d, 27d], samples.Select(sample => sample.Milliseconds));
        Assert.Equal(37, measurement.MedianMilliseconds);
        Assert.Equal(27, measurement.MinimumMilliseconds);
        Assert.Equal(47, measurement.MaximumMilliseconds);
        Assert.False(measurement.Record(0, Start, Start.AddMilliseconds(137)));

        measurement.ProjectorMilliseconds = 100;

        Assert.Empty(samples);
        Assert.Null(measurement.MedianMilliseconds);
        Assert.Null(measurement.MinimumMilliseconds);
        Assert.Null(measurement.MaximumMilliseconds);

        measurement.ProjectorMilliseconds = 7;

        Assert.Equal([2, 0, 1], samples.Select(sample => sample.SpotIndex));
        Assert.Equal([90d, 30d, 70d], samples.Select(sample => sample.Milliseconds));
        Assert.Equal(70, measurement.MedianMilliseconds);
        Assert.Equal(30, measurement.MinimumMilliseconds);
        Assert.Equal(90, measurement.MaximumMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16.999)]
    public void InitiallyNegativeCameraDelayRetainsFirstArrivalForRecalculation(double elapsed)
    {
        var measurement = new CameraLatencyMeasurement();
        var firstArrival = Start.AddMilliseconds(elapsed);
        Assert.False(measurement.Record(0, Start, firstArrival));
        Assert.Empty(measurement.Samples);
        Assert.False(measurement.Record(0, Start, Start.AddMilliseconds(87)));

        measurement.ProjectorMilliseconds = 0;

        var sample = Assert.Single(measurement.Samples);
        Assert.Equal(firstArrival, sample.ArrivedAt);
        Assert.Equal((firstArrival - Start).TotalMilliseconds, sample.Milliseconds);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(2000.001)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidProjectorDelayWithoutChangingSamples(double delay)
    {
        var measurement = new CameraLatencyMeasurement { ProjectorMilliseconds = 25 };
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(87)));

        Assert.Throws<ArgumentOutOfRangeException>(() => measurement.ProjectorMilliseconds = delay);

        Assert.Equal(25, measurement.ProjectorMilliseconds);
        Assert.Equal(62, measurement.MedianMilliseconds);
        Assert.Equal(62, Assert.Single(measurement.Samples).Milliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public void ProjectorDelayLimitsAreInclusive(double delay)
    {
        var measurement = new CameraLatencyMeasurement { ProjectorMilliseconds = delay };

        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(2000)));

        Assert.Equal(2000 - delay, measurement.MedianMilliseconds);
    }

    [Fact]
    public void SummarizesVariableSamplesWithoutChangingTheirAcquisitionOrder()
    {
        var measurement = new CameraLatencyMeasurement();
        double[] totals = [117, 47, 87, 77, 147];

        for (int i = 0; i < totals.Length; i++)
        {
            var shown = Start.AddSeconds(i);
            Assert.True(measurement.Record(i, shown, shown.AddMilliseconds(totals[i])));
            if (i == 1) Assert.Equal(65, measurement.MedianMilliseconds);
            if (i == 3) Assert.Equal(65, measurement.MedianMilliseconds);
        }

        Assert.Equal(70, measurement.MedianMilliseconds);
        Assert.Equal(30, measurement.MinimumMilliseconds);
        Assert.Equal(130, measurement.MaximumMilliseconds);
        Assert.Equal([0, 1, 2, 3, 4], measurement.Samples.Select(sample => sample.SpotIndex));
    }

    [Fact]
    public void DuplicateOrReorderedResultsCannotReplaceFirstAcceptedObservation()
    {
        var measurement = new CameraLatencyMeasurement();
        Assert.True(measurement.Record(1, Start.AddSeconds(1), Start.AddSeconds(1).AddMilliseconds(117)));
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(87)));

        Assert.False(measurement.Record(1, Start.AddSeconds(1), Start.AddSeconds(1).AddMilliseconds(47)));
        Assert.False(measurement.Record(0, Start, Start.AddMilliseconds(187)));

        Assert.Equal(2, measurement.Samples.Count);
        Assert.Equal(85, measurement.MedianMilliseconds);
        Assert.Equal([1, 0], measurement.Samples.Select(sample => sample.SpotIndex));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void RejectsInvalidSpotIndices(int index)
    {
        var measurement = new CameraLatencyMeasurement();
        Assert.False(measurement.Record(index, Start, Start.AddMilliseconds(87)));
        Assert.Empty(measurement.Samples);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(2000.001)]
    [InlineData(10000)]
    public void InvalidTimingDoesNotConsumeTheSpot(double elapsed)
    {
        var measurement = new CameraLatencyMeasurement();
        Assert.False(measurement.Record(0, Start, Start.AddMilliseconds(elapsed)));
        Assert.Empty(measurement.Samples);
        Assert.Null(measurement.MedianMilliseconds);
        Assert.Null(measurement.MinimumMilliseconds);
        Assert.Null(measurement.MaximumMilliseconds);
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(87)));
        Assert.Equal(70, measurement.MedianMilliseconds);
    }

    [Theory]
    [InlineData(17, 0)]
    [InlineData(2000, 1983)]
    public void TimingLimitsAreInclusive(double elapsed, double expected)
    {
        var measurement = new CameraLatencyMeasurement();
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(elapsed)));
        Assert.Equal(expected, measurement.MedianMilliseconds);
    }

    [Fact]
    public void ResetClearsSamplesAndAllowsANewScan()
    {
        var measurement = new CameraLatencyMeasurement { ProjectorMilliseconds = 30 };
        var samples = measurement.Samples;
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(87)));
        Assert.False(measurement.Record(1, Start, Start.AddMilliseconds(20)));
        Assert.True(samples is IList<CameraLatencySample> { IsReadOnly: true });

        measurement.Reset();

        Assert.Empty(samples);
        Assert.Null(measurement.MedianMilliseconds);
        Assert.Null(measurement.MinimumMilliseconds);
        Assert.Null(measurement.MaximumMilliseconds);
        Assert.Equal(30, measurement.ProjectorMilliseconds);
        Assert.True(measurement.Record(0, Start.AddSeconds(10), Start.AddSeconds(10).AddMilliseconds(57)));
        Assert.Equal(27, measurement.MedianMilliseconds);
        Assert.True(measurement.Record(1, Start.AddSeconds(10), Start.AddSeconds(10).AddMilliseconds(77)));
        Assert.Equal(37, measurement.MedianMilliseconds);
        measurement.ProjectorMilliseconds = 0;
        Assert.Equal([57d, 77d], samples.Select(sample => sample.Milliseconds));
    }

    [Fact]
    public void ProjectionReferenceAgeRequiresThreeValidDistinctObservations()
    {
        var measurement = new CameraLatencyMeasurement();
        Assert.Equal(TimeSpan.Zero, measurement.ProjectionReferenceMinimumAge(60));
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(100)));
        Assert.Equal(TimeSpan.Zero, measurement.ProjectionReferenceMinimumAge(60));
        Assert.True(measurement.Record(1, Start, Start.AddMilliseconds(120)));
        Assert.False(measurement.Record(1, Start, Start.AddMilliseconds(150)));
        Assert.False(measurement.Record(2, Start, Start.AddMilliseconds(-1)));
        Assert.False(measurement.Record(2, Start, Start.AddMilliseconds(2001)));
        Assert.Equal(TimeSpan.Zero, measurement.ProjectionReferenceMinimumAge(60));
        Assert.True(measurement.Record(2, Start, Start.AddMilliseconds(110)));
        Assert.True(measurement.ProjectionReferenceMinimumAge(60) > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(60, 66.6666667)]
    [InlineData(30, 33.3333333)]
    [InlineData(100, 80)]
    public void ProjectionReferenceAgeUsesShortestTotalMinusTwoFramePeriods(double fps, double expectedMilliseconds)
    {
        var measurement = new CameraLatencyMeasurement();
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(130)));
        Assert.True(measurement.Record(1, Start, Start.AddMilliseconds(100)));
        Assert.True(measurement.Record(2, Start, Start.AddMilliseconds(170)));

        Assert.InRange(measurement.ProjectionReferenceMinimumAge(fps).TotalMilliseconds,
            expectedMilliseconds - .0001, expectedMilliseconds + .0001);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void ProjectionReferenceAgeNeverBecomesNegative(double fps)
    {
        var measurement = new CameraLatencyMeasurement();
        for (int i = 0; i < 3; i++)
            Assert.True(measurement.Record(i, Start, Start.AddMilliseconds(20 + i)));

        Assert.Equal(TimeSpan.Zero, measurement.ProjectionReferenceMinimumAge(fps));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    public void ProjectionReferenceAgeRequiresFinitePositiveFrameRate(double fps)
    {
        var measurement = new CameraLatencyMeasurement();
        for (int i = 0; i < 3; i++)
            Assert.True(measurement.Record(i, Start, Start.AddMilliseconds(100 + i)));

        Assert.Equal(TimeSpan.Zero, measurement.ProjectionReferenceMinimumAge(fps));
    }

    [Fact]
    public void ProjectionReferenceAgeUsesRawObservationsIndependentOfProjectorSetting()
    {
        var measurement = new CameraLatencyMeasurement { ProjectorMilliseconds = 500 };
        for (int i = 0; i < 3; i++)
            Assert.False(measurement.Record(i, Start, Start.AddMilliseconds(100 + i)));
        Assert.Empty(measurement.Samples);
        var minimumAge = measurement.ProjectionReferenceMinimumAge(60);
        Assert.InRange(minimumAge.TotalMilliseconds, 66.6666, 66.6667);

        foreach (double projectorDelay in new[] { 0d, 17, 90, 2000 })
        {
            measurement.ProjectorMilliseconds = projectorDelay;
            Assert.Equal(minimumAge, measurement.ProjectionReferenceMinimumAge(60));
        }
    }

    [Fact]
    public void ResetClearsProjectionReferenceTimingUntilThreeNewObservations()
    {
        var measurement = new CameraLatencyMeasurement();
        for (int i = 0; i < 3; i++)
            Assert.True(measurement.Record(i, Start, Start.AddMilliseconds(100 + i)));
        Assert.True(measurement.ProjectionReferenceMinimumAge(60) > TimeSpan.Zero);

        measurement.Reset();

        Assert.Equal(TimeSpan.Zero, measurement.ProjectionReferenceMinimumAge(60));
        Assert.True(measurement.Record(0, Start, Start.AddMilliseconds(50)));
        Assert.True(measurement.Record(1, Start, Start.AddMilliseconds(55)));
        Assert.Equal(TimeSpan.Zero, measurement.ProjectionReferenceMinimumAge(60));
        Assert.True(measurement.Record(2, Start, Start.AddMilliseconds(60)));
        Assert.InRange(measurement.ProjectionReferenceMinimumAge(60).TotalMilliseconds, 16.6666, 16.6667);
    }
}
