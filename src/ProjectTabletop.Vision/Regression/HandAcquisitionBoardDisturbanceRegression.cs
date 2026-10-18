using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionBoardDisturbanceRegression
{
    public static void Run()
    {
        RecordedPaintThumbnailEdge();
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
        // The button's narrow approach corridor continues to the player-facing
        // board edge, so the same finger cannot veto its own caption hold.
        var scene = ordinaryScene with { BoardDisturbanceExclusionRegion = new(.725, .835, .21, .165) };
        var polygon = Read<PixelPoint[]>(root, "SearchPolygon");
        var epoch = new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);
        var arrow = new HandTrackingBounds(.76, .86, .14, .07);
        byte[] empty = Pixels("camera");
        // The veto is measured only while a caption is broken, so false-veto
        // checks cover the camera variations while the arrow itself is held.
        byte[] held = Obstruct(arrow);
        var fingerApproach = new HandTrackingBounds(.79, .90, .08, .10);
        FingerApproachCorridor(Obstruct(arrow, fingerApproach),
            additional => Obstruct(arrow, fingerApproach, additional), Feed, "Generated down arrow");
        foreach (var (name, gain, offsets) in new[] { ("clear", 1.0, new[] { 0, 0, 0 }),
            ("dimmer", .72, new[] { 8, 8, 8 }), ("brighter", 1.12, new[] { 12, 12, 12 }),
            ("white balance", .76, new[] { 24, 9, 38 }) })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            Quiet(Feed(tracker, held, 0), "Held arrow on the generated menu");
            Quiet(Feed(tracker, Transform(held, gain, offsets), 100), name + " with the arrow held");
        }
        byte[] noise = (byte[])held.Clone();
        var random = new Random(412);
        for (int pixel = 0; pixel < noise.Length; pixel += 4)
            for (int channel = 0; channel < 3; channel++)
                noise[pixel + channel] = (byte)Math.Clamp(noise[pixel + channel] + random.Next(-5, 6), 0, 255);
        Quiet(Feed(new(), noise, 0), "Camera noise with the arrow held");
        using (var source = new Mat(height, width, MatType.CV_8UC4))
        using (var blurred = new Mat())
        {
            Marshal.Copy(held, 0, source.Data, held.Length);
            Cv2.GaussianBlur(source, blurred, new Size(5, 5), 1.2);
            byte[] pixels = new byte[held.Length]; Marshal.Copy(blurred.Data, pixels, 0, pixels.Length);
            Quiet(Feed(new(), pixels, 0), "Small optical blur with the arrow held");
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
            // The veto protects a hold, so it is measured while the arrow is covered.
            byte[] obstruction = Obstruct(bounds, arrow);
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
            Quiet(Feed(tracker, held, 2500), "Removed obstruction while the arrow remains held");
        }
        byte[] arrowOnly = held;
        Quiet(Feed(new(), arrowOnly, 0), "Excluded arrow alone");
        // An intact arrow cannot start or extend a hold. With every caption readable,
        // activity elsewhere is not measured, and the rest of the board is not read.
        var idleGap = new HandAcquisitionPresenceTracker();
        Quiet(Feed(idleGap, Obstruct(new HandTrackingBounds(.485, .26, .03, .50)), 0),
            "Unmeasured gap obstruction beside intact captions");
        byte[] speck = Obstruct(new HandTrackingBounds(.49, .29, .006, .006), arrow);
        Quiet(Feed(new(), speck, 0), "Tiny residual speck with the arrow held");
        byte[] broad = Obstruct(new HandTrackingBounds(.01, .01, .97, .80), arrow);
        Quiet(Feed(new(), broad, 0), "Broad uncertain camera appearance with the arrow held");

        var ordinary = new HandAcquisitionPresenceTracker();
        var watched = new HandAcquisitionPresenceTracker();
        var without = ordinary.Update(width, height, width * 4, empty, polygon, ordinaryScene, epoch, epoch);
        var with = Feed(watched, empty, 0);
        Require(!without.OutsideControlDisturbance && without.OutsideControlForegroundFraction == 0 &&
            watched.SampledCellCount == ordinary.SampledCellCount && idleGap.SampledCellCount == ordinary.SampledCellCount,
            "An idle opted-in menu read the whole board.");
        RequireSameEvidence(without, with);
        var heldTime = epoch.AddMilliseconds(100);
        var heldWithout = ordinary.Update(width, height, width * 4, arrowOnly, polygon, ordinaryScene, heldTime, heldTime);
        var heldWith = Feed(watched, arrowOnly, 100);
        Require(watched.SampledCellCount > ordinary.SampledCellCount,
            "Whole-board sampling was not restricted to explicitly opted-in scenes with a covered caption.");
        RequireSameEvidence(heldWithout, heldWith);
        Console.WriteLine("Whole-board arrow veto passed: generated clear/blur/noisy/exposure/white-balance menu, " +
            "cold and stationary gap/artwork/caption obstruction while the arrow is held, excluded arrow, tiny/broad " +
            "uncertainty, release, idle-menu skip and opt-in sampling.");

        HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var time = epoch.AddMilliseconds(milliseconds);
            return tracker.Update(width, height, width * 4, pixels, polygon, scene, time, time);
        }
        static void RequireSameEvidence(HandAcquisitionPresenceResult without, HandAcquisitionPresenceResult with) =>
            Require(JsonSerializer.Serialize(without.TextPatterns) == JsonSerializer.Serialize(with.TextPatterns) &&
                without.Hints.SequenceEqual(with.Hints) && without.ForegroundFraction == with.ForegroundFraction,
                "The separate board veto changed caption or hand acquisition evidence.");
        static byte[] Transform(byte[] source, double gain, int[] offsets)
        {
            byte[] pixels = (byte[])source.Clone();
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                for (int channel = 0; channel < 3; channel++)
                    pixels[pixel + channel] = (byte)Math.Clamp(source[pixel + channel] * gain + offsets[channel], 0, 255);
            return pixels;
        }
        byte[] Obstruct(params HandTrackingBounds[] regions)
        {
            byte[] pixels = (byte[])empty.Clone();
            var m = scene.CameraToBoard;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double u = m[0] * x + m[2], v = m[4] * y + m[5];
                if (!regions.Any(bounds => u >= bounds.X && u <= bounds.X + bounds.Width &&
                    v >= bounds.Y && v <= bounds.Y + bounds.Height)) continue;
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
            BoardDisturbanceExclusionRegion = new(.725, .835, .21, .165)
        };
        int width = root.GetProperty("cameraWidth").GetInt32(), height = root.GetProperty("cameraHeight").GetInt32();
        int stride = root.GetProperty("Stride").GetInt32();
        var polygon = JsonSerializer.Deserialize<PixelPoint[]>(root.GetProperty("SearchPolygon").GetRawText())!;
        var epoch = new DateTimeOffset(2026, 10, 17, 3, 19, 23, TimeSpan.Zero);
        // The veto is measured only while a caption is broken. Every recorded
        // frame below therefore keeps the arrow covered, as during a real hold.
        var empty = Cover(Pixels("camera"));
        var fingerApproach = new HandTrackingBounds(.79, .90, .08, .10);
        byte[] finger = Cover(empty, fingerApproach, [75, 95, 185]);
        FingerApproachCorridor(finger, additional => Cover(finger, additional, [75, 95, 185]),
            Feed, "Recorded up arrow");
        Quiet(Feed(new(), empty, 0), "Recorded second menu page with the arrow held");
        var adjacent = Cover(Pixels("camera-adjacent"));
        Quiet(Feed(new(), adjacent, 0), "Adjacent second-page camera frame with the arrow held");
        var steady = new HandAcquisitionPresenceTracker();
        Quiet(Feed(steady, empty, 0), "Initial held-arrow second-page frame");
        Quiet(Feed(steady, adjacent, 100), "Held-arrow second-page camera variation");
        foreach (var (name, bounds, color) in new[] {
            ("artwork hand", new HandTrackingBounds(.78, .27, .10, .12), new byte[] {75,95,185}),
            ("lit artwork hand", new HandTrackingBounds(.78, .27, .10, .12), new byte[] {220,225,230}),
            ("dark gap hand", new HandTrackingBounds(.485,.26,.03,.50), new byte[] {45,55,85}) })
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) Quiet(Feed(tracker, empty, 0), "Warm recorded second page with the arrow held");
            byte[] obstruction = Cover(empty, bounds, color);
            foreach (int time in new[] { 100, 1400 })
            {
                var result = Feed(tracker, obstruction, time);
                Require(result.OutsideControlDisturbance, $"Recorded {name}, warm={warm}, {time}ms was fitted away: {result.OutsideControlForegroundFraction}");
            }
            Quiet(Feed(tracker, empty, 1500), "Recorded removed " + name);
        }
        byte[] Cover(byte[] source, HandTrackingBounds? region = null, byte[]? color = null)
        {
            byte[] pixels = (byte[])source.Clone();
            var m = scene.CameraToBoard;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double d = m[6]*x+m[7]*y+m[8], u = (m[0]*x+m[1]*y+m[2])/d, v = (m[3]*x+m[4]*y+m[5])/d;
                int index = y*stride+x*4;
                if (u >= .76 && u <= .90 && v >= .86 && v <= .93)
                {
                    pixels[index] = 75; pixels[index+1] = 95; pixels[index+2] = 185;
                }
                else if (region is { } bounds && color is not null && u >= bounds.X && u <= bounds.X+bounds.Width &&
                    v >= bounds.Y && v <= bounds.Y+bounds.Height)
                    for (int channel=0;channel<3;channel++) pixels[index+channel]=color[channel];
            }
            return pixels;
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

    private static void RecordedPaintThumbnailEdge()
    {
        // This real held-arrow frame still vetoed after excluding the finger's
        // bottom approach: the untouched Paint thumbnail contained camera colour
        // mixtures between neighbouring balls. Metadata records that the expected
        // artwork is reused, with calibration reconstructed for this capture.
        const string prefix = "menu-page-two-paint-edge";
        using var metadata = JsonDocument.Parse(File.ReadAllText(Fixture(prefix + ".json")));
        var root = metadata.RootElement;
        var scene = JsonSerializer.Deserialize<HandAcquisitionSceneImage>(root.GetRawText())! with
        {
            Bgra = Pixels(root.GetProperty("ExpectedRasterFixture").GetString()!),
            BoardDisturbanceExclusionRegion = new(.725, .835, .21, .165)
        };
        int width = root.GetProperty("cameraWidth").GetInt32(), height = root.GetProperty("cameraHeight").GetInt32();
        int stride = root.GetProperty("Stride").GetInt32();
        var polygon = JsonSerializer.Deserialize<PixelPoint[]>(root.GetProperty("SearchPolygon").GetRawText())!;
        var epoch = new DateTimeOffset(2026, 10, 18, 16, 53, 2, 39, TimeSpan.Zero);
        byte[] held = Pixels(prefix + "-camera.bgra.gz");
        var result = Feed(new(), held, 0);
        Require(result.TextPatterns?.Any(pattern => pattern.CaptionHoldEvidence) == true,
            "Recorded Paint-edge fixture must retain actual arrow-caption damage.");
        Quiet(result, "Untouched Paint thumbnail beside the recorded held arrow");
        foreach (var (name, bounds) in new[] {
            ("small thumbnail finger", new HandTrackingBounds(.82, .33, .07, .065)),
            ("exact thumbnail edge", new HandTrackingBounds(.824, .337, .062, .04)),
            ("larger thumbnail hand", new HandTrackingBounds(.78, .27, .10, .12)) })
        foreach (var (shade, color) in new[] {
            ("skin", new byte[] { 75, 95, 185 }),
            ("lit", new byte[] { 220, 225, 230 }),
            ("dark", new byte[] { 45, 55, 85 }) })
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) Quiet(Feed(tracker, held, 0), "Initial Paint-edge frame");
            byte[] obstruction = (byte[])held.Clone();
            var m = scene.CameraToBoard;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double d = m[6] * x + m[7] * y + m[8];
                double u = (m[0] * x + m[1] * y + m[2]) / d;
                double v = (m[3] * x + m[4] * y + m[5]) / d;
                if (u < bounds.X || u > bounds.X + bounds.Width || v < bounds.Y || v > bounds.Y + bounds.Height) continue;
                for (int channel = 0; channel < 3; channel++) obstruction[y * stride + x * 4 + channel] = color[channel];
            }
            foreach (int time in new[] { 100, 1400 })
                Require(Feed(tracker, obstruction, time).OutsideControlDisturbance,
                    $"Recorded {shade} {name}, warm={warm}, {time}ms was explained away as thumbnail colours.");
            Quiet(Feed(tracker, held, 1500), "Removed recorded " + shade + " " + name);
        }

        HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var time = epoch.AddMilliseconds(milliseconds);
            return tracker.Update(width, height, stride, pixels, polygon, scene, time, time);
        }
        static byte[] Pixels(string name)
        {
            using var input = File.OpenRead(Fixture(name));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
    }

    private static void FingerApproachCorridor(byte[] finger,
        Func<HandTrackingBounds, byte[]> withAdditionalObstruction,
        Func<HandAcquisitionPresenceTracker, byte[], int, HandAcquisitionPresenceResult> feed,
        string description)
    {
        var held = feed(new(), finger, 0);
        Require(held.TextPatterns?.Any(pattern => pattern.CaptionHoldEvidence) == true,
            description + " fixture must actually corrupt the arrow caption.");
        Quiet(held, description + " finger continuing to the board's bottom edge");
        foreach (var (position, bounds) in new[] {
            ("above", new HandTrackingBounds(.79, .75, .07, .06)),
            ("left", new HandTrackingBounds(.64, .91, .06, .07)),
            ("right", new HandTrackingBounds(.94, .91, .055, .07)) })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            Quiet(feed(tracker, finger, 0), description + " initial approach");
            byte[] obstructed = withAdditionalObstruction(bounds);
            foreach (int time in new[] { 100, 1400 })
                Require(feed(tracker, obstructed, time).OutsideControlDisturbance,
                    $"{description} independent obstruction {position} of the finger did not veto at {time}ms.");
            Quiet(feed(tracker, finger, 1500), description + " after removing the " + position + " obstruction");
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static void Quiet(HandAcquisitionPresenceResult result, string description) => Require(
        !result.OutsideControlDisturbance, description + " falsely vetoed arrow hold: " + result.OutsideControlForegroundFraction);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
