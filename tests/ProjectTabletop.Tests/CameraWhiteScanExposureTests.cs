using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class CameraWhiteScanExposureTests
{
    private const int Width = 320, Height = 240, Stride = Width * 4 + 12;
    private static readonly PixelPoint[] Board = [new(80, 50), new(260, 55), new(250, 210), new(85, 205)];

    [Fact]
    public void DimWhiteBoardRequestsOneBrighterStopWhileIgnoringTheDarkRoom()
    {
        var pixels = Frame(0, 0, 0);
        Fill(pixels, 85, 55, 250, 205, 140, 122, 135);
        var result = Evaluate(pixels);
        Assert.True(result.IsUsable);
        Assert.Equal(140, result.BrightnessP90);
        Assert.Equal(1, result.Direction);
        Assert.Equal(-6, result.SuggestedExposureLog2Seconds);
    }

    [Fact]
    public void MeasuredDimScanAt165RequestsOneBrighterStop()
    {
        var result = Evaluate(Frame(165, 150, 160), current: -7);
        Assert.True(result.IsUsable);
        Assert.Equal(165, result.BrightnessP90);
        Assert.Equal(1, result.Direction);
        Assert.Equal(-6, result.SuggestedExposureLog2Seconds);
    }

    [Fact]
    public void TrustedBoardInsetExcludesBrightSurroundings()
    {
        var pixels = Frame(255, 255, 255);
        Fill(pixels, 85, 55, 250, 205, 130, 120, 140);
        var result = Evaluate(pixels, board: Board);
        Assert.True(result.IsUsable);
        Assert.Equal(140, result.BrightnessP90);
        Assert.Equal(0, result.ClippedFraction);
        Assert.Equal(-6, result.SuggestedExposureLog2Seconds);
    }

    [Theory]
    [InlineData(180)]
    [InlineData(200)]
    [InlineData(235)]
    public void WhiteWithHeadroomKeepsCurrentExposure(byte level)
    {
        var result = Evaluate(Frame(level, level, level));
        Assert.True(result.IsUsable);
        Assert.Equal(0, result.Direction);
        Assert.Null(result.SuggestedExposureLog2Seconds);
    }

    [Fact]
    public void OneClippedColorChannelRequestsLessExposure()
    {
        var result = Evaluate(Frame(250, 160, 165));
        Assert.True(result.IsUsable);
        Assert.Equal(250, result.BrightnessP90);
        Assert.Equal(1, result.ClippedFraction);
        Assert.Equal(-1, result.Direction);
        Assert.Equal(-8, result.SuggestedExposureLog2Seconds);
    }

    [Fact]
    public void SmallBrightSpotCannotMakeAnUnlitFrameUsable()
    {
        var pixels = Frame(8, 6, 10);
        Fill(pixels, 145, 100, 170, 125, 255, 255, 255);
        var result = Evaluate(pixels);
        Assert.False(result.IsUsable);
        Assert.Null(result.SuggestedExposureLog2Seconds);
    }

    [Fact]
    public void IsolatedClippedHighlightDoesNotDarkenAnOtherwiseUsefulWhite()
    {
        var pixels = Frame(200, 200, 200);
        Fill(pixels, 145, 100, 170, 125, 255, 255, 255);
        var result = Evaluate(pixels);
        Assert.True(result.IsUsable);
        Assert.Equal(200, result.BrightnessP90);
        Assert.Null(result.SuggestedExposureLog2Seconds);
    }

    [Theory]
    [InlineData(60, -6, 1, null)]
    [InlineData(60, -7, 1, -6d)]
    [InlineData(30, -6, 1, -5d)]
    [InlineData(100, -8, 1, -7d)]
    [InlineData(60, -7, 2, null)]
    public void BrighterRecommendationsRespectFrameTimeAndDriverSteps(double fps, double current,
        double step, double? expected)
    {
        var result = Evaluate(Frame(140, 140, 140), current, fps, step);
        Assert.True(result.IsUsable);
        Assert.Equal(expected, result.SuggestedExposureLog2Seconds);
        if (result.SuggestedExposureLog2Seconds is { } target)
        {
            Assert.True(Math.Pow(2, target) <= 1 / fps);
            Assert.Equal(Math.Round((target + 13) / step), (target + 13) / step);
        }
    }

    [Fact]
    public void ClippingAtTheDriverMinimumCannotRequestAnInvalidSetting()
    {
        var result = Evaluate(Frame(255, 255, 255), current: -13);
        Assert.True(result.IsUsable);
        Assert.Equal(0, result.Direction);
        Assert.Null(result.SuggestedExposureLog2Seconds);
    }

    [Theory]
    [InlineData(double.NaN, 60, 1)]
    [InlineData(-7, 0, 1)]
    [InlineData(-7, double.PositiveInfinity, 1)]
    [InlineData(-7, 60, 0)]
    [InlineData(-7.5, 60, 1)]
    public void InvalidDriverMetadataCannotSteerExposure(double current, double fps, double step)
    {
        var result = Evaluate(Frame(140, 140, 140), current, fps, step);
        Assert.False(result.IsUsable);
        Assert.Null(result.SuggestedExposureLog2Seconds);
    }

    [Fact]
    public void InvalidTrustedGeometryDoesNotFallBackToUnrelatedBrightPixels()
    {
        PixelPoint[] crossed = [Board[0], Board[2], Board[1], Board[3]];
        Assert.False(Evaluate(Frame(140, 140, 140), board: crossed).IsUsable);
        Assert.False(Evaluate(Frame(140, 140, 140), board: [new(-1, 50), .. Board.Skip(1)]).IsUsable);
    }

    private static CameraWhiteScanExposureResult Evaluate(byte[] pixels, double current = -7,
        double fps = 60, double step = 1, IReadOnlyList<PixelPoint>? board = null) =>
        CameraWhiteScanExposure.Evaluate(Width, Height, Stride, pixels, current, -13, -3, step, fps, board);

    private static byte[] Frame(byte red, byte green, byte blue)
    {
        var pixels = new byte[Stride * Height];
        Array.Fill(pixels, (byte)255); // Non-image row padding must not affect the measurement.
        Fill(pixels, 0, 0, Width, Height, red, green, blue);
        return pixels;
    }

    private static void Fill(byte[] pixels, int x0, int y0, int x1, int y1, byte red, byte green, byte blue)
    {
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int offset = y * Stride + x * 4;
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }
    }
}
