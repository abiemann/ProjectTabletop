using System.Collections.ObjectModel;

namespace ProjectTabletop.Calibration;

/// <summary>One first observation of a registration dot in a received camera frame.</summary>
public sealed record CameraLatencySample(int SpotIndex, DateTimeOffset ShownAt,
    DateTimeOffset ArrivedAt, double Milliseconds)
{
    public double TotalMilliseconds => (ArrivedAt - ShownAt).TotalMilliseconds;
}

/// <summary>
/// Estimates camera delivery delay from the registration dots, after subtracting
/// the configured projector delay. The caller supplies times from the same monotonic
/// clock and validates that the detected dot belongs to the current presentation.
/// </summary>
public sealed class CameraLatencyMeasurement
{
    public const double DefaultProjectorMilliseconds = 17;
    public const double MaximumTotalMilliseconds = 2000;

    private readonly List<(int SpotIndex, DateTimeOffset ShownAt, DateTimeOffset ArrivedAt)> _observations = [];
    private readonly List<CameraLatencySample> _samples = [];
    private readonly ReadOnlyCollection<CameraLatencySample> _readOnlySamples;
    private double _projectorMilliseconds = DefaultProjectorMilliseconds;

    public CameraLatencyMeasurement() => _readOnlySamples = _samples.AsReadOnly();

    /// <summary>
    /// Projector delay to subtract from each first observation. Changing the delay
    /// recalculates the current scan; observations shorter than the delay remain
    /// available if the setting is subsequently lowered, but are not camera samples.
    /// </summary>
    public double ProjectorMilliseconds
    {
        get => _projectorMilliseconds;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > MaximumTotalMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"Projector latency must be between 0 and {MaximumTotalMilliseconds} milliseconds.");
            if (_projectorMilliseconds == value) return;
            _projectorMilliseconds = value;
            _samples.Clear();
            foreach (var observation in _observations)
                AddSample(observation.SpotIndex, observation.ShownAt, observation.ArrivedAt);
        }
    }

    public IReadOnlyList<CameraLatencySample> Samples => _readOnlySamples;
    public double? MinimumMilliseconds => _samples.Count == 0 ? null : _samples.Min(sample => sample.Milliseconds);
    public double? MaximumMilliseconds => _samples.Count == 0 ? null : _samples.Max(sample => sample.Milliseconds);
    public double? MedianMilliseconds
    {
        get
        {
            if (_samples.Count == 0) return null;
            var ordered = _samples.Select(sample => sample.Milliseconds).Order().ToArray();
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) / 2
                : ordered[middle];
        }
    }

    /// <summary>
    /// Retains only the first nonnegative, non-stale arrival per dot. Returns true
    /// when that observation also produces a nonnegative camera delay under the
    /// current projector setting. Shorter intervals are retained for recalculation,
    /// not replaced by a later frame or clamped into a misleading zero-delay result.
    /// This helper is owned by the setup/UI thread.
    /// </summary>
    public bool Record(int index, DateTimeOffset shownAt, DateTimeOffset arrivedAt)
    {
        if (index < 0 || index >= BoardRegistration.SpotCount ||
            _observations.Any(observation => observation.SpotIndex == index)) return false;
        double total = (arrivedAt - shownAt).TotalMilliseconds;
        if (total < 0 || total > MaximumTotalMilliseconds) return false;
        _observations.Add((index, shownAt, arrivedAt));
        return AddSample(index, shownAt, arrivedAt);
    }

    private bool AddSample(int index, DateTimeOffset shownAt, DateTimeOffset arrivedAt)
    {
        double total = (arrivedAt - shownAt).TotalMilliseconds;
        if (total < ProjectorMilliseconds) return false;
        _samples.Add(new(index, shownAt, arrivedAt, total - ProjectorMilliseconds));
        return true;
    }

    public void Reset()
    {
        _observations.Clear();
        _samples.Clear();
    }
}
