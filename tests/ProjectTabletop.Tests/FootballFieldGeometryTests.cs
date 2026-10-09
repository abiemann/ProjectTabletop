using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class FootballFieldGeometryTests
{
    [Fact]
    public void BehindGoalMarkersKeepTheirPlayerAndMeasuredPosition()
    {
        var now = DateTimeOffset.UtcNow;
        var detection = new ColorTipDetectionResult(
            [new(new(40, 100), 8, 200, 1), new(new(960, 100), 8, 200, 1)], "multiple-black-bar-pairs");
        static PixelPoint? Map(PixelPoint p)
        {
            var uv = new PixelPoint((p.X - 80) / 840, p.Y / 200);
            return FootballFieldGeometry.ContainsMarker(uv.X, uv.Y) ? uv : null;
        }
        for (int player = 0; player < 2; player++)
        {
            var tracker = new ColorTipTracker();
            Assert.Equal(FootballTipAction.Hold, FootballTipAssignment.Update(tracker, player, detection, Map, now, now).Action);
            var later = now.AddMilliseconds(40);
            var result = FootballTipAssignment.Update(tracker, player, detection, Map, later, later);
            Assert.Equal(FootballTipAction.Publish, result.Action);
            Assert.Equal(player == 0 ? 40 : 960, result.Tip!.Center.X);
            Assert.True(player == 0 ? result.FieldPoint!.Value.X < 0 : result.FieldPoint!.Value.X > 1);
        }
    }

    [Theory]
    [InlineData(-.06, .5)]
    [InlineData(1.06, .5)]
    [InlineData(.25, -.05)]
    [InlineData(.75, 1.05)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void MarkersAreAcceptedAcrossTheGoalLineAndOnSurroundingGrass(double x, double y)
        => Assert.True(FootballFieldGeometry.ContainsMarker(x, y));

    [Theory]
    [InlineData(-.07, .5)]
    [InlineData(1.07, .5)]
    [InlineData(.5, -.065)]
    [InlineData(.5, 1.065)]
    [InlineData(-.068, -.063)]
    [InlineData(1.068, 1.063)]
    [InlineData(double.NaN, .5)]
    [InlineData(.5, double.PositiveInfinity)]
    public void MarkersOutsideGrassIncludingRoundedCornersAreRejected(double x, double y)
        => Assert.False(FootballFieldGeometry.ContainsMarker(x, y));
}
