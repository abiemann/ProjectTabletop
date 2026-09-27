namespace ProjectTabletop.Vision;

/// <summary>
/// A current fingertip/pose observation and the expiry of its visual pinch pulse.
/// SelectionPosition optionally preserves where the same hand pointed just
/// before closing; Position always remains the actual observed fingertip.
/// A non-finite selection position means the anchor was cancelled and callers
/// must not fall back to Position. SelectionFrameTime is the source camera time
/// of that pointing observation, including after cancellation.
/// </summary>
public sealed record HandCursor(PixelPoint Position, DateTimeOffset ExecuteUntil, long ExecuteEventId = 0,
    PixelPoint? SelectionPosition = null, DateTimeOffset? SelectionFrameTime = null)
{
    /// <summary>Stable identity for the matched hand; never reused after a tracker reset.</summary>
    public long TrackingId { get; init; }
    /// <summary>Read-only snapshot of index, middle, ring and little fingertips in raw camera pixels.</summary>
    public IReadOnlyList<PixelPoint> FingerTips { get; init; } = Array.Empty<PixelPoint>();
    /// <summary>All four front fingers are currently extended; thumb position and finger separation are irrelevant.</summary>
    public bool HasFourExtendedFingers { get; init; }
    /// <summary>Four front fingers are grouped for aiming; current geometry only, with no dwell or command.</summary>
    public bool FingersTogether { get; init; }
    /// <summary>Index extended sideways while middle, ring and little fingers stay grouped.</summary>
    public bool IndexFingerSeparated { get; init; }
    /// <summary>Current spread-hand pose confirmed by fresh observations; never an execute command.</summary>
    public bool IsSpreadOut { get; init; }
    public bool IsExecuting(DateTimeOffset now) => now < ExecuteUntil;
}
