using ProjectTabletop.Vision;

internal static class PhotoCopySelectionRegression
{
    public static void Run()
    {
        CheckShutterSelection();
        CheckGestureShutterSelection();
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

    private static void CheckGestureShutterSelection()
    {
        var command = GestureHand(separated: true, offset: 600);
        var subject = GestureHand(separated: false, offset: 200);
        var cursor = GestureCursor(command);
        Require(cursor.ExecuteEventId == 0 && cursor.ExecuteUntil == DateTimeOffset.MinValue &&
            PhotoCopyHandSelector.TrySelectGestureShutter([command], cursor, out var shutter) && ReferenceEquals(shutter, command),
            "A confirmed index-separation shutter required a fabricated pinch event.");
        foreach (var hands in new[] { new[] { command, subject }, new[] { subject, command } })
        {
            Require(PhotoCopyHandSelector.TrySelectGestureShutter(hands, cursor, out shutter) && ReferenceEquals(shutter, command) &&
                ReferenceEquals(hands.Single(hand => !ReferenceEquals(hand, shutter)), subject),
                "Detection order confused the shutter hand and the hand to photograph.");
            Require(PhotoCopyHandSelector.TrySelectGestureShutter(hands, cursor with
                { SelectionPosition = subject.IndexTip, SelectionFrameTime = DateTimeOffset.UtcNow }, out shutter) && ReferenceEquals(shutter, command),
                "An earlier button anchor replaced the actual index-separation shutter hand.");
        }
        Require(PhotoCopyHandSelector.TrySelectGestureShutter([GestureHand(true, 200), command], cursor, out shutter) && ReferenceEquals(shutter, command),
            "An unconfirmed separated pose on the subject hand was mistaken for a second shutter command.");
        Require(PhotoCopyHandSelector.TrySelectGestureShutter([Hand(.15, 200), command], cursor, out shutter) && ReferenceEquals(shutter, command),
            "Geometry-only shutter matching attempted to arbitrate another hand's unconfirmed pinch pose.");
        foreach (var invalidCursor in new[]
        {
            cursor with { TrackingId = 0 }, cursor with { TrackingId = -1 },
            cursor with { HasFourExtendedFingers = false }, cursor with { IndexFingerSeparated = false },
            cursor with { FingersTogether = true }, cursor with { Position = new(double.NaN, 0) },
            cursor with { Position = subject.IndexTip, SelectionPosition = command.IndexTip }
        }) Require(!PhotoCopyHandSelector.TrySelectGestureShutter([command], invalidCursor, out shutter) && shutter is null,
            "An invalid, unqualified or unmatched cursor reached gesture shutter selection.");
        var overlapping = command with { Confidence = .8 };
        Require(!PhotoCopyHandSelector.TrySelectGestureShutter([command, overlapping], cursor, out _) &&
            !PhotoCopyHandSelector.TrySelectGestureShutter([], cursor, out _) &&
            !PhotoCopyHandSelector.TrySelectGestureShutter([subject], cursor, out _) &&
            !PhotoCopyHandSelector.TrySelectGestureShutter([subject, command, GestureHand(false, 1000)], cursor, out _),
            "Ambiguous, missing or unsupported detections reached gesture shutter selection.");
        foreach (var invalid in new[]
        {
            subject with { Landmarks = [] }, subject with { Confidence = double.NaN },
            subject with { RightHandProbability = 2 },
            subject with { Landmarks = Enumerable.Repeat(new PixelPoint(10, 10), 21).ToArray() }
        }) Require(!PhotoCopyHandSelector.TrySelectGestureShutter([invalid, command], cursor, out _),
            "Malformed companion-hand landmarks reached capture selection.");
        Require(!PhotoCopyHandSelector.TrySelectGestureShutter([subject], GestureCursor(subject), out _) &&
            !PhotoCopyHandSelector.TrySelectGestureShutter([Hand(.15, 600)], GestureCursor(Hand(.15, 600)), out _) &&
            !PhotoCopyHandSelector.TrySelectGestureShutter([command with { Confidence = .2 }], cursor, out _),
            "Stale pose flags bypassed actual open-index landmark validation.");
        foreach (double angle in new[] { 0.0, Math.PI / 2, 2.4 })
        foreach (double scale in new[] { .3, 1.0, 2.0 })
        foreach (bool mirrored in new[] { false, true })
        {
            var transformed = GestureHand(true, 650, scale, angle, mirrored);
            Require(PhotoCopyHandSelector.TrySelectGestureShutter([transformed], GestureCursor(transformed), out shutter) && ReferenceEquals(shutter, transformed),
                "Rotation, mirroring or hand size changed index-separation shutter matching.");
        }
        Console.WriteLine("Photo Copy index-separation shutter regression: no pinch event, same-frame actual tip, one/two-hand order, " +
            "subject poses, invalid/ambiguous/missing rejection and actual gesture geometry under transforms passed.");
    }

    private static HandDetection GestureHand(bool separated, double offset, double scale = 1, double angle = 0, bool mirrored = false)
    {
        PixelPoint[] points = [new(0, 100), new(-28, 72), new(-57, 48), new(-84, 25), new(-110, 8),
            new(-35, 10), default, default, default, new(0, 0), default, default, default,
            new(30, 10), default, default, default, new(55, 30), default, default, default];
        foreach (int root in new[] { 5, 9, 13, 17 })
        {
            double tipX = root == 5 ? separated ? -48 : -20 : points[root].X;
            double tipY = root switch { 5 => -98, 9 => -113, 13 => -96, _ => -65 };
            for (int part = 1; part <= 3; part++)
                points[root + part] = new(points[root].X + (tipX - points[root].X) * part / 3,
                    points[root].Y + (tipY - points[root].Y) * part / 3);
        }
        return new(points.Select(point => new PixelPoint(offset + scale * ((mirrored ? -point.X : point.X) * Math.Cos(angle) - point.Y * Math.Sin(angle)),
            300 + scale * ((mirrored ? -point.X : point.X) * Math.Sin(angle) + point.Y * Math.Cos(angle)))).ToArray(), .95, .5);
    }

    private static HandCursor GestureCursor(HandDetection hand) => new(hand.IndexTip, DateTimeOffset.MinValue)
    {
        TrackingId = 73, HasFourExtendedFingers = true, IndexFingerSeparated = true,
        FingerTips = Array.AsReadOnly(new[] { hand.Landmarks[8], hand.Landmarks[12], hand.Landmarks[16], hand.Landmarks[20] })
    };

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
