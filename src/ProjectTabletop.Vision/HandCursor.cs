namespace ProjectTabletop.Vision;

/// <summary>
/// A current fingertip observation and the expiry of its visual pinch pulse.
/// SelectionPosition optionally preserves where the same hand pointed just
/// before closing; Position always remains the actual observed fingertip.
/// A non-finite selection position means the anchor was cancelled and callers
/// must not fall back to Position. SelectionFrameTime is the source camera time
/// of that pointing observation, including after cancellation.
/// </summary>
public sealed record HandCursor(PixelPoint Position, DateTimeOffset ExecuteUntil, long ExecuteEventId = 0,
    PixelPoint? SelectionPosition = null, DateTimeOffset? SelectionFrameTime = null)
{
    public bool IsExecuting(DateTimeOffset now) => now < ExecuteUntil;
}
