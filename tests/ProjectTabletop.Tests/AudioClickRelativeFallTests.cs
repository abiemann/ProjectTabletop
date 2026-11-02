using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class AudioClickRelativeFallTests
{
    private const int Rate = 48000, WindowSamples = Rate / 200;
    private static readonly DateTimeOffset Origin = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static AudioClickProfile Profile => new()
    {
        Version = 2, MinimumPeak = .322108256816864, MaximumPeak = 1,
        MinimumRms = .0422035350304387, MaximumRms = .1713615829596438,
        MinimumDurationMs = 37.5, MaximumDurationMs = 125,
        MinimumHighFrequencyRatio = .4, MaximumHighFrequencyRatio = 1
    };

    // Numerical 5 ms peak/RMS envelopes from two rejected 20 ms triggers on
    // 2026-11-01. PCM below is deliberately SYNTHETIC, not the recorded waveform.
    // Its spectral range is broad because phase/frequency cannot be reconstructed
    // from level telemetry. The measured live frequency ratios were .58394/.58415.
    private static (double Peak, double Rms)[] Envelope(bool second) => second
        ? [(.2934, .0441), (.9932, .3181), (.4107, .1155), (.2509, .0733),
           (.1481, .0554), (.1380, .0508), (.1966, .0710), (.1375, .0471), (.1635, .0471)]
        : [(.0511, .0175), (.9920, .2946), (.3458, .1098), (.1914, .0673),
           (.1417, .0499), (.1160, .0438), (.1471, .0533), (.1345, .0431), (.1068, .0373)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedLevelNotchAllowsPromptReboundToFinishARealDuration(bool second)
    {
        var run = Run(Profile, Envelope(second));
        Assert.Equal(Origin.AddSeconds(1), Assert.Single(run.Clicks));
        var completed = Assert.Single(run.Completed);
        Assert.Equal(35, completed.Features.DurationMs);
        Assert.Equal(Origin.AddMilliseconds(1045), completed.ReportedAt);
        Assert.Equal(0, run.Detector.Analysis.RejectedByProfile);
    }

    [Fact]
    public void AlreadyMatchingClickHasNoAdditionalObservationDelay()
    {
        var run = Run(Profile with { MinimumDurationMs = 15 }, Envelope(false));
        Assert.Single(run.Clicks);
        var completed = Assert.Single(run.Completed);
        Assert.Equal(20, completed.Features.DurationMs);
        Assert.Equal(Origin.AddMilliseconds(1030), completed.ReportedAt);
    }

    [Fact]
    public void TrueQuietCompletesAndRejectsAShortSoundImmediately()
    {
        var run = Run(Profile, Envelope(false)[..4]);
        Assert.Empty(run.Clicks);
        var completed = Assert.Single(run.Completed);
        Assert.Equal(20, completed.Features.DurationMs);
        Assert.Equal(Origin.AddMilliseconds(1030), completed.ReportedAt);
    }

    [Fact]
    public void TrueQuietDuringObservationEndsTheGraceWithoutPaddingDuration()
    {
        var run = Run(Profile, Envelope(false)[..6]);
        Assert.Empty(run.Clicks);
        var completed = Assert.Single(run.Completed);
        Assert.Equal(20, completed.Features.DurationMs);
        Assert.Equal(Origin.AddMilliseconds(1035), completed.ReportedAt);
    }

    [Fact]
    public void NoReboundExpiresAfterOnlyFifteenMillisecondsAndNeverBecomesAShot()
    {
        var envelope = Envelope(false)[..6].Concat(Enumerable.Repeat((.06, .02), 12)).ToArray();
        var run = Run(Profile, envelope);
        Assert.Empty(run.Clicks);
        var completed = Assert.Single(run.Completed);
        Assert.Equal(20, completed.Features.DurationMs);
        Assert.Equal(Origin.AddMilliseconds(1045), completed.ReportedAt);
    }

    [Fact]
    public void AReboundCannotStartASecondGraceForTheSameCandidate()
    {
        var run = Run(Profile with { MinimumDurationMs = 80 }, Envelope(false));
        Assert.Empty(run.Clicks);
        var completed = Assert.Single(run.Completed);
        Assert.Equal(35, completed.Features.DurationMs);
        Assert.Equal(Origin.AddMilliseconds(1045), completed.ReportedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrongLoudnessOrFrequencyDoesNotGetObservationGrace(bool loudness)
    {
        var profile = loudness ? Profile with { MaximumPeak = .8 } :
            Profile with { MinimumHighFrequencyRatio = 0, MaximumHighFrequencyRatio = .05 };
        var run = Run(profile, Envelope(false));
        Assert.Empty(run.Clicks);
        Assert.Equal(Origin.AddMilliseconds(1030), Assert.Single(run.Completed).ReportedAt);
    }

    [Fact]
    public void AShortSoundWithoutASharpCrestDoesNotGetObservationGrace()
    {
        (double Peak, double Rms)[] envelope =
            [(.99, .7), (.99, .7), (.99, .7), (.99, .7), (.1, .03), (.1, .03), (.12, .1)];
        var run = Run(Profile with { MaximumRms = 1 }, envelope);
        Assert.Empty(run.Clicks);
        Assert.Equal(Origin.AddMilliseconds(1030), Assert.Single(run.Completed).ReportedAt);
    }

    [Fact]
    public void OriginalHostDeadlineStillStopsAnOtherwisePromisingRebound()
    {
        var run = Run(Profile, Envelope(false), timestampDelayAfterWindow: 4);
        Assert.Empty(run.Clicks);
        Assert.Empty(run.Completed);
        Assert.Equal(1, run.Detector.Analysis.TimedOutCandidates);
    }

    [Fact]
    public void RecoveredClickKeepsItsOriginalOnsetAndSuppressesItsEcho()
    {
        var envelope = Envelope(false).Concat(Enumerable.Repeat((.0003, .0003), 11))
            .Concat(Envelope(false)).ToArray();
        var run = Run(Profile, envelope);
        Assert.Equal(Origin.AddSeconds(1), Assert.Single(run.Clicks));
        Assert.Single(run.Completed);
    }

    [Fact]
    public void PendingGraceCannotSurviveResetOrProfileReplacement()
    {
        var run = Run(Profile, Envelope(false)[..6], trailingQuietWindows: 0);
        Assert.True(run.Detector.Diagnostics.Candidate);
        Assert.Empty(run.Completed);
        run.Detector.SetProfile(Profile);
        Assert.False(run.Detector.Diagnostics.Candidate);
        var tail = Samples(Envelope(false)[6..]);
        Assert.Null(run.Detector.Process(tail, Rate, 1, Origin.AddMilliseconds(1030)));
        Assert.Equal(0, run.Detector.Analysis.CompletedTransients);
    }

    private sealed record Completion(DateTimeOffset ReportedAt, AudioClickFeatures Features);
    private sealed record Replay(AudioClickDetector Detector, List<DateTimeOffset> Clicks, List<Completion> Completed);

    private static Replay Run(AudioClickProfile profile, (double Peak, double Rms)[] envelope,
        int timestampDelayAfterWindow = int.MaxValue, int trailingQuietWindows = 80)
    {
        var detector = new AudioClickDetector(); detector.SetProfile(profile);
        var clicks = new List<DateTimeOffset>(); var completed = new List<Completion>();
        var quiet = Samples([(.0003, .0003)]);
        for (int window = 0; window < 200; window++)
            detector.Process(quiet, Rate, 1, Origin.AddMilliseconds(window * 5));
        float[] samples = Samples(envelope);
        int total = envelope.Length + trailingQuietWindows;
        for (int window = 0; window < total; window++)
        {
            var at = Origin.AddMilliseconds(1000 + window * 5 + (window >= timestampDelayAfterWindow ? 100 : 0));
            ReadOnlySpan<float> pcm = window < envelope.Length ? samples.AsSpan(window * WindowSamples, WindowSamples) : quiet;
            if (detector.Process(pcm, Rate, 1, at) is { } click) clicks.Add(click);
            if (detector.LastTransient is { } transient)
                completed.Add(new(at.AddMilliseconds(5), transient.Features));
        }
        return new(detector, clicks, completed);
    }

    private static float[] Samples((double Peak, double Rms)[] envelope)
    {
        var samples = new float[envelope.Length * WindowSamples];
        for (int window = 0; window < envelope.Length; window++)
        {
            var (peak, rms) = envelope[window];
            double level = Math.Sqrt(Math.Max(0, (rms * rms * WindowSamples - peak * peak) / (WindowSamples - 1)));
            samples[window * WindowSamples] = (float)peak;
            for (int i = 1; i < WindowSamples; i++)
                samples[window * WindowSamples + i] = (float)(level * (i % 2 == 0 ? 1 : -1));
        }
        return samples;
    }
}
