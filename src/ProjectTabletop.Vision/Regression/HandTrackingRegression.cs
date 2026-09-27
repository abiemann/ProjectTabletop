using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandTrackingRegression
{
    public static void Run()
    {
        using var engine = new HandTrackingEngine(
            Path.Combine(AppContext.BaseDirectory, "Models", "Hands"));
        using Mat pointing = ReadFixture("mediapipe-pointing-up.jpg");
        if (pointing.Width != 358 || pointing.Height != 376)
            throw new Exception("The licensed hand fixture changed dimensions.");

        // Independent reference: MediaPipe's pointing_up_landmarks.pbtxt,
        // landmark 8. These coordinates are not fitted to our inference output.
        PixelPoint reference = new(0.47388697 * pointing.Width,
            0.19592366 * pointing.Height);
        CheckTip(engine, "portrait pointing hand", pointing, reference, 18);

        using Mat rotated = new();
        Cv2.Rotate(pointing, rotated, RotateFlags.Rotate90Clockwise);
        CheckTip(engine, "clockwise pointing hand", rotated,
            new PixelPoint(pointing.Height - 1 - reference.Y, reference.X), 18);

        // Embed the unchanged photograph off-center in a landscape frame. This
        // exercises letterboxing and crop-to-camera coordinates independently.
        using Mat landscape = new(480, 700, MatType.CV_8UC4,
            new Scalar(110, 110, 110, 255));
        using (var destination = new Mat(landscape,
            new Rect(101, 55, pointing.Width, pointing.Height)))
            pointing.CopyTo(destination);
        PixelPoint landscapeReference = new(reference.X + 101, reference.Y + 55);
        HandDetection packed = CheckTip(engine, "off-center landscape hand",
            landscape, landscapeReference, 24);

        byte[] packedPixels = Bytes(landscape);
        int landscapeWidth = landscape.Width, landscapeHeight = landscape.Height;
        int paddedStride = landscapeWidth * 4 + 28;
        byte[] paddedPixels = new byte[paddedStride * landscapeHeight];
        Array.Fill(paddedPixels, (byte)213);
        for (int row = 0; row < landscapeHeight; row++)
            Buffer.BlockCopy(packedPixels, row * landscapeWidth * 4,
                paddedPixels, row * paddedStride, landscapeWidth * 4);
        HandDetection padded = CheckTip(engine, "padded camera stride",
            landscapeWidth, landscapeHeight, paddedStride, paddedPixels,
            landscapeReference, 24);
        if (Distance(packed.IndexTip, padded.IndexTip) > 0.1)
            throw new Exception("Camera row padding changed the fingertip position.");

        using Mat twoHands = new(420, 860, MatType.CV_8UC4,
            new Scalar(110, 110, 110, 255));
        using Mat mirrored = new();
        Cv2.Flip(pointing, mirrored, FlipMode.Y);
        using (var first = new Mat(twoHands, new Rect(20, 20, pointing.Width, pointing.Height)))
            pointing.CopyTo(first);
        using (var second = new Mat(twoHands, new Rect(480, 20, pointing.Width, pointing.Height)))
            mirrored.CopyTo(second);
        HandDetection[] pair = engine.Detect(twoHands.Width, twoHands.Height,
            twoHands.Width * 4, Bytes(twoHands)).OrderBy(hand => hand.IndexTip.X).ToArray();
        PixelPoint firstExpected = new(reference.X + 20, reference.Y + 20);
        PixelPoint secondExpected = new(480 + pointing.Width - 1 - reference.X, reference.Y + 20);
        if (pair.Length != 2 || Distance(pair[0].IndexTip, firstExpected) > 24 ||
            Distance(pair[1].IndexTip, secondExpected) > 24)
            throw new Exception("Two separated real hands did not produce two localized fingertips.");
        Console.WriteLine($"Two-hand frame: fingertip reference errors " +
            $"{Distance(pair[0].IndexTip, firstExpected):F2}px and " +
            $"{Distance(pair[1].IndexTip, secondExpected):F2}px");

        foreach (byte brightness in new byte[] { 0, 128, 255 })
        {
            using Mat blank = new(360, 640, MatType.CV_8UC4,
                new Scalar(brightness, brightness, brightness, 255));
            CheckNoHands(engine, $"blank {brightness}", blank);
        }
        foreach (string name in new[]
        {
            "moved-cardboard-ambient.png",
            "moved-cardboard-projected-grid.png",
            "board-clipped-solid-image.png"
        })
        {
            using Mat board = ReadFixture(name);
            CheckNoHands(engine, name, board);
        }
        Console.WriteLine("Hand tracking regression: real pointing finger, rotation, " +
            "landscape mapping, padded stride and two hands passed; blank and board frames rejected.");
    }

    private static HandDetection CheckTip(HandTrackingEngine engine, string name,
        Mat frame, PixelPoint expected, double tolerance) =>
        CheckTip(engine, name, frame.Width, frame.Height, frame.Width * 4,
            Bytes(frame), expected, tolerance);

    private static HandDetection CheckTip(HandTrackingEngine engine, string name,
        int width, int height, int stride, byte[] pixels, PixelPoint expected,
        double tolerance)
    {
        IReadOnlyList<HandDetection> hands = engine.Detect(width, height, stride, pixels);
        if (hands.Count != 1)
            throw new Exception($"{name}: expected one hand, detected {hands.Count}.");
        HandDetection hand = hands[0];
        if (hand.Landmarks.Count != 21 ||
            hand.Landmarks.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)) ||
            !double.IsFinite(hand.Confidence) || hand.Confidence < 0.5 || hand.Confidence > 1 ||
            !double.IsFinite(hand.RightHandProbability) ||
            hand.RightHandProbability < 0 || hand.RightHandProbability > 1)
            throw new Exception($"{name}: invalid hand landmarks or confidence.");
        double error = Distance(hand.IndexTip, expected);
        Console.WriteLine($"{name}: tip=({hand.IndexTip.X:F1},{hand.IndexTip.Y:F1}); " +
            $"reference error={error:F2}px; confidence={hand.Confidence:F3}");
        if (error > tolerance)
            throw new Exception($"{name}: fingertip reference error {error:F2}px " +
                $"exceeded {tolerance:F0}px.");
        return hand;
    }

    private static void CheckNoHands(HandTrackingEngine engine, string name, Mat frame)
    {
        if (engine.Detect(frame.Width, frame.Height, frame.Width * 4, Bytes(frame)).Count != 0)
            throw new Exception($"{name}: detected a hand in a no-hand frame.");
    }

    private static Mat ReadFixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        using Mat original = Cv2.ImRead(path, ImreadModes.Color);
        if (original.Empty()) throw new Exception($"Missing hand regression fixture: {path}");
        Mat bgra = new();
        Cv2.CvtColor(original, bgra, ColorConversionCodes.BGR2BGRA);
        return bgra;
    }

    private static byte[] Bytes(Mat image)
    {
        byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return pixels;
    }

    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
}
