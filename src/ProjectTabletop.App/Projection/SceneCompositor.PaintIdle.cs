using System.Numerics;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal const int PaintIdleStreakDropCount = 18;
    internal const double PaintIdleStreakIntervalMilliseconds = 120;
    internal const double PaintIdleSeconds = 10;
    private readonly Queue<PaintDrop> _paintIdleStreakDrops = new();
    private DateTimeOffset? _paintIdleStreakStartedAt, _paintNextIdleStreakAt, _paintNextIdleDropAt;
    private DateTimeOffset? _paintAutomaticLastDrawAt, _paintLastUserActivityAt;
    private long _paintIdleStreakCount;
    private int _paintLastIdlePigment = -1;
#if DEBUG
    private bool _paintIdleStreaksDisabledForVerification;
    private readonly Queue<PaintAutomaticDrop> _paintAutomaticDropsForVerification = new();
    internal sealed record PaintAutomaticDrop(string Kind, Point2 Center, float Radius,
        Vector3 Pigment, DateTimeOffset GeneratedAt, long StreakId);

    internal PaintAutomaticDrop[] GetPaintAutomaticDropsForVerification()
    {
        lock (_gate) return _paintAutomaticDropsForVerification.ToArray();
    }

    internal void DisablePaintIdleStreaksForVerification()
    {
        lock (_gate)
        {
            _paintIdleStreaksDisabledForVerification = true;
            CancelPaintIdleStreak();
            _paintNextIdleStreakAt = null;
        }
    }

    internal void NotifyPaintUserActivityForVerification()
    {
        lock (_gate) NotePaintUserActivity();
    }
#endif

    private void AdvancePaintAutomatic(DateTimeOffset now)
    {
        if (_disposed || _boardSession.Screen != BoardScreen.Paint || _blackOutput || _boardSetup ||
            _calibrationTarget >= 0 || IsBoardRevealActive)
        {
            PausePaintIdle();
            return;
        }
        // A paused/minimized surface gets ten new visible seconds, rather than
        // replaying everything that would have dripped while no draw was shown.
        if (_paintAutomaticLastDrawAt is null || now - _paintAutomaticLastDrawAt > TimeSpan.FromSeconds(PaintIdleSeconds))
        {
            CancelPaintIdleStreak();
            _paintNextIdleStreakAt = now.AddSeconds(PaintIdleSeconds);
        }
        if (_paintAutomaticLastDrawAt is { } last && now < last) return;
        _paintAutomaticLastDrawAt = now;
        AdvancePaintIntroduction(now);
#if DEBUG
        if (_paintIdleStreaksDisabledForVerification) return;
#endif
        if (_paintIdleStreakDrops.Count == 0 && _paintNextIdleStreakAt is { } next && now >= next)
            SchedulePaintIdleStreak(now);
        if (_paintIdleStreakDrops.Count > 0 && _paintNextIdleDropAt is { } due && now >= due)
        {
            var drop = _paintIdleStreakDrops.Dequeue();
            RecordPaintAutomaticDrop("IdleStreak", drop, now, _paintIdleStreakCount);
            QueuePaintDrop(drop);
            var nextDrop = due.AddMilliseconds(PaintIdleStreakIntervalMilliseconds);
            // Slow drawing can lengthen the swing, but cannot dump a backlog
            // of paint in one frame or duplicate it in the second view.
            _paintNextIdleDropAt = nextDrop > now ? nextDrop : now.AddMilliseconds(PaintIdleStreakIntervalMilliseconds);
        }
    }

    private void SchedulePaintIdleStreak(DateTimeOffset now)
    {
        var random = Random.Shared;
        int pigment = random.Next(PaintPigments.Length);
        if (pigment == _paintLastIdlePigment)
            pigment = (pigment + 1 + random.Next(PaintPigments.Length - 1)) % PaintPigments.Length;
        _paintLastIdlePigment = pigment;
        _paintIdleStreakStartedAt = now;
        _paintNextIdleDropAt = now;
        _paintNextIdleStreakAt = now.AddSeconds(PaintIdleSeconds);
        _paintIdleStreakCount++;

        double angle = random.NextDouble() * Math.Tau;
        double aspect = PaintBoardAspect();
        double dx = Math.Cos(angle) / aspect, dy = Math.Sin(angle);
        double nx = -Math.Sin(angle) / aspect, ny = Math.Cos(angle);
        double span = .60 + random.NextDouble() * .20;
        double bow = (.10 + random.NextDouble() * .12) * (random.Next(2) == 0 ? -1 : 1);
        var center = new Point2(.38 + random.NextDouble() * .24, .38 + random.NextDouble() * .24);
        var path = new Point2[PaintIdleStreakDropCount];
        for (int index = 0; index < path.Length; index++)
        {
            // A swinging brush slows near the ends: evenly timed drips cluster
            // there and spread farther apart through the middle of its arc.
            double position = .5 - .5 * Math.Cos(Math.PI * index / (path.Length - 1));
            double across = (position - .5) * span;
            double bend = bow * 4 * position * (1 - position);
            path[index] = new(dx * across + nx * bend, dy * across + ny * bend);
        }
        double extentX = path.Max(point => Math.Abs(point.X));
        double extentY = path.Max(point => Math.Abs(point.Y));
        double scale = Math.Min(1, Math.Min(
            (Math.Min(center.X, 1 - center.X) - .07) / Math.Max(extentX, 1e-6),
            (Math.Min(center.Y, 1 - center.Y) - .07) / Math.Max(extentY, 1e-6)));
        foreach (var point in path)
            _paintIdleStreakDrops.Enqueue(new(new(center.X + point.X * scale, center.Y + point.Y * scale),
                .025f + (float)random.NextDouble() * .018f, PaintPigments[pigment], random.Next(1, 4096)));
    }

    private void NotePaintUserActivity()
    {
        if (_boardSession.Screen != BoardScreen.Paint || _blackOutput || _boardSetup ||
            _calibrationTarget >= 0 || IsBoardRevealActive) return;
        var now = _paintClock();
        _paintLastUserActivityAt = now;
        _paintNextIdleStreakAt = now.AddSeconds(PaintIdleSeconds);
        CancelPaintIdleStreak();
    }

    private void CancelPaintIdleStreak()
    {
        _paintIdleStreakDrops.Clear();
        _paintIdleStreakStartedAt = null;
        _paintNextIdleDropAt = null;
    }

    private void PausePaintIdle()
    {
        CancelPaintIdleStreak();
        _paintAutomaticLastDrawAt = null;
        _paintNextIdleStreakAt = null;
    }

    private void ResetPaintAutomatic()
    {
        PausePaintIdle();
        _paintLastUserActivityAt = null;
        _paintIdleStreakCount = 0;
        _paintLastIdlePigment = -1;
#if DEBUG
        _paintAutomaticDropsForVerification.Clear();
#endif
    }

    private void RecordPaintAutomaticDrop(string kind, PaintDrop drop, DateTimeOffset now, long streakId)
    {
#if DEBUG
        _paintAutomaticDropsForVerification.Enqueue(new(kind, drop.Center, drop.Radius, drop.Pigment, now, streakId));
        while (_paintAutomaticDropsForVerification.Count > 128) _paintAutomaticDropsForVerification.Dequeue();
#endif
    }
}
