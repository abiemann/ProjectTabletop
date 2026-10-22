namespace ProjectTabletop.Interaction;

public readonly record struct DuckHuntMeasurementSnapshot(
    BoardAim? LiveDetectedXY, DateTimeOffset? LiveFrameTimestamp,
    BoardAim? OldDetectedXY, DateTimeOffset? OldFrameTimestamp,
    BoardAim? NewDetectedXY, DateTimeOffset? NewFrameTimestamp, DateTimeOffset? ClickOnset)
{
    public bool HasCompletedPair => OldDetectedXY.HasValue && NewDetectedXY.HasValue;
}

/// <summary>
/// Keeps a short history of completed camera observations, independent of audio
/// completion order. Each fresh click freezes the latest valid pre-onset point
/// and its own snapshot point. Continued tracking cannot move that measurement.
/// Coordinates stay normalized; the renderer computes distance in display pixels.
/// Callers serialize access with the scene's existing input lock.
/// </summary>
public sealed class DuckHuntMeasurementTracker
{
    private readonly record struct Observation(BoardAim? Aim, DateTimeOffset FrameTimestamp);
    private readonly Observation[] _history = new Observation[32];
    private readonly DuckHuntAimGate _snapshotGate = new();
    private int _count, _next;
    private DateTimeOffset _resetThrough, _lastFrame;
    private BoardAim? _live;
    private DuckHuntMeasurementSnapshot _measurement;

    public void Reset(DateTimeOffset now)
    {
        _snapshotGate.Reset(now);
        _resetThrough = _lastFrame = now;
        _count = _next = 0;
        _live = null;
        _measurement = default;
    }

    public bool Observe(BoardAim? aim, DateTimeOffset frameTimestamp, DateTimeOffset now)
    {
        if (frameTimestamp <= _resetThrough || frameTimestamp <= _lastFrame || frameTimestamp > now ||
            now - frameTimestamp > DuckHuntAimGate.Freshness) return false;
        _lastFrame = frameTimestamp;
        _live = Valid(aim) ? aim : null;
        _history[_next] = new(_live, frameTimestamp);
        _next = (_next + 1) % _history.Length;
        _count = Math.Min(_count + 1, _history.Length);
        return true;
    }

    public bool Capture(BoardAim? aim, DateTimeOffset frameTimestamp, DateTimeOffset clickOnset,
        DateTimeOffset confirmedAt, DateTimeOffset now)
    {
        var shot = _snapshotGate.EvaluateSnapshot(aim, frameTimestamp, clickOnset, confirmedAt, now);
        if (shot is not { } current) return false;

        Observation? previous = null;
        for (int i = 0; i < _count; i++)
        {
            var sample = _history[(_next - 1 - i + _history.Length) % _history.Length];
            // A continuous detector can finish a post-click frame before the
            // independent click worker finishes. It must not become the old point.
            if (sample.FrameTimestamp >= clickOnset) continue;
            if (clickOnset - sample.FrameTimestamp > DuckHuntAimGate.Freshness) break;
            if (sample.Aim is null) continue;
            previous = sample;
            break;
        }

        // Preserve a valid new point even when no recent old point exists. The
        // renderer must not invent a radius or borrow a prior click's centre.
        _measurement = new(null, null, previous?.Aim, previous?.FrameTimestamp,
            current.Aim, frameTimestamp, clickOnset);
        return true;
    }

    public DuckHuntMeasurementSnapshot GetSnapshot(DateTimeOffset now) => _measurement with
    {
        LiveDetectedXY = _lastFrame <= now && now - _lastFrame <= DuckHuntAimGate.Freshness ? _live : null,
        LiveFrameTimestamp = _count == 0 ? null : _lastFrame
    };

    private static bool Valid(BoardAim? aim) => aim is { } point &&
        double.IsFinite(point.U) && double.IsFinite(point.V) &&
        point.U is >= 0 and <= 1 && point.V is >= 0 and <= 1;
}
