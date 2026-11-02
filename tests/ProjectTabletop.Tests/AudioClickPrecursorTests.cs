using System.IO.Compression;
using System.Runtime.InteropServices;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class AudioClickPrecursorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private const int Rate = 48000, WindowSamples = Rate / 200;
    private static AudioClickProfile RecordedProfile => new()
    {
        Version = 2, TrainingNoiseRms = .0027049959972244952,
        MinimumPeak = .322108256816864, MaximumPeak = 1,
        MinimumRms = .0422035350304387, MaximumRms = .1713615829596438,
        MinimumDurationMs = 37.5, MaximumDurationMs = 125,
        MinimumHighFrequencyRatio = .4745169725593068, MaximumHighFrequencyRatio = .7145169725593068
    };
    private static AudioClickProfile SyntheticProfile => new()
    {
        MinimumPeak = .3, MaximumPeak = 1, MinimumRms = .1, MaximumRms = 1,
        MinimumDurationMs = 20, MaximumDurationMs = 120,
        MinimumHighFrequencyRatio = .5, MaximumHighFrequencyRatio = 1
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void RecordedTriggerAndItsQuietMechanismProduceExactlyOneShot(int example)
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(RecordedProfile);
        float[] clip = ReadClip(example);
        float[] samples = new float[Rate + clip.Length + Rate / 2];
        clip.CopyTo(samples, Rate);
        var clicks = Replay(detector, samples);
        // An isolated clip has a quieter synthetic warmup than the full room
        // recording, so its ordinary onset may include a little more lead-in.
        Assert.InRange((Assert.Single(clicks) - Origin).TotalSeconds, 1.12, 1.17);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void RecordedMechanicalTailCannotAddASecondShotDuringTheRealClickCooldown(int example)
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(RecordedProfile);
        float[] core = ReadClip(5)[(int)(Rate * .1)..(int)(Rate * .23)];
        float[] tail = ReadClip(example)[(int)(Rate * .28)..];
        float[] samples = new float[Rate + core.Length + tail.Length + Rate / 2];
        core.CopyTo(samples, Rate);
        tail.CopyTo(samples, Rate + core.Length);
        Assert.Single(Replay(detector, samples));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(14)]
    public void BelowBothLearnedLevelsRearmsForTheRealClickWithoutWaiting(int precursorWindows)
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(SyntheticProfile);
        int clickWindow = 180 + precursorWindows + 4;
        float[] samples = Signal(350, window => window >= 180 && window < 180 + precursorWindows ? .03f :
            window >= clickWindow && window < clickWindow + 8 ? .7f : .0003f);
        var clicks = Replay(detector, samples);
        Assert.Equal(Origin.AddMilliseconds(clickWindow * 5), Assert.Single(clicks));
        Assert.Equal(1, detector.Analysis.RejectedByProfile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectionBelowOnlyOneLearnedLevelRetainsCooldown(bool belowPeakOnly)
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(belowPeakOnly ? SyntheticProfile : SyntheticProfile with { MinimumPeak = .02 });
        // .1 RMS is above the usual RMS floor but below its peak floor; .03 is
        // above the alternate peak floor but below its RMS floor.
        var clicks = Replay(detector, Signal(350, w => w == 180 ? belowPeakOnly ? .1f : .03f :
            w is >= 186 and < 194 or >= 270 and < 278 ? .7f : .0003f));
        Assert.Equal(Origin.AddMilliseconds(1350), Assert.Single(clicks));
    }

    [Fact]
    public void AcceptedClickStillSuppressesTinyMechanismAndLoudEcho()
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(SyntheticProfile);
        var clicks = Replay(detector, Signal(350, w => w is >= 180 and < 188 or >= 210 and < 218 or
            >= 270 and < 278 ? .7f : w == 204 ? .03f : .0003f));
        Assert.Equal(new[] { Origin.AddMilliseconds(900), Origin.AddMilliseconds(1350) }, clicks);
    }

    [Fact]
    public void LoudShortRejectionAndSustainedNoiseNeverUseWeakPrecursorRecovery()
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(SyntheticProfile);
        Assert.Empty(Replay(detector, Signal(350,
            w => w == 180 || w is >= 186 and < 194 ? .7f : .0003f)));
        detector = new AudioClickDetector();
        detector.SetProfile(SyntheticProfile);
        Assert.Empty(Replay(detector, Signal(350, w => w == 180 ? .03f : w >= 186 ? .7f : .0003f)));
        Assert.True(detector.Analysis.TimedOutCandidates > 0);
    }

    [Fact]
    public void RoomLikeLowFrequencySoundRemainsRejectedAfterQuietPrecursor()
    {
        var detector = new AudioClickDetector();
        detector.SetProfile(SyntheticProfile);
        float[] samples = Signal(350, w => w == 180 ? .03f : .0003f);
        for (int i = 186 * WindowSamples; i < 194 * WindowSamples; i++)
            samples[i] = (float)(.7 * Math.Sin(2 * Math.PI * 180 * i / Rate));
        Assert.Empty(Replay(detector, samples));
        Assert.Equal(2, detector.Analysis.RejectedByProfile);
    }

    [Fact]
    public void UntrainedDetectorRetainsItsExistingImpulseAndEchoRules()
    {
        var detector = new AudioClickDetector();
        var clicks = Replay(detector, Signal(350, w => w == 180 ? .03f :
            w is >= 186 and < 194 or >= 210 and < 218 ? .7f : .003f));
        Assert.Equal(Origin.AddMilliseconds(930), Assert.Single(clicks));
    }

    [Fact]
    public void DifferentProfilesKeepTheSameCleanSoundSegmentation()
    {
        // The correction changes rearming after known-too-quiet rejected sounds,
        // never the samples used to characterize a candidate that has started.
        var learned = new AudioClickDetector();
        learned.SetProfile(SyntheticProfile);
        var permissive = new AudioClickDetector();
        permissive.SetProfile(SyntheticProfile with
        {
            MinimumPeak = .00001, MinimumRms = .00001, MinimumDurationMs = 1,
            MinimumHighFrequencyRatio = 0
        });
        var signal = Signal(300, w => w is >= 180 and < 188 ? .7f : .0003f);
        Assert.Single(Replay(learned, signal));
        Assert.Single(Replay(permissive, signal));
        Assert.Equal(permissive.Analysis.LastCompletedTransient, learned.Analysis.LastCompletedTransient);
    }

    private static float[] ReadClip(int example)
    {
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"click-precursor-{example}.f32.gz"));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return MemoryMarshal.Cast<byte, float>(output.ToArray()).ToArray();
    }

    private static float[] Signal(int windows, Func<int, float> amplitude)
    {
        var signal = new float[windows * WindowSamples];
        for (int i = 0; i < signal.Length; i++) signal[i] = amplitude(i / WindowSamples) * (i % 2 == 0 ? 1 : -1);
        return signal;
    }

    private static List<DateTimeOffset> Replay(AudioClickDetector detector, float[] samples)
    {
        var clicks = new List<DateTimeOffset>();
        for (int offset = 0; offset < samples.Length; offset += WindowSamples)
            if (detector.Process(samples.AsSpan(offset, Math.Min(WindowSamples, samples.Length - offset)), Rate, 1,
                Origin.AddSeconds((double)offset / Rate)) is { } click) clicks.Add(click);
        return clicks;
    }
}
