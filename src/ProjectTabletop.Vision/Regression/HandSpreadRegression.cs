using ProjectTabletop.Vision;

internal static class HandSpreadRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
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
        CheckDwellAndImmediateClear();
        CheckFreshnessAndReset();
        CheckIndependentHands();
        Console.WriteLine("Spread hand regression: extension, finger separation and thumb abduction; " +
            "rotation/mirror/scale invariance; fist/pointing/pinch/together rejection; fresh dwell, immediate clear, " +
            "two-hand identity, invalid input and reset passed without execution events.");
    }

    private static void CheckPosesAndInvariance()
    {
        foreach (var (scale, angle, mirror) in new[]
        {
            (1.0, 0.0, false), (.3, 37.0, false), (2.4, 93.0, true), (.6, 213.0, true), (1.5, 307.0, false)
        })
        {
            foreach (string pose in new[] { "spread", "together", "fist", "point", "pinch", "thumb_tucked", "one_curled" })
            {
                HandDetection hand = Hand(pose, 650, scale, angle, mirror);
                Require(HandPoseClassifier.IsSpreadOut(hand) == (pose == "spread"),
                    $"Pose {pose} changed classification under scale={scale}, angle={angle}, mirror={mirror}.");
            }
        }
    }

    private static void CheckInvalidData()
    {
        var good = Hand();
        Require(!HandPoseClassifier.IsSpreadOut(null), "A null hand was accepted.");
        foreach (HandDetection invalid in new[]
        {
            good with { Landmarks = [] }, good with { Confidence = double.NaN },
            good with { Confidence = .2 }, good with { Confidence = 1.1 },
            good with { RightHandProbability = double.PositiveInfinity },
            good with { RightHandProbability = -.1 },
            good with { Landmarks = Enumerable.Repeat(new PixelPoint(10, 10), 21).ToArray() }
        }) Require(!HandPoseClassifier.IsSpreadOut(invalid), "Invalid hand data was accepted.");
        foreach (double bad in new[] { double.NaN, double.NegativeInfinity, double.MaxValue })
        {
            var points = good.Landmarks.ToArray(); points[12] = new(bad, 10);
            Require(!HandPoseClassifier.IsSpreadOut(good with { Landmarks = points }), "A malformed fingertip was accepted.");
        }
    }

    private static void CheckDwellAndImmediateClear()
    {
        var tracker = new HandGestureTracker(); var spread = Hand();
        Require(!One(At(tracker, 0, spread)).IsSpreadOut, "One frame bypassed the spread dwell.");
        Require(!One(At(tracker, 60, spread)).IsSpreadOut, "A brief spread pose passed the dwell.");
        var confirmed = One(At(tracker, 120, spread));
        Require(confirmed.IsSpreadOut && confirmed.ExecuteEventId == 0 && !confirmed.IsExecuting(Time(120)),
            "A stable spread pose was missed or became an execute command.");
        Require(!One(At(tracker, 150, Hand("together"))).IsSpreadOut, "Closing finger spacing did not clear the pose immediately.");
        Require(!One(At(tracker, 180, spread)).IsSpreadOut, "Returning to spread reused the old dwell.");
        Require(One(At(tracker, 300, spread)).IsSpreadOut, "A new spread dwell did not confirm.");
        Require(At(tracker, 330).Count == 0, "A missing hand retained a visible pose.");
        Require(!One(At(tracker, 360, spread)).IsSpreadOut, "A missed hand retained spread confirmation on reacquisition.");
        Require(One(At(tracker, 480, spread)).IsSpreadOut, "Reacquired hand could not confirm again.");
        var invalid = spread with { Landmarks = [] };
        Require(At(tracker, 510, invalid).Count == 0, "An invalid observation retained a cursor.");
        Require(!One(At(tracker, 540, spread)).IsSpreadOut, "Invalid landmarks preserved spread evidence.");
        Require(!One(At(tracker, 940, spread)).IsSpreadOut, "A long unobserved gap counted toward spread dwell.");
    }

    private static void CheckFreshnessAndReset()
    {
        foreach (var (frame, now) in new[] { (120, 200), (100, 220), (400, 300), (250, 700), (150, 100) })
        {
            var tracker = new HandGestureTracker(); var spread = Hand();
            At(tracker, 0, spread); Require(One(At(tracker, 120, spread)).IsSpreadOut, "Fixture did not confirm spread.");
            Require(tracker.Update([spread], Time(frame), Time(now)).Count == 0,
                "Duplicate, old, future, stale or backwards-clock frame emitted a pose.");
            int next = Math.Max(Math.Max(frame, now), 120) + 40;
            Require(!One(At(tracker, next, spread)).IsSpreadOut, "Rejected time evidence preserved a confirmed spread pose.");
        }
        var delayed = new HandGestureTracker(); var hand = Hand();
        Require(!One(delayed.Update([hand], Time(0), Time(200))).IsSpreadOut, "Inference latency counted as spread dwell.");
        Require(!One(delayed.Update([hand], Time(60), Time(260))).IsSpreadOut, "Completion time replaced camera dwell time.");
        Require(One(delayed.Update([hand], Time(120), Time(320))).IsSpreadOut, "Fresh delayed observations could not confirm spread.");
        delayed.Reset();
        Require(!One(At(delayed, 0, hand)).IsSpreadOut, "Reset retained pose evidence or blocked repeated camera timestamps.");
        var slower = new HandGestureTracker();
        slower.Update([hand], Time(0), Time(220));
        Require(One(slower.Update([hand], Time(220), Time(440))).IsSpreadOut,
            "Fresh hand observations at a slower legal cadence could never confirm spread.");
    }

    private static void CheckIndependentHands()
    {
        var tracker = new HandGestureTracker();
        var first = Hand(x: 250);
        var second = Hand("together", x: 850, mirror: true);
        At(tracker, 0, first, second);
        var reordered = At(tracker, 120, second with { Confidence = .99 }, first with { Confidence = .85 });
        Require(Find(reordered, first).IsSpreadOut && !Find(reordered, second).IsSpreadOut,
            "Confidence-order swap moved spread state to the other hand.");
        var secondSpread = Hand(x: 850, mirror: true);
        var changing = At(tracker, 150, secondSpread, first);
        Require(!Find(changing, secondSpread).IsSpreadOut && Find(changing, first).IsSpreadOut,
            "A second hand borrowed the first hand's spread dwell.");
        var onlySecond = At(tracker, 270, secondSpread);
        Require(One(onlySecond).IsSpreadOut, "One missing hand cleared the other hand's independent evidence.");
        var returned = At(tracker, 300, first, secondSpread);
        Require(!Find(returned, first).IsSpreadOut && Find(returned, secondSpread).IsSpreadOut,
            "A returning hand borrowed the other hand's confirmed pose.");
        Require(returned.All(cursor => cursor.ExecuteEventId == 0), "Spread/together poses generated execute events.");
    }

    private static HandDetection Hand(string pose = "spread", double x = 350, double scale = 1,
        double angle = 0, bool mirror = false)
    {
        PixelPoint[] points = SpreadPoints.ToArray();
        if (pose == "together")
            foreach (int root in new[] { 5, 9, 13, 17 })
            {
                double tipY = root switch { 5 => -98, 9 => -113, 13 => -96, _ => -65 };
                for (int part = 1; part <= 3; part++)
                    points[root + part] = new(points[root].X, points[root].Y + (tipY - points[root].Y) * part / 3);
            }
        if (pose is "fist" or "point" or "one_curled")
            foreach (int root in new[] { 5, 9, 13, 17 })
            {
                if (pose == "point" && root == 5 || pose == "one_curled" && root != 13) continue;
                var knuckle = points[root];
                points[root + 1] = new(knuckle.X + 3, knuckle.Y - 25);
                points[root + 2] = new(knuckle.X + 5, knuckle.Y + 4);
                points[root + 3] = new(knuckle.X + 2, knuckle.Y + 30);
            }
        if (pose == "pinch") points[4] = points[8];
        if (pose == "thumb_tucked")
        {
            points[2] = new(-42, 35); points[3] = new(-30, 5); points[4] = new(-12, -10);
        }
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
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Spread hand regression: " + message); }
}
