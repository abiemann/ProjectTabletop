using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandPalmColorCorrectionRegression
{
    private static readonly HandTrackingBounds Focus = new(1050, 370, 420, 420);

    public static void Run()
    {
        CheckEvidenceAndPixelGeometry();
        using var source = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mediapipe-pointing-up.jpg"));
        Require(!source.Empty(), "The licensed hand fixture is missing.");
        using var engine = new HandTrackingEngine(Path.Combine(AppContext.BaseDirectory, "Models", "Hands"))
            { CaptureDiagnostics = true };
        foreach (double greenGain in new[] { .60, .75 })
        {
            using var frame = CastHandFrame(source, greenGain, out PixelPoint expected);
            byte[] pixels = Bytes(frame), original = (byte[])pixels.Clone();
            engine.ResetTracking();
            var hands = engine.Detect(frame.Width, frame.Height, frame.Width * 4, pixels, [Focus]);
            var trace = engine.LastDiagnostics!;
            Require(hands.Count == 1 && Distance(hands[0].IndexTip, expected) < 6,
                "Palm color compensation did not recover the color-cast hand in original camera coordinates.");
            Require(trace.Candidates.Where(candidate => candidate.Source != "motion-roi-normalized")
                .All(candidate => candidate.Hand is null),
                "The controlled fixture no longer exercises recovery after all original hand fits failed.");
            var selected = trace.Candidates[trace.SelectedCandidateIndices.Single()];
            Require(selected.Source == "motion-roi-normalized" && selected.PalmScore >= .5 &&
                selected.HandConfidence >= .8 && ReferenceEquals(selected.Hand, hands[0]),
                "Recovery lowered confidence thresholds or synthesized a result instead of using fresh inference.");
            var retry = trace.Searches.Single(search => search.Source == "motion-roi-normalized");
            Require(retry.ViewBounds == Focus && retry.ColorCorrection is { ReferencePixelCount: >= 64 } correction &&
                correction.GreenGain > 1 && correction.RedGain < 1 &&
                new[] { correction.RedGain, correction.GreenGain, correction.BlueGain }.All(gain => gain is >= .75 and <= 1.35),
                "The focused retry lost its real camera crop or bounded adaptive color evidence.");
            Require(pixels.SequenceEqual(original), "Search compensation modified the original camera image.");

            // Continue on unchanged original RGB: acquired geometry is sufficient
            // for normal fresh tracked-crop inference, with no further correction.
            for (int index = 0; index < 20; index++)
            {
                hands = engine.Detect(frame.Width, frame.Height, frame.Width * 4, pixels, [Focus]);
                Require(hands.Count == 1 && Distance(hands[0].IndexTip, expected) < 8 &&
                    engine.LastDiagnostics!.Searches.All(search => search.ColorCorrection is null),
                    "A tracked hand required repeated color retries or moved away from its native fingertip.");
            }
            Console.WriteLine($"Palm cast correction G×{greenGain:F2}: recovered hand; native tip error " +
                $"{Distance(hands[0].IndexTip, expected):F2}px after20raw tracked frames.");
        }

        // A bright colored projection alone must not become a hand. Also prove
        // retries stay bounded to the two original focus regions and disappear
        // immediately when a previously acquired real hand is removed.
        using var empty = new Mat(1080, 1920, MatType.CV_8UC4, new Scalar(220, 180, 245, 255));
        var absent = engine.Detect(empty.Width, empty.Height, empty.Width * 4, Bytes(empty),
            [Focus, new(200, 370, 420, 420), new(650, 370, 420, 420)]);
        var emptyTrace = engine.LastDiagnostics!;
        Require(absent.Count == 0 && emptyTrace.Searches.Count(search => search.Source == "motion-roi-normalized") == 2,
            "Empty projected color created a hand, retained old landmarks, or exceeded the retry limit.");
        Require(emptyTrace.Searches.Any(search => search.Source == "full-frame") &&
            emptyTrace.Searches.Any(search => search.Source == "tile"),
            "Color compensation replaced ordinary acquisition searches.");
        engine.ResetTracking();
        absent = engine.Detect(empty.Width, empty.Height, empty.Width * 4, Bytes(empty));
        Require(absent.Count == 0 && engine.LastDiagnostics!.Searches.All(search => search.ColorCorrection is null),
            "The bounded focused fallback ran without any supplied search region.");
        Console.WriteLine("Palm color correction: controlled licensed-hand recovery, original RGB preserved, " +
            "20-frame raw tracking, bounded retries, empty projection rejection and evidence guards passed.");
    }

    private static void CheckEvidenceAndPixelGeometry()
    {
        foreach (var color in new[] { new Scalar(0, 0, 0), new Scalar(220, 220, 220), new Scalar(255, 80, 210) })
        {
            using var input = new Mat(192, 192, MatType.CV_8UC3, color);
            Require(!PalmProjectionColorCorrection.TryCreate(input, out var result, out var evidence) &&
                result is null && evidence is null,
                "Dark, already-neutral, or highly saturated pixels supplied false neutral-light evidence.");
        }
        using var sparse = new Mat(192, 192, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(sparse, new Rect(10, 10, 12, 12), new Scalar(245, 180, 220), -1);
        Require(!PalmProjectionColorCorrection.TryCreate(sparse, out _, out _),
            "A few bright pixels were enough to change an entire model input.");
        using var image = new Mat(192, 192, MatType.CV_8UC3, new Scalar(245, 180, 220));
        Cv2.Rectangle(image, new Rect(14, 22, 36, 48), Scalar.Black, -1);
        byte[] before = Bytes(image);
        Require(PalmProjectionColorCorrection.TryCreate(image, out var corrected, out var correction),
            "A bright projected color cast was not recognized.");
        using (corrected)
        {
            Require(corrected is not null && corrected.Size() == image.Size() && Bytes(image).SequenceEqual(before),
                "Compensation resized or modified its source image.");
            var white = corrected!.At<Vec3b>(0, 0);
            Require(Math.Max(white.Item0, Math.Max(white.Item1, white.Item2)) -
                Math.Min(white.Item0, Math.Min(white.Item1, white.Item2)) <= 1,
                "Adaptive compensation did not neutralize the reference light.");
            int width = image.Width, height = image.Height;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Require((image.At<Vec3b>(y, x) == new Vec3b(0, 0, 0)) ==
                    (corrected.At<Vec3b>(y, x) == new Vec3b(0, 0, 0)),
                    "Compensation moved an edge or changed the geometry of the thumbnail.");
        }
    }

    private static Mat CastHandFrame(Mat source, double greenGain, out PixelPoint expected)
    {
        const int width = 96;
        int height = (int)Math.Round(width * source.Height / (double)source.Width);
        using var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size(width, height), interpolation: InterpolationFlags.Area);
        var channels = Cv2.Split(scaled);
        try
        {
            channels[0].ConvertTo(channels[0], -1, 1.15);
            channels[1].ConvertTo(channels[1], -1, greenGain);
            Cv2.Merge(channels, scaled);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
        using var bgra = new Mat();
        Cv2.CvtColor(scaled, bgra, ColorConversionCodes.BGR2BGRA);
        var frame = new Mat(1080, 1920, MatType.CV_8UC4, new Scalar(220, 180, 245, 255));
        int left = 1260 - width / 2, top = 580 - height / 2;
        using (var destination = new Mat(frame, new Rect(left, top, width, height))) bgra.CopyTo(destination);
        expected = new(left + (0.47388697 * source.Width + .5) * width / source.Width - .5,
            top + (0.19592366 * source.Height + .5) * height / source.Height - .5);
        return frame;
    }

    private static byte[] Bytes(Mat image)
    {
        var bytes = new byte[image.Width * image.Height * image.Channels()];
        Marshal.Copy(image.Data, bytes, 0, bytes.Length);
        return bytes;
    }
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
