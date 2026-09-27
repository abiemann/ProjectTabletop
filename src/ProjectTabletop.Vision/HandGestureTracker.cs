namespace ProjectTabletop.Vision;

/// <summary>
/// Recognizes a thumb/index pinch and a separate spread-hand pose from current
/// camera landmarks. Only a pinch creates execute feedback. Distances are
/// normalized by the observed palm, so thresholds are image-space heuristics,
/// not measurements of physical contact. This class performs no commands.
/// Call Update and Reset on the same thread.
/// </summary>
public sealed class HandGestureTracker
{
    private const double PinchThreshold = 0.25;
    private const double PinchHoldThreshold = 0.35;
    private const double ReleaseThreshold = 0.45;
    private const double MaximumMatchDistance = 1.5;
    private static readonly TimeSpan PinchDwell = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SpreadDwell = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ReleaseDwell = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan CandidateDropoutGrace = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan OpenSelectionLifetime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SelectionLifetime = TimeSpan.FromMilliseconds(750);
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
        {
            ClearSpreadEvidence();
            return Array.Empty<HandCursor>();
        }
        _lastNow = now;
        if (frameTime > now || now - frameTime > ObservationLifetime ||
            (_lastFrameTime is { } previousFrame && frameTime <= previousFrame))
        {
            ClearSpreadEvidence();
            return Array.Empty<HandCursor>();
        }
        // Identity follows camera time. Comparing a previous frame with the
        // completion time would count inference latency as a detection gap and
        // erase a live pinch even when successive source frames are fresh.
        _tracks.RemoveAll(track => frameTime - track.LastSeen > ObservationLifetime);
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
            ResetSpread(track);
            // Brief misses pause the accumulated evidence instead of making a
            // user start the pinch over. Missing frames never add dwell time or
            // release an already-fired pinch.
            if (track.CloseLastObserved is { } last && frameTime - last > CandidateDropoutGrace)
                ResetCandidate(track);
            else
                track.CandidateWasMissing = true;
            track.ReleaseStarted = null;
            track.ReleaseSamples = 0;
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
            UpdateSelection(track, observation, frameTime);
            active.Add(track);
            cursors.Add(new HandCursor(observation.IndexTip, track.ExecuteUntil, track.ExecuteEventId,
                track.SelectionPosition, track.SelectionFrameTime)
            {
                IsSpreadOut = UpdateSpread(track, observation.IsSpreadOut, frameTime)
            });
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
        if (track.Latched)
        {
            // A single open-looking landmark error must not rearm a held pinch.
            if (ratio >= ReleaseThreshold)
            {
                track.ReleaseStarted ??= frameTime;
                track.ReleaseSamples = Math.Min(2, track.ReleaseSamples + 1);
                if (track.ReleaseSamples >= 2 && frameTime - track.ReleaseStarted.Value >= ReleaseDwell)
                    track.Latched = false;
            }
            else
            {
                track.ReleaseStarted = null;
                track.ReleaseSamples = 0;
            }
            return;
        }
        if (ratio > PinchHoldThreshold)
        {
            ResetCandidate(track);
            return;
        }
        if (track.CandidateWasMissing && track.CloseLastObserved is { } previous &&
            frameTime - previous > CandidateDropoutGrace)
            ResetCandidate(track);
        if (track.CloseLastObserved is null)
        {
            // The wider holding band cannot start a pinch on its own.
            if (ratio > PinchThreshold) return;
            track.CloseSamples = 1;
        }
        else
        {
            if (!track.CandidateWasMissing)
                track.CloseEvidence += frameTime - track.CloseLastObserved.Value;
            if (ratio <= PinchThreshold)
                track.CloseSamples = Math.Min(2, track.CloseSamples + 1);
        }
        track.CloseLastObserved = frameTime;
        track.CandidateWasMissing = false;
        // Jitter may preserve evidence, but execution still needs a currently
        // closed pinch and at least two distinct closed observations.
        if (ratio > PinchThreshold || track.CloseSamples < 2 || track.CloseEvidence < PinchDwell) return;
        track.ExecuteUntil = now + ExecuteDuration;
        track.ExecuteEventId = Interlocked.Increment(ref s_nextExecuteEventId);
        track.Latched = true;
        track.ReleaseStarted = null;
        track.ReleaseSamples = 0;
        ResetCandidate(track);
    }

    private static bool UpdateSpread(Track track, bool spread, DateTimeOffset frameTime)
    {
        if (!spread)
        {
            ResetSpread(track);
            return false;
        }
        if (track.SpreadLastObserved is not { } previous || frameTime - previous > ObservationLifetime)
            track.SpreadStarted = frameTime;
        track.SpreadLastObserved = frameTime;
        return track.SpreadStarted is { } started && frameTime - started >= SpreadDwell;
    }

    private void ClearSpreadEvidence()
    {
        foreach (var track in _tracks) ResetSpread(track);
    }

    private static void ResetSpread(Track track)
    {
        track.SpreadStarted = null;
        track.SpreadLastObserved = null;
    }

    private static void ResetCandidate(Track track)
    {
        track.CloseLastObserved = null;
        track.CloseEvidence = TimeSpan.Zero;
        track.CloseSamples = 0;
        track.CandidateWasMissing = false;
    }

    private static void UpdateSelection(Track track, Observation observation, DateTimeOffset frameTime)
    {
        if (!track.Latched && observation.PinchRatio >= ReleaseThreshold)
        {
            track.OpenPose = observation;
            track.OpenFrameTime = frameTime;
            track.SelectionPosition = null;
            track.SelectionFrameTime = null;
            return;
        }
        if (track.SelectionFrameTime is null && observation.PinchRatio < ReleaseThreshold &&
            track.OpenPose is { } openPose && track.OpenFrameTime is { } openTime &&
            frameTime - openTime <= OpenSelectionLifetime)
        {
            // Closing the index finger changes its tip position. Preserve the
            // preceding pointing position before the tighter pinch threshold.
            track.SelectionPosition = openPose.IndexTip;
            track.SelectionFrameTime = openTime;
        }
        if (track.SelectionFrameTime is not { } selectionTime || track.OpenPose is not { } origin) return;
        if (frameTime - selectionTime > SelectionLifetime ||
            Distance(observation.Wrist, origin.Wrist) > origin.PalmScale ||
            Distance(observation.PalmCenter, origin.PalmCenter) > origin.PalmScale)
        {
            // Retain the cancelled anchor instead of falling back to the curled
            // fingertip and potentially clicking a different button. It remains
            // cancelled until the user opens their hand again.
            track.SelectionPosition = new PixelPoint(double.NaN, double.NaN);
        }
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
        return new Observation(points[0], center, scale, points[8], pinchRatio, HandPoseClassifier.IsSpreadOut(hand));
    }

    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private sealed record Observation(PixelPoint Wrist, PixelPoint PalmCenter,
        double PalmScale, PixelPoint IndexTip, double PinchRatio, bool IsSpreadOut);

    private sealed class Track
    {
        public PixelPoint Wrist;
        public PixelPoint PalmCenter;
        public double PalmScale;
        public DateTimeOffset LastSeen;
        public DateTimeOffset? SpreadStarted;
        public DateTimeOffset? SpreadLastObserved;
        public DateTimeOffset? CloseLastObserved;
        public TimeSpan CloseEvidence;
        public int CloseSamples;
        public bool CandidateWasMissing;
        public DateTimeOffset? ReleaseStarted;
        public int ReleaseSamples;
        public bool Latched;
        public Observation? OpenPose;
        public DateTimeOffset? OpenFrameTime;
        public PixelPoint? SelectionPosition;
        public DateTimeOffset? SelectionFrameTime;
        public DateTimeOffset ExecuteUntil = DateTimeOffset.MinValue;
        public long ExecuteEventId;
    }
}
