namespace ProjectTabletop.Vision;

/// <summary>
/// Display-only fingertip filtering in camera pixels. Commands, target positions
/// and pose classification must continue to use the original cursors and hands.
/// Missing observations never produce a cursor, and rejected frames cannot move
/// the retained history. Call Update and Reset on the same thread.
/// </summary>
public sealed class HandVisualSmoother
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(350);
    private const double HoldMilliseconds = 140;
    private const double FollowMilliseconds = 28;
    private const double JitterPalmFraction = .035;
    private const double MotionPalmFraction = .18;
    private const double ResetPalmFraction = .8;
    private readonly Dictionary<long, Track> _tracks = [];
    private DateTimeOffset? _lastFrameTime, _lastNow;

    private sealed record Track(DateTimeOffset FrameTime, PixelPoint RawPosition, PixelPoint Position,
        PixelPoint[] Fingers, PixelPoint Wrist, PixelPoint PalmCenter, double PalmScale);

    public IReadOnlyList<HandCursor> Update(IReadOnlyList<HandCursor> cursors,
        IReadOnlyList<HandDetection> hands, DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(hands);
        if (frameTime > now || now - frameTime > Lifetime ||
            _lastFrameTime is { } priorFrame && frameTime <= priorFrame ||
            _lastNow is { } priorNow && now < priorNow) return Array.Empty<HandCursor>();
        _lastFrameTime = frameTime; _lastNow = now;
        foreach (long id in _tracks.Where(pair => frameTime - pair.Value.FrameTime > Lifetime)
            .Select(pair => pair.Key).ToArray()) _tracks.Remove(id);
        var duplicateIds = cursors.Where(cursor => cursor.TrackingId > 0)
            .GroupBy(cursor => cursor.TrackingId).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        var result = new List<HandCursor>(cursors.Count);
        foreach (HandCursor cursor in cursors)
        {
            PixelPoint[] fingers = cursor.FingerTips.ToArray();
            if (cursor.TrackingId <= 0 || duplicateIds.Contains(cursor.TrackingId) ||
                !Finite(cursor.Position) || fingers.Length is not (0 or 4) || fingers.Any(point => !Finite(point)) ||
                fingers.Length == 4 && fingers[0] != cursor.Position ||
                !TryPalm(cursor, hands, out var wrist, out var palm, out double scale))
            {
                _tracks.Remove(cursor.TrackingId);
                result.Add(cursor with { FingerTips = Array.AsReadOnly(fingers) });
                continue;
            }

            PixelPoint position = cursor.Position;
            if (_tracks.TryGetValue(cursor.TrackingId, out var previous) &&
                Distance(previous.RawPosition, position) <= ResetPalmFraction * Math.Max(scale, previous.PalmScale) &&
                Distance(previous.Wrist, wrist) <= ResetPalmFraction * Math.Max(scale, previous.PalmScale) &&
                Distance(previous.PalmCenter, palm) <= ResetPalmFraction * Math.Max(scale, previous.PalmScale) &&
                scale / previous.PalmScale is >= .55 and <= 1.8)
            {
                double elapsed = (frameTime - previous.FrameTime).TotalMilliseconds;
                position = Smooth(previous.Position, position, scale, elapsed);
                if (fingers.Length == 4)
                {
                    fingers[0] = position;
                    if (previous.Fingers.Length == 4)
                        for (int index = 1; index < 4; index++)
                            fingers[index] = Distance(previous.Fingers[index], fingers[index]) > ResetPalmFraction * scale
                                ? fingers[index] : Smooth(previous.Fingers[index], fingers[index], scale, elapsed);
                }
            }
            _tracks[cursor.TrackingId] = new(frameTime, cursor.Position, position, fingers.ToArray(), wrist, palm, scale);
            result.Add(cursor with { Position = position, FingerTips = Array.AsReadOnly(fingers) });
        }
        return result.AsReadOnly();
    }

    public void Reset()
    {
        _tracks.Clear();
        _lastFrameTime = _lastNow = null;
    }

    private static PixelPoint Smooth(PixelPoint previous, PixelPoint current, double scale, double elapsed)
    {
        double movement = Distance(previous, current) / scale;
        double t = Math.Clamp((movement - JitterPalmFraction) / (MotionPalmFraction - JitterPalmFraction), 0, 1);
        t = t * t * (3 - 2 * t);
        double timeConstant = HoldMilliseconds + (FollowMilliseconds - HoldMilliseconds) * t;
        double alpha = 1 - Math.Exp(-elapsed / timeConstant);
        return new(previous.X + (current.X - previous.X) * alpha,
            previous.Y + (current.Y - previous.Y) * alpha);
    }

    private static bool TryPalm(HandCursor cursor, IReadOnlyList<HandDetection> hands,
        out PixelPoint wrist, out PixelPoint center, out double scale)
    {
        wrist = center = default; scale = 0;
        HandDetection? matched = null;
        foreach (var hand in hands)
        {
            if (hand.Landmarks.Count != 21 || hand.IndexTip != cursor.Position) continue;
            if (matched is not null) return false; // Ambiguous raw fingertip cannot establish identity.
            matched = hand;
        }
        if (matched is null || !double.IsFinite(matched.Confidence) || matched.Confidence is < 0 or > 1 ||
            !double.IsFinite(matched.RightHandProbability) || matched.RightHandProbability is < 0 or > 1 ||
            matched.Landmarks.Any(point => !Finite(point))) return false;
        var points = matched.Landmarks;
        wrist = points[0];
        center = new((points[0].X + points[5].X + points[9].X + points[13].X + points[17].X) / 5,
            (points[0].Y + points[5].Y + points[9].Y + points[13].Y + points[17].Y) / 5);
        scale = Math.Max(Distance(points[0], points[9]), Distance(points[5], points[17]));
        return double.IsFinite(scale) && scale > 1;
    }

    private static bool Finite(PixelPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
