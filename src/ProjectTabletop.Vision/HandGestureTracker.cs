namespace ProjectTabletop.Vision;

/// <summary>
/// Recognizes a thumb/index pinch from current camera landmarks. Distances are
/// normalized by the observed palm, so thresholds are image-space heuristics,
/// not measurements of physical contact. This class performs no commands.
/// Call Update and Reset on the same thread.
/// </summary>
public sealed class HandGestureTracker
{
    private const double PinchThreshold = 0.25;
    private const double ReleaseThreshold = 0.45;
    private const double MaximumMatchDistance = 1.5;
    private static readonly TimeSpan PinchDwell = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ExecuteDuration = TimeSpan.FromSeconds(1);
    private static long s_nextExecuteEventId;
    private readonly List<Track> _tracks = new(2);
    private DateTimeOffset? _lastFrameTime;
    private DateTimeOffset? _lastNow;

    public IReadOnlyList<HandCursor> Update(IReadOnlyList<HandDetection> hands,
        DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hands);
        if (_lastNow is { } previousNow && now < previousNow)
            return Array.Empty<HandCursor>();
        _lastNow = now;
        _tracks.RemoveAll(track => now - track.LastSeen > ObservationLifetime);
        if (frameTime > now || now - frameTime > ObservationLifetime ||
            (_lastFrameTime is { } previousFrame && frameTime <= previousFrame))
            return Array.Empty<HandCursor>();
        _lastFrameTime = frameTime;

        var observations = new List<Observation>(2);
        foreach (var hand in hands)
        {
            if (Observe(hand) is not { } observation) continue;
            observations.Add(observation);
            if (observations.Count == 2) break;
        }

        Track?[] assignments = Match(observations);
        foreach (var track in _tracks)
        {
            if (assignments.Contains(track)) continue;
            // A missing observation cannot contribute to a pending dwell, but
            // keeps an already-fired pinch latched through a brief detection miss.
            track.CloseStarted = null;
            track.CloseSamples = 0;
        }

        var active = new List<Track>(2);
        var cursors = new List<HandCursor>(observations.Count);
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            var track = assignments[index];
            if (track is null)
            {
                if (_tracks.Count == 2)
                {
                    var oldestUnmatched = _tracks.Where(candidate =>
                        !assignments.Contains(candidate) && !active.Contains(candidate))
                        .MinBy(candidate => candidate.LastSeen)!;
                    _tracks.Remove(oldestUnmatched);
                }
                track = new Track();
                _tracks.Add(track);
            }

            track.Wrist = observation.Wrist;
            track.PalmCenter = observation.PalmCenter;
            track.PalmScale = observation.PalmScale;
            track.LastSeen = frameTime;
            UpdatePinch(track, observation.PinchRatio, frameTime, now);
            active.Add(track);
            cursors.Add(new HandCursor(observation.IndexTip, track.ExecuteUntil, track.ExecuteEventId));
        }
        return cursors;
    }

    public void Reset()
    {
        _tracks.Clear();
        _lastFrameTime = null;
        _lastNow = null;
    }

    private static void UpdatePinch(Track track, double ratio, DateTimeOffset frameTime,
        DateTimeOffset now)
    {
        if (ratio >= ReleaseThreshold)
        {
            track.Latched = false;
            track.CloseStarted = null;
            track.CloseSamples = 0;
            return;
        }
        if (track.Latched) return;
        if (ratio > PinchThreshold)
        {
            track.CloseStarted = null;
            track.CloseSamples = 0;
            return;
        }
        track.CloseStarted ??= frameTime;
        track.CloseSamples = Math.Min(2, track.CloseSamples + 1);
        if (track.CloseSamples < 2 || frameTime - track.CloseStarted.Value < PinchDwell) return;
        track.ExecuteUntil = now + ExecuteDuration;
        track.ExecuteEventId = Interlocked.Increment(ref s_nextExecuteEventId);
        track.Latched = true;
        track.CloseStarted = null;
        track.CloseSamples = 0;
    }

    private Track?[] Match(IReadOnlyList<Observation> observations)
    {
        // At most two hands: enumerate the few possible one-to-one assignments.
        // Prefer retaining both tracks, then minimize wrist/palm movement. A
        // confidence-order swap must not move a held-pinch latch to another hand.
        var candidate = new Track?[observations.Count];
        var best = new Track?[observations.Count];
        var bestCount = -1;
        var bestCost = double.PositiveInfinity;
        Search(0, 0, 0, 0);
        return best;

        void Search(int index, int used, int matched, double cost)
        {
            if (index == observations.Count)
            {
                if (matched > bestCount || matched == bestCount && cost < bestCost)
                {
                    bestCount = matched;
                    bestCost = cost;
                    Array.Copy(candidate, best, candidate.Length);
                }
                return;
            }
            candidate[index] = null;
            Search(index + 1, used, matched, cost);
            for (var trackIndex = 0; trackIndex < _tracks.Count; trackIndex++)
            {
                if ((used & (1 << trackIndex)) != 0) continue;
                var track = _tracks[trackIndex];
                var observation = observations[index];
                var scale = Math.Max(track.PalmScale, observation.PalmScale);
                var distance = (Distance(track.Wrist, observation.Wrist) +
                    Distance(track.PalmCenter, observation.PalmCenter)) / (2 * scale);
                if (!double.IsFinite(distance) || distance > MaximumMatchDistance) continue;
                candidate[index] = track;
                Search(index + 1, used | (1 << trackIndex), matched + 1, cost + distance);
            }
        }
    }

    private static Observation? Observe(HandDetection? hand)
    {
        if (hand?.Landmarks is not { Count: 21 } points ||
            !double.IsFinite(hand.Confidence) || hand.Confidence is < 0 or > 1 ||
            !double.IsFinite(hand.RightHandProbability) || hand.RightHandProbability is < 0 or > 1 ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return null;
        var scale = Math.Max(Distance(points[0], points[9]), Distance(points[5], points[17]));
        if (!double.IsFinite(scale) || scale <= 1) return null;
        var center = new PixelPoint(
            (points[0].X + points[5].X + points[9].X + points[13].X + points[17].X) / 5,
            (points[0].Y + points[5].Y + points[9].Y + points[13].Y + points[17].Y) / 5);
        var pinchRatio = Distance(points[4], points[8]) / scale;
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(pinchRatio)) return null;
        return new Observation(points[0], center, scale, points[8], pinchRatio);
    }

    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private sealed record Observation(PixelPoint Wrist, PixelPoint PalmCenter,
        double PalmScale, PixelPoint IndexTip, double PinchRatio);

    private sealed class Track
    {
        public PixelPoint Wrist;
        public PixelPoint PalmCenter;
        public double PalmScale;
        public DateTimeOffset LastSeen;
        public DateTimeOffset? CloseStarted;
        public int CloseSamples;
        public bool Latched;
        public DateTimeOffset ExecuteUntil = DateTimeOffset.MinValue;
        public long ExecuteEventId;
    }
}
