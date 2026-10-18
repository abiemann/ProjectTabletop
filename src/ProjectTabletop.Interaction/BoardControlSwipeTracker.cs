namespace ProjectTabletop.Interaction;

/// <summary>A disturbance center in player-facing, normalized board coordinates. Identity is optional.</summary>
public readonly record struct BoardSwipeSample(long TrackingId, double U, double V);

public enum BoardControlSwipe
{
    Toggle
}

/// <summary>Fits a bottom-band motion vector from coordinates, without requiring a tracked hand.</summary>
public sealed class BoardControlSwipeTracker
{
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RearmDelay = TimeSpan.FromMilliseconds(600);
    private Candidate _candidate;
    private DateTimeOffset? _lastFrameTime;
    private DateTimeOffset _rearmAt;

    /// <summary>Only a fresh, established horizontal traversal may suppress caption holds.</summary>
    public bool HasTraversalInProgress { get; private set; }

    public BoardControlSwipe? Update(IReadOnlyList<BoardSwipeSample> samples,
        DateTimeOffset frameTime, DateTimeOffset now)
    {
        HasTraversalInProgress = false;
        if (frameTime > now || now - frameTime > MaximumFrameAge)
        {
            _candidate = default;
            return null;
        }
        if (_lastFrameTime is { } previous && frameTime <= previous) return null;
        _lastFrameTime = frameTime;
        if (frameTime < _rearmAt) return null;

        if (_candidate.Observations != 0 && frameTime - _candidate.LastTime > MaximumGap)
            _candidate = default;
        if (!TryGetCenter(samples, out double u, out double v))
        {
            // Brief empty frames may occur between unrelated optical component
            // IDs. Ambiguous simultaneous disturbances must not be joined.
            if (samples.Count != 0) _candidate = default;
            else
            {
                _candidate.HadMissingFrames = true;
                if (!_candidate.PendingBridge && CompletesDeparture(frameTime)) return Complete(frameTime);
            }
            return null;
        }
        ref var candidate = ref _candidate;
        if (candidate.Observations == 0)
        {
            candidate = Start(u, v, frameTime);
            return null;
        }
        // Start anywhere. Stationary observations supply neither direction nor
        // extra evidence; waiting before the motion does not consume its window.
        if (candidate.Direction == 0)
        {
            if (Math.Abs(u - candidate.StartU) < .035)
            {
                candidate.StartTime = frameTime;
                candidate.LastTime = frameTime;
                candidate.LastU = u;
                candidate.LastV = v;
                candidate.LastMovement = 0;
                // Rebase the sole vertical observation as the hand settles;
                // retain its horizontal anchor to accumulate slow movement.
                candidate.SumV = candidate.MinV = candidate.MaxV = v;
                return null;
            }
            candidate.Direction = u > candidate.StartU ? 1 : -1;
        }
        var duration = frameTime - candidate.StartTime;
        double minV = Math.Min(candidate.MinV, v);
        double maxV = Math.Max(candidate.MaxV, v);
        double movement = (u - candidate.LastU) * candidate.Direction;
        double reversal = (candidate.FurthestU - u) * candidate.Direction;
        if (duration > MaximumDuration || Math.Abs(u - candidate.LastU) > .40 &&
            !TryBeginGapBridge(ref candidate, u, v, frameTime))
        {
            candidate = Start(u, v, frameTime);
            return null;
        }
        if (candidate.PendingBridge)
        {
            double bridgeSeconds = (frameTime - candidate.BridgeTime).TotalSeconds;
            double bridgeProgress = (u - candidate.BridgeU) * candidate.Direction;
            // Reacquisition is provisional. A stationary endpoint or one flash
            // cannot stand in for the missing middle of a sweep.
            if (frameTime - candidate.BridgeTime > MaximumGap || movement < -.035 ||
                bridgeProgress < -.035 || bridgeProgress > candidate.BridgeSpeed * 2 * bridgeSeconds + .035 ||
                Math.Abs(v - candidate.BridgeV) > Math.Max(.035, Math.Abs(bridgeProgress) * .6))
            {
                candidate = Start(u, v, frameTime);
                return null;
            }
            if (bridgeProgress >= .035) candidate.PendingBridge = false;
        }
        candidate.HadMissingFrames = false;
        if (reversal > .10)
        {
            // A new sweep in the opposite direction starts at the observed
            // turning point; discard the previous direction's evidence.
            candidate = Start(candidate.LastU, candidate.LastV, candidate.LastTime);
            candidate.Direction = u > candidate.StartU ? 1 : -1;
            duration = frameTime - candidate.StartTime;
            minV = Math.Min(candidate.MinV, v);
            maxV = Math.Max(candidate.MaxV, v);
            movement = Math.Abs(u - candidate.LastU);
        }
        if (maxV - minV > .22)
        {
            candidate = Start(u, v, frameTime);
            return null;
        }
        candidate.LastTime = frameTime;
        candidate.LastU = u;
        candidate.LastV = v;
        candidate.LastMovement = movement;
        candidate.MinV = minV;
        candidate.MaxV = maxV;
        // Distinct progress, not repeated frames or component count, supplies
        // the bounded stream's least-squares horizontal and vertical velocity.
        if ((u - candidate.LastEvidenceU) * candidate.Direction >= .035)
        {
            candidate.Observations++;
            candidate.LastEvidenceU = u;
            double seconds = duration.TotalSeconds;
            candidate.SumTime += seconds;
            candidate.SumTimeSquared += seconds * seconds;
            candidate.SumU += u;
            candidate.SumV += v;
            candidate.SumTimeU += seconds * u;
            candidate.SumTimeV += seconds * v;
        }
        if (candidate.PendingBridge) return null;
        if (candidate.Observations >= 3 && movement > .002 &&
            (u - candidate.StartU) * candidate.Direction >= .10)
            HasTraversalInProgress = true;
        if ((u - candidate.FurthestU) * candidate.Direction > 0)
            candidate.FurthestU = u;
        bool reachesEdge = candidate.Direction > 0 ? u >= .85 - 1e-9 : u <= .15 + 1e-9;
        if (!reachesEdge) return null;
        if (duration < MinimumDuration)
        {
            candidate = Start(u, v, frameTime);
            return null;
        }
        if (!TryGetDepartureVector(frameTime, out _)) return null;
        return Complete(frameTime);
    }

