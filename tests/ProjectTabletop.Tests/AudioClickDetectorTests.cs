using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class AudioClickDetectorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private const int Rate = 48000;

    [Fact]
    public void SilenceAndSteadyNoiseNeverFire()
    {
        Assert.Empty(Run(new AudioClickDetector(), 400, _ => 0));
        Assert.Empty(Run(new AudioClickDetector(), 400, _ => .35f));
        Assert.Empty(Run(new AudioClickDetector(), 400, block => block < 160 ? .01f : .5f));
    }

    [Fact]
    public void ShortImpulseReportsOriginalOnsetOnlyOnce()
    {
        var detector = new AudioClickDetector();
        var events = Run(detector, 300, block => block is >= 180 and < 183 ? .7f : .003f);
        Assert.Single(events);
        Assert.InRange((events[0] - Origin).TotalMilliseconds, 899, 906);
        Assert.True(detector.Diagnostics.Ready);
    }

    [Fact]
    public void ClickEchoWithinRefractoryDoesNotShootTwice()
    {
        var events = Run(new AudioClickDetector(), 300,
            block => block is 180 or 190 or 200 or 250 ? .7f : .003f);
        Assert.Equal(2, events.Count);
        Assert.InRange((events[1] - events[0]).TotalMilliseconds, 349, 356);
    }

    [Fact]
    public void StartupAndSlowRiseDoNotBecomeShots()
    {
        Assert.Empty(Run(new AudioClickDetector(), 300,
            block => block is 10 or 30 or 60 ? .8f : .003f));
        Assert.Empty(Run(new AudioClickDetector(), 350,
            block => block < 160 ? .003f : (float)Math.Min(.65, (block - 160) * .005)));
    }

    [Fact]
    public void ResetAndStreamGapDiscardPendingClick()
    {
        var detector = new AudioClickDetector();
        Assert.Empty(Run(detector, 181, block => block == 180 ? .7f : .003f));
        detector.Reset();
        Assert.False(detector.Diagnostics.Ready);
        Assert.Empty(Run(detector, 100, _ => .003f, 181));

        detector = new AudioClickDetector();
        Assert.Empty(Run(detector, 181, block => block == 180 ? .7f : .003f));
        Assert.Empty(Run(detector, 100, _ => .003f, 300));
    }

    [Fact]
    public void ModerateAmbientNoiseStillAllowsDistinctClick()
    {
        Assert.Single(Run(new AudioClickDetector(), 300,
            block => block is >= 180 and < 183 ? .7f : .035f));
    }

    [Fact]
    public void ArbitraryPacketBoundariesAndStereoDoNotChangeResult()
    {
        var detector = new AudioClickDetector();
        var samples = new float[Rate * 2 * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            double seconds = (double)(i / 2) / Rate;
            samples[i] = seconds is >= .9 and < .915 ? .7f : .003f;
        }
        var events = new List<DateTimeOffset>();
        for (int offset = 0; offset < samples.Length; offset += 254)
        {
            int length = Math.Min(254, samples.Length - offset);
            var click = detector.Process(samples.AsSpan(offset, length), Rate, 2,
                Origin.AddSeconds((double)offset / (Rate * 2)));
            if (click is { } at) events.Add(at);
        }
        Assert.Single(events);
        Assert.InRange((events[0] - Origin).TotalMilliseconds, 899, 906);
    }

    [Fact]
    public void ClickStraddlingAWindowInNoiseUsesTheWholeBurst()
    {
        var detector = new AudioClickDetector();
        Assert.Empty(Run(detector, 180, _ => .035f));
        var partial = Enumerable.Repeat(.035f, Rate / 200).ToArray();
        for (int i = partial.Length - 4; i < partial.Length; i++) partial[i] = .8f;
        Assert.Null(detector.Process(partial, Rate, 1, Origin.AddMilliseconds(900)));
        var events = Run(detector, 60, block => block < 3 ? .8f : .035f, 181);
        Assert.Single(events);
        Assert.InRange((events[0] - Origin).TotalMilliseconds, 899, 906);
    }

    [Fact]
    public void DiagnosticsRetainCompletedAndTimedOutFeaturesWithoutReportingATimeoutAsAClick()
    {
        var detector = new AudioClickDetector();
        var events = Run(detector, 400, block => block is >= 180 and < 220 or >= 300 and < 303 ? .7f : .003f);
        Assert.Single(events);
        Assert.Equal(1, detector.Analysis.TimedOutCandidates);
        Assert.Equal(1, detector.Analysis.CompletedTransients);
        var timedOut = detector.Analysis.LastTimedOutCandidate!.Value;
        Assert.InRange((timedOut.At - Origin).TotalMilliseconds, 899, 906);
        Assert.InRange(timedOut.Features.DurationMs, 120, 131);
        Assert.InRange(timedOut.Features.Peak, .69, .71);
        Assert.InRange(timedOut.Features.Rms, .69, .71);
        var complete = detector.Analysis.LastCompletedTransient!.Value;
        Assert.Equal(events[0], complete.At);
        Assert.InRange(complete.Features.DurationMs, 14, 16);
        Assert.Null(detector.LastTransient);
        detector.Reset();
        Assert.Equal(default, detector.Analysis);
    }

    [Theory]
    [InlineData(23)]
    [InlineData(24)]
    public void LearnedClickCanFinishItsQuietConfirmationAfterAFullSoundWindow(int soundBlocks)
    {
        var detector = LearnedDetector();
        var confirmed = new List<DateTimeOffset>();
        var events = Run(detector, 240,
            block => block >= 180 && block < 180 + soundBlocks ? .7f : .0003f,
            confirmedAt: confirmed);
        Assert.Equal(Origin.AddMilliseconds(900), Assert.Single(events));
        Assert.Equal(soundBlocks * 5 + 10, (Assert.Single(confirmed) - events[0]).TotalMilliseconds);
        Assert.InRange((confirmed[0] - events[0]).TotalMilliseconds, 0, 135);
        Assert.Equal(soundBlocks * 5, detector.Analysis.LastCompletedTransient!.Value.Features.DurationMs);
        Assert.Equal(0, detector.Analysis.TimedOutCandidates);
        var gate = new DuckHuntAimGate();
        gate.Reset(Origin);
        Assert.NotNull(gate.EvaluateSnapshot(new(.5, .5), confirmed[0], events[0], confirmed[0],
            confirmed[0].AddMilliseconds(5)));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(27)]
    [InlineData(40)]
    public void LearnedConfirmationGraceNeverTruncatesALongerSoundIntoAnAcceptedClick(int soundBlocks)
    {
        var detector = LearnedDetector();
        Assert.Empty(Run(detector, 300,
            block => block >= 180 && block < 180 + soundBlocks ? .7f : .0003f));
        Assert.Equal(0, detector.Analysis.CompletedTransients);
        Assert.Equal(1, detector.Analysis.TimedOutCandidates);
        Assert.True(detector.Analysis.LastTimedOutCandidate!.Value.Features.DurationMs > 120);
    }

    [Theory]
    [InlineData(135, true)]
    [InlineData(135.0001, false)]
    [InlineData(140, false)]
    public void LearnedConfirmationDeadlineUsesTheQuietWindowEnd(double confirmationEndMs, bool expected)
    {
        var detector = LearnedDetector();
        Assert.Empty(Run(detector, 204, block => block >= 180 ? .7f : .0003f));
        var quiet = Enumerable.Repeat(.0003f, Rate / 200).ToArray();
        var onset = Origin.AddMilliseconds(900);
        // The sound core is exactly 120 ms. A small callback timestamp gap
        // delays its two quiet windows without extending the sound itself.
        Assert.Null(detector.Process(quiet, Rate, 1, onset.AddMilliseconds(125)));
        var click = detector.Process(quiet, Rate, 1, onset.AddMilliseconds(confirmationEndMs - 5));
        Assert.Equal(expected, click.HasValue);
        if (expected)
        {
            Assert.Equal(onset, click);
            Assert.Equal(120, detector.Analysis.LastCompletedTransient!.Value.Features.DurationMs);
            Assert.Equal(0, detector.Analysis.TimedOutCandidates);
        }
        else
        {
            Assert.Equal(0, detector.Analysis.CompletedTransients);
            Assert.Equal(1, detector.Analysis.TimedOutCandidates);
        }
    }

    [Theory]
    [InlineData(.6)]
    [InlineData(4.4)]
    public void LearnedSoundDurationUsesSamplesDespiteLateCallbackTimestamps(double delayMs)
    {
        var detector = LearnedDetector();
        Assert.Empty(Run(detector, 180, _ => .0003f));
        var loud = Enumerable.Range(0, Rate / 200).Select(i => i % 2 == 0 ? .7f : -.7f).ToArray();
        var quiet = Enumerable.Repeat(.0003f, Rate / 200).ToArray();
        var onset = Origin.AddMilliseconds(900);
        for (int window = 0; window < 24; window++)
        {
            double callbackDelay = window == 23 ? delayMs : 0;
            Assert.Null(detector.Process(loud, Rate, 1, onset.AddMilliseconds(window * 5 + callbackDelay)));
        }
        Assert.Null(detector.Process(quiet, Rate, 1, onset.AddMilliseconds(120 + delayMs)));
        var click = detector.Process(quiet, Rate, 1, onset.AddMilliseconds(125 + delayMs));
        Assert.Equal(onset, click);
        Assert.Equal(120, detector.Analysis.LastCompletedTransient!.Value.Features.DurationMs);
        Assert.Equal(0, detector.Analysis.TimedOutCandidates);
        var confirmedAt = onset.AddMilliseconds(130 + delayMs);
        Assert.InRange((confirmedAt - onset).TotalMilliseconds, 130, 135);
        var gate = new DuckHuntAimGate();
        gate.Reset(Origin);
        Assert.NotNull(gate.EvaluateSnapshot(new(.5, .5), confirmedAt, onset, confirmedAt,
            confirmedAt.AddMilliseconds(5)));
    }

    [Fact]
    public void LearnedSoundDurationRejectsLongPcmEvenWhenHostTimestampsAreCompressed()
    {
        var detector = LearnedDetector();
        Assert.Empty(Run(detector, 180, _ => .0003f));
        var loud = Enumerable.Range(0, Rate / 200).Select(i => i % 2 == 0 ? .7f : -.7f).ToArray();
        var quiet = Enumerable.Repeat(.0003f, Rate / 200).ToArray();
        var onset = Origin.AddMilliseconds(900);
        for (int window = 0; window < 25; window++)
        {
            // Two buffered quanta can share a callback timestamp. Their samples
            // still represent 125 ms of sound, even though host time says 120 ms.
            double timestampMs = Math.Min(window, 23) * 5;
            Assert.Null(detector.Process(loud, Rate, 1, onset.AddMilliseconds(timestampMs)));
        }
        Assert.Null(detector.Process(quiet, Rate, 1, onset.AddMilliseconds(120)));
        Assert.Null(detector.Process(quiet, Rate, 1, onset.AddMilliseconds(125)));
        Assert.Equal(0, detector.Analysis.CompletedTransients);
        Assert.Equal(1, detector.Analysis.TimedOutCandidates);
    }

    [Fact]
    public void LearnedGraceDoesNotDelayShortClicksOrChangeTheUntrainedDeadline()
    {
        var detector = LearnedDetector();
        var confirmed = new List<DateTimeOffset>();
        var events = Run(detector, 240, block => block is >= 180 and < 183 ? .7f : .0003f,
            confirmedAt: confirmed);
        Assert.Equal(Origin.AddMilliseconds(900), Assert.Single(events));
        Assert.Equal(25, (Assert.Single(confirmed) - events[0]).TotalMilliseconds);

        var untrained = new AudioClickDetector();
        Assert.Empty(Run(untrained, 240, block => block is >= 180 and < 204 ? .7f : .003f));
        Assert.Equal(1, untrained.Analysis.TimedOutCandidates);
    }

    private static AudioClickDetector LearnedDetector()
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(new AudioClickProfile
        {
            MinimumPeak = .1, MaximumPeak = 1, MinimumRms = .05, MaximumRms = 1,
            MinimumDurationMs = 1, MaximumDurationMs = 120,
            MinimumHighFrequencyRatio = 0, MaximumHighFrequencyRatio = 1
        });
        return detector;
    }

    private static List<DateTimeOffset> Run(AudioClickDetector detector, int blocks,
        Func<int, float> amplitude, int startBlock = 0, List<DateTimeOffset>? confirmedAt = null)
    {
        var events = new List<DateTimeOffset>();
        var samples = new float[Rate / 200];
        for (int block = 0; block < blocks; block++)
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = amplitude(block) * (i % 2 == 0 ? 1 : -1);
            var click = detector.Process(samples, Rate, 1, Origin.AddMilliseconds((startBlock + block) * 5));
            if (click is { } at)
            {
                events.Add(at);
                confirmedAt?.Add(Origin.AddMilliseconds((startBlock + block + 1) * 5));
            }
        }
        return events;
    }
}
