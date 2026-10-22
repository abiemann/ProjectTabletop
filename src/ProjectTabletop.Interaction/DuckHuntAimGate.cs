namespace ProjectTabletop.Interaction;

public readonly record struct DuckHuntShot(BoardAim Aim, DateTimeOffset ClickedAt);

/// <summary>
/// Decides each click from one fresh camera snapshot. No prior dot visibility or
/// pending aim is retained. A rejected snapshot cannot be retried with a later dot.
/// Timestamps are host times, not hardware-synchronized exposure timestamps.
/// </summary>
public sealed class DuckHuntAimGate
{
    public static readonly TimeSpan Freshness = TimeSpan.FromMilliseconds(150);
    private DateTimeOffset _resetThrough, _lastClick;

    public void Reset(DateTimeOffset now)
    {
        _resetThrough = _lastClick = now;
    }

    public DuckHuntShot? EvaluateSnapshot(BoardAim? aim, DateTimeOffset frameTime,
        DateTimeOffset clickOnset, DateTimeOffset confirmedAt, DateTimeOffset now)
    {
        if (clickOnset <= _resetThrough || clickOnset <= _lastClick || clickOnset > confirmedAt ||
            confirmedAt > now || now - clickOnset > Freshness) return null;
        _lastClick = clickOnset;
        if (frameTime <= _resetThrough || frameTime < clickOnset || frameTime > confirmedAt ||
            now - frameTime > Freshness || aim is not { } point ||
            !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            point.U is < 0 or > 1 || point.V is < 0 or > 1) return null;
        // Hit testing must use the target present in this photo, especially if
        // audio confirmation crosses the boundary where a replacement duck appears.
        return new(point, frameTime);
    }
}
