using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class AudioClickExtendedImpulseTests
{
    private const int Rate = 48000;
    private static readonly DateTimeOffset Origin = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewlyLearnedImpulseCanEndAt125WithoutExtendingConfirmationOrShotFreshness()
    {
        var detector = Detector();
        var events = Replay(detector, 25);
        var click = Assert.Single(events);
        Assert.Equal(Origin.AddMilliseconds(900), click.Onset);
        Assert.Equal(135, (click.Confirmed - click.Onset).TotalMilliseconds);
        Assert.Equal(125, detector.Analysis.LastCompletedTransient!.Value.Features.DurationMs);
        Assert.Equal(0, detector.Analysis.TimedOutCandidates);
        var gate = new DuckHuntAimGate();
        gate.Reset(Origin);
        Assert.NotNull(gate.EvaluateSnapshot(new(.5, .5), click.Confirmed, click.Onset,
            click.Confirmed, click.Confirmed.AddMilliseconds(5)));
    }

    [Theory]
    [InlineData(25, 1, .15, 0)] // Previous learning remains unchanged.
    [InlineData(25, 2, .7, 0)]  // Sustained energy, without a dominant impulse.
    [InlineData(26, 2, .15, 0)] // A longer core cannot be truncated to the limit.
    [InlineData(40, 2, .15, 0)]
    [InlineData(25, 2, .15, .0001)] // Preserve the original confirmation deadline.
    [InlineData(25, 2, .15, 5)]
    public void ExtraWindowRejectsUnqualifiedSoundsAndLateConfirmation(
        int blocks, int version, double tailAmplitude, double delayMs)
    {
        var detector = Detector(version);
        Assert.Empty(Replay(detector, blocks, tailAmplitude, delayMs));
        Assert.Equal(0, detector.Analysis.CompletedTransients);
        Assert.Equal(1, detector.Analysis.TimedOutCandidates);
    }

    [Fact]
    public void ShortImpulseTimingAndEchoSuppressionAreUnchanged()
    {
        var oldEvents = Replay(Detector(1), 3, echo: true);
        var newEvents = Replay(Detector(2), 3, echo: true);
        Assert.Equal(oldEvents, newEvents);
        var click = Assert.Single(newEvents);
        Assert.Equal(25, (click.Confirmed - click.Onset).TotalMilliseconds);
    }

    [Fact]
    public void ExtendedImpulseStillNeedsTheLearnedFrequencyAndLevel()
    {
        var detector = Detector();
        detector.SetProfile(Profile() with { MinimumHighFrequencyRatio = 0, MaximumHighFrequencyRatio = .1 });
        Assert.Empty(Replay(detector, 25));
        Assert.Equal(1, detector.Analysis.RejectedByProfile);
        detector.SetProfile(Profile() with { MinimumRms = .4 });
        Assert.Empty(Replay(detector, 25));
        Assert.Equal(1, detector.Analysis.RejectedByProfile);
    }

    private static AudioClickProfile Profile(int version = 2) => new()
    {
        Version = version, MinimumPeak = .1, MaximumPeak = 1,
        MinimumRms = .01, MaximumRms = 1, MinimumDurationMs = 1,
        MaximumDurationMs = version == 2 ? 125 : 120,
        MinimumHighFrequencyRatio = 0, MaximumHighFrequencyRatio = 1
    };

    private static AudioClickDetector Detector(int version = 2)
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(Profile(version));
        return detector;
    }

    private static List<(DateTimeOffset Onset, DateTimeOffset Confirmed)> Replay(
        AudioClickDetector detector, int soundBlocks, double tailAmplitude = .15,
        double confirmationDelayMs = 0, bool echo = false)
    {
        var events = new List<(DateTimeOffset, DateTimeOffset)>();
        var samples = new float[Rate / 200];
        for (int block = 0; block < 300; block++)
        {
            double amplitude = block == 180 ? .7 : block > 180 && block < 180 + soundBlocks
                ? tailAmplitude : echo && block == 220 ? .7 : .0003;
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)amplitude * (i % 2 == 0 ? 1 : -1);
            double delay = block >= 180 + soundBlocks ? confirmationDelayMs : 0;
            var at = Origin.AddMilliseconds(block * 5).AddTicks((long)Math.Round(delay * TimeSpan.TicksPerMillisecond));
            if (detector.Process(samples, Rate, 1, at) is { } click)
                events.Add((click, at.AddMilliseconds(5)));
        }
        return events;
    }
}
