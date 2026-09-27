using ProjectTabletop.Vision;

internal static class FourFingerPoseRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] SpreadPoints =
    [
        new(0, 100), new(-28, 72), new(-57, 48), new(-84, 25), new(-110, 8),
        new(-35, 10), new(-51, -32), new(-64, -64), new(-76, -92),
        new(0, 0), new(-3, -48), new(-7, -83), new(-10, -113),
        new(30, 10), new(46, -33), new(57, -62), new(67, -87),
        new(55, 30), new(79, 2), new(96, -20), new(112, -39)
    ];

    public static void Run()
    {
        CheckPosesAndInvariance();
        CheckInvalidData();
        CheckCursorContract();
        CheckFreshnessAndPoseClear();
        CheckTrackingIdentity();
        CheckFingerSelection();
        CheckFingerSelectionCursor();
        Console.WriteLine("Four-finger regression: relaxed/together/spread fingers with either thumb pose; " +
            "rotation/mirror/scale invariance; curled/fist/pointing rejection; immutable ordered tips; " +
            "fresh per-frame pose and independent identities across reorder, dropout and reset; " +
            "grouped/index-open selection geometry, neutral hysteresis, sideways index and thumb independence passed.");
    }

    private static void CheckFingerSelection()
    {
        foreach (string pose in new[] { "together", "thumb_tucked", "relaxed", "straight_pinch", "index_out", "index_out_tucked",
            "index_sideways", "index_neutral", "live_together", "live_separated", "live_neutral",
            "ring_out", "spread", "fist", "point", "curled_pinch" })
        {
            bool together = pose is "together" or "thumb_tucked" or "relaxed" or "straight_pinch" or "live_together";
            bool separated = pose is "index_out" or "index_out_tucked" or "index_sideways" or "live_separated";
            var expected = HandPoseClassifier.DescribeFingerSelection(Hand(pose));
            Require(expected.Together == together && expected.IndexSeparated == separated,
                $"Unexpected finger-selection pose for {pose}: {expected}.");
            Require(!expected.Together || !expected.IndexSeparated, "Together and separated thresholds overlap.");
            if (pose.StartsWith("live_", StringComparison.Ordinal))
            {
                double expectedGap = pose == "live_together" ? .20 : pose == "live_separated" ? .48 : .39;
                Require(expected.OtherFingersGrouped && Math.Abs(expected.IndexMiddleGap!.Value - expectedGap) < 1e-10,
                    "Live-range fixture lost its measured gap or grouped remaining fingers.");
            }
            if (pose == "index_neutral") Require(expected.OtherFingersGrouped, "Neutral gap lost the grouped remaining fingers.");
            if (pose is "ring_out" or "spread") Require(!expected.OtherFingersGrouped, "Opening other fingers became an index-only action.");
            foreach (var (scale, angle, mirror) in new[]
                { (.2, 41.0, false), (2.4, 93.0, true), (.6, 213.0, true), (1.5, 307.0, false) })
            {
                var hand = Hand(pose, scale: scale, angle: angle, mirror: mirror);
                var found = HandPoseClassifier.DescribeFingerSelection(hand);
                Require(found.Together == together && found.IndexSeparated == separated && found.OtherFingersGrouped == expected.OtherFingersGrouped,
                    $"Finger-selection pose {pose} changed under 2D transform.");
                if (separated) Require(HandPoseClassifier.AreFourFingersExtended(hand), "An open index lost the four extended fingers contract.");
                foreach (var (actual, original) in new[] { (found.IndexMiddleGap, expected.IndexMiddleGap),
                    (found.MiddleRingGap, expected.MiddleRingGap), (found.RingLittleGap, expected.RingLittleGap) })
                    Require(actual == original || actual is { } a && original is { } b && Math.Abs(a - b) < 1e-10,
                        "Normalized lateral gaps changed under rotation, mirroring or scale.");
            }
        }
        foreach (HandDetection? invalid in new[] { null, Hand() with { Landmarks = [] }, Hand() with { Confidence = double.NaN }, Hand("fist") })
        {
            var pose = HandPoseClassifier.DescribeFingerSelection(invalid);
            Require(!pose.Together && !pose.IndexSeparated && !pose.OtherFingersGrouped &&
                pose.IndexMiddleGap is null && pose.MiddleRingGap is null && pose.RingLittleGap is null,
                "Invalid finger selection retained pose evidence or non-serializable gap diagnostics.");
        }
    }

    private static void CheckFingerSelectionCursor()
    {
        var tracker = new HandGestureTracker();
        var together = One(At(tracker, 0, Hand()));
        Require(together.FingersTogether && !together.IndexFingerSeparated, "Current together pose is missing.");
        var neutral = One(At(tracker, 30, Hand("index_neutral")));
        Require(!neutral.FingersTogether && !neutral.IndexFingerSeparated && neutral.HasFourExtendedFingers,
            "The transition gap did not stay neutral while preserving fingertip tracking.");
        var openHand = Hand("index_out"); var opened = One(At(tracker, 60, openHand));
        Require(opened.IndexFingerSeparated && !opened.FingersTogether && opened.HasFourExtendedFingers &&
            opened.Position == openHand.IndexTip && opened.TrackingId == together.TrackingId && opened.ExecuteEventId == 0,
            "Index opening changed identity/index position, required an extra dwell, or issued a pinch.");
        var held = One(At(tracker, 90, openHand));
        Require(held.IndexFingerSeparated && held.ExecuteEventId == 0, "Held index opening generated a pinch event.");
        var folded = One(At(tracker, 120, Hand("fist")));
        Require(!folded.FingersTogether && !folded.IndexFingerSeparated, "Folded hand retained finger-selection flags.");
        Require(At(tracker, 150).Count == 0, "A missing hand retained finger-selection evidence.");
        var reopened = One(At(tracker, 180, openHand));
        Require(reopened.IndexFingerSeparated && !reopened.FingersTogether, "Reacquisition reused a together pose.");
    }

    private static void CheckPosesAndInvariance()
    {
        foreach (var (scale, angle, mirror) in new[]
        {
            (1.0, 0.0, false), (.3, 37.0, false), (2.4, 93.0, true), (.6, 213.0, true), (1.5, 307.0, false)
        })
        {
            foreach (string pose in new[] { "spread", "together", "thumb_tucked", "relaxed", "straight_pinch", "fist", "point", "curled_pinch" })
            {
                var hand = Hand(pose, scale: scale, angle: angle, mirror: mirror);
                bool expected = pose is "spread" or "together" or "thumb_tucked" or "relaxed" or "straight_pinch";
                Require(HandPoseClassifier.AreFourFingersExtended(hand) == expected,
                    $"Pose {pose} changed classification at scale={scale}, angle={angle}, mirror={mirror}.");
            }
            // Any one folded finger must cancel the pose, including the little
            // finger which can be partly hidden behind the ring finger.
            foreach (int root in new[] { 5, 9, 13, 17 })
                Require(!HandPoseClassifier.AreFourFingersExtended(Hand($"curl_{root}", scale: scale, angle: angle, mirror: mirror)),
                    $"Folded finger {root} passed four-finger detection.");
        }
    }

    private static void CheckInvalidData()
    {
        var good = Hand();
        Require(!HandPoseClassifier.AreFourFingersExtended(null), "Null hand passed.");
        foreach (HandDetection invalid in new[]
        {
            good with { Landmarks = [] }, good with { Landmarks = good.Landmarks.Take(20).ToArray() },
            good with { Confidence = double.NaN }, good with { Confidence = .2 }, good with { Confidence = 1.1 },
            good with { RightHandProbability = double.PositiveInfinity }, good with { RightHandProbability = -.1 },
            good with { Landmarks = Enumerable.Repeat(new PixelPoint(10, 10), 21).ToArray() },
            good with { Landmarks = good.Landmarks.Select(p => new PixelPoint(p.X, 10)).ToArray() }
        }) Require(!HandPoseClassifier.AreFourFingersExtended(invalid), "Invalid hand data passed.");
        foreach (int index in new[] { 4, 8, 12, 16, 20 })
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.MaxValue })
            {
                var points = good.Landmarks.ToArray(); points[index] = new(bad, 10);
                Require(!HandPoseClassifier.AreFourFingersExtended(good with { Landmarks = points }), "Malformed landmark passed.");
            }
        var collapsed = good.Landmarks.ToArray(); collapsed[11] = collapsed[10];
        Require(!HandPoseClassifier.AreFourFingersExtended(good with { Landmarks = collapsed }), "Collapsed finger joint passed.");
    }

    private static void CheckCursorContract()
    {
        var tracker = new HandGestureTracker();
        var hand = Hand("thumb_tucked");
        var cursor = One(At(tracker, 0, hand));
        Require(cursor.TrackingId > 0, "Tracked hand has no identity.");
        Require(cursor.HasFourExtendedFingers, "A valid first frame incurred an extra pose dwell.");
        Require(cursor.Position == hand.IndexTip, "The legacy index cursor moved to another finger.");
        Require(cursor.FingerTips.SequenceEqual(new[] { hand.Landmarks[8], hand.Landmarks[12], hand.Landmarks[16], hand.Landmarks[20] }),
            "Four tips were missing or in the wrong anatomical order.");
        Require(cursor.FingerTips is IList<PixelPoint> { IsReadOnly: true }, "Fingertip snapshot is mutable.");
        var savedMiddle = cursor.FingerTips[1];
        ((PixelPoint[])hand.Landmarks)[12] = new(999, 999);
        Require(cursor.FingerTips[1] == savedMiddle, "Cursor points changed when source landmarks were reused.");
        Require(cursor.ExecuteEventId == 0 && !cursor.IsExecuting(Time(0)) && cursor.SelectionPosition is null,
            "Four extended fingers generated a pinch command or changed selection anchoring.");
        var legacy = new HandCursor(new(10, 20), DateTimeOffset.MinValue);
        Require(legacy.FingerTips.Count == 0 && !legacy.HasFourExtendedFingers && legacy.TrackingId == 0,
            "Legacy cursors incorrectly claim tracked four-finger evidence.");
    }

    private static void CheckFreshnessAndPoseClear()
    {
        var hand = Hand(); var tracker = new HandGestureTracker();
        Require(One(At(tracker, 0, hand)).HasFourExtendedFingers, "Valid pose absent.");
        Require(!One(At(tracker, 30, Hand("fist"))).HasFourExtendedFingers, "Folded hand retained a four-finger pose.");
        Require(At(tracker, 60).Count == 0, "Missing hand retained a cursor.");
        Require(One(At(tracker, 90, hand)).HasFourExtendedFingers, "Reacquired pose incurred a second dwell.");
        Require(At(tracker, 120, hand with { Landmarks = [] }).Count == 0, "Invalid hand retained a cursor.");
        foreach (var (frame, now) in new[] { (0, 30), (-1, 30), (60, 30), (30, 381), (30, -1) })
        {
            var fresh = new HandGestureTracker(); At(fresh, 0, hand);
            Require(fresh.Update([hand], Time(frame), Time(now)).Count == 0,
                "Duplicate, out-of-order, future, stale or backwards-clock frame emitted four-finger evidence.");
        }
        var delayed = new HandGestureTracker();
        Require(One(delayed.Update([hand], Time(0), Time(250))).HasFourExtendedFingers,
            "Fresh source frame was rejected because inference completed later.");
    }

    private static void CheckTrackingIdentity()
    {
        var tracker = new HandGestureTracker();
        var first = Hand(x: 250); var second = Hand("point", x: 850, mirror: true);
        var initial = At(tracker, 0, first, second);
        long firstId = Find(initial, first).TrackingId, secondId = Find(initial, second).TrackingId;
        Require(firstId != secondId, "Two hands shared an identity.");
        var reversed = At(tracker, 30, second with { Confidence = .99 }, first with { Confidence = .85 });
        Require(Find(reversed, first).TrackingId == firstId && Find(reversed, second).TrackingId == secondId,
            "Confidence-order swap exchanged hand identities.");
        Require(Find(reversed, first).HasFourExtendedFingers && !Find(reversed, second).HasFourExtendedFingers,
            "Four-finger pose leaked to the other hand.");
        At(tracker, 60, second);
        Require(Find(At(tracker, 90, first, second), first).TrackingId == firstId, "A brief dropout replaced a matched hand identity.");
        var moved = Hand(x: 268);
        Require(One(At(tracker, 120, moved)).TrackingId == firstId, "Ordinary movement replaced hand identity.");
        Require(One(At(tracker, 500, moved)).TrackingId > Math.Max(firstId, secondId), "Expired identity was reused.");
        long beforeReset = One(At(tracker, 530, moved)).TrackingId;
        tracker.Reset();
        long afterReset = One(At(tracker, 0, moved)).TrackingId;
        Require(afterReset > beforeReset, "Reset reused an old identity.");
        long independent = One(At(new HandGestureTracker(), 0, moved)).TrackingId;
        Require(independent > afterReset, "A second tracker reused a globally issued identity.");
    }

    private static HandDetection Hand(string pose = "together", double x = 350, double scale = 1,
        double angle = 0, bool mirror = false)
    {
        PixelPoint[] points = SpreadPoints.ToArray();
        if (pose != "spread")
            foreach (int root in new[] { 5, 9, 13, 17 })
            {
                double tipY = root switch { 5 => -98, 9 => -113, 13 => -96, _ => -65 };
                for (int part = 1; part <= 3; part++)
                    points[root + part] = new(points[root].X, points[root].Y + (tipY - points[root].Y) * part / 3);
            }
        if (pose is "thumb_tucked" or "relaxed" or "index_out_tucked")
        {
            points[2] = new(-42, 35); points[3] = new(-30, 5); points[4] = new(-12, -10);
        }
        if (pose == "relaxed")
            foreach (int root in new[] { 5, 9, 13, 17 })
            {
                points[root + 1] = new(points[root + 1].X - 8, points[root + 1].Y);
                points[root + 2] = new(points[root + 2].X + 4, points[root + 2].Y);
                points[root + 3] = new(points[root + 3].X + 9, points[root + 3].Y + 10);
            }
        if (pose.StartsWith("index_", StringComparison.Ordinal) || pose == "ring_out")
        {
            int root = pose == "ring_out" ? 13 : 5;
            double opening = (pose == "index_neutral" ? 2 : pose == "index_sideways" ? 90 : 25) * Math.PI / 180;
            for (int part = 1; part <= 3; part++)
            {
                double along = points[root + part].Y - points[root].Y;
                points[root + part] = new(points[root].X + along * Math.Sin(opening) * (root == 13 ? -1 : 1),
                    points[root].Y + along * Math.Cos(opening));
            }
        }
        if (pose.StartsWith("live_", StringComparison.Ordinal))
        {
            double tipX = pose == "live_together" ? -20 : pose == "live_separated" ? -48 : -39;
            for (int part = 1; part <= 3; part++)
                points[5 + part] = new(points[5].X + (tipX - points[5].X) * part / 3, points[5 + part].Y);
        }
        foreach (int root in new[] { 5, 9, 13, 17 })
            if (pose == "fist" || pose == "point" && root != 5 || pose == $"curl_{root}" || pose == "curled_pinch" && root == 5)
            {
                var knuckle = points[root];
                points[root + 1] = new(knuckle.X + 3, knuckle.Y - 25);
                points[root + 2] = new(knuckle.X + 5, knuckle.Y + 4);
                points[root + 3] = new(knuckle.X + 2, knuckle.Y + 30);
            }
        if (pose is "straight_pinch" or "curled_pinch") points[4] = points[8];
        double radians = angle * Math.PI / 180, cosine = Math.Cos(radians), sine = Math.Sin(radians);
        return new(points.Select(p => new PixelPoint(x + scale * ((mirror ? -p.X : p.X) * cosine - p.Y * sine),
            350 + scale * ((mirror ? -p.X : p.X) * sine + p.Y * cosine))).ToArray(), .95, mirror ? .1 : .9);
    }

    private static DateTimeOffset Time(int ms) => Epoch.AddMilliseconds(ms);
    private static IReadOnlyList<HandCursor> At(HandGestureTracker tracker, int ms, params HandDetection[] hands) =>
        tracker.Update(hands, Time(ms), Time(ms));
    private static HandCursor One(IReadOnlyList<HandCursor> cursors)
    {
        Require(cursors.Count == 1, "Expected one current cursor."); return cursors[0];
    }
    private static HandCursor Find(IReadOnlyList<HandCursor> cursors, HandDetection hand) =>
        cursors.Single(cursor => cursor.Position == hand.IndexTip);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Four-finger regression: " + message);
    }
}
