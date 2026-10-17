using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionBoardDisturbanceRegression
{
    public static void Run()
    {
        RecordedSecondPage();
        // The same generated menu and camera response used by the caption
        // regressions contain detailed thumbnail art and large unlabelled gaps.
        const string prefix = "menu-control-reflectance-fit";
        using var metadata = JsonDocument.Parse(File.ReadAllText(Fixture(prefix + ".json")));
        var root = metadata.RootElement;
        T Read<T>(JsonElement value, string name) => JsonSerializer.Deserialize<T>(value.GetProperty(name).GetRawText())!;
        int width = Read<int>(root, "width"), height = Read<int>(root, "height");
        var expected = root.GetProperty("expected");
        var references = Read<HandTrackingBounds[]>(expected, "BoardReferenceRegions");
        var ordinaryScene = new HandAcquisitionSceneImage(Read<int>(expected, "Width"), Read<int>(expected, "Height"),
            Pixels("expected"), Read<double[]>(expected, "CameraToBoard"),
            Read<HandTrackingBounds[]>(expected, "BoardSearchRegions"), references,
            Read<HandTrackingBounds[]>(expected, "BoardTriggerRegions"), BoardControlReferenceRegions: references);
        var scene = ordinaryScene with { BoardDisturbanceExclusionRegion = new(.725, .835, .21, .135) };
        var polygon = Read<PixelPoint[]>(root, "SearchPolygon");
        var epoch = new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);
        byte[] empty = Pixels("camera");
        foreach (var (name, gain, offsets) in new[] { ("clear", 1.0, new[] { 0, 0, 0 }),
            ("dimmer", .72, new[] { 8, 8, 8 }), ("brighter", 1.12, new[] { 12, 12, 12 }),
            ("white balance", .76, new[] { 24, 9, 38 }) })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            Quiet(Feed(tracker, empty, 0), "Clear generated menu");
            Quiet(Feed(tracker, Transform(gain, offsets), 100), name);
        }
        byte[] noise = (byte[])empty.Clone();
        var random = new Random(412);
        for (int pixel = 0; pixel < noise.Length; pixel += 4)
            for (int channel = 0; channel < 3; channel++)
                noise[pixel + channel] = (byte)Math.Clamp(noise[pixel + channel] + random.Next(-5, 6), 0, 255);
        Quiet(Feed(new(), noise, 0), "Camera noise");
        using (var source = new Mat(height, width, MatType.CV_8UC4))
        using (var blurred = new Mat())
        {
            Marshal.Copy(empty, 0, source.Data, empty.Length);
            Cv2.GaussianBlur(source, blurred, new Size(5, 5), 1.2);
            byte[] pixels = new byte[empty.Length]; Marshal.Copy(blurred.Data, pixels, 0, pixels.Length);
            Quiet(Feed(new(), pixels, 0), "Small optical blur");
        }

        foreach (var (name, bounds) in new[] {
            ("unlabelled central gap", new HandTrackingBounds(.485, .26, .03, .50)),
            ("thumbnail artwork", new HandTrackingBounds(.20, .20, .07, .05)),
            ("another caption", new HandTrackingBounds(.13, .30, .10, .065)),
            ("large connected reach", new HandTrackingBounds(.04, .16, .46, .46)) })
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) Quiet(Feed(tracker, empty, 0), "Initial clear menu");
            byte[] obstruction = Obstruct(bounds);
            foreach (int time in new[] { 100, 200, 1200, 2400 })
            {
                var result = Feed(tracker, obstruction, time);
                if (name == "large connected reach")
                    Require(result.OutsideControlForegroundFraction is > .18 and < .35,
                        "Large-reach fixture must retain a reliable fit with 18–35% residual board occupancy: " +
                        result.OutsideControlForegroundFraction);
                Require(result.OutsideControlDisturbance, $"{name} did not veto a {(warm ? "warm" : "cold")} hold at {time}: {result.OutsideControlForegroundFraction}");
            }
            Require(!Feed(tracker, obstruction, 2400).OutsideControlDisturbance,
                "Repeated camera frames emitted another board obstruction observation.");
            Quiet(Feed(tracker, empty, 2500), "Removed obstruction");
        }
        byte[] arrowOnly = Obstruct(new(.76, .86, .14, .07));
        Quiet(Feed(new(), arrowOnly, 0), "Excluded arrow alone");
        byte[] speck = Obstruct(new(.49, .29, .006, .006));
        Quiet(Feed(new(), speck, 0), "Tiny residual speck");
        byte[] broad = Obstruct(new(.01, .01, .97, .80));
        Quiet(Feed(new(), broad, 0), "Broad uncertain camera appearance");

        var ordinary = new HandAcquisitionPresenceTracker();
        var watched = new HandAcquisitionPresenceTracker();
        var without = ordinary.Update(width, height, width * 4, empty, polygon, ordinaryScene, epoch, epoch);
        var with = Feed(watched, empty, 0);
        Require(!without.OutsideControlDisturbance && without.OutsideControlForegroundFraction == 0 &&
            watched.SampledCellCount > ordinary.SampledCellCount,
            "Whole-board sampling was not restricted to explicitly opted-in scenes.");
        Require(JsonSerializer.Serialize(without.TextPatterns) == JsonSerializer.Serialize(with.TextPatterns) &&
            without.Hints.SequenceEqual(with.Hints) && without.ForegroundFraction == with.ForegroundFraction,
            "The separate board veto changed caption or hand acquisition evidence.");
        Console.WriteLine("Whole-board arrow veto passed: generated clear/blur/noisy/exposure/white-balance menu, " +
            "cold and stationary gap/artwork/caption obstruction, excluded arrow, tiny/broad uncertainty, release and opt-in sampling.");

        HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var time = epoch.AddMilliseconds(milliseconds);
            return tracker.Update(width, height, width * 4, pixels, polygon, scene, time, time);
        }
        byte[] Transform(double gain, int[] offsets)
        {
            byte[] pixels = (byte[])empty.Clone();
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                for (int channel = 0; channel < 3; channel++)
                    pixels[pixel + channel] = (byte)Math.Clamp(empty[pixel + channel] * gain + offsets[channel], 0, 255);
            return pixels;
        }
        byte[] Obstruct(HandTrackingBounds bounds)
        {
            byte[] pixels = (byte[])empty.Clone();
            var m = scene.CameraToBoard;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double u = m[0] * x + m[2], v = m[4] * y + m[5];
                if (u < bounds.X || u > bounds.X + bounds.Width || v < bounds.Y || v > bounds.Y + bounds.Height) continue;
                int index = (y * width + x) * 4;
                pixels[index] = 75; pixels[index + 1] = 95; pixels[index + 2] = 185;
            }
            return pixels;
        }
        byte[] Pixels(string role)
        {
            using var image = Cv2.ImRead(Fixture(prefix + "-" + role + ".png"), ImreadModes.Unchanged);
            Require(image.Type() == MatType.CV_8UC4 && image.Width == width && image.Height == height,
                "Generated menu fixture geometry changed.");
            byte[] pixels = new byte[width * height * 4]; Marshal.Copy(image.Data, pixels, 0, pixels.Length); return pixels;
        }
    }

    private static void RecordedSecondPage()
    {
        // The first generated fixture passed while this clear live second page
        // falsely vetoed its arrow: saturated Paint art was absent from the
        // caption-plate colour fit. Preserve the exact camera/render pair.
        using var metadata = JsonDocument.Parse(File.ReadAllText(Fixture("menu-page-two-clear.json")));
        var root = metadata.RootElement;
        var scene = JsonSerializer.Deserialize<HandAcquisitionSceneImage>(root.GetRawText())! with
        {
            Bgra = Pixels("expected"),
            BoardDisturbanceExclusionRegion = new(.725, .835, .21, .135)
        };
        int width = root.GetProperty("cameraWidth").GetInt32(), height = root.GetProperty("cameraHeight").GetInt32();
        int stride = root.GetProperty("Stride").GetInt32();
        var polygon = JsonSerializer.Deserialize<PixelPoint[]>(root.GetProperty("SearchPolygon").GetRawText())!;
        var epoch = new DateTimeOffset(2026, 10, 17, 3, 19, 23, TimeSpan.Zero);
        var empty = Pixels("camera");
        Quiet(Feed(new(), empty, 0), "Recorded clear second menu page");
        var adjacent = Pixels("camera-adjacent");
        Quiet(Feed(new(), adjacent, 0), "Adjacent clear second-page camera frame");
        var steady = new HandAcquisitionPresenceTracker();
        Quiet(Feed(steady, empty, 0), "Initial clear second-page frame");
        Quiet(Feed(steady, adjacent, 100), "Clear second-page camera variation");
        foreach (var (name, bounds, color) in new[] {
            ("artwork hand", new HandTrackingBounds(.78, .27, .10, .12), new byte[] {75,95,185}),
            ("lit artwork hand", new HandTrackingBounds(.78, .27, .10, .12), new byte[] {220,225,230}),
            ("dark gap hand", new HandTrackingBounds(.485,.26,.03,.50), new byte[] {45,55,85}) })
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) Quiet(Feed(tracker, empty, 0), "Warm recorded clear second page");
            byte[] obstruction = (byte[])empty.Clone();
            var m = scene.CameraToBoard;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double d = m[6]*x+m[7]*y+m[8], u = (m[0]*x+m[1]*y+m[2])/d, v = (m[3]*x+m[4]*y+m[5])/d;
                if (u < bounds.X || u > bounds.X+bounds.Width || v < bounds.Y || v > bounds.Y+bounds.Height) continue;
                int index = y*stride+x*4;
                for (int channel=0;channel<3;channel++) obstruction[index+channel]=color[channel];
            }
            foreach (int time in new[] { 100, 1400 })
            {
                var result = Feed(tracker, obstruction, time);
                Require(result.OutsideControlDisturbance, $"Recorded {name}, warm={warm}, {time}ms was fitted away: {result.OutsideControlForegroundFraction}");
            }
            Quiet(Feed(tracker, empty, 1500), "Recorded removed " + name);
        }
        HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var time = epoch.AddMilliseconds(milliseconds);
            return tracker.Update(width,height,stride,pixels,polygon,scene,time,time);
        }
        static byte[] Pixels(string role)
        {
            using var input = File.OpenRead(Fixture("menu-page-two-clear-"+role+".bgra.gz"));
            using var gzip = new GZipStream(input,CompressionMode.Decompress);
            using var output = new MemoryStream(); gzip.CopyTo(output); return output.ToArray();
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static void Quiet(HandAcquisitionPresenceResult result, string description) => Require(
        !result.OutsideControlDisturbance, description + " falsely vetoed arrow hold: " + result.OutsideControlForegroundFraction);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
