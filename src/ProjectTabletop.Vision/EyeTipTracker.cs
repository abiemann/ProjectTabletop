namespace ProjectTabletop.Vision;

public sealed record EyeTipTrackResult(EyeTipObservation? Observation, bool Confirmed, string Reason);

/// <summary>Single-owner temporal association for a camera eye marker. Missing, ambiguous and
/// stale observations never produce an extrapolated tip. Acquire selects the user's clicked eye.</summary>
public sealed class EyeTipTracker
{
    private EyeTipObservation? _previous;
    private DateTimeOffset _lastSeen, _lastFrame;
    private int _consecutive;

    public void Acquire(EyeTipObservation observation, DateTimeOffset frameTime)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!Valid(observation)) throw new ArgumentException("Invalid eye observation.", nameof(observation));
        _previous = observation;
        _lastSeen = _lastFrame = frameTime;
        _consecutive = 1;
    }

    public EyeTipTrackResult Update(EyeTipDetectionResult detection, DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(detection);
        if (frameTime > now + TimeSpan.FromMilliseconds(30) || now - frameTime > TimeSpan.FromMilliseconds(300))
        { Reset(); return new(null, false, "stale-camera-frame"); }
        if (_lastFrame != default && frameTime <= _lastFrame)
            return new(null, false, "old-camera-frame");
        _lastFrame = frameTime;
        if (_previous is not null && frameTime - _lastSeen > TimeSpan.FromMilliseconds(450))
        { _previous = null; _consecutive = 0; }
        EyeTipObservation[] candidates = detection.Candidates.Where(Valid).ToArray();
        EyeTipObservation? selected;
        if (_previous is { } previous)
        {
            double elapsed = Math.Max(0, (frameTime - _lastSeen).TotalSeconds);
            double radius = previous.RadiusPixels;
            // A small pupil may cross many of its own radii between 15 Hz camera
            // samples. Allow ordinary sweeps while still bounding sudden reassignment.
            double reach = Math.Max(32, radius * 8) + Math.Min(.25, elapsed) * Math.Max(240, radius * 240);
            var nearby = candidates.Where(item => item.RadiusPixels >= radius * .50 &&
                    item.RadiusPixels <= radius * 1.85 && EyeTipDetector.Distance(item.Center, previous.Center) <= reach)
                .OrderBy(item => EyeTipDetector.Distance(item.Center, previous.Center)).ToArray();
            if (nearby.Length > 1 && EyeTipDetector.Distance(nearby[1].Center, previous.Center) -
                    EyeTipDetector.Distance(nearby[0].Center, previous.Center) < Math.Max(8, radius * 2))
            { _consecutive = 0; return new(null, false, "ambiguous-eye-marker"); }
            selected = nearby.FirstOrDefault();
        }
        else
        {
            var ranked = candidates.OrderByDescending(item => item.Score).ToArray();
            if (ranked.Length > 1)
            { _consecutive = 0; return new(null, false, "ambiguous-eye-marker"); }
            selected = ranked.FirstOrDefault();
        }
        if (selected is null)
        { _consecutive = 0; return new(null, false, "eye-marker-lost"); }
        _previous = selected;
        _lastSeen = frameTime;
        _consecutive++;
        bool confirmed = _consecutive >= 2;
        return new(selected, confirmed, confirmed ? "eye-marker-tracked" : "confirming-eye-marker");
    }

    public void Reset()
    {
        _previous = null;
        _lastSeen = _lastFrame = default;
        _consecutive = 0;
    }

    private static bool Valid(EyeTipObservation observation) => observation is not null &&
        double.IsFinite(observation.Center.X) && double.IsFinite(observation.Center.Y) &&
        observation.Center.X >= 0 && observation.Center.Y >= 0 &&
        double.IsFinite(observation.RadiusPixels) && observation.RadiusPixels is >= 1 and <= 1024 &&
        double.IsFinite(observation.Score) && observation.Score is >= 0 and <= 1 &&
        double.IsFinite(observation.Contrast) && observation.Contrast >= 28 &&
        double.IsFinite(observation.BrightRingCoverage) && observation.BrightRingCoverage is >= .80 and <= 1;
}
