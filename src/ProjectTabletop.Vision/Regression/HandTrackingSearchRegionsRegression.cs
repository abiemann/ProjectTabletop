using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandTrackingSearchRegionsRegression
{
    public static void Run()
    {
        using var engine = new HandTrackingEngine(Path.Combine(AppContext.BaseDirectory, "Models", "Hands"))
            { CaptureDiagnostics = true };
        CheckValidationAndLimits(engine);
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mediapipe-pointing-up.jpg"));
        Require(!original.Empty(), "The licensed hand photograph is missing.");
        using var photograph = new Mat();
        Cv2.CvtColor(original, photograph, ColorConversionCodes.BGR2BGRA);
        CheckSmallHands(engine, photograph);
        CheckOtherHandAndCadence(engine, photograph);
        CheckRestrictedAcquisition(engine, photograph);
        CheckEdgePalmContext(engine, photograph);
        CheckRecoveryCropBounds(engine);
        Console.WriteLine("Focused hand search: native square crops, tiny-hand acquisition, full-frame landmark coordinates, " +
            "invalid/duplicate/two-region limits, unchanged search cadence, fresh tracked priority, fallback second hand, " +
            "restricted control acquisition with idle/lost-track waiting, and conditional camera-edge palm recovery passed.");
    }

    private static void CheckValidationAndLimits(HandTrackingEngine engine)
    {
        using var blank = new Mat(480, 640, MatType.CV_8UC4, new Scalar(128, 128, 128, 255));
        HandTrackingBounds[] invalid =
        [
            new(double.NaN, 0, 100, 100), new(0, double.PositiveInfinity, 100, 100),
            new(0, 0, double.PositiveInfinity, double.PositiveInfinity), new(0, 0, double.NaN, double.NaN),
            new(-1, 0, 100, 100), new(0, -1, 100, 100), new(0, 0, -100, -100),
            new(0, 0, 0, 0), new(0, 0, 31, 31), new(0, 0, 100, 99),
            new(600, 0, 100, 100), new(0, 450, 100, 100), new(double.MaxValue, 0, 100, 100)
        ];
        var absent = Detect(engine, blank, invalid);
        Require(absent.Count == 0 && engine.LastDiagnostics!.Searches.All(search => search.Source != "motion-roi"),
            "An invalid crop produced a detection or reached the model.");
        var ordinarySearches = engine.LastDiagnostics!.Searches.ToArray();
        HandTrackingBounds first = new(60, 50, 160, 160), duplicate = new(64, 54, 160, 160),
            second = new(380.75, 250.25, 150.5, 150.5), third = new(250, 30, 100, 100);
        absent = Detect(engine, blank, [.. invalid, first, first, duplicate, second, third]);
        var trace = engine.LastDiagnostics!;
        var focused = trace.Searches.Where(search => search.Source == "motion-roi").ToArray();
        Require(absent.Count == 0 && focused.Length == 2 && focused[0].ViewBounds == first &&
            focused[1].ViewBounds == new HandTrackingBounds(380, 250, 150, 150),
            "Valid crops were not bounded, deduplicated and rounded to two native square views.");
        Require(trace.Searches.Take(2).All(search => search.Source == "motion-roi") &&
            trace.Searches.Where(search => search.Source is "full-frame" or "tile").SequenceEqual(ordinarySearches),
            "Focused hints changed or suppressed ordinary full-frame/tile fallback searches.");

        engine.ResetTracking();
        absent = Detect(engine, blank, [.. invalid, first, first, duplicate, second, third], restricted: true);
        trace = engine.LastDiagnostics!;
        focused = trace.Searches.Where(search => search.Source == "motion-roi").ToArray();
        Require(absent.Count == 0 && focused.Length == 2 && focused[0].ViewBounds == first &&
            focused[1].ViewBounds == new HandTrackingBounds(380, 250, 150, 150) &&
            trace.Searches.All(search => IsFocusedSource(search.Source)),
            "Restricted acquisition bypassed region validation/limits or searched beyond focused control context.");
        Require(trace.Searches.All(search => search.ViewBounds.Width == search.ViewBounds.Height &&
            search.ViewBounds.X >= 0 && search.ViewBounds.Y >= 0 &&
            search.ViewBounds.X + search.ViewBounds.Width <= blank.Width &&
            search.ViewBounds.Y + search.ViewBounds.Height <= blank.Height),
            "Focused recovery used a non-square or out-of-frame model input.");
    }

    private static void CheckSmallHands(HandTrackingEngine engine, Mat photograph)
    {
        int improvements = 0;
        double worstTipError = 0;
        foreach (int photoWidth in new[] { 96, 112, 128 })
        {
            using var frame = BlankFrame();
            PixelPoint expected = PlaceHand(frame, photograph, photoWidth, 1260, 580);
            var hint = new HandTrackingBounds(1100, 420, 320, 320);
            engine.ResetTracking();
            var unhinted = Detect(engine, frame);
            bool ordinaryFound = unhinted.Any(hand => Distance(hand.IndexTip, expected) <= 10);
            engine.ResetTracking();
            var focused = Detect(engine, frame, [hint]);
            var trace = engine.LastDiagnostics!;
            Require(focused.Count == 1 && Distance(focused[0].IndexTip, expected) <= 10,
                $"A {photoWidth}px photograph was not acquired accurately through its native square crop.");
            worstTipError = Math.Max(worstTipError, Distance(focused[0].IndexTip, expected));
            var proposal = trace.Candidates.FirstOrDefault(candidate => candidate.Source == "motion-roi" &&
                candidate.Hand is not null && Distance(candidate.Hand.IndexTip, expected) <= 10);
            Require(proposal is { SearchViewBounds: not null, PreviousHandIndex: null } &&
                proposal.SearchViewBounds.Value == hint && proposal.PalmBounds.X > hint.X &&
                proposal.PalmBounds.Y > hint.Y,
                "The focused candidate lost its native camera-coordinate palm/landmark provenance.");
            Require(trace.Searches[0].Source == "motion-roi" &&
                trace.Searches.Any(search => search.Source == "full-frame") &&
                trace.Searches.Any(search => search.Source == "tile"),
                "Focused acquisition did not retain normal full-frame and tile searches.");
            foreach (int index in trace.SelectedCandidateIndices)
                Require(ReferenceEquals(trace.Candidates[index].Hand, focused[0]),
                    "Focused acquisition synthesized or copied a selected inference.");
            if (!ordinaryFound) improvements++;
            Console.WriteLine($"Focused {photoWidth}px photograph: unhinted found={ordinaryFound}, " +
                $"hinted fingertip error={Distance(focused[0].IndexTip, expected):F2}px.");
        }
        Require(improvements > 0, "The small-hand fixture did not demonstrate additional focused acquisition.");
        Console.WriteLine($"Focused tiny-hand acquisition: {improvements} additional acquisitions, worst error {worstTipError:F2}px.");
    }

    private static void CheckOtherHandAndCadence(HandTrackingEngine engine, Mat photograph)
    {
        using var frame = BlankFrame();
        PixelPoint near = PlaceHand(frame, photograph, 112, 1400, 540);
        PixelPoint outside = PlaceHand(frame, photograph, 233, 350, 540);
        HandTrackingBounds hint = new(1240, 380, 320, 320);
        engine.ResetTracking();
        var hands = Detect(engine, frame, [hint]);
        RequirePair(hands, near, outside);
        Require(engine.LastDiagnostics!.Candidates.Any(candidate => candidate.Source is "tile" or "full-frame" &&
            candidate.Hand is not null && Distance(candidate.Hand.IndexTip, outside) < 12),
            "A successful focused crop prevented detection of the hand outside the supplied region.");

        for (int frameIndex = 1; frameIndex <= 6; frameIndex++)
        {
            hands = Detect(engine, frame, [hint]);
            var trace = engine.LastDiagnostics!;
            RequirePair(hands, near, outside);
            Require(trace.TrackedRoiAttempts == 2, "Focused acquisition did not become ordinary fresh ROI tracking.");
            if (frameIndex < 6)
            {
                Require(!trace.FullSearch && trace.Searches.Count == 0 &&
                    trace.Candidates.All(candidate => candidate.Source == "tracked-roi"),
                    "A supplied crop caused extra palm inference during a tracked-only frame.");
            }
            else
            {
                Require(trace.FullSearchReason == "periodic" &&
                    trace.Searches.Count(search => search.Source == "motion-roi") == 1,
                    "Focused hints changed the existing six-frame search cadence.");
                foreach (var tracked in trace.Candidates.Where(candidate => candidate.Source == "tracked-roi" &&
                    candidate.HandConfidence >= .90 && candidate.PreviousBoundsIou >= .65))
                {
                    var competing = trace.Candidates.Where(candidate => candidate.Source != "tracked-roi" &&
                        candidate.Hand is not null && tracked.Hand is not null &&
                        Distance(candidate.Hand.IndexTip, tracked.Hand.IndexTip) < 12).ToArray();
                    if (competing.All(candidate => candidate.HandConfidence <= tracked.HandConfidence + .01))
                        Require(trace.SelectedCandidateIndices.Contains(tracked.Index),
                            "Focused search replaced a reliable near-tied current-frame tracked inference.");
                }
            }
        }
        using var blank = BlankFrame();
        hands = Detect(engine, blank, [hint]);
        Require(hands.Count == 0 && engine.LastDiagnostics!.FullSearch &&
            engine.LastDiagnostics.Searches.Any(search => search.Source == "motion-roi"),
            "Old focused hints retained vanished hands or skipped normal reacquisition.");
    }

    private static void CheckRestrictedAcquisition(HandTrackingEngine engine, Mat photograph)
    {
        using var frame = BlankFrame();
        PixelPoint near = PlaceHand(frame, photograph, 112, 1400, 540);
        PlaceHand(frame, photograph, 233, 350, 540);
        HandTrackingBounds hint = new(1240, 380, 320, 320);
        engine.ResetTracking();
        foreach (IReadOnlyList<HandTrackingBounds>? hints in new IReadOnlyList<HandTrackingBounds>?[]
            { null, [], [new(-1, 0, 100, 100), new(0, 0, 100, 99), new(double.NaN, 0, 320, 320)] })
        {
            var idle = Detect(engine, frame, hints, restricted: true);
            var trace = engine.LastDiagnostics!;
            Require(idle.Count == 0 && !trace.FullSearch && trace.Searches.Count == 0 &&
                trace.Candidates.Count == 0 && trace.TrackedRoiAttempts == 0 &&
                trace.FullSearchReason == "waiting-for-control-disturbance",
                "A restricted idle frame inferred a hand without a valid control crop.");
        }

        var hands = Detect(engine, frame, [hint], restricted: true);
        Require(hands.Count == 1 && Distance(hands[0].IndexTip, near) < 12 &&
            engine.LastDiagnostics!.Searches.All(search => search.Source is "motion-roi" or "motion-roi-normalized") &&
            engine.LastDiagnostics.Candidates.Any(candidate => candidate.Source == "motion-roi" &&
                candidate.Hand is not null && candidate.SearchViewBounds == hint &&
                Distance(candidate.Hand.IndexTip, near) < 12),
            "Restricted acquisition missed its native crop or acquired the other hand outside it.");

        // Once the control's hand has been acquired, fresh ROI landmarks do not
        // require a continuing disturbance hint or periodic whole-frame scans.
        for (int frameIndex = 1; frameIndex <= 12; frameIndex++)
        {
            hands = Detect(engine, frame, restricted: true);
            var trace = engine.LastDiagnostics!;
            Require(hands.Count == 1 && Distance(hands[0].IndexTip, near) < 12 &&
                trace.TrackedRoiAttempts == 1 && !trace.FullSearch && trace.Searches.Count == 0 &&
                trace.Candidates.All(candidate => candidate.Source == "tracked-roi"),
                "Restricted tracking lost fresh ROI continuity or periodically scanned outside its controls.");
        }
        for (int frameIndex = 1; frameIndex <= 6; frameIndex++)
        {
            hands = Detect(engine, frame, [hint], restricted: true);
            var trace = engine.LastDiagnostics!;
            Require(hands.Count == 1 && Distance(hands[0].IndexTip, near) < 12 &&
                trace.Searches.All(search => search.Source is "motion-roi" or "motion-roi-normalized"),
                "Restricted periodic acquisition searched outside the control crop.");
            if (frameIndex == 6)
                Require(trace.FullSearchReason == "periodic" &&
                    trace.Searches.Count(search => search.Source == "motion-roi") == 1,
                    "Restricted control search changed the six-frame periodic search cadence.");
        }

        using var blank = BlankFrame();
        hands = Detect(engine, blank, restricted: true);
        var lost = engine.LastDiagnostics!;
        Require(hands.Count == 0 && lost.TrackedRoiAttempts == 1 && !lost.FullSearch &&
            lost.Searches.Count == 0 && lost.FullSearchReason == "waiting-for-control-disturbance",
            "A lost restricted hand triggered a global search or retained stale landmarks.");
        hands = Detect(engine, frame, restricted: true);
        Require(hands.Count == 0 && engine.LastDiagnostics!.TrackedRoiAttempts == 0 &&
            engine.LastDiagnostics.Searches.Count == 0,
            "A returning hand was acquired without a new control crop after its old track vanished.");
        hands = Detect(engine, frame, [hint], restricted: true);
        Require(hands.Count == 1 && Distance(hands[0].IndexTip, near) < 12,
            "A fresh control crop failed to reacquire the vanished restricted hand.");

        using var resized = new Mat(480, 640, MatType.CV_8UC4, new Scalar(128, 128, 128, 255));
        hands = Detect(engine, resized, [hint], restricted: true);
        Require(hands.Count == 0 && engine.LastDiagnostics!.PreviousHandCount == 1 &&
            engine.LastDiagnostics.TrackedRoiAttempts == 0 && engine.LastDiagnostics.Searches.Count == 0 &&
            engine.LastDiagnostics.FullSearchReason == "waiting-for-control-disturbance",
            "A changed camera size reused an out-of-frame control crop or old hand ROI.");
    }

    private static IReadOnlyList<HandDetection> Detect(HandTrackingEngine engine, Mat frame,
        IReadOnlyList<HandTrackingBounds>? hints = null, bool restricted = false)
    {
        var pixels = new byte[frame.Width * frame.Height * 4];
        Marshal.Copy(frame.Data, pixels, 0, pixels.Length);
        return engine.Detect(frame.Width, frame.Height, frame.Width * 4, pixels, hints, restricted);
    }

    private static Mat BlankFrame() => new(1080, 1920, MatType.CV_8UC4, new Scalar(128, 128, 128, 255));

    private static bool IsFocusedSource(string source) => source is "motion-roi" or "motion-roi-normalized" or
        "motion-roi-context" or "motion-roi-context-normalized";

    private static void CheckEdgePalmContext(HandTrackingEngine engine, Mat photograph)
    {
        // The same licensed hand reaches down from the camera's top edge.
        // A caption can locate its fingertip accurately while its narrow crop
        // excludes the palm above it. No private camera photograph is needed.
        foreach (var (photoWidth, hintSide, needsContext) in new[]
            { (350, 220, true), (420, 281, true), (280, 220, false) })
        {
            using var frame = BlankFrame();
            int height = (int)Math.Round(photoWidth * photograph.Height / (double)photograph.Width);
            using var scaled = new Mat();
            using var rotated = new Mat();
            Cv2.Resize(photograph, scaled, new Size(photoWidth, height), interpolation: InterpolationFlags.Area);
            Cv2.Rotate(scaled, rotated, RotateFlags.Rotate180);
            const int left = 1200, top = 0;
            using (var destination = new Mat(frame, new Rect(left, top, photoWidth, height))) rotated.CopyTo(destination);
            PixelPoint expected = new(left + photoWidth - .5 -
                (0.47388697 * photograph.Width + .5) * photoWidth / photograph.Width,
                top + height - .5 - (0.19592366 * photograph.Height + .5) * height / photograph.Height);
            var hint = new HandTrackingBounds(Math.Clamp((int)Math.Round(expected.X - hintSide / 2.0), 0, frame.Width - hintSide),
                Math.Clamp((int)Math.Round(expected.Y - hintSide / 2.0), 0, frame.Height - hintSide), hintSide, hintSide);
            engine.ResetTracking();
            var hands = Detect(engine, frame, [hint], restricted: true);
            var trace = engine.LastDiagnostics!;
            Require(hands.Count == 1 && hands[0].Confidence >= .8 && Distance(hands[0].IndexTip, expected) < 10,
                $"A {photoWidth}px hand at the camera edge was not acquired with valid confidence and native coordinates.");
            Require(trace.Searches.All(search => IsFocusedSource(search.Source)) &&
                trace.Searches[0].Source == "motion-roi" && trace.Searches[0].ViewBounds == hint,
                "An edge-hand recovery skipped its actual control crop or scanned the entire webcam.");
            var selected = trace.Candidates.Single(candidate => trace.SelectedCandidateIndices.Contains(candidate.Index));
            Require(ReferenceEquals(selected.Hand, hands[0]),
                "An edge-hand recovery synthesized a pose instead of selecting the current model inference.");
            if (needsContext)
            {
                Require(trace.Candidates.Where(candidate => candidate.Source == "motion-roi").All(candidate => candidate.Hand is null) &&
                    selected.Source == "motion-roi-context" && selected.PalmBounds.Y + selected.PalmBounds.Height / 2 < hint.Y,
                    "The edge-hand fixture did not recover the palm excluded by its tip-centered control crop.");
                Require(trace.Searches.Count == 2 && trace.Searches[1].Source == "motion-roi-context" &&
                    trace.Searches[1].ViewBounds.Width > hint.Width,
                    "A successful raw context recovery ran unnecessary color or global retries.");
            }
            else
                Require(trace.Searches.Count == 1 && selected.Source == "motion-roi",
                    "A successful original focused fit unnecessarily ran an expanded context search.");

            // Once acquired, this edge hand must remain a normal fresh track;
            // a continuing disturbed caption is unnecessary for landmark input.
            for (int index = 0; index < 8; index++)
            {
                var tracked = Detect(engine, frame, restricted: true);
                var trackedTrace = engine.LastDiagnostics!;
                Require(tracked.Count == 1 && tracked[0].Confidence >= .8 &&
                    Distance(tracked[0].IndexTip, expected) < 10 && trackedTrace.Searches.Count == 0 &&
                    trackedTrace.TrackedRoiAttempts == 1 &&
                    trackedTrace.Candidates.All(candidate => candidate.Source == "tracked-roi"),
                    "An acquired edge hand lost fresh ROI continuity or ran unrequested recovery searches.");
            }
            engine.ResetTracking();
            Require(Detect(engine, frame, restricted: true).Count == 0 && engine.LastDiagnostics!.Searches.Count == 0,
                "An idle restricted frame acquired the visible edge hand without a disturbed control hint.");
            Console.WriteLine($"Camera-edge {photoWidth}px hand: context needed={needsContext}, " +
                $"confidence={hands[0].Confidence:F3}, fingertip error={Distance(hands[0].IndexTip, expected):F2}px.");
        }
    }

    private static void CheckRecoveryCropBounds(HandTrackingEngine engine)
    {
        using var blank = BlankFrame();
        // Both extremes of the native camera require a square recovery view
        // to shift inward rather than stretch or read beyond the image.
        HandTrackingBounds[] hints = [new(0, 0, 281, 281), new(1639, 799, 281, 281)];
        engine.ResetTracking();
        Require(Detect(engine, blank, hints, restricted: true).Count == 0,
            "Recovery context invented a hand on a blank camera frame.");
        var trace = engine.LastDiagnostics!;
        var contexts = trace.Searches.Where(search => search.Source == "motion-roi-context").ToArray();
        Require(contexts.Length == 4 && contexts[0].ViewBounds == new HandTrackingBounds(0, 0, 506, 506) &&
            contexts[1].ViewBounds == new HandTrackingBounds(1414, 574, 506, 506) &&
            contexts[2].ViewBounds == new HandTrackingBounds(0, 0, 562, 562) &&
            contexts[3].ViewBounds == new HandTrackingBounds(1358, 518, 562, 562) &&
            trace.Searches.All(search => IsFocusedSource(search.Source)),
            "Camera-edge recovery views lost their square size, frame clamp, or focused-only restriction.");

        engine.ResetTracking();
        Require(Detect(engine, blank, [new(1320, 480, 600, 600)], restricted: true).Count == 0,
            "Capped recovery invented a hand on a blank camera frame.");
        Require(engine.LastDiagnostics!.Searches.Single(search => search.Source == "motion-roi-context").ViewBounds ==
            new HandTrackingBounds(1272, 432, 648, 648),
            "Recovery exceeded the 60% camera-short-side limit or lost its camera-edge clamp.");

        engine.ResetTracking();
        Require(Detect(engine, blank, [new(0, 0, 648, 648)], restricted: true).Count == 0 &&
            engine.LastDiagnostics!.Searches.All(search => !search.Source.Contains("context", StringComparison.Ordinal)),
            "An already capped focused crop ran a duplicate or smaller context retry.");
    }

    private static PixelPoint PlaceHand(Mat frame, Mat source, int width, int centerX, int centerY)
    {
        int height = (int)Math.Round(width * source.Height / (double)source.Width);
        using var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size(width, height), interpolation: InterpolationFlags.Area);
        int left = centerX - width / 2, top = centerY - height / 2;
        using (var destination = new Mat(frame, new Rect(left, top, width, height))) scaled.CopyTo(destination);
        return new(left + (0.47388697 * source.Width + .5) * width / source.Width - .5,
            top + (0.19592366 * source.Height + .5) * height / source.Height - .5);
    }

    private static void RequirePair(IReadOnlyList<HandDetection> hands, PixelPoint near, PixelPoint outside) =>
        Require(hands.Count == 2 && hands.Any(hand => Distance(hand.IndexTip, near) < 12) &&
            hands.Any(hand => Distance(hand.IndexTip, outside) < 12),
            "Focused acquisition or its fallback failed to preserve both independent hands.");

    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
