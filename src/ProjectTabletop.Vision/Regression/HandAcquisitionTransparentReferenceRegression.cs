using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionTransparentReferenceRegression
{
    private const int Size = 1000;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Polygon = [new(0, 0), new(Size, 0), new(Size, Size), new(0, Size)];
    private delegate bool SampleColor(HandAcquisitionSceneImage scene, PixelPoint point, Span<double> color);

    public static void Run()
    {
        BilinearOpacity();
        AnimatedCutoutsAndCaptionCorruption();
        Console.WriteLine("Transparent acquisition reference regression: bilinear alpha exclusion, opaque black " +
            "retention, animated cutouts, unchanged clean captions and genuine caption obstruction passed.");
    }

    private static void BilinearOpacity()
    {
        // Probe the real sampler without making a test-only production API.
        var sample = typeof(HandAcquisitionPresenceTracker).GetMethod("TemplateColor",
            BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<SampleColor>();
        byte[] pixels = [0, 0, 0, 255, 20, 40, 60, 255, 40, 80, 120, 255, 60, 120, 180, 255];
        var scene = new HandAcquisitionSceneImage(2, 2, pixels, [1, 0, 0, 0, 1, 0, 0, 0, 1]);
        Span<double> color = stackalloc double[3];
        Require(sample(scene, new(0, 0), color) && color[0] == 0 && color[1] == 0 && color[2] == 0,
            "Opaque black was mistaken for absent reference data.");
        for (int corner = 0; corner < 4; corner++)
        {
            foreach (byte alpha in new byte[] { 0, 1, 128, 253 })
            {
                pixels[corner * 4 + 3] = alpha;
                Require(!sample(scene, new(.5, .5), color),
                    "A contributing transparent or antialiased texel entered a bilinear colour reference.");
            }
            pixels[corner * 4 + 3] = 254;
            Require(sample(scene, new(.5, .5), color), "Near-opaque raster rounding was rejected.");
            pixels[corner * 4 + 3] = 0;
            Require(sample(scene, corner == 0 ? new(1, 1) : new(0, 0), color),
                "A zero-weight transparent neighbour invalidated an exact opaque texel.");
            pixels[corner * 4 + 3] = 255;
        }
    }

    private static void AnimatedCutoutsAndCaptionCorruption()
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "paint-compact-controls-expected.png"),
            ImreadModes.Unchanged);
        Require(image.Width == Size && image.Height == Size && image.Channels() == 4 && image.IsContinuous(),
            "The real rendered-caption fixture changed format.");
        byte[] original = new byte[Size * Size * 4];
        Marshal.Copy(image.Data, original, 0, original.Length);
        byte[] pixels = new byte[Size * Size * 4];
        Fill(pixels, 0, 0, Size, Size, 30, 35, 40);
        for (int stripe = 0; stripe < 8; stripe++)
            Fill(pixels, 100 + stripe * 100, 100, 100, 220,
                (byte)(20 + stripe * 9), (byte)(30 + stripe * 11), (byte)(50 + stripe * 13));
        for (int row = 0; row < 51; row++)
            Array.Copy(original, ((892 + row) * Size + 72) * 4, pixels, ((692 + row) * Size + 432) * 4, 116 * 4);
        var scene = new HandAcquisitionSceneImage(Size, Size, pixels,
            [1.0 / 999, 0, 0, 0, 1.0 / 999, 0, 0, 0, 1],
            [new(.432, .692, .116, .051)], [new(.10, .10, .80, .22)], [new(.467, .702, .048, .027)]);
        byte[] camera = (byte[])pixels.Clone();
        var opaque = new HandAcquisitionPresenceTracker();
        Require(Feed(opaque, camera, scene, 0).BaselineReady, "Opaque reference did not initialize.");
        int opaqueSamples = opaque.SampledCellCount;
        scene = scene with { Bgra = (byte[])pixels.Clone() };
        for (int window = 0; window < 8; window++)
            Fill(scene.Bgra, 133 + window * 100, 145, 34, 90, 0, 0, 0, 0);
        var tracker = new HandAcquisitionPresenceTracker();
        for (int frame = 0; frame < 8; frame++)
        {
            // The static foreground contains holes; the camera instead sees
            // live panes switching between bright amber, teal and nearly black.
            for (int window = 0; window < 8; window++)
                Fill(camera, 133 + window * 100, 145, 34, 90,
                    (byte)((frame + window) % 3 == 0 ? 230 : 8),
                    (byte)((frame + window) % 3 == 1 ? 180 : 10),
                    (byte)((frame + window) % 3 == 2 ? 245 : 12));
            var result = Feed(tracker, camera, scene, frame * 100);
            Require(result.BaselineReady && result.Hints.Count == 0 && result.TextPatterns is [{ LabelIntact: true, ShapeCorrupted: false }],
                "Animation behind reference cutouts polluted an intact caption: " + result.Reason + ".");
            Require(tracker.SampledCellCount < opaqueSamples - 500,
                "Transparent reference apertures were still included in the sampled photometric surface.");
        }
        byte[] covered = (byte[])camera.Clone();
        for (int finger = 0; finger < 4; finger++) Fill(covered, 464 + finger * 12, 698, 10, 35, 75, 95, 185);
        Require(Feed(tracker, covered, scene, 800).Hints.Count == 0,
            "A first obstruction frame bypassed fresh caption confirmation.");
        var confirmed = Feed(tracker, covered, scene, 900);
        Require(confirmed.Hints.Count == 1 && confirmed.TextPatterns is [{ ShapeCorrupted: true, ConfirmationFrames: >= 2 }] &&
            confirmed.Hints[0].ControlCoverage >= .07 && confirmed.Hints[0].ControlTriggerCoverage >= .07,
            "Excluding animated apertures suppressed real broken-letter evidence: " + confirmed.Reason + ".");
        Require(Feed(tracker, camera, scene, 1000).TextPatterns is [{ LabelIntact: true, ShapeCorrupted: false }],
            "Removing a real obstruction did not restore intact-caption evidence.");
    }

    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels,
        HandAcquisitionSceneImage scene, int milliseconds) => tracker.Update(Size, Size, Size * 4, pixels,
            Polygon, scene, Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(milliseconds));

    private static void Fill(byte[] pixels, int left, int top, int width, int height,
        byte blue, byte green, byte red, byte alpha = 255)
    {
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
        {
            int p = (y * Size + x) * 4;
            pixels[p] = blue; pixels[p + 1] = green; pixels[p + 2] = red; pixels[p + 3] = alpha;
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
