using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class BoardColorCalibrationTests
{
    private const int Width = 800, Height = 720, Stride = Width * 4 + 24;
    private static PixelPoint Map(PixelPoint point) => new(80 + point.X * 620, 40 + point.Y * 620);

    [Fact]
    public void SettledChartUsesSharedLayoutAndPreservesNeutralTintCompensation()
    {
        var result = Analyze(CreateFrame(.85));
        Assert.True(result.IsUsable);
        Assert.Equal(4, result.ValidEdges);
        Assert.InRange(result.WhiteLevel, 209, 212);
        Assert.InRange(result.ColorError, 0, .005);
        Assert.InRange(result.WhiteBalanceScore, 0, .025);
        Assert.Equal(0, result.ExposureDirection);
        Assert.Equal(0, result.WhiteBalanceDirection);
    }

    [Fact]
    public void ClippedHighlightsRequireLessExposureBeforeWhiteBalance()
    {
        var clipped = Analyze(CreateFrame(1.35, redGain: 1.08, blueGain: .92));
        var healthy = Analyze(CreateFrame(.85));
        Assert.True(clipped.IsUsable);
        Assert.Equal(-1, clipped.ExposureDirection);
        Assert.Equal(0, clipped.WhiteBalanceDirection);
        Assert.InRange(clipped.ClippedFraction, .5, 1);
        Assert.True(healthy.ExposureScore < clipped.ExposureScore);
    }

    [Fact]
    public void FullProjectedWhiteGetsHeadroomBeforeTheLowerRampStepClips()
    {
        var narrowHeadroom = Analyze(CreateFrame(1));
        Assert.True(narrowHeadroom.IsUsable);
        Assert.Equal(0, narrowHeadroom.ClippedFraction);
        Assert.Equal(-1, narrowHeadroom.ExposureDirection);
        Assert.True(Analyze(CreateFrame(.5)).ExposureScore < narrowHeadroom.ExposureScore);
    }

    [Fact]
    public void DimButRecognizableChartRequestsMoreExposure()
    {
        var dim = Analyze(CreateFrame(.35));
        Assert.True(dim.IsUsable);
        Assert.Equal(1, dim.ExposureDirection);
        Assert.Equal(0, dim.WhiteBalanceDirection);
        Assert.True(Analyze(CreateFrame(.85)).ExposureScore < dim.ExposureScore);
    }

    [Theory]
    [InlineData(1.14, .86, -1)]
    [InlineData(.86, 1.14, 1)]
    public void UnclippedNeutralCastSuggestsBoundedKelvinDirection(double red, double blue, int direction)
    {
        var tinted = Analyze(CreateFrame(.82, redGain: red, blueGain: blue));
        var neutral = Analyze(CreateFrame(.82));
        Assert.True(tinted.IsUsable);
        Assert.Equal(0, tinted.ExposureDirection);
        Assert.Equal(direction, tinted.WhiteBalanceDirection);
        Assert.True(neutral.WhiteBalanceScore < tinted.WhiteBalanceScore);
        Assert.True(neutral.ColorError < tinted.ColorError);
    }

    [Fact]
    public void NeutralGreenCastDoesNotInventAKelvinCorrection()
    {
        var result = Analyze(CreateFrame(.75, greenGain: 1.2));
        Assert.True(result.IsUsable);
        Assert.InRange(result.NeutralGreenError, .15, .25);
        Assert.Equal(0, result.WhiteBalanceDirection);
    }

    [Fact]
    public void LowContrastWashIsScoredWorseWithoutInventingBrightnessOrContrastChanges()
    {
        var washed = Analyze(CreateFrame(.2, offset: 150));
        Assert.True(washed.IsUsable);
        Assert.Equal(0, washed.ExposureDirection);
        Assert.True(Analyze(CreateFrame(.85)).ExposureScore < washed.ExposureScore);
    }

    [Theory]
    [InlineData(1, true, 3)]
    [InlineData(2, false, 2)]
    public void OneObscuredEdgeIsToleratedButAFragmentCannotSteerControls(int missing, bool usable, int edges)
    {
        var result = Analyze(CreateFrame(.85, omittedEdges: missing));
        Assert.Equal(usable, result.IsUsable);
        Assert.Equal(edges, result.ValidEdges);
        if (!usable)
        {
            Assert.Equal(0, result.ExposureDirection);
            Assert.Equal(0, result.WhiteBalanceDirection);
        }
    }

    [Fact]
    public void FlatWhiteWrongAlignmentAndOutOfFrameMappingsAreRejected()
    {
        byte[] white = new byte[Stride * Height];
        Array.Fill(white, (byte)255);
        Assert.False(Analyze(white).IsUsable);
        byte[] chart = CreateFrame(.85);
        Assert.False(BoardColorCalibration.Analyze(Width, Height, Stride, chart,
            point => new(Map(point).X + 80, Map(point).Y + 80)).IsUsable);
        Assert.False(BoardColorCalibration.Analyze(Width, Height, Stride, chart,
            point => new(-100 + point.X, -100 + point.Y)).IsUsable);
        Assert.False(BoardColorCalibration.Analyze(Width, Height, Stride, chart,
            point => new(double.NaN, double.NaN)).IsUsable);
    }

    [Fact]
    public void LowResolutionAndMalformedBuffersAreRejected()
    {
        Assert.False(BoardColorCalibration.Analyze(Width, Height, Stride, CreateFrame(.85),
            point => new(100 + point.X * 10, 100 + point.Y * 10)).IsUsable);
        Assert.Throws<ArgumentException>(() => BoardColorCalibration.Analyze(Width, Height, Width,
            new byte[Width * Height], Map));
    }

    [Fact]
    public void AClippedEdgeIsNotHiddenByTheOtherThreeHealthyEdges()
    {
        byte[] frame = CreateFrame(.85);
        DrawEdge(frame, 0, 1.35, 0, 1, 1, 1);
        var result = Analyze(frame);
        Assert.True(result.IsUsable);
        Assert.Equal(4, result.ValidEdges);
        Assert.Equal(-1, result.ExposureDirection);
    }

    private static BoardColorCalibrationResult Analyze(byte[] frame) =>
        BoardColorCalibration.Analyze(Width, Height, Stride, frame, Map);

    private static byte[] CreateFrame(double gain, double offset = 0, double redGain = 1,
        double greenGain = 1, double blueGain = 1, int omittedEdges = 0)
    {
        byte[] frame = new byte[Stride * Height];
        for (int edge = omittedEdges; edge < 4; edge++)
            DrawEdge(frame, edge, gain, offset, redGain, greenGain, blueGain);
        return frame;
    }

    private static void DrawEdge(byte[] frame, int edge, double gain, double offset,
        double redGain, double greenGain, double blueGain)
    {
        foreach (var segment in BoardCalibrationTestCard.Segments)
        {
            var a = Map(BoardCalibrationTestCard.Point(edge, segment.Start, segment.Near));
            var b = Map(BoardCalibrationTestCard.Point(edge, segment.End, segment.Far));
            for (int y = (int)Math.Ceiling(Math.Min(a.Y, b.Y)); y <= Math.Floor(Math.Max(a.Y, b.Y)); y++)
            for (int x = (int)Math.Ceiling(Math.Min(a.X, b.X)); x <= Math.Floor(Math.Max(a.X, b.X)); x++)
            {
                int at = y * Stride + x * 4;
                frame[at] = Channel(segment.Blue, blueGain);
                frame[at + 1] = Channel(segment.Green, greenGain);
                frame[at + 2] = Channel(segment.Red, redGain);
                frame[at + 3] = 255;
            }
        }
        byte Channel(byte value, double channelGain) => (byte)Math.Clamp(Math.Round(value * gain * channelGain + offset), 0, 255);
    }
}