    private bool CompletesDeparture(DateTimeOffset frameTime)
    {
        double missingMilliseconds = (frameTime - _candidate.LastTime).TotalMilliseconds;
        if (missingMilliseconds is < 60 or > 250 || _candidate.LastMovement <= .002 ||
            !TryGetDepartureVector(frameTime, out double horizontalVelocity)) return false;
        bool nearEdge = _candidate.Direction > 0 ? _candidate.LastU >= .75 : _candidate.LastU <= .25;
        double projectedU = _candidate.LastU + horizontalVelocity * missingMilliseconds / 1000;
        return nearEdge && (_candidate.Direction > 0 ? projectedU >= .85 : projectedU <= .15);
    }

    private static bool TryBeginGapBridge(ref Candidate candidate, double u, double v, DateTimeOffset frameTime)
    {
        double progress = (candidate.LastU - candidate.StartU) * candidate.Direction;
        double gapSeconds = (frameTime - candidate.LastTime).TotalSeconds;
        double jump = (u - candidate.LastU) * candidate.Direction;
        if (candidate.PendingBridge || !candidate.HadMissingFrames || candidate.Observations < 4 ||
            progress < .15 || candidate.LastMovement <= .002 ||
            candidate.LastTime - candidate.StartTime < MinimumDuration ||
            !TryFitVelocity(candidate, out double velocity) || jump <= 0 ||
            Math.Abs(v - candidate.LastV) > jump * .6 ||
            jump > Math.Abs(velocity) * 2 * gapSeconds + .035) return false;
        // Permit bounded acceleration during an observed gap, then require
        // independently observed movement on the far side before joining it.
        // Measure that local confirmation from the reacquired position; drift
        // across the gap has already passed the cross-gap slope check above.
        candidate.PendingBridge = true;
        candidate.BridgeU = u;
        candidate.BridgeV = v;
        candidate.BridgeTime = frameTime;
        candidate.BridgeSpeed = Math.Abs(velocity);
        return true;
    }

