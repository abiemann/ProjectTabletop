using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class AudioClickTraceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DisabledByDefaultAndDisablingErasesHistory()
    {
        var trace = new AudioClickTrace();
        trace.Add(Origin, .8, .1, true);
        Assert.False(trace.Enabled);
        Assert.Empty(trace.CopySamples());
        trace.SetEnabled(true);
        trace.Add(Origin, .8, .1, true);
        Assert.Single(trace.CopySamples());
        trace.SetEnabled(false);
        Assert.Empty(trace.CopySamples());
        trace.SetEnabled(true);
        Assert.Empty(trace.CopySamples());
    }

    [Fact]
    public void WrappedHistoryContainsOnlyLatestWindowsInCaptureOrder()
    {
        var trace = new AudioClickTrace();
        trace.SetEnabled(true);
        for (int i = 0; i < AudioClickTrace.Capacity * 2 + 7; i++)
            trace.Add(Origin.AddMilliseconds(i * 5), i, i / 10.0, i % 2 == 0);
        var windows = trace.CopySamples();
        Assert.Equal(AudioClickTrace.Capacity, windows.Length);
        for (int i = 0; i < windows.Length; i++)
        {
            int sample = AudioClickTrace.Capacity + 7 + i;
            Assert.Equal(new(Origin.AddMilliseconds(sample * 5), sample, sample / 10.0, sample % 2 == 0), windows[i]);
        }
    }

    [Fact]
    public void CopiesAreIndependentAndRepeatedEnableDoesNotEraseCapture()
    {
        var trace = new AudioClickTrace();
        trace.SetEnabled(true);
        trace.Add(Origin, .8, .1, true);
        var first = trace.CopySamples();
        trace.SetEnabled(true);
        trace.Add(Origin.AddMilliseconds(5), .1, .02, false);
        var second = trace.CopySamples();
        first[0] = default;
        Assert.Equal(2, second.Length);
        Assert.Equal(Origin, second[0].At);
        Assert.True(second[0].Candidate);
        Assert.False(second[1].Candidate);
        trace.Clear();
        Assert.True(trace.Enabled);
        Assert.Empty(trace.CopySamples());
        Assert.Equal(2, second.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioWindowWritesDoNotAllocate(bool enabled)
    {
        var trace = new AudioClickTrace();
        trace.SetEnabled(enabled);
        for (int i = 0; i < 1000; i++) trace.Add(Origin, .8, .1, true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) trace.Add(Origin, .8, .1, i % 2 == 0);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
