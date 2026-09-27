using ProjectTabletop.Vision;

internal static class HandGestureRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckPointingAndDwell();
        CheckPulseAndRelease();
        CheckJitterAndDropout();
        CheckRejectedFramesAndReset();
        CheckIndependentHands();
        CheckExecutionEventIdentity();
        CheckPoseInvariance();
        Console.WriteLine("Hand gesture regression: pointing/dwell, one-second pulse, release, " +
            "jitter, dropout, frame freshness, reset, independent hands, event identity and pose invariance passed.");
    }

    private static void CheckPointingAndDwell()
    {
        var tracker = new HandGestureTracker();
        HandDetection open = Hand(0.65), closed = Hand(0.15);
        for (int time = 0; time <= 400; time += 40)
            Quiet(One(At(tracker, time, open)), time, "Pointing triggered execution.");

        Quiet(One(At(tracker, 440, closed)), 440, "A single pinch frame triggered execution.");
        Quiet(One(At(tracker, 500, closed)), 500, "A pinch shorter than 120ms triggered execution.");
        HandCursor triggered = One(At(tracker, 560, closed));
        Pulse(triggered, 560, "A sustained pinch did not execute after 120ms.");

        tracker.Reset();
        At(tracker, 0, open);
        At(tracker, 40, closed);
        At(tracker, 100, open);
        Quiet(One(At(tracker, 160, open)), 160, "A released brief pinch triggered execution.");

        tracker.Reset();
        At(tracker, 0, open);
        At(tracker, 40, closed);
        // Elapsed wall time with the same photograph is not another observation.
        Empty(At(tracker, 40, 180, closed), "A duplicate frame was accepted.");
        Empty(At(tracker, 20, 190, closed), "An older frame was accepted.");
        Pulse(One(At(tracker, 200, closed)), 200, "Two distinct sustained pinch frames did not execute.");
    }

    private static void CheckPulseAndRelease()
    {
        var tracker = new HandGestureTracker();
        HandDetection open = Hand(0.65), closed = Hand(0.15);
        At(tracker, 0, open);
        At(tracker, 40, closed);
        DateTimeOffset deadline = One(At(tracker, 160, closed)).ExecuteUntil;
        Equal(deadline, Time(1160), "The execution pulse is not exactly one second.");
        for (int time = 200; time <= 1400; time += 40)
        {
            HandCursor cursor = One(At(tracker, time, closed));
            Equal(cursor.ExecuteUntil, deadline, "Holding a pinch extended or retriggered the pulse.");
            Require(cursor.IsExecuting(Time(time)) == (time < 1160),
                "Execution visibility did not end at its deadline.");
        }
        Quiet(One(At(tracker, 1440, open)), 1440, "Release restarted the expired pulse.");
        Quiet(One(At(tracker, 1480, closed)), 1480, "Re-pinching skipped the dwell.");
        Pulse(One(At(tracker, 1600, closed)), 1600, "Release followed by re-pinch did not retrigger.");
    }

    private static void CheckJitterAndDropout()
    {
        var tracker = new HandGestureTracker();
        HandDetection open = Hand(0.65), closed = Hand(0.15), band = Hand(0.35);
        At(tracker, 0, open);
        At(tracker, 40, closed);
        DateTimeOffset deadline = One(At(tracker, 160, closed)).ExecuteUntil;
        for (int time = 200; time <= 400; time += 40)
            Equal(One(At(tracker, time, time % 80 == 0 ? closed : band)).ExecuteUntil,
                deadline, "Jitter inside the release band rearmed a held pinch.");

        Empty(At(tracker, 440), "A missing hand left a visible cursor.");
        Equal(One(At(tracker, 480, closed)).ExecuteUntil, deadline,
            "A brief missing hand lost its existing pulse.");
        for (int time = 520; time <= 1280; time += 40)
            Equal(One(At(tracker, time, closed)).ExecuteUntil, deadline,
                "Returning while still pinched retriggered after a brief dropout.");

        // Missing a hand during the initial dwell breaks its visual evidence.
        tracker.Reset();
        At(tracker, 0, open);
        At(tracker, 40, closed);
        Empty(At(tracker, 100), "A missing candidate left a visible cursor.");
        Quiet(One(At(tracker, 160, closed)), 160, "Pinch dwell survived a missing observation.");
        Pulse(One(At(tracker, 280, closed)), 280, "A fresh dwell after dropout did not execute.");
    }

    private static void CheckRejectedFramesAndReset()
    {
        HandDetection open = Hand(0.65), closed = Hand(0.15);
        var tracker = new HandGestureTracker();
        Empty(At(tracker, 0, 351, closed), "A stale frame produced a cursor.");
        Quiet(One(At(tracker, 400, closed)), 400, "A stale frame seeded the pinch dwell.");
        Pulse(One(At(tracker, 520, closed)), 520, "Fresh frames failed after stale input.");

        tracker.Reset();
        Empty(At(tracker, 500, 100, closed), "A future frame produced a cursor.");
        Quiet(One(At(tracker, 200, closed)), 200, "A future frame seeded the pinch dwell.");
        Pulse(One(At(tracker, 320, closed)), 320, "A future timestamp blocked later valid frames.");

        // An out-of-order open observation must not rearm a completed pinch.
        DateTimeOffset deadline = One(At(tracker, 360, closed)).ExecuteUntil;
        Empty(At(tracker, 240, 400, open), "An out-of-order release was accepted.");
        Equal(One(At(tracker, 440, closed)).ExecuteUntil, deadline,
            "An out-of-order release changed the execution pulse.");
        Equal(One(At(tracker, 560, closed)).ExecuteUntil, deadline,
            "An out-of-order release allowed a held pinch to retrigger.");

        // A camera/tracking restart can repeat timestamps; Reset clears both
        // execution feedback and the monotonic-frame guard.
        tracker.Reset();
        Quiet(One(At(tracker, 0, open)), 0, "Reset retained an execution pulse.");
        Quiet(One(At(tracker, 40, closed)), 40, "Reset retained a pending dwell.");
        Pulse(One(At(tracker, 160, closed)), 160, "Reset did not permit a new pinch sequence.");
    }

    private static void CheckIndependentHands()
    {
        var tracker = new HandGestureTracker();
        HandDetection firstOpen = Hand(0.65, x: 300, confidence: 0.99);
        HandDetection firstClosed = Hand(0.15, x: 300, confidence: 0.99);
        HandDetection secondOpen = Hand(0.65, x: 700, confidence: 0.90);
        At(tracker, 0, firstOpen, secondOpen);
        At(tracker, 40, firstClosed, secondOpen);
        IReadOnlyList<HandCursor> cursors = At(tracker, 160, secondOpen, firstClosed);
        Require(cursors.Count == 2, "Two hands did not retain separate cursors.");
        Pulse(Nearest(cursors, firstClosed.IndexTip), 160, "Reordering hands lost the pinching hand.");
        Quiet(Nearest(cursors, secondOpen.IndexTip), 160, "Execution colored the other hand.");

        HandDetection firstMoved = Hand(0.15, x: 308, confidence: 0.85);
        HandDetection secondMoved = Hand(0.65, x: 692, confidence: 0.99);
        cursors = At(tracker, 200, secondMoved, firstMoved);
        Equal(Nearest(cursors, firstMoved.IndexTip).ExecuteUntil, Time(1160),
            "Motion/confidence reordering switched the execution pulse between hands.");
        Quiet(Nearest(cursors, secondMoved.IndexTip), 200, "The higher-confidence open hand turned red.");

        HandDetection secondClosed = Hand(0.15, x: 692, confidence: 0.99);
        At(tracker, 240, firstMoved, secondClosed);
        cursors = At(tracker, 360, secondClosed, firstMoved);
        Equal(Nearest(cursors, firstMoved.IndexTip).ExecuteUntil, Time(1160),
            "The second hand extended the first hand's pulse.");
        Pulse(Nearest(cursors, secondClosed.IndexTip), 360, "The second hand could not execute independently.");
    }

    private static void CheckPoseInvariance()
    {
        foreach (var (scale, angle, x, y) in new[]
        {
            (0.4, 37.0, 120.0, 100.0),
            (1.0, 90.0, 700.0, 300.0),
            (2.5, 211.0, 1100.0, 600.0)
        })
        {
            var tracker = new HandGestureTracker();
            HandDetection open = Hand(0.60, x, y, scale, angle);
            HandDetection closed = Hand(0.20, x, y, scale, angle);
            Quiet(One(At(tracker, 0, open)), 0, "A transformed pointing pose executed.");
            Quiet(One(At(tracker, 40, closed)), 40, "A transformed pinch skipped the dwell.");
            HandCursor cursor = One(At(tracker, 160, closed));
            Pulse(cursor, 160, "Pinch recognition depended on scale, rotation, or translation.");
            Require(Distance(cursor.Position, closed.IndexTip) < 0.001,
                "The cursor was not centered on the transformed index fingertip.");
        }
    }

    private static void CheckExecutionEventIdentity()
    {
        var tracker = new HandGestureTracker();
        HandDetection first = Hand(.15, x: 300), second = Hand(.15, x: 700);
        var cursors = At(tracker, 0, first, second);
        Require(cursors.All(cursor => cursor.ExecuteEventId == 0), "An untriggered hand received an event ID.");
        cursors = At(tracker, 120, first, second);
        long firstId = Nearest(cursors, first.IndexTip).ExecuteEventId;
        long secondId = Nearest(cursors, second.IndexTip).ExecuteEventId;
        Require(firstId > 0 && secondId > firstId, "Simultaneous pinches did not receive unique monotonic IDs.");
        cursors = At(tracker, 160, second, first);
        Require(Nearest(cursors, first.IndexTip).ExecuteEventId == firstId &&
            Nearest(cursors, second.IndexTip).ExecuteEventId == secondId, "Reordering moved event IDs between hands.");
        At(tracker, 200);
        cursors = At(tracker, 240, first, second);
        Require(Nearest(cursors, first.IndexTip).ExecuteEventId == firstId, "A brief dropout changed a held-pinch event ID.");
        At(tracker, 280, Hand(.65, x: 300), second);
        At(tracker, 320, first, second);
        cursors = At(tracker, 440, first, second);
        long retriggeredId = Nearest(cursors, first.IndexTip).ExecuteEventId;
        Require(retriggeredId > secondId && Nearest(cursors, second.IndexTip).ExecuteEventId == secondId,
            "A fresh pinch did not receive its own event ID.");
        tracker.Reset();
        At(tracker, 0, first);
        long resetId = One(At(tracker, 120, first)).ExecuteEventId;
        Require(resetId > retriggeredId, "Reset reused a gesture event ID.");
        var replacement = new HandGestureTracker();
        At(replacement, 0, first);
        Require(One(At(replacement, 120, first)).ExecuteEventId > resetId,
            "A replacement tracker reused an event ID.");
    }

    private static HandDetection Hand(double gapRatio, double x = 320, double y = 240,
        double scale = 1, double angle = 0, double confidence = 0.95)
    {
        // Anatomical landmark order: wrist; thumb; index; middle; ring; pinky.
        // Wrist-to-middle knuckle is 100 units; index-to-pinky knuckles is less.
        // Move the thumb along a diagonal toward a slightly bent index finger.
        PixelPoint tip = new(-30, -60);
        PixelPoint thumb = new(tip.X - 80 * gapRatio, tip.Y + 60 * gapRatio);
        PixelPoint[] points =
        [
            new(0, 100), new(-32, 78), new(-55, 35),
            new((-55 + thumb.X) / 2, (35 + thumb.Y) / 2), thumb,
            new(-35, 20), new(-40, -20), new(-34, -45), tip,
            new(0, 0), new(0, -40), new(5, -70), new(5, -90),
            new(28, 10), new(32, -25), new(34, -50), new(35, -70),
            new(50, 30), new(60, 10), new(65, -10), new(65, -25)
        ];
        double radians = angle * Math.PI / 180, cosine = Math.Cos(radians), sine = Math.Sin(radians);
        return new HandDetection(points.Select(point => new PixelPoint(
            x + scale * (point.X * cosine - point.Y * sine),
            y + scale * (point.X * sine + point.Y * cosine))).ToArray(), confidence, 0.5);
    }

    private static IReadOnlyList<HandCursor> At(HandGestureTracker tracker, int time,
        params HandDetection[] hands) => At(tracker, time, time, hands);

    private static IReadOnlyList<HandCursor> At(HandGestureTracker tracker, int frameTime,
        int now, params HandDetection[] hands) => tracker.Update(hands, Time(frameTime), Time(now));

    private static DateTimeOffset Time(int milliseconds) => Epoch.AddMilliseconds(milliseconds);
    private static HandCursor One(IReadOnlyList<HandCursor> cursors)
    {
        Require(cursors.Count == 1, $"Expected one hand cursor, got {cursors.Count}.");
        return cursors[0];
    }

    private static HandCursor Nearest(IReadOnlyList<HandCursor> cursors, PixelPoint expected)
    {
        Require(cursors.Count == 2, $"Expected two hand cursors, got {cursors.Count}.");
        HandCursor cursor = cursors.MinBy(item => Distance(item.Position, expected))!;
        Require(Distance(cursor.Position, expected) < 0.001, "The expected hand cursor was missing.");
        return cursor;
    }

    private static void Pulse(HandCursor cursor, int now, string message)
    {
        Require(cursor.IsExecuting(Time(now)), message);
        Equal(cursor.ExecuteUntil, Time(now + 1000), "The execution pulse must last exactly one second.");
        Require(!cursor.IsExecuting(Time(now + 1000)), "The pulse remained active at its expiry.");
    }

    private static void Quiet(HandCursor cursor, int now, string message) =>
        Require(!cursor.IsExecuting(Time(now)), message);
    private static void Empty(IReadOnlyList<HandCursor> cursors, string message) => Require(cursors.Count == 0, message);
    private static void Equal(DateTimeOffset actual, DateTimeOffset expected, string message) =>
        Require(actual == expected, message);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Hand gesture regression: " + message);
    }
    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
}
