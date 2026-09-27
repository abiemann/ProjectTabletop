using ProjectTabletop.Vision;

internal static class PhotoCopySelectionRegression
{
    public static void Run()
    {
        CheckShutterSelection();
        HandDetection open = Hand(.7), pinch = Hand(.15, 350);
        var cursor = Cursor(pinch);
        Require(PhotoCopyHandSelector.TrySelect([open, pinch], cursor, out var selected) && ReferenceEquals(selected, open),
            "Photo Copy did not select the other, non-pinching hand.");
        Require(PhotoCopyHandSelector.TrySelect([pinch, open], cursor, out selected) && ReferenceEquals(selected, open),
            "Reordering detections changed which hand Photo Copy selected.");
        Require(PhotoCopyHandSelector.TrySelect([open, pinch], cursor with
            { SelectionPosition = open.IndexTip, SelectionFrameTime = DateTimeOffset.UtcNow }, out selected) &&
            ReferenceEquals(selected, open), "A button selection anchor replaced the real pinching fingertip during photo capture.");
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

    private static void CheckShutterSelection()
    {
        HandDetection open = Hand(.7), pinch = Hand(.15, 350);
        var cursor = Cursor(pinch);
        Require(PhotoCopyHandSelector.TrySelectShutter([pinch], cursor, out var shutter) &&
            ReferenceEquals(shutter, pinch), "A single confirmed pinching hand could not act as the shutter.");
        Require(PhotoCopyHandSelector.TrySelectShutter([open, pinch], cursor, out shutter) &&
            ReferenceEquals(shutter, pinch), "The non-pinching hand was selected as the shutter.");
        Require(PhotoCopyHandSelector.TrySelectShutter([pinch, open], cursor, out shutter) &&
            ReferenceEquals(shutter, pinch), "Detection order changed the shutter hand.");
        Require(PhotoCopyHandSelector.TrySelectShutter([open, pinch], cursor with
            { SelectionPosition = open.IndexTip, SelectionFrameTime = DateTimeOffset.UtcNow }, out shutter) &&
            ReferenceEquals(shutter, pinch), "The earlier button anchor replaced the actual shutter fingertip.");
        Require(PhotoCopyHandSelector.TrySelectShutter([pinch], cursor with
            { SelectionPosition = new(double.NaN, double.NaN) }, out shutter) && ReferenceEquals(shutter, pinch),
            "A cancelled button anchor blocked an otherwise valid shutter pinch.");
        Require(!PhotoCopyHandSelector.TrySelectShutter([pinch], cursor with
            { Position = new(999, 999), SelectionPosition = pinch.IndexTip }, out shutter) && shutter is null,
            "A selection anchor incorrectly rescued an unmatched actual fingertip.");
        Require(!PhotoCopyHandSelector.TrySelectShutter([Hand(.2), pinch], cursor, out shutter) && shutter is null,
            "Two pinching hands were accepted as an unambiguous shutter.");
        Require(!PhotoCopyHandSelector.TrySelectShutter([Hand(.7, 350), pinch], cursor, out shutter) && shutter is null,
            "Overlapping fingertips arbitrarily selected a shutter.");
        Require(!PhotoCopyHandSelector.TrySelectShutter([open], Cursor(open), out _),
            "An open hand accepted a stale pinch pulse as a shutter.");
        Require(!PhotoCopyHandSelector.TrySelectShutter([], cursor, out _) &&
            !PhotoCopyHandSelector.TrySelectShutter([open, pinch, Hand(.7, 700)], cursor, out _),
            "An unsupported hand count reached shutter selection.");
        Require(!PhotoCopyHandSelector.TrySelectShutter([pinch], cursor with { ExecuteEventId = 0 }, out _) &&
            !PhotoCopyHandSelector.TrySelectShutter([pinch], cursor with { Position = new(double.NaN, 0) }, out _),
            "An unconfirmed or invalid cursor reached shutter selection.");

        var invalidPoints = open.Landmarks.ToArray();
        invalidPoints[12] = new(double.PositiveInfinity, 0);
        HandDetection[] invalidHands =
        [
            open with { Landmarks = Array.Empty<PixelPoint>() },
            open with { Landmarks = invalidPoints },
            open with { Confidence = double.NaN },
            open with { RightHandProbability = 2 },
            open with { Landmarks = Enumerable.Repeat(new PixelPoint(10, 10), 21).ToArray() }
        ];
        foreach (var invalidHand in invalidHands)
            Require(!PhotoCopyHandSelector.TrySelectShutter([invalidHand, pinch], cursor, out shutter) && shutter is null,
                "An invalid observed hand reached shutter selection.");
        Require(!PhotoCopyHandSelector.TrySelectShutter([pinch with { Confidence = -1 }], cursor, out _),
            "An invalid triggering hand reached shutter selection.");

        foreach (double angle in new[] { 0.0, Math.PI / 2, 2.4 })
        foreach (double scale in new[] { .3, 1.0, 2.0 })
        {
            pinch = Hand(.15, 650, scale, angle);
            Require(PhotoCopyHandSelector.TrySelectShutter([pinch], Cursor(pinch), out shutter) &&
                ReferenceEquals(shutter, pinch), "Rotation or size changed single-hand shutter selection.");
        }
        Console.WriteLine("Photo Copy shutter regression: one/two hands, actual fingertip versus button anchor, " +
            "detection order, both-pinched/ambiguous/invalid rejection and pose invariance passed.");
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
