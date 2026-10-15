namespace ProjectTabletop.Interaction;

/// <summary>A stable observation's center in player-facing, normalized board coordinates.</summary>
public readonly record struct BoardSwipeSample(long TrackingId, double U, double V);

public enum BoardControlSwipe
{
    Hide,
    Show
}

/// <summary>Recognizes deliberate horizontal crossings without joining different tracks or stale frames.</summary>
public sealed class BoardControlSwipeTracker
{
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RearmDelay = TimeSpan.FromMilliseconds(600);
    private readonly Candidate[] _candidates = new Candidate[2];
    private DateTimeOffset? _lastFrameTime;
    private DateTimeOffset _rearmAt;

    /// <summary>Only a fresh, established horizontal traversal may suppress caption holds.</summary>
    public bool HasTraversalInProgress { get; private set; }

    public BoardControlSwipe? Update(IReadOnlyList<BoardSwipeSample> samples,
        DateTimeOffset frameTime, DateTimeOffset now)
    {
        HasTraversalInProgress = false;
        if (frameTime > now || now - frameTime > MaximumGap)
        {
            Array.Clear(_candidates);
            return null;
        }
        if (_lastFrameTime is { } previous && frameTime <= previous) return null;
        _lastFrameTime = frameTime;
        if (frameTime < _rearmAt) return null;

        for (int i = 0; i < _candidates.Length; i++)
            if (_candidates[i].TrackingId != 0 && frameTime - _candidates[i].LastTime > MaximumGap)
                _candidates[i] = default;

        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (sample.TrackingId <= 0 || !double.IsFinite(sample.U) || !double.IsFinite(sample.V) ||
                sample.U is < 0 or > 1 || sample.V is < 0 or > 1) continue;
            int index = FindCandidate(sample.TrackingId);
            if (index < 0) continue;
            ref var candidate = ref _candidates[index];
            if (candidate.TrackingId == 0)
            {
                candidate = Start(sample, frameTime);
                continue;
            }
            // A repeated tracking ID in one frame must not add another observation.
            if (candidate.LastTime == frameTime) continue;
            // Waiting at the starting edge is not part of the movement duration.
            bool startingEdge = candidate.Direction > 0 ? sample.U <= .25 : sample.U >= .75;
            if (startingEdge)
            {
                candidate = Start(sample, frameTime);
                continue;
            }
            var duration = frameTime - candidate.StartTime;
            double minV = Math.Min(candidate.MinV, sample.V);
            double maxV = Math.Max(candidate.MaxV, sample.V);
            double reversal = (candidate.FurthestU - sample.U) * candidate.Direction;
            if (duration > MaximumDuration || maxV - minV > .22 || reversal > .10)
            {
                candidate = Start(sample, frameTime);
                continue;
            }
            candidate.LastTime = frameTime;
            candidate.MinV = minV;
            candidate.MaxV = maxV;
            candidate.Observations++;
            if (candidate.Observations >= 3 &&
                (sample.U - candidate.StartU) * candidate.Direction >= .10)
                HasTraversalInProgress = true;
            if ((sample.U - candidate.FurthestU) * candidate.Direction > 0)
                candidate.FurthestU = sample.U;
            bool oppositeEdge = candidate.Direction > 0 ? sample.U >= .75 : sample.U <= .25;
            if (!oppositeEdge || (sample.U - candidate.StartU) * candidate.Direction < .50) continue;
            if (duration < MinimumDuration)
            {
                candidate = Start(sample, frameTime);
                continue;
            }
            if (candidate.Observations < 4) continue;
            var result = candidate.Direction > 0 ? BoardControlSwipe.Show : BoardControlSwipe.Hide;
            HasTraversalInProgress = false;
            Array.Clear(_candidates);
            _rearmAt = frameTime + RearmDelay;
            return result;
        }
        return null;
    }

    public void Reset()
    {
        HasTraversalInProgress = false;
        Array.Clear(_candidates);
        _lastFrameTime = null;
        _rearmAt = default;
    }

    private int FindCandidate(long trackingId)
    {
        int empty = -1;
        for (int i = 0; i < _candidates.Length; i++)
        {
            if (_candidates[i].TrackingId == trackingId) return i;
            if (_candidates[i].TrackingId == 0) empty = i;
        }
        return empty;
    }

    private static Candidate Start(BoardSwipeSample sample, DateTimeOffset frameTime)
    {
        int direction = sample.U <= .25 ? 1 : sample.U >= .75 ? -1 : 0;
        return direction == 0 ? default : new Candidate
        {
            TrackingId = sample.TrackingId,
            Direction = direction,
            StartU = sample.U,
            FurthestU = sample.U,
            MinV = sample.V,
            MaxV = sample.V,
            StartTime = frameTime,
            LastTime = frameTime,
            Observations = 1
        };
    }

    private struct Candidate
    {
        public long TrackingId;
        public int Direction;
        public double StartU, FurthestU, MinV, MaxV;
        public DateTimeOffset StartTime, LastTime;
        public int Observations;
    }
}
