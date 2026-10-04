using System.Diagnostics;

namespace ProjectTabletop.Interaction;

/// <summary>
/// The app's timing clock: UTC when the process started plus elapsed monotonic time.
/// Games, input barriers and camera frames compare times that must never move
/// backwards, so a Windows clock correction cannot freeze or replay board input.
/// Use <see cref="DateTimeOffset.UtcNow"/> only for file names and persisted dates.
/// </summary>
public static class MonotonicClock
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.UtcNow;
    private static readonly long OriginTicks = Stopwatch.GetTimestamp();

    public static DateTimeOffset UtcNow => Origin + Stopwatch.GetElapsedTime(OriginTicks);
}
