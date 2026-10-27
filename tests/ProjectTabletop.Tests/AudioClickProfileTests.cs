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
            Valid with { Version = 3 }, Valid with { TrainingNoiseRms = double.NaN },
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

    [Fact]
    public void NewProfileAllowsOnlyAnImpulsiveExtraWindowAndPreservesEveryOtherBound()
    {
        var profile = CapturedProfile with { Version = 2, MaximumDurationMs = 125 };
        profile.Validate();
        var click = new AudioClickFeatures(.6, .08, 125, .62);
        Assert.True(profile.Matches(click));
        Assert.False(CapturedProfile.Matches(click));
        Assert.False(profile.Matches(click with { DurationMs = 125.001 }));
        Assert.False(profile.Matches(click with { HighFrequencyRatio = .3 }));
        Assert.False(profile.Matches(click with { Peak = .05, Rms = .008 }));
        Assert.False(profile.Matches(click with { Peak = .25, Rms = .1 }));
        Assert.Throws<ArgumentException>((profile with { MaximumDurationMs = 125.001 }).Validate);
        Assert.Throws<ArgumentException>((profile with { Version = 1 }).Validate);
    }

    private static AudioClickProfile CapturedProfile => new()
    {
        TrainingNoiseRms = .0021028156131392612,
        MinimumPeak = .2988905608654022, MaximumPeak = 1,
        MinimumRms = .040370718127672886, MaximumRms = .1453345852596224,
        MinimumDurationMs = 41.249425, MaximumDurationMs = 120,
        MinimumHighFrequencyRatio = .5017959747869696, MaximumHighFrequencyRatio = .7417959747869696
    };

    [Theory]
    // Two intended clicks rejected in the six-click live capture solely by level.
    [InlineData(.2544645667076111, .03242415425118958, 100.6258, .6104022244207472, true)]
    [InlineData(.23345859348773956, .036888607682798294, 109.7079, .6008066124289484, true)]
    // Quieter mechanical tails and room sounds from the same capture stay rejected.
    [InlineData(.05751063674688339, .009216473428883057, 60.9118, .6060906721672393, false)]
    [InlineData(.062325745820999146, .008843034948813762, 45.6071, .5880092367100016, false)]
    [InlineData(.01804439164698124, .006941738607647627, 39.9779, .02498027033002216, false)]
    [InlineData(.02675601653754711, .004735662220668731, 50.095, .13866640711004055, false)]
    public void CapturedClicksAllowModestLevelChangesWithoutAcceptingTheirMechanicalTails(
        double peak, double rms, double duration, double highFrequency, bool expected)
    {
        Assert.Equal(expected, CapturedProfile.Matches(new(peak, rms, duration, highFrequency)));
    }

    [Fact]
    public void LevelToleranceDoesNotWidenCharacterUpperBoundsOrPermitArbitraryQuietSounds()
    {
        var profile = CapturedProfile;
        var click = new AudioClickFeatures(.35, .05, 100, .62);
        Assert.True(profile.Matches(click));
        Assert.False(profile.Matches(click with { Peak = profile.MinimumPeak * .74 }));
        Assert.False(profile.Matches(click with { Rms = profile.MinimumRms * .74 }));
        Assert.False(profile.Matches(click with { Peak = profile.MaximumPeak + .001 }));
        Assert.False(profile.Matches(click with { Rms = profile.MaximumRms + .001 }));
        Assert.False(profile.Matches(click with { DurationMs = profile.MinimumDurationMs - .001 }));
        Assert.False(profile.Matches(click with { DurationMs = profile.MaximumDurationMs + .001 }));
        Assert.False(profile.Matches(click with { HighFrequencyRatio = profile.MinimumHighFrequencyRatio - .001 }));
        Assert.False(profile.Matches(click with { HighFrequencyRatio = profile.MaximumHighFrequencyRatio + .001 }));
    }
}
