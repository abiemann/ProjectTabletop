using OpenCvSharp;
using ProjectTabletop.Vision;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

internal static class EyeTipProjectedFrameReplay
{
    // --eye-tip-projected-frame camera.png projection.png homography.json [radius [clickX clickY]]
    // homography.json is a nine-number raw-camera-to-projection-UV array. This also accepts a
    // full projector screenshot with its cameraToProjector matrix for actual optical replay.
    public static void Run(string[] args)
    {
        if (args.Length is not (3 or 4 or 6))
            throw new ArgumentException("Expected camera image, projected image, nine-number matrix JSON, optional radius and click coordinates.");
        using Mat source = Read(args[0]);
        using Mat expected = Read(args[1]);
        double[] map = JsonSerializer.Deserialize<double[]>(File.ReadAllText(args[2])) ?? [];
        double? radius = args.Length >= 4 ? double.Parse(args[3], CultureInfo.InvariantCulture) : null;
        PixelPoint? center = args.Length == 6 ? new(double.Parse(args[4], CultureInfo.InvariantCulture),
            double.Parse(args[5], CultureInfo.InvariantCulture)) : null;
        DateTimeOffset time = DateTimeOffset.UtcNow;
        byte[] cameraPixels = BoardDetectionRegression.BytesOf(source);
        var reference = new EyeTipProjectionFrame(expected.Width, expected.Height,
            BoardDetectionRegression.BytesOf(expected), map, time);
        var options = new EyeTipDetectionOptions(radius, center, [reference], time);
        var timer = Stopwatch.StartNew();
        var raw = EyeTipDetector.Detect(source.Width, source.Height, (int)source.Step(), cameraPixels);
        double rawMilliseconds = timer.Elapsed.TotalMilliseconds;
        var shaped = EyeTipDetector.Detect(source.Width, source.Height, (int)source.Step(), cameraPixels,
            options with { ProjectionFrames = null });
        timer.Restart();
        var result = EyeTipDetector.Detect(source.Width, source.Height, (int)source.Step(), cameraPixels, options);
        double milliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var repeated = EyeTipDetector.Detect(source.Width, source.Height, (int)source.Step(), cameraPixels, options);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            source.Width, source.Height, rawMilliseconds, milliseconds,
            cachedMilliseconds = timer.Elapsed.TotalMilliseconds,
            rawCandidates = raw.Candidates, shapedCandidates = shaped.Candidates, result.Reason, result.Candidates,
            repeatCandidates = repeated.Candidates
        }));
    }

    private static Mat Read(string path)
    {
        using Mat source = Cv2.ImRead(path, ImreadModes.Color);
        if (source.Empty()) throw new ArgumentException("Could not read image: " + path);
        Mat bgra = new();
        Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        return bgra;
    }
}
