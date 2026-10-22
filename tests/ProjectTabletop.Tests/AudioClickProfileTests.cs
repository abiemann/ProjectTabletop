using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class AudioClickProfileTests
{
    private static AudioClickProfile Valid => new()
    {
        TrainingNoiseRms = .003, MinimumPeak = .01, MaximumPeak = .4,
        MinimumRms = .004, MaximumRms = .2, MinimumDurationMs = 5, MaximumDurationMs = 60,
        MinimumHighFrequencyRatio = .1, MaximumHighFrequencyRatio = .8
    };

    [Fact]
    public void RejectsUnsupportedMalformedAndNonfiniteProfiles()
    {
        AudioClickProfile[] invalid = [
            Valid with { Version = 2 }, Valid with { TrainingNoiseRms = double.NaN },
            Valid with { TrainingNoiseRms = .16 }, Valid with { MinimumPeak = 0 },
            Valid with { MaximumPeak = 1.01 }, Valid with { MinimumRms = .3 },
            Valid with { MaximumRms = double.PositiveInfinity }, Valid with { MinimumDurationMs = 0 },
            Valid with { MaximumDurationMs = 121 }, Valid with { MinimumHighFrequencyRatio = -.01 },
            Valid with { MaximumHighFrequencyRatio = 1.01 }
        ];
        foreach (var profile in invalid)
        {
            Assert.Throws<ArgumentException>(profile.Validate);
            Assert.Throws<ArgumentException>(() => new AudioClickDetector().SetProfile(profile));
        }
    }

    [Fact]
    public void EveryFeatureMustMatchAndNonfiniteInputIsRejected()
    {
        var profile = Valid;
        profile.Validate();
        var good = new AudioClickFeatures(.05, .02, 20, .5);
        Assert.True(profile.Matches(good));
        Assert.False(profile.Matches(good with { Peak = .5 }));
        Assert.False(profile.Matches(good with { Rms = .001 }));
        Assert.False(profile.Matches(good with { DurationMs = 100 }));
        Assert.False(profile.Matches(good with { HighFrequencyRatio = .9 }));
        Assert.False(profile.Matches(good with { Rms = double.NaN }));
    }
}
