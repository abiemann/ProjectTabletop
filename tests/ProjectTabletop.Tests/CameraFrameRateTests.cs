using ProjectTabletop.App.Camera;

namespace ProjectTabletop.Tests;

public class CameraFrameRateTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(100)]
    public void RequestedRatePrefers1080pWithoutChoosingCostly4K(int requested)
    {
        CameraVideoMode[] modes = [new(3840, 2160, requested), new(1280, 720, requested),
            new(1920, 1080, requested), new(1920, 1080, 25)];

        Assert.Equal(new CameraVideoMode(1920, 1080, requested), Ordered(modes, requested)[0]);
    }

    [Fact]
    public void Uses720pToReachRequestedRateBeforeFallingBackTo1080p30()
    {
        CameraVideoMode[] modes = [new(1920, 1080, 30), new(1280, 720, 60), new(3840, 2160, 60)];

        Assert.Equal([modes[1], modes[2], modes[0]], Ordered(modes, 60));
    }

    [Theory]
    [InlineData(30, 30000d / 1001)]
    [InlineData(60, 60000d / 1001)]
    [InlineData(100, 99.9)]
    public void FractionalRatesCountAsMatchingAndPrefer1080p(int requested, double fractional)
    {
        CameraVideoMode[] modes = [new(1280, 720, requested), new(1920, 1080, fractional)];

        Assert.Equal(modes[1], Ordered(modes, requested)[0]);
        Assert.Contains(requested, CameraFrameRatePolicy.SupportedRequestedRates(modes));
    }

    [Fact]
    public void Unavailable100FallsBackToHighestLowerRate()
    {
        CameraVideoMode[] modes = [new(1920, 1080, 30), new(1920, 1080, 60), new(1920, 1080, 25)];

        Assert.Equal([modes[1], modes[0], modes[2]], Ordered(modes, 100));
        Assert.Equal([30, 60], CameraFrameRatePolicy.SupportedRequestedRates(modes));
    }

    [Fact]
    public void Missing60Prefers30FallbackOverUnrequested100()
    {
        CameraVideoMode[] modes = [new(1920, 1080, 100), new(1920, 1080, 30)];

        Assert.Equal([modes[1], modes[0]], Ordered(modes, 60));
    }

    [Fact]
    public void RejectedModeCanTryAnotherSourceAtSameRateBeforeLowerRate()
    {
        var rejected = (Source: "record", Mode: new CameraVideoMode(1920, 1080, 100));
        var working = (Source: "preview", Mode: new CameraVideoMode(1280, 720, 100));
        var fallback = (Source: "record", Mode: new CameraVideoMode(1920, 1080, 60));

        var candidates = CameraFrameRatePolicy.Order([rejected, fallback, working], value => value.Mode, 100);

        Assert.Equal([rejected, working, fallback], candidates);
        Assert.Equal(working, candidates.Skip(1).First());
    }

    [Theory]
    [InlineData(640, 480, 100)]
    [InlineData(1920, 1080, 120)]
    [InlineData(1920, 1080, 19)]
    [InlineData(4096, 2160, 60)]
    [InlineData(1920, 1080, double.NaN)]
    public void UnsupportedModesCannotAffectSelectionOrCapabilities(int width, int height, double fps)
    {
        var mode = new CameraVideoMode(width, height, fps);

        Assert.Empty(Ordered([mode], 100));
        Assert.Empty(CameraFrameRatePolicy.SupportedRequestedRates([mode]));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(100)]
    public void SelectedRateProcessesEveryFrameIncludingDeliveryJitter(int rate)
    {
        var budget = new CameraCopyRateBudget(CameraFrameRatePolicy.CopyRateLimit(rate, rate));
        var now = 0d;

        for (var i = 0; i < rate * 10; i++)
        {
            // A late frame followed by a quick frame must not halve the working rate.
            now += (i % 2 == 0 ? 1.45 : .55) / rate;
            Assert.True(budget.TryTake(now));
        }
    }

    [Fact]
    public void Shared100FpsCameraIsStillCopyLimitedWhenUserSelects30()
    {
        var budget = new CameraCopyRateBudget(CameraFrameRatePolicy.CopyRateLimit(30, 100));
        var copied = Enumerable.Range(0, 1000).Count(i => budget.TryTake(i / 100d));

        Assert.InRange(copied, 311, 315);
    }

    [Fact]
    public void Unsupported100RequestWith30FallbackUses30CopyBudget()
    {
        Assert.Equal(31.25, CameraFrameRatePolicy.CopyRateLimit(100, 30), 10);
        Assert.Equal(104.16666666666667, CameraFrameRatePolicy.CopyRateLimit(100, 100), 10);
    }

    [Fact]
    public void IdleTimeCannotAccumulateAnUnboundedBurst()
    {
        var budget = new CameraCopyRateBudget(100);
        Assert.True(budget.TryTake(0));
        Assert.True(budget.TryTake(60));
        Assert.True(budget.TryTake(60));
        Assert.False(budget.TryTake(60));
    }

    [Fact]
    public void CalibrationBypassesCopyCapWithoutLeavingFutureDebt()
    {
        var budget = new CameraCopyRateBudget(30);
        for (var i = 0; i < 100; i++) Assert.True(budget.TryTake(0, captureEveryFrame: true));

        Assert.False(budget.TryTake(0));
        Assert.True(budget.TryTake(1d / 30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(120)]
    public void RejectsInvalidRequestedRates(int requested)
    {
        Assert.False(CameraFrameRatePolicy.IsRequestedRate(requested));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ordered([], requested));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraFrameRatePolicy.CopyRateLimit(requested, 30));
    }

    private static IReadOnlyList<CameraVideoMode> Ordered(CameraVideoMode[] modes, int requested) =>
        CameraFrameRatePolicy.Order(modes, mode => mode, requested);
}
