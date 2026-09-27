using System.Diagnostics.CodeAnalysis;

namespace ProjectTabletop.Vision;

/// <summary>Identifies the shutter hand and optional other hand in a confirmed pinch observation.</summary>
public static class PhotoCopyHandSelector
{
    private const double ReleaseThreshold = .45;

    /// <summary>
    /// The caller supplies a fresh, newly confirmed pinch cursor and detections
    /// from that same frame. This method does not recognize or replay events.
    /// Ambiguous overlapping fingertips, two pinches, and a missing second hand
    /// are rejected rather than photographing the hand making the command.
    /// </summary>
    public static bool TrySelect(IReadOnlyList<HandDetection> hands, HandCursor triggeringCursor,
        [NotNullWhen(true)] out HandDetection? photoHand)
    {
        ArgumentNullException.ThrowIfNull(hands);
        ArgumentNullException.ThrowIfNull(triggeringCursor);
        photoHand = null;
        if (hands.Count != 2 || !TryMatchShutter(hands, triggeringCursor, out int matchedIndex)) return false;

        photoHand = hands[1 - matchedIndex];
        return true;
    }

    /// <summary>
    /// Identifies the hand to exclude from a photo of an object on the board.
    /// The caller supplies a fresh, newly confirmed pinch and detections from
    /// that same frame. Match the actual fingertip, never the earlier button
    /// selection anchor. One or two hands are allowed, but only one may pinch.
    /// </summary>
    public static bool TrySelectShutter(IReadOnlyList<HandDetection> hands, HandCursor triggeringCursor,
        [NotNullWhen(true)] out HandDetection? shutter)
    {
        ArgumentNullException.ThrowIfNull(hands);
        ArgumentNullException.ThrowIfNull(triggeringCursor);
        shutter = null;
        if (!TryMatchShutter(hands, triggeringCursor, out int matchedIndex)) return false;

        shutter = hands[matchedIndex];
        return true;
    }

    private static bool TryMatchShutter(IReadOnlyList<HandDetection> hands, HandCursor triggeringCursor,
        out int matchedIndex)
    {
        matchedIndex = -1;
        if (hands.Count is < 1 or > 2 || triggeringCursor.ExecuteEventId <= 0 ||
            !Finite(triggeringCursor.Position)) return false;

        var pinchRatios = new double[hands.Count];
        for (int index = 0; index < hands.Count; index++)
        {
            if (!TryObserve(hands[index], out double scale, out double ratio)) return false;
            pinchRatios[index] = ratio;
            if (Distance(hands[index].IndexTip, triggeringCursor.Position) > Math.Max(2, .1 * scale)) continue;
            if (matchedIndex >= 0) return false;
            matchedIndex = index;
        }
        return matchedIndex >= 0 && pinchRatios[matchedIndex] < ReleaseThreshold &&
            (hands.Count == 1 || pinchRatios[1 - matchedIndex] >= ReleaseThreshold);
    }

    private static bool TryObserve(HandDetection? hand, out double scale, out double pinchRatio)
    {
        scale = pinchRatio = 0;
        if (hand?.Landmarks is not { Count: 21 } points || points.Any(point => !Finite(point)) ||
            !double.IsFinite(hand.Confidence) || hand.Confidence is < 0 or > 1 ||
            !double.IsFinite(hand.RightHandProbability) || hand.RightHandProbability is < 0 or > 1) return false;
        scale = Math.Max(Distance(points[0], points[9]), Distance(points[5], points[17]));
        if (!double.IsFinite(scale) || scale <= 1) return false;
        pinchRatio = Distance(points[4], points[8]) / scale;
        return double.IsFinite(pinchRatio);
    }

    private static bool Finite(PixelPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
}
