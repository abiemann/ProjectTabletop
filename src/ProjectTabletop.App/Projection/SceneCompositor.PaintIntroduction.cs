using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal const int PaintIntroductionDropCount = 5;
    internal const double PaintIntroductionDropIntervalMilliseconds = 180;
    private readonly Queue<PaintDrop> _paintIntroductionDrops = new();
    private DateTimeOffset? _paintIntroductionStartedAt;

    private void SchedulePaintIntroduction()
    {
        CancelPaintIntroduction();
        var random = Random.Shared;
        var pigments = Enumerable.Range(0, PaintPigments.Length).OrderBy(_ => random.Next()).ToArray();
        double direction = random.NextDouble() * Math.Tau;
        for (int index = 0; index < PaintIntroductionDropCount; index++)
        {
            double angle = direction + index * Math.Tau / (PaintIntroductionDropCount - 1) +
                (random.NextDouble() - .5) * .5;
            double distance = .045 + random.NextDouble() * .035;
            var center = index == PaintIntroductionDropCount - 1 ? new Point2(.5, .5) :
                new Point2(.5 + Math.Cos(angle) * distance, .5 + Math.Sin(angle) * distance);
            _paintIntroductionDrops.Enqueue(new(center, .045f + (float)random.NextDouble() * .025f,
                PaintPigments[pigments[index]], random.Next(1, 4096)));
        }
    }

    private void AdvancePaintIntroduction(DateTimeOffset now)
    {
        if (_paintIntroductionDrops.Count == 0 || _boardSession.Screen != BoardScreen.Paint ||
            _blackOutput || _boardSetup || _calibrationTarget >= 0 || IsBoardRevealActive) return;
        // Start on the first visible surface, so calibration or waiting to open
        // the board cannot consume the introductory animation offscreen.
        _paintIntroductionStartedAt ??= now;
        double elapsed = Math.Max(0, (now - _paintIntroductionStartedAt.Value).TotalMilliseconds);
        while (_paintIntroductionDrops.Count > 0 && elapsed >=
            (PaintIntroductionDropCount - _paintIntroductionDrops.Count) * PaintIntroductionDropIntervalMilliseconds)
        {
            var drop = _paintIntroductionDrops.Dequeue();
            RecordPaintAutomaticDrop("Introduction", drop, now, 0);
            QueuePaintDrop(drop);
        }
    }

    private void QueuePaintDrop(PaintDrop drop)
    {
        _paintPendingDrops.Enqueue(drop);
        _paintDropCount++;
        _paintRevision++;
        // Generated hints never enter the camera's timestamps or cooldowns.
    }

    private void CancelPaintIntroduction()
    {
        _paintIntroductionDrops.Clear();
        _paintIntroductionStartedAt = null;
    }
}
