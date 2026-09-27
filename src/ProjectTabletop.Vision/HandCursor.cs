namespace ProjectTabletop.Vision;

/// <summary>A current fingertip observation and the expiry of its visual pinch pulse.</summary>
public sealed record HandCursor(PixelPoint Position, DateTimeOffset ExecuteUntil, long ExecuteEventId = 0)
{
    public bool IsExecuting(DateTimeOffset now) => now < ExecuteUntil;
}
