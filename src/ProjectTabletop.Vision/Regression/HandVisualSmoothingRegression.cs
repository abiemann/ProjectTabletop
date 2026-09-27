using ProjectTabletop.Vision;

internal static class HandVisualSmoothingRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckSteadyJitterAndResolution();
        CheckMotionAndCadence();
        CheckIdentityAndDropouts();
        CheckRejectedTimesAndReset();
        CheckMetadataAndInvalidObservations();
        Console.WriteLine("Hand visual smoothing regression: steady fingertip jitter reduction, responsive deliberate " +
            "motion and jump reset, resolution/cadence invariance, four-tip consistency, independent identities, " +
            "no missing-hand ghosts, stale/order/reset barriers, and untouched raw gesture metadata passed.");
    }

    private static void CheckSteadyJitterAndResolution()
    {
        var smoother = new HandVisualSmoother();
        var doubled = new HandVisualSmoother();
        Feed(smoother, Hand(), 0);
        Feed(doubled, Hand(resolution: 2), 0);
        double rawError = 0, visualError = 0;
        int samples = 0;
        for (int index = 1; index <= 90; index++)
        {
            double dx = 3 * Math.Sin(index * 1.9), dy = 2 * Math.Cos(index * 1.37);
            var hand = Hand(dx, dy);
            var visual = Feed(smoother, hand, index * 30);
            var highResolution = Feed(doubled, Hand(dx, dy, 2), index * 30);
            Require(Distance(visual.Position, new(highResolution.Position.X / 2, highResolution.Position.Y / 2)) < 1e-8,
                "Changing camera resolution changed the normalized visual response.");
            Require(visual.FingerTips.Count == 4 && visual.FingerTips[0] == visual.Position,
                "The index fingertip marker disagrees with the main visual cursor.");
            if (index <= 15) continue;
            var baseline = Hand();
            for (int finger = 0; finger < 4; finger++)
            {
                PixelPoint expected = baseline.Landmarks[new[] { 8, 12, 16, 20 }[finger]];
                rawError += Squared(hand.Landmarks[new[] { 8, 12, 16, 20 }[finger]], expected);
                visualError += Squared(visual.FingerTips[finger], expected);
                samples++;
            }
        }
        double rawRms = Math.Sqrt(rawError / samples), visualRms = Math.Sqrt(visualError / samples);
        Require(visualRms < rawRms * .4,
            $"Steady fingertip jitter was not reduced enough: raw {rawRms:F3}px, visual {visualRms:F3}px.");
        Console.WriteLine($"Steady hand: raw tip jitter RMS {rawRms:F2}px → visual {visualRms:F2}px.");
    }

    private static void CheckMotionAndCadence()
    {
        var step = new HandVisualSmoother();
        var origin = Feed(step, Hand(), 0);
        var response = Feed(step, Hand(20), 33);
        Require(response.Position.X - origin.Position.X >= 12,
            "An intentional fingertip movement waited too long behind the strong jitter filter.");
        for (int frame = 2; frame <= 5; frame++) response = Feed(step, Hand(20), frame * 33);
        Require(Math.Abs(response.Position.X - (origin.Position.X + 20)) < 2.5,
            "The visual cursor kept a sluggish tail after an intentional movement.");
        var jumped = Feed(step, Hand(140), 198);
        Require(jumped.Position == Hand(140).IndexTip, "A large position jump interpolated across unrelated locations.");

        var moving = new HandVisualSmoother();
        Feed(moving, Hand(), 0);
        double maximumLag = 0;
        for (int frame = 1; frame <= 48; frame++)
        {
            var hand = Hand(frame * 8);
            var visual = Feed(moving, hand, frame * 33);
            maximumLag = Math.Max(maximumLag, Distance(visual.Position, hand.IndexTip));
        }
        Require(maximumLag < 10, $"A steadily moving hand accumulated {maximumLag:F2}px visual lag.");

        PixelPoint AtCadence(int milliseconds)
        {
            var filter = new HandVisualSmoother();
            Feed(filter, Hand(), 0);
            HandCursor? visual = null;
            for (int time = milliseconds; time <= 200; time += milliseconds)
                visual = Feed(filter, Hand(1), time);
            return visual!.Position;
        }
        Require(Distance(AtCadence(20), AtCadence(25)) < 1e-8,
            "Equal elapsed time at different frame cadence changed the small-motion response.");
        Console.WriteLine($"Deliberate motion: maximum fingertip lag {maximumLag:F2}px at 8px/33ms.");
    }

    private static void CheckIdentityAndDropouts()
    {
        var filter = new HandVisualSmoother();
        HandDetection a = Hand(), b = Hand(600);
        filter.Update([Cursor(a, 1), Cursor(b, 2)], [a, b], Time(0), Time(0));
        var movedA = Hand(2); var movedB = Hand(598);
        var reordered = filter.Update([Cursor(movedB, 2), Cursor(movedA, 1)], [movedA, movedB], Time(33), Time(33));
        Require(reordered[0].TrackingId == 2 && reordered[0].Position.X > movedB.IndexTip.X &&
            reordered[1].TrackingId == 1 && reordered[1].Position.X < movedA.IndexTip.X,
            "Detection/cursor reordering mixed independent smoothing histories.");
        var onlyB = filter.Update([Cursor(b, 2)], [b], Time(100), Time(100));
        Require(onlyB.Count == 1 && onlyB[0].TrackingId == 2,
            "A missing hand produced a retained ghost cursor.");
        var returned = Feed(filter, Hand(3), 130, id: 1);
        Require(returned.Position.X < Hand(3).IndexTip.X,
            "A brief dropout discarded a valid same-identity visual history.");
        Require(filter.Update([], [], Time(150), Time(150)).Count == 0,
            "Empty detections returned old visual tips.");
        var expired = Feed(filter, Hand(4), 500, id: 1);
        Require(expired.Position == Hand(4).IndexTip, "A past-350ms gap interpolated from an old hand.");
        var newIdentity = Feed(filter, Hand(8), 533, id: 3);
        Require(newIdentity.Position == Hand(8).IndexTip, "A new tracking ID inherited another hand's filtered position.");
        var unknown = Feed(filter, Hand(10), 566, id: 0);
        Require(unknown.Position == Hand(10).IndexTip, "An anonymous hand was assigned persistent visual identity.");

        var duplicates = filter.Update([Cursor(a, 9), Cursor(b, 9)], [a, b], Time(600), Time(600));
        Require(duplicates[0].Position == a.IndexTip && duplicates[1].Position == b.IndexTip,
            "Duplicate tracking IDs interpolated between separate hands.");
        Require(Feed(filter, Hand(1), 633, id: 9).Position == Hand(1).IndexTip,
            "Duplicate tracking IDs left ambiguous visual history behind.");
    }

    private static void CheckRejectedTimesAndReset()
    {
        var filter = new HandVisualSmoother();
        var control = new HandVisualSmoother();
        Feed(filter, Hand(), 0); Feed(control, Hand(), 0);
        Feed(filter, Hand(2), 33); Feed(control, Hand(2), 33);
        var altered = Hand(45); var cursor = Cursor(altered);
        Require(filter.Update([cursor], [altered], Time(10), Time(40)).Count == 0,
            "An out-of-order frame produced a visual cursor.");
        Require(filter.Update([cursor], [altered], Time(50), Time(49)).Count == 0,
            "A future source frame produced a visual cursor.");
        Require(filter.Update([cursor], [altered], Time(100), Time(500)).Count == 0,
            "A stale source frame produced a visual cursor.");
        Require(filter.Update([cursor], [altered], Time(34), Time(30)).Count == 0,
            "A reversed completion clock produced a visual cursor.");
        Require(Feed(filter, Hand(-2), 66).Position == Feed(control, Hand(-2), 66).Position,
            "A rejected timestamp advanced or contaminated visual smoothing history.");
        filter.Reset();
        Require(Feed(filter, Hand(1), 20).Position == Hand(1).IndexTip,
            "Reset did not clear source-order barriers and prior filtered positions.");
    }

    private static void CheckMetadataAndInvalidObservations()
    {
        var filter = new HandVisualSmoother();
        Feed(filter, Hand(), 0);
        var hand = Hand(2);
        var raw = Cursor(hand) with
        {
            ExecuteEventId = 81, ExecuteUntil = Time(999), SelectionPosition = new(123, 456),
            SelectionFrameTime = Time(10), HasFourExtendedFingers = true, FingersTogether = true,
            IndexFingerSeparated = true, IsSpreadOut = true
        };
        PixelPoint[] landmarks = hand.Landmarks.ToArray(), tips = raw.FingerTips.ToArray();
        var visual = filter.Update([raw], [hand], Time(33), Time(33)).Single();
        Require(visual.Position != raw.Position && visual.ExecuteEventId == raw.ExecuteEventId &&
            visual.ExecuteUntil == raw.ExecuteUntil && visual.SelectionPosition == raw.SelectionPosition &&
            visual.SelectionFrameTime == raw.SelectionFrameTime && visual.TrackingId == raw.TrackingId &&
            visual.HasFourExtendedFingers == raw.HasFourExtendedFingers && visual.FingersTogether == raw.FingersTogether &&
            visual.IndexFingerSeparated == raw.IndexFingerSeparated && visual.IsSpreadOut == raw.IsSpreadOut,
            "Display smoothing changed a raw gesture, selection anchor or identity field.");
        Require(raw.Position == hand.IndexTip && raw.FingerTips.SequenceEqual(tips) && hand.Landmarks.SequenceEqual(landmarks),
            "Display smoothing modified original camera observations.");
        Require(visual.FingerTips is not PixelPoint[], "Visual fingertips expose mutable smoothing storage.");
        var noMatchedHand = Hand(6);
        var unmatched = filter.Update([Cursor(noMatchedHand)], [], Time(66), Time(66)).Single();
        Require(unmatched.Position == noMatchedHand.IndexTip, "Unmatched raw cursor used an unrelated hand's scale.");
        Require(Feed(filter, Hand(7), 99).Position == Hand(7).IndexTip,
            "Invalid/unmatched observations left interpolation history active.");
        var badHand = Hand(8) with { Confidence = double.NaN };
        Require(filter.Update([Cursor(badHand)], [badHand], Time(132), Time(132)).Single().Position == badHand.IndexTip,
            "Invalid confidence entered visual smoothing.");
        var ambiguous = Hand(9);
        Require(filter.Update([Cursor(ambiguous)], [ambiguous, ambiguous], Time(165), Time(165)).Single().Position == ambiguous.IndexTip,
            "An ambiguous fingertip-to-hand match entered visual smoothing.");
    }

    private static HandCursor Feed(HandVisualSmoother filter, HandDetection hand, int milliseconds, long id = 1) =>
        filter.Update([Cursor(hand, id)], [hand], Time(milliseconds), Time(milliseconds)).Single();
    private static HandCursor Cursor(HandDetection hand, long id = 1) => new(hand.IndexTip, DateTimeOffset.MinValue)
    {
        TrackingId = id, FingerTips = Array.AsReadOnly(new[] { 8, 12, 16, 20 }.Select(i => hand.Landmarks[i]).ToArray()),
        HasFourExtendedFingers = true
    };
    private static HandDetection Hand(double dx = 0, double dy = 0, double resolution = 1)
    {
        PixelPoint[] points =
        [
            new(0, 80), new(-25, 60), new(-40, 35), new(-55, 10), new(-65, -10),
            new(-30, 0), new(-35, -40), new(-38, -70), new(-40, -100),
            new(0, 0), new(0, -45), new(0, -80), new(0, -115),
            new(30, 0), new(35, -40), new(38, -70), new(40, -95),
            new(55, 15), new(65, -10), new(72, -40), new(77, -60)
        ];
        return new(Array.AsReadOnly(points.Select(p => new PixelPoint((300 + p.X + dx) * resolution,
            (300 + p.Y + dy) * resolution)).ToArray()), .98, .8);
    }
    private static DateTimeOffset Time(int milliseconds) => Epoch.AddMilliseconds(milliseconds);
    private static double Squared(PixelPoint a, PixelPoint b) => Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Squared(a, b));
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
