using ProjectTabletop.Vision;

internal static class HandGestureRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckPointingAndDwell();
        CheckPulseAndRelease();
        CheckJitterAndDropout();
        CheckNoisyPinchEvidence();
        CheckReleaseEvidence();
        CheckDelayedFreshFrames();
        CheckRejectedFramesAndReset();
        CheckIndependentHands();
        CheckExecutionEventIdentity();
        CheckPoseInvariance();
        CheckSelectionAnchor();
        CheckSelectionCancellation();
        CheckSelectionFallbackAndIdentity();
        Console.WriteLine("Hand gesture regression: pointing/dwell, one-second pulse, release, " +
            "jitter, paused dropout evidence, release confirmation, delayed fresh frames, reset, " +
            "independent hands, event identity, pose invariance and selection anchoring passed.");
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
        Quiet(One(At(tracker, 1510, open)), 1510, "Confirmed release restarted the expired pulse.");
        Quiet(One(At(tracker, 1550, closed)), 1550, "Re-pinching skipped the dwell.");
        Pulse(One(At(tracker, 1670, closed)), 1670, "Release followed by re-pinch did not retrigger.");
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

        // Missing time cannot contribute visual evidence to the initial dwell.
        tracker.Reset();
        At(tracker, 0, open);
        At(tracker, 40, closed);
        Empty(At(tracker, 100), "A missing candidate left a visible cursor.");
        Quiet(One(At(tracker, 160, closed)), 160, "Missing time contributed to pinch dwell.");
        Pulse(One(At(tracker, 280, closed)), 280, "A fresh dwell after dropout did not execute.");
    }

    private static void CheckNoisyPinchEvidence()
    {
        var tracker = new HandGestureTracker();
        HandDetection closed = Hand(.22), near = Hand(.32), separated = Hand(.40);

        // A small tip-estimation wobble should not restart a deliberate pinch.
        At(tracker, 0, closed);
        Quiet(One(At(tracker, 43, near)), 43, "Near-close jitter executed without confirmation.");
        Quiet(One(At(tracker, 91, closed)), 91, "Uneven sampling skipped the 120 ms dwell.");
        Quiet(One(At(tracker, 137, near)), 137, "A non-closed current frame executed.");
        Pulse(One(At(tracker, 174, closed)), 174, "Near-close jitter repeatedly restarted the pinch.");

        tracker.Reset();
        for (int time = 0; time <= 320; time += 40)
            Quiet(One(At(tracker, time, near)), time, "The holding band started a pinch by itself.");
        At(tracker, 360, closed);
        Quiet(One(At(tracker, 440, near)), 440, "One closed observation was enough to execute.");
        Quiet(One(At(tracker, 520, near)), 520, "A single noisy closed sample eventually executed.");
        At(tracker, 560, separated);
        Quiet(One(At(tracker, 600, closed)), 600, "A separated thumb preserved old pinch evidence.");
        Pulse(One(At(tracker, 720, closed)), 720, "A new pinch failed after a rejected candidate.");

        // Preserve 60 ms of real evidence through a short miss, but do not add
        // the 100 ms during which there was no usable observation.
        tracker.Reset();
        At(tracker, 0, closed);
        At(tracker, 60, closed);
        Empty(At(tracker, 90), "A missing hand produced a cursor.");
        Quiet(One(At(tracker, 160, closed)), 160, "Dropout time finished an incomplete pinch.");
        Pulse(One(At(tracker, 220, closed)), 220, "A short dropout discarded prior observed evidence.");

        tracker.Reset();
        At(tracker, 0, closed);
        At(tracker, 60, closed);
        At(tracker, 100);
        Quiet(One(At(tracker, 240, closed)), 240, "A long dropout preserved a pending pinch.");
        Quiet(One(At(tracker, 300, closed)), 300, "A long dropout retained old dwell evidence.");
        Pulse(One(At(tracker, 360, closed)), 360, "A new dwell failed after a long dropout.");
    }

    private static void CheckReleaseEvidence()
    {
        var tracker = new HandGestureTracker();
        HandDetection closed = Hand(.15), open = Hand(.65);
        At(tracker, 0, closed);
        long eventId = One(At(tracker, 120, closed)).ExecuteEventId;
        At(tracker, 160, open);
        At(tracker, 200, closed);
        Require(One(At(tracker, 320, closed)).ExecuteEventId == eventId,
            "One bad open estimate rearmed a held pinch.");

        At(tracker, 360, open);
        At(tracker, 390, open);
        At(tracker, 410, closed);
        Require(One(At(tracker, 530, closed)).ExecuteEventId == eventId,
            "An open interval shorter than 60 ms rearmed a held pinch.");

        At(tracker, 570, open);
        Empty(At(tracker, 610), "A missing release produced a cursor.");
        At(tracker, 650, open);
        At(tracker, 690, closed);
        Require(One(At(tracker, 810, closed)).ExecuteEventId == eventId,
            "Missing observations counted toward confirmed release.");

        At(tracker, 850, open);
        At(tracker, 920, open);
        At(tracker, 960, closed);
        HandCursor next = One(At(tracker, 1080, closed));
        Pulse(next, 1080, "A confirmed release did not allow the next pinch.");
        Require(next.ExecuteEventId > eventId, "A new pinch reused the prior event identity.");
    }

    private static void CheckDelayedFreshFrames()
    {
        var tracker = new HandGestureTracker();
        HandDetection closed = Hand(.15), open = Hand(.65);
        // Each result is 220 ms old when it arrives, and each source frame is
        // 220 ms after the last. Both are fresh; adding the two gaps is wrong.
        Quiet(One(At(tracker, 0, 220, closed)), 220, "A delayed first frame executed.");
        HandCursor first = One(At(tracker, 220, 440, closed));
        Pulse(first, 440, "Inference latency erased a fresh pending pinch.");
        for (int frameTime = 440; frameTime <= 1320; frameTime += 220)
            Require(One(At(tracker, frameTime, frameTime + 220, closed)).ExecuteEventId == first.ExecuteEventId,
                "Inference latency erased a held pinch and allowed repeated execution.");

        Empty(At(tracker, 1320, 1560, open), "A delayed duplicate release was accepted.");
        Empty(At(tracker, 1900, 1600, open), "A future release was accepted.");
        Empty(At(tracker, 1400, 1800, open), "A stale release was accepted.");
        Require(One(At(tracker, 1580, 1800, closed)).ExecuteEventId == first.ExecuteEventId,
            "Invalid release frames changed a delayed held-pinch identity.");

        // Genuine gaps between camera observations still expire the identity.
        Quiet(One(At(tracker, 2000, 2220, closed)), 2220,
            "An expired hand observation retained its old execution state.");
        HandCursor reacquired = One(At(tracker, 2220, 2440, closed));
        Pulse(reacquired, 2440, "A new source-time dwell failed after hand reacquisition.");
        Require(reacquired.ExecuteEventId > first.ExecuteEventId, "Reacquisition reused the expired event ID.");
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
        At(tracker, 340, Hand(.65, x: 300), second);
        At(tracker, 380, first, second);
        cursors = At(tracker, 500, first, second);
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

    private static void CheckSelectionAnchor()
    {
        var tracker = new HandGestureTracker();
        HandDetection open = Hand(.65), latestOpen = Hand(.65, x: 332);
        HandDetection closing = CurlIndex(Hand(.40, x: 332));
        HandDetection closed = CurlIndex(Hand(.15, x: 332));
        HandDetection jitter = CurlIndex(Hand(.32, x: 332));
        NoAnchor(One(At(tracker, 0, open)), "An open pointing hand already had an anchor.");
        NoAnchor(One(At(tracker, 40, latestOpen)), "Moving while open retained an old target.");
        Anchored(One(At(tracker, 80, closing)), latestOpen.IndexTip, 40, closing.IndexTip);
        Anchored(One(At(tracker, 120, closed)), latestOpen.IndexTip, 40, closed.IndexTip);
        Anchored(One(At(tracker, 173, jitter)), latestOpen.IndexTip, 40, jitter.IndexTip);
        Anchored(One(At(tracker, 210, closed)), latestOpen.IndexTip, 40, closed.IndexTip);
        Empty(At(tracker, 240), "A missing anchored hand produced a cursor.");
        Anchored(One(At(tracker, 270, closed)), latestOpen.IndexTip, 40, closed.IndexTip);
        HandCursor selected = One(At(tracker, 305, closed));
        Anchored(selected, latestOpen.IndexTip, 40, closed.IndexTip);
        Pulse(selected, 305, "A curled finger failed to confirm the anchored selection.");
        Require(Distance(selected.Position, selected.SelectionPosition!.Value) > 60,
            "The test did not move the closing fingertip away from its pointed target.");

        // An isolated open-looking estimate cannot discard a fired selection.
        Anchored(One(At(tracker, 345, latestOpen)), latestOpen.IndexTip, 40, latestOpen.IndexTip);
        NoAnchor(One(At(tracker, 415, latestOpen)), "Confirmed release retained a selection anchor.");
        Anchored(One(At(tracker, 455, closed)), latestOpen.IndexTip, 415, closed.IndexTip);
    }

    private static void CheckSelectionCancellation()
    {
        var tracker = new HandGestureTracker();
        HandDetection open = Hand(.65), closing = CurlIndex(Hand(.40)), closed = CurlIndex(Hand(.15));
        At(tracker, 0, open);
        At(tracker, 40, closing);
        foreach (int time in new[] { 200, 350, 500, 650 })
            Anchored(One(At(tracker, time, closing)), open.IndexTip, 0, closing.IndexTip);
        Cancelled(One(At(tracker, 780, closed)), 0, "An old pointing target did not expire.");
        HandCursor expired = One(At(tracker, 900, closed));
        Pulse(expired, 900, "An expired selection incorrectly disabled gesture feedback.");
        Cancelled(expired, 0, "An expired target fell back to the curled fingertip at execution.");
        At(tracker, 940, open);
        NoAnchor(One(At(tracker, 1010, open)), "Release did not clear an expired anchor.");
        Anchored(One(At(tracker, 1050, closed)), open.IndexTip, 1010, closed.IndexTip);

        tracker.Reset();
        At(tracker, 0, open);
        At(tracker, 40, closing);
        HandDetection moved = CurlIndex(Hand(.40, x: 430));
        Cancelled(One(At(tracker, 100, moved)), 0, "Moving the palm over one palm-width kept the old target.");
        Cancelled(One(At(tracker, 160, closed)), 0, "Returning to the old location resurrected a cancelled target.");
        HandCursor cancelled = One(At(tracker, 280, closed));
        Pulse(cancelled, 280, "Cancellation incorrectly disabled the pinch gesture itself.");
        Cancelled(cancelled, 0, "A moved-away selection fell back to the current fingertip.");
    }

    private static void CheckSelectionFallbackAndIdentity()
    {
        var tracker = new HandGestureTracker();
        HandDetection open = Hand(.65), closed = CurlIndex(Hand(.15));
        At(tracker, 0, closed);
        HandCursor withoutOpen = One(At(tracker, 120, closed));
        Pulse(withoutOpen, 120, "A hand first seen pinched stopped recognizing gestures.");
        NoAnchor(withoutOpen, "A hand without a pointing observation invented an anchor.");
        tracker.Reset();
        At(tracker, 0, open);
        NoAnchor(One(At(tracker, 250, closed)), "A closing hand borrowed a pointing pose older than 200 ms.");

        tracker.Reset();
        At(tracker, 0, open);
        At(tracker, 40, closed);
        tracker.Reset();
        NoAnchor(One(At(tracker, 0, closed)), "Reset retained a previous pointing target.");
        tracker.Reset();
        At(tracker, 0, open);
        At(tracker, 40, closed);
        NoAnchor(One(At(tracker, 440, closed)), "A lost hand identity retained a previous pointing target.");

        tracker.Reset();
        HandDetection firstOpen = Hand(.65, x: 300), firstClosed = CurlIndex(Hand(.15, x: 300));
        HandDetection secondOpen = Hand(.65, x: 700), secondClosed = CurlIndex(Hand(.15, x: 700));
        At(tracker, 0, firstOpen, secondOpen);
        At(tracker, 40, firstClosed, secondOpen);
        var pair = At(tracker, 160, secondClosed, firstClosed);
        Anchored(Nearest(pair, firstClosed.IndexTip), firstOpen.IndexTip, 0, firstClosed.IndexTip);
        Anchored(Nearest(pair, secondClosed.IndexTip), secondOpen.IndexTip, 40, secondClosed.IndexTip);
        pair = At(tracker, 280, firstClosed, secondClosed);
        Pulse(Nearest(pair, secondClosed.IndexTip), 280, "The second hand failed to execute independently.");
        Anchored(Nearest(pair, secondClosed.IndexTip), secondOpen.IndexTip, 40, secondClosed.IndexTip);

        tracker.Reset();
        At(tracker, 0, firstOpen, secondClosed);
        At(tracker, 40, secondClosed, firstClosed);
        pair = At(tracker, 160, firstClosed, secondClosed);
        NoAnchor(Nearest(pair, secondClosed.IndexTip), "One hand borrowed the other hand's pointing target.");
        Anchored(Nearest(pair, firstClosed.IndexTip), firstOpen.IndexTip, 0, firstClosed.IndexTip);
    }

    private static HandDetection CurlIndex(HandDetection hand)
    {
        PixelPoint[] points = hand.Landmarks.ToArray();
        foreach (int index in new[] { 4, 8 })
            points[index] = new PixelPoint(points[index].X, points[index].Y + 75);
        return hand with { Landmarks = points };
    }

    private static void Anchored(HandCursor cursor, PixelPoint expected, int time, PixelPoint actualTip)
    {
        Require(cursor.SelectionPosition is { } position && Distance(position, expected) < .001,
            "The selection anchor did not preserve the most recent open fingertip.");
        Require(cursor.SelectionFrameTime == Time(time), "The selection anchor lost its source timestamp.");
        Require(Distance(cursor.Position, actualTip) < .001, "Anchoring changed the actual fingertip cursor.");
    }

    private static void NoAnchor(HandCursor cursor, string message) =>
        Require(cursor.SelectionPosition is null && cursor.SelectionFrameTime is null, message);

    private static void Cancelled(HandCursor cursor, int time, string message) =>
        Require(cursor.SelectionPosition is { } position && !double.IsFinite(position.X) &&
            !double.IsFinite(position.Y) && cursor.SelectionFrameTime == Time(time), message);

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
