using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class AudioClickProfileLoudnessTests
{
    // Saved ten-click profile used during the 2026-11-01 HUNT capture. These are
    // measured feature fixtures, not reconstructed audio or a microphone identity.
    private static AudioClickProfile CapturedProfile => new()
    {
        Version = 2,
        TrainingNoiseRms = .0027049959972244952,
        MinimumPeak = .322108256816864, MaximumPeak = 1,
        MinimumRms = .0422035350304387, MaximumRms = .1713615829596438,
        MinimumDurationMs = 37.5, MaximumDurationMs = 125,
        MinimumHighFrequencyRatio = .4745169725593068,
        MaximumHighFrequencyRatio = .7145169725593068
    };

    [Theory]
    // Three shots at birds 5, 7 and 8 were rejected only for 0.3–4.3% excess RMS.
    [InlineData(.9923632144927979, .17874223840166337, 55, .5977797629888395)]
    [InlineData(.9879393577575684, .17188332955649765, 50, .6047844283705942)]
    [InlineData(.991669774055481, .1759915651845179, 55, .5951773373839172)]
    // Two later clicks retained their signature but measured one 5 ms window shorter.
    [InlineData(.9907069206237793, .16953876863328035, 35, .5992624697911787)]
    [InlineData(.9909631609916687, .17591654963600867, 35, .5752960081578001)]
    // The accepted START and the ten successful bird hits from the same round.
    [InlineData(.9915227293968201, .12945320364231286, 55, .5960116492630125)]
    [InlineData(.9920412302017212, .14672282598762565, 50, .6200101258659009)]
    [InlineData(.9887636303901672, .15787707168848975, 60, .598596681404503)]
    [InlineData(.9888266921043396, .1429575138701543, 50, .5837917384393055)]
    [InlineData(.9912241101264954, .16731571156214628, 50, .5660002091697238)]
    [InlineData(.9914469718933105, .15250838230453764, 55, .5897880763216472)]
    [InlineData(.9886053204536438, .17004302915723962, 50, .5827535217575965)]
    [InlineData(.9930807948112488, .1529621410031451, 55, .5914016326617934)]
    [InlineData(.9914337396621704, .15682962297018319, 60, .5949356145978757)]
    [InlineData(.9888952970504761, .1558614503943699, 50, .6205508094060627)]
    [InlineData(.9933936595916748, .1684849090818539, 45, .6202923813580024)]
    public void CapturedAimedClicksMatchWithoutChangingTheSavedProfile(
        double peak, double rms, double duration, double highFrequency)
    {
        Assert.True(CapturedProfile.Matches(new(peak, rms, duration, highFrequency)));
    }

    [Theory]
    // Unrelated room transients recorded after the round remain rejected.
    [InlineData(.03819822147488594, .005986333859580916, 10, .5136521714770774)]
    [InlineData(.019770236685872078, .008049089746774415, 20, .027187674336554807)]
    [InlineData(.02994983084499836, .015036050576460753, 85, .0018918042384056667)]
    [InlineData(.030457090586423874, .013720026538787454, 120, .001980088790991246)]
    [InlineData(.029140349477529526, .01657156615805791, 30, .00149369546940996)]
    [InlineData(.02434670552611351, .006989721576594125, 30, .02857595008022894)]
    [InlineData(.03920051455497742, .011732907478401593, 35, .020667869276834568)]
    [InlineData(.03021913394331932, .01256171433157027, 5, .03206352192951338)]
    [InlineData(.022206149995326996, .009946583769264782, 10, .02274968472025881)]
    public void CapturedUnrelatedSoundsRemainRejected(
        double peak, double rms, double duration, double highFrequency)
    {
        Assert.False(CapturedProfile.Matches(new(peak, rms, duration, highFrequency)));
    }

    [Fact]
    public void SlightlyLouderAllowanceRequiresAnImpulseAndKeepsEveryOtherLimit()
    {
        var profile = CapturedProfile;
        double upper = profile.MaximumRms * 1.10;
        var click = new AudioClickFeatures(.99, upper, 55, .60);
        Assert.True(profile.Matches(click));
        Assert.False(profile.Matches(click with { Rms = upper + .000001 }));
        Assert.True(profile.Matches(click with { Peak = upper * 3 }));
        Assert.False(profile.Matches(click with { Peak = upper * 3 - .000001 }));
        Assert.False(profile.Matches(click with { Peak = profile.MaximumPeak + .000001 }));
        Assert.False(profile.Matches(click with { Peak = profile.MinimumPeak * .74 }));
        Assert.False(profile.Matches(click with { Rms = profile.MinimumRms * .74 }));
        Assert.False(profile.Matches(click with { DurationMs = profile.MinimumDurationMs - 5.001 }));
        Assert.False(profile.Matches(click with { DurationMs = profile.MaximumDurationMs + .001 }));
        Assert.False(profile.Matches(click with { HighFrequencyRatio = profile.MinimumHighFrequencyRatio - .001 }));
        Assert.False(profile.Matches(click with { HighFrequencyRatio = profile.MaximumHighFrequencyRatio + .001 }));
        Assert.False(profile.Matches(click with { Peak = double.NaN }));
        Assert.False(profile.Matches(click with { Peak = double.PositiveInfinity }));
        Assert.False(profile.Matches(click with { Rms = double.NaN }));
        Assert.False(profile.Matches(click with { Rms = double.PositiveInfinity }));
    }

    [Fact]
    public void ShorterDurationAllowanceRequiresAnImpulseAndIsOnlyOneWindow()
    {
        var profile = CapturedProfile;
        var click = new AudioClickFeatures(.99, .16, 35, .60);
        Assert.True(profile.Matches(click));
        Assert.True(profile.Matches(click with { DurationMs = 32.5 }));
        Assert.False(profile.Matches(click with { DurationMs = 32.499 }));
        Assert.False(profile.Matches(click with { DurationMs = 30 }));
        Assert.False(profile.Matches(click with { Peak = .47 }));
        Assert.True(profile.Matches(click with { Peak = .48 }));
        Assert.False(profile.Matches(click with { HighFrequencyRatio = .3 }));
        Assert.False(profile.Matches(click with { Peak = .1 }));
        Assert.False(profile.Matches(click with { DurationMs = double.NaN }));
        Assert.False(profile.Matches(click with { DurationMs = double.NegativeInfinity }));
        Assert.Equal(37.5, profile.MinimumDurationMs);

        var shortProfile = profile with { MinimumDurationMs = 3 };
        Assert.True(shortProfile.Matches(click with { DurationMs = 1 }));
        Assert.False(shortProfile.Matches(click with { DurationMs = .999 }));
        Assert.False(shortProfile.Matches(click with { DurationMs = 0 }));
    }

    [Fact]
    public void ExistingProfileVersionsKeepTheirDurationRulesAndStoredEnvelope()
    {
        var current = CapturedProfile;
        var legacy = current with { Version = 1, MaximumDurationMs = 120 };
        var click = new AudioClickFeatures(.99, .17874223840166337, 55, .60);
        Assert.True(current.Matches(click));
        Assert.True(legacy.Matches(click));
        Assert.False(legacy.Matches(click with { DurationMs = 125 }));
        Assert.Equal(.1713615829596438, current.MaximumRms);
        Assert.Equal(current.MaximumRms, legacy.MaximumRms);
    }
}