    private bool TryGetDepartureVector(DateTimeOffset frameTime, out double horizontalVelocity)
    {
        horizontalVelocity = 0;
        ref var candidate = ref _candidate;
        var duration = frameTime - candidate.StartTime;
        if (candidate.Observations < 4 || duration < MinimumDuration || duration > MaximumDuration ||
            (candidate.LastU - candidate.StartU) * candidate.Direction < .35 - 1e-9) return false;
        return TryFitVelocity(candidate, out horizontalVelocity);
    }

    private static bool TryFitVelocity(in Candidate candidate, out double horizontalVelocity)
    {
        horizontalVelocity = 0;
        double variance = candidate.Observations * candidate.SumTimeSquared - candidate.SumTime * candidate.SumTime;
        if (variance <= 1e-9) return false;
        horizontalVelocity = (candidate.Observations * candidate.SumTimeU - candidate.SumTime * candidate.SumU) / variance;
        double verticalVelocity = (candidate.Observations * candidate.SumTimeV - candidate.SumTime * candidate.SumV) / variance;
        return horizontalVelocity * candidate.Direction > 0 && Math.Abs(verticalVelocity) <= Math.Abs(horizontalVelocity) * .6;
    }

    private BoardControlSwipe Complete(DateTimeOffset frameTime)
    {
        HasTraversalInProgress = false;
        _candidate = default;
        _rearmAt = frameTime + RearmDelay;
        return BoardControlSwipe.Toggle;
    }

    public void Reset()
    {
        HasTraversalInProgress = false;
        _candidate = default;
        _lastFrameTime = null;
        _rearmAt = default;
    }

    private static bool TryGetCenter(IReadOnlyList<BoardSwipeSample> samples, out double u, out double v)
    {
        double minU = 1, maxU = 0, minV = 1, maxV = 0, sumU = 0, sumV = 0;
        int count = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (!double.IsFinite(sample.U) || !double.IsFinite(sample.V) ||
                sample.U is < 0 or > 1 || sample.V is < 0 or > 1) continue;
            minU = Math.Min(minU, sample.U); maxU = Math.Max(maxU, sample.U);
            minV = Math.Min(minV, sample.V); maxV = Math.Max(maxV, sample.V);
            sumU += sample.U; sumV += sample.V;
            count++;
        }
        u = count > 0 ? sumU / count : 0;
        v = count > 0 ? sumV / count : 0;
        // Nearby components can be separate fingers or parts of one optical
        // disturbance. Opposing edges or scattered changes are not one sweep.
        return count > 0 && maxU - minU <= .12 && maxV - minV <= .22;
    }

    private static Candidate Start(double u, double v, DateTimeOffset frameTime)
    {
        return new Candidate
        {
            StartU = u,
            FurthestU = u,
            LastU = u,
            LastV = v,
            LastEvidenceU = u,
            MinV = v,
            MaxV = v,
            StartTime = frameTime,
            LastTime = frameTime,
            SumU = u,
            SumV = v,
            Observations = 1
        };
    }

    private struct Candidate
    {
        public int Direction;
        public double StartU, FurthestU, LastU, LastV, LastEvidenceU, LastMovement, MinV, MaxV;
        public double SumTime, SumTimeSquared, SumU, SumV, SumTimeU, SumTimeV;
        public DateTimeOffset StartTime, LastTime;
        public int Observations;
        public bool HadMissingFrames, PendingBridge;
        public double BridgeU, BridgeV, BridgeSpeed;
        public DateTimeOffset BridgeTime;
    }
}
