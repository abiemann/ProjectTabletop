using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandTrackingDiagnosticsRegression
{
    public static void Run()
    {
        string models = Path.Combine(AppContext.BaseDirectory, "Models", "Hands");
        using var ordinary = new HandTrackingEngine(models);
        using var instrumented = new HandTrackingEngine(models) { CaptureDiagnostics = true };
        using var fixture = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mediapipe-pointing-up.jpg"));
        Require(!fixture.Empty(), "The licensed hand fixture is missing.");
        using var bgra = new Mat();
        Cv2.CvtColor(fixture, bgra, ColorConversionCodes.BGR2BGRA);
        using var resized = new Mat();
        Cv2.Resize(bgra, resized, new Size(233, 244), interpolation: InterpolationFlags.Area);
        using var frame = new Mat(1080, 1920, MatType.CV_8UC4, new Scalar(128, 128, 128, 255));
        using (var destination = new Mat(frame, new Rect(850, 400, resized.Width, resized.Height)))
            resized.CopyTo(destination);
        byte[] pixels = Bytes(frame);
        bool sawTracked = false, sawPeriodic = false, sawTile = false, sawSuppression = false;
        HandTrackingDiagnostics? firstSnapshot = null;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            var expected = ordinary.Detect(frame.Width, frame.Height, frame.Width * 4, pixels);
            var actual = instrumented.Detect(frame.Width, frame.Height, frame.Width * 4, pixels);
            SameHands(expected, actual);
            Require(ordinary.LastDiagnostics is null, "Disabled capture retained diagnostics.");
            var trace = instrumented.LastDiagnostics;
            Require(trace is not null && trace.SelectedCandidateIndices.Count == actual.Count,
                "The trace did not describe the final selected detections.");
            firstSnapshot ??= trace;
            Require(trace!.PreNmsCandidateIndices.Count >= trace.SelectedCandidateIndices.Count,
                "Post-NMS candidates were absent from pre-NMS candidates.");
            for (int index = 0; index < actual.Count; index++)
            {
                var candidate = trace.Candidates[trace.SelectedCandidateIndices[index]];
                Require(candidate.NmsResult == "selected" && candidate.Result == "hand-accepted" &&
                    candidate.Hand is not null && candidate.HandConfidence == candidate.Hand.Confidence &&
                    ReferenceEquals(candidate.Hand, actual[index]), "Selected candidate provenance or landmark identity was lost.");
            }
            foreach (var candidate in trace.Candidates)
            {
                Require(candidate.Index >= 0 && candidate.Index < trace.Candidates.Count && candidate.Result != "not-run",
                    "An inference attempt was missing a final outcome.");
                if (candidate.Source == "tracked-roi")
                {
                    sawTracked = true;
                    Require(candidate.PreviousHandIndex is not null && candidate.SearchViewBounds is null,
                        "A tracked ROI lost its previous-hand reference.");
                    if (candidate.Hand is not null)
                        Require(candidate.PreviousBoundsIou is >= 0 and <= 1, "A returned tracked pose lost its IoU decision.");
                }
                else Require(candidate.SearchViewBounds is not null && candidate.PreviousHandIndex is null,
                    "A search proposal lost its full-frame/tile coordinates.");
                if (candidate.NmsResult == "overlap")
                {
                    sawSuppression = true;
                    Require(candidate.SuppressedByCandidateIndex is { } suppressedBy &&
                        trace.SelectedCandidateIndices.Contains(suppressedBy), "Suppression did not identify the winning candidate.");
                }
            }
            Require(trace.TrackedRoiAttempts == trace.Candidates.Count(candidate => candidate.Source == "tracked-roi"),
                "The tracked inference count disagrees with its candidates.");
            Require(trace.Searches.Sum(search => search.ProposalCount) ==
                trace.Candidates.Count(candidate => candidate.Source != "tracked-roi"),
                "Search proposal counts disagreed with attempted hand inferences.");
            sawPeriodic |= trace.FullSearchReason == "periodic";
            sawTile |= trace.Searches.Any(search => search.Source == "tile");
        }
        Require(sawTracked && sawPeriodic && sawTile && sawSuppression,
            "The fixture did not exercise tracked ROI, periodic search, tiled search and duplicate suppression.");
        Require(firstSnapshot is { FullSearch: true, FullSearchReason: "no-tracked-hand", PreviousHandCount: 0 } &&
            firstSnapshot.SelectedCandidateIndices.Count == 1,
            "A later Detect call mutated the original diagnostic snapshot.");

        using var empty = new Mat(1080, 1920, MatType.CV_8UC4, new Scalar(128, 128, 128, 255));
        pixels = Bytes(empty);
        SameHands(ordinary.Detect(empty.Width, empty.Height, empty.Width * 4, pixels),
            instrumented.Detect(empty.Width, empty.Height, empty.Width * 4, pixels));
        var lost = instrumented.LastDiagnostics!;
        Require(lost.FullSearch && lost.TrackedRoiAttempts > 0 && lost.SelectedCandidateIndices.Count == 0 &&
            lost.Candidates.Any(candidate => candidate.Source == "tracked-roi" && candidate.Hand is null &&
                candidate.HandConfidence is not null && candidate.Result == "hand-score-rejected"),
            "A failed tracked inference omitted its score/rejection or search fallback.");
        instrumented.CaptureDiagnostics = false;
        Require(instrumented.LastDiagnostics is null, "Disabling diagnostics retained a previous capture.");
        SameHands(ordinary.Detect(empty.Width, empty.Height, empty.Width * 4, pixels),
            instrumented.Detect(empty.Width, empty.Height, empty.Width * 4, pixels));
        Require(instrumented.LastDiagnostics is null, "A disabled Detect allocated a diagnostic snapshot.");
        instrumented.CaptureDiagnostics = true;
        instrumented.ResetTracking();
        Require(instrumented.LastDiagnostics is null, "Reset retained diagnostic state from an older track.");
        Console.WriteLine("Hand diagnostics regression: enabled/disabled inference parity, immutable snapshots, " +
            "tracked/full/tile provenance, IoU, pre/post NMS, suppression, rejected scores and reset passed.");
    }

    private static void SameHands(IReadOnlyList<HandDetection> expected, IReadOnlyList<HandDetection> actual)
    {
        Require(expected.Count == actual.Count, "Diagnostics changed the number of detected hands.");
        for (int hand = 0; hand < expected.Count; hand++)
        {
            Require(Math.Abs(expected[hand].Confidence - actual[hand].Confidence) < 1e-6 &&
                Math.Abs(expected[hand].RightHandProbability - actual[hand].RightHandProbability) < 1e-6,
                "Diagnostics changed model confidence or handedness.");
            for (int index = 0; index < 21; index++)
            {
                PixelPoint a = expected[hand].Landmarks[index], b = actual[hand].Landmarks[index];
                Require(Math.Abs(a.X - b.X) < 1e-4 && Math.Abs(a.Y - b.Y) < 1e-4,
                    "Diagnostics changed an inferred landmark or selection order.");
            }
        }
    }

    private static byte[] Bytes(Mat image)
    {
        var result = new byte[image.Width * image.Height * 4];
        Marshal.Copy(image.Data, result, 0, result.Length);
        return result;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
