namespace ProjectTabletop.Interaction;

/// <summary>Small, microphone-specific ranges learned from clicks; contains no audio samples.</summary>
public sealed record AudioClickProfile
{
    private const double MinimumAmplitudeFactor = .75;
    public const double LegacyMaximumDurationMs = 120;
    public const double MaximumLearnedDurationMs = 125;

    public int Version { get; init; } = 1;
    public double TrainingNoiseRms { get; init; }
    public double MinimumPeak { get; init; }
    public double MaximumPeak { get; init; }
    public double MinimumRms { get; init; }
    public double MaximumRms { get; init; }
    public double MinimumDurationMs { get; init; }
    public double MaximumDurationMs { get; init; }
    public double MinimumHighFrequencyRatio { get; init; }
    public double MaximumHighFrequencyRatio { get; init; }

    public void Validate()
    {
        if (Version is not (1 or 2) || !double.IsFinite(TrainingNoiseRms) || TrainingNoiseRms is < 0 or > .15 ||
            !Range(MinimumPeak, MaximumPeak, double.Epsilon, 1) ||
            !Range(MinimumRms, MaximumRms, double.Epsilon, 1) ||
            !Range(MinimumDurationMs, MaximumDurationMs, 1,
                Version == 2 ? MaximumLearnedDurationMs : LegacyMaximumDurationMs) ||
            !Range(MinimumHighFrequencyRatio, MaximumHighFrequencyRatio, 0, 1))
            throw new ArgumentException("The learned click profile is invalid or unsupported.");
    }

    // Aiming the same click device changes its level at the microphone. Allow a
    // modest drop without relaxing the sound's duration, frequency or upper bounds.
    public bool Matches(AudioClickFeatures features) =>
        Within(features.Peak, MinimumPeak * MinimumAmplitudeFactor, MaximumPeak) &&
        Within(features.Rms, MinimumRms * MinimumAmplitudeFactor, MaximumRms) &&
        Within(features.DurationMs, MinimumDurationMs, MaximumDurationMs) &&
        Within(features.HighFrequencyRatio, MinimumHighFrequencyRatio, MaximumHighFrequencyRatio) &&
        (features.DurationMs <= LegacyMaximumDurationMs || IsExtendedImpulse(features));

    internal bool IsBelowBothMinimumLevels(AudioClickFeatures features) =>
        features.Peak < MinimumPeak * MinimumAmplitudeFactor &&
        features.Rms < MinimumRms * MinimumAmplitudeFactor;

    // Only newly trained profiles can include the extra 5 ms window. A strongly
    // peaked waveform distinguishes that narrow allowance from sustained sound.
    // The detector still requires a real decay and its original 135 ms deadline.
    internal static bool IsExtendedImpulse(AudioClickFeatures features) =>
        double.IsFinite(features.Peak) && double.IsFinite(features.Rms) &&
        features.Rms > 0 && features.Peak >= features.Rms * 3;

    private static bool Range(double minimum, double maximum, double lower, double upper) =>
        double.IsFinite(minimum) && double.IsFinite(maximum) && minimum >= lower && maximum <= upper && minimum <= maximum;
    private static bool Within(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}

public readonly record struct AudioClickFeatures(double Peak, double Rms, double DurationMs, double HighFrequencyRatio);
public readonly record struct AudioClickTransient(DateTimeOffset At, AudioClickFeatures Features);
