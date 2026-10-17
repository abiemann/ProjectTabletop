namespace ProjectTabletop.Vision;

public sealed record MarkerTipTrackResult(MarkerTipObservation? Observation, bool Confirmed, string Reason);

/// <summary>Associates fresh measured marker components. Stationary tips remain valid, but missing,
/// ambiguous or stale observations immediately clear confirmation; no extrapolated point is emitted.</summary>
public sealed class MarkerTipTracker
{
    private MarkerTipObservation? _previous;
    private DateTimeOffset _lastSeen, _lastFrame;
    private int _consecutive;

    public void Acquire(MarkerTipObservation observation, DateTimeOffset frameTime)
    {
        if (!Valid(observation)) throw new ArgumentException("Invalid marker-tip observation.", nameof(observation));
        _previous = observation; _lastSeen = _lastFrame = frameTime; _consecutive = 1;
    }

    public MarkerTipTrackResult Update(MarkerTipDetectionResult detection, DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(detection);
        if (frameTime > now + TimeSpan.FromMilliseconds(30) || now - frameTime > TimeSpan.FromMilliseconds(300))
        { Reset(); return new(null, false, "stale-camera-frame"); }
        if (_lastFrame != default && frameTime <= _lastFrame) return new(null, false, "old-camera-frame");
        _lastFrame = frameTime;
        if (_previous is not null && frameTime - _lastSeen > TimeSpan.FromMilliseconds(450))
        { _previous = null; _consecutive = 0; }
        var candidates = detection.Candidates.Where(Valid).ToArray();
        MarkerTipObservation? selected;
        if (_previous is { } previous)
        {
            double elapsed = Math.Max(0, (frameTime - _lastSeen).TotalSeconds), radius = previous.RadiusPixels;
            double reach = Math.Max(32, radius * 8) + Math.Min(.25, elapsed) * Math.Max(240, radius * 240);
            var nearby = candidates.Where(item => item.AreaPixels >= previous.AreaPixels * .30 &&
                item.AreaPixels <= previous.AreaPixels * 3.2 && MarkerTipGeometry.Distance(item.Center, previous.Center) <= reach)
                .OrderBy(item => MarkerTipGeometry.Distance(item.Center, previous.Center)).ToArray();
            if (nearby.Length > 1 && MarkerTipGeometry.Distance(nearby[1].Center, previous.Center) -
                MarkerTipGeometry.Distance(nearby[0].Center, previous.Center) < Math.Max(8, radius * 2))
            { _consecutive = 0; return new(null, false, "ambiguous-marker-tip"); }
            selected = nearby.FirstOrDefault();
        }
        else
        {
            if (candidates.Length > 1) { _consecutive = 0; return new(null, false, "ambiguous-marker-tip"); }
            selected = candidates.FirstOrDefault();
        }
        if (selected is null) { _consecutive = 0; return new(null, false, "marker-tip-lost"); }
        _previous = selected; _lastSeen = frameTime; _consecutive++;
        bool confirmed = _consecutive >= 2;
        return new(selected, confirmed, confirmed ? "marker-tip-tracked" : "confirming-marker-tip");
    }

    public void Reset() { _previous = null; _lastSeen = _lastFrame = default; _consecutive = 0; }
    private static bool Valid(MarkerTipObservation? observation) => observation is not null &&
        double.IsFinite(observation.Center.X) && double.IsFinite(observation.Center.Y) &&
        observation.Center.X >= 0 && observation.Center.Y >= 0 &&
        double.IsFinite(observation.RadiusPixels) && observation.RadiusPixels is >= 1 and <= 1024 &&
        double.IsFinite(observation.AreaPixels) && observation.AreaPixels is >= 6 and <= 1000000 &&
        double.IsFinite(observation.Score) && observation.Score is >= 0 and <= 1;
}
