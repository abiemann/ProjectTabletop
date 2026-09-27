using ProjectTabletop.Vision;

internal static class PhotoCopySelectionRegression
{
    public static void Run()
    {
        HandDetection open = Hand(.7), pinch = Hand(.15, 350);
        var cursor = Cursor(pinch);
        Require(PhotoCopyHandSelector.TrySelect([open, pinch], cursor, out var selected) && ReferenceEquals(selected, open),
            "Photo Copy did not select the other, non-pinching hand.");
        Require(PhotoCopyHandSelector.TrySelect([pinch, open], cursor, out selected) && ReferenceEquals(selected, open),
            "Reordering detections changed which hand Photo Copy selected.");
        Require(!PhotoCopyHandSelector.TrySelect([Hand(.2), pinch], cursor, out _),
            "Two simultaneous pinches selected a hand to photograph.");
        Require(!PhotoCopyHandSelector.TrySelect([pinch], cursor, out _),
            "The triggering hand was photographed when the other hand was missing.");
        Require(!PhotoCopyHandSelector.TrySelect([open], cursor, out _),
            "Photo Copy accepted a trigger absent from the current detections.");
        Require(!PhotoCopyHandSelector.TrySelect([open, pinch], Cursor(open), out _),
            "A cursor on the non-pinching hand selected the pinching hand for capture.");
        Require(!PhotoCopyHandSelector.TrySelect([open, pinch], cursor with { ExecuteEventId = 0 }, out _),
            "An unconfirmed gesture selected a hand to photograph.");
        Require(!PhotoCopyHandSelector.TrySelect([open, pinch], cursor with { Position = new(999, 999) }, out _),
            "An unmatched cursor was assigned to a distant hand.");
        Require(!PhotoCopyHandSelector.TrySelect([Hand(.7, 350), pinch], cursor, out _),
            "Ambiguous overlapping fingertips selected an arbitrary hand.");
        Require(!PhotoCopyHandSelector.TrySelect([open, pinch, Hand(.7, 700)], cursor, out _),
            "More than two hands bypassed the two-hand selection gate.");
        Require(!PhotoCopyHandSelector.TrySelect([open with { Landmarks = Array.Empty<PixelPoint>() }, pinch], cursor, out _),
            "Incomplete landmarks reached Photo Copy capture.");
        Require(!PhotoCopyHandSelector.TrySelect([open with { Confidence = double.NaN }, pinch], cursor, out _),
            "Invalid confidence reached Photo Copy capture.");

        foreach (double angle in new[] { 0.0, Math.PI / 2, 2.4 })
        foreach (double scale in new[] { .3, 1.0, 2.0 })
        {
            open = Hand(.7, 250, scale, angle);
            pinch = Hand(.15, 650, scale, angle);
            Require(PhotoCopyHandSelector.TrySelect([pinch, open], Cursor(pinch), out selected) && ReferenceEquals(selected, open),
                "Rotation or hand size changed the other-hand selection.");
        }
        Console.WriteLine("Photo Copy selection regression: other-hand capture, detection order, both-pinched rejection, " +
            "missing/invalid observations, ambiguous fingertips and pose invariance passed.");
    }

    private static HandDetection Hand(double pinchRatio, double offset = 0, double scale = 1, double angle = 0)
    {
        var points = Enumerable.Repeat(new PixelPoint(0, 0), 21).ToArray();
        points[0] = new(0, 100);
        points[5] = new(-50, 0);
        points[17] = new(50, 0);
        points[8] = new(0, -80);
        points[4] = new(100 * pinchRatio, -80);
        return new(points.Select(point => new PixelPoint(
            offset + scale * (point.X * Math.Cos(angle) - point.Y * Math.Sin(angle)),
            300 + scale * (point.X * Math.Sin(angle) + point.Y * Math.Cos(angle)))).ToArray(), .9, .5);
    }

    private static HandCursor Cursor(HandDetection hand) => new(hand.IndexTip, DateTimeOffset.UtcNow.AddSeconds(1), 1);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
