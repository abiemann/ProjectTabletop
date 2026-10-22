namespace ProjectTabletop.Interaction;

/// <summary>Small, microphone-specific ranges learned from clicks; contains no audio samples.</summary>
public sealed record AudioClickProfile
{
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
        if (Version != 1 || !double.IsFinite(TrainingNoiseRms) || TrainingNoiseRms is < 0 or > .15 ||
            !Range(MinimumPeak, MaximumPeak, double.Epsilon, 1) ||
            !Range(MinimumRms, MaximumRms, double.Epsilon, 1) ||
            !Range(MinimumDurationMs, MaximumDurationMs, 1, 120) ||
            !Range(MinimumHighFrequencyRatio, MaximumHighFrequencyRatio, 0, 1))
            throw new ArgumentException("The learned click profile is invalid or unsupported.");
    }

    public bool Matches(AudioClickFeatures features) =>
        Within(features.Peak, MinimumPeak, MaximumPeak) &&
        Within(features.Rms, MinimumRms, MaximumRms) &&
        Within(features.DurationMs, MinimumDurationMs, MaximumDurationMs) &&
        Within(features.HighFrequencyRatio, MinimumHighFrequencyRatio, MaximumHighFrequencyRatio);

    private static bool Range(double minimum, double maximum, double lower, double upper) =>
        double.IsFinite(minimum) && double.IsFinite(maximum) && minimum >= lower && maximum <= upper && minimum <= maximum;
    private static bool Within(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}

public readonly record struct AudioClickFeatures(double Peak, double Rms, double DurationMs, double HighFrequencyRatio);
public readonly record struct AudioClickTransient(DateTimeOffset At, AudioClickFeatures Features);
