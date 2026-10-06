using OpenCvSharp;
using ProjectTabletop.Vision;
using System.Globalization;
using System.Text.Json;

internal static class ColorTipRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    public static void Run()
    {
        CheckLearningAndLighting();
        CheckRejections();
        CheckPinkSkinCannotReplaceRedPin();
        CheckSmallCoreSaturationChange();
        CheckGlintAndThinMaskTails();
        CheckPinRemainsSeparateFromSameHueShaft();
        CheckProjection();
        CheckGreyTextureCannotVetoColoredPin();
        CheckTracking();
        Console.WriteLine("Colour tip regression: small saturated marker learning, arbitrary hue, red hue wrap, " +
            "exposure, normalized scale, projected-colour exclusion, ambiguity, stationary confirmation and loss passed.");
    }

    private static void CheckLearningAndLighting()
    {
        foreach (double hue in new[] { 0.0, 58.0, 124.0, 210.0, 300.0, 358.0 })
        {
            byte[] original = Frame(320, 240);
            Tip(original, 320, 240, 160, 120, 4, hue, .78, .80);
            var profile = ColorTipDetector.Learn(320, 240, 1280, original, new(160, 120));
            Require(profile.IsValid && ColorTipDetector.HueDistance(profile.HueDegrees, hue) <= 3,
                "Learning did not preserve the selected hue.");
            var roundTrip = JsonSerializer.Deserialize<ColorTipProfile>(JsonSerializer.Serialize(profile));
            Require(roundTrip == profile, "A saved colour profile did not round-trip.");
            foreach (var (saturation, brightness) in new[] { (.74, .35), (.72, .95), (.95, .90) })
            {
                byte[] changed = Frame(320, 240);
                Tip(changed, 320, 240, 163, 122, 4, (hue + 5) % 360, saturation, brightness);
                var detected = ColorTipDetector.Detect(320, 240, 1280, changed, profile);
                Require(detected.Candidates.Count == 1 && Near(detected.Candidates[0], 163, 122),
                    $"Hue {hue} was lost after a reasonable exposure/saturation change.");
            }
            byte[] larger = Frame(640, 480);
            Tip(larger, 640, 480, 320, 240, 8, hue, .78, .80);
            Require(ColorTipDetector.Detect(640, 480, 2560, larger, profile).Candidates.Count == 1,
                "Changing camera resolution invalidated the normalized marker scale.");
        }
    }

    private static void CheckPinkSkinCannotReplaceRedPin()
    {
        const int width = 320, height = 240;
        byte[] training = Frame(width, height);
        Tip(training, width, height, 160, 120, 6, 340, .71, .46);
        var profile = ColorTipDetector.Learn(width, height, width * 4, training, new(160, 120));
        // Projected texture can make a hand's red/pink areas separate into pin-sized shapes.
        // Hue and geometry deliberately match; only the learned saturated core distinguishes them.
        byte[] covered = Frame(width, height);
        foreach (var (x, saturation) in new[] { (45, .46), (95, .54), (145, .60), (195, .60), (245, .55) })
            Tip(covered, width, height, x, 120, 6, 340, saturation, .35);
        Require(ColorTipDetector.Detect(width, height, width * 4, covered, profile).Candidates.Count == 0,
            "Pink skin fragments replaced a covered saturated red pin.");
        foreach (double brightness in new[] { .25, .45, .85 })
        {
            byte[] visible = covered.ToArray();
            Tip(visible, width, height, 160, 175, 6, 340, .78, brightness);
            var result = ColorTipDetector.Detect(width, height, width * 4, visible, profile);
            Require(result.Candidates.Count == 1 && Near(result.Candidates[0], 160, 175),
                "Core colour conformity lost a real red pin under a brightness change.");
        }
    }

    private static void CheckPinRemainsSeparateFromSameHueShaft()
    {
        const int width = 320, height = 240;
        byte[] Scene(double hue, double brightness)
        {
            byte[] pixels = Frame(width, height);
            // A weaker same-hue shaft touches the pin. The old permissive mask joined them
            // into one elongated contour, which prevented both learning and tracking.
            for (int y = 90; y <= 180; y += 4) Tip(pixels, width, height, 160, y, 5, hue, .42, .60);
            Tip(pixels, width, height, 160, 80, 6, hue, .74, brightness);
            return pixels;
        }
        byte[] cool = Scene(340, .46), warm = Scene(352, .65);
        var profile = ColorTipDetector.Learn(width, height, width * 4, cool, new(160, 80));
        foreach (byte[] frame in new[] { cool, warm })
        {
            var result = ColorTipDetector.Detect(width, height, width * 4, frame, profile);
            Require(result.Candidates.Count == 1 && Near(result.Candidates[0], 160, 80),
                "A same-hue wooden shaft swallowed the saturated pin after an illumination change.");
        }
        Require(ColorTipDetector.Learn(width, height, width * 4, warm, new(160, 80)).IsValid,
            "A pin attached to warmer same-hue wood could not be relearned.");
    }

    private static void CheckSmallCoreSaturationChange()
    {
        const int width = 320, height = 240;
        byte[] training = Frame(width, height);
        Tip(training, width, height, 160, 120, 6, 352, .757, .59);
        var profile = ColorTipDetector.Learn(width, height, width * 4, training, new(160, 120));
        // A modest camera exposure/white-balance variation reduces saturation while preserving
        // the red pin's shape and hue. Keep a margin from the earlier brittle 90% core cutoff.
        byte[] changed = Frame(width, height);
        Tip(changed, width, height, 160, 120, 6, 350, .66, .69);
        Require(ColorTipDetector.Detect(width, height, width * 4, changed, profile).Candidates.Count == 1,
            "Small normal saturation variation intermittently erased a stationary red pin.");
        byte[] skin = Frame(width, height);
        Tip(skin, width, height, 160, 120, 6, 350, .60, .40);
        Require(ColorTipDetector.Detect(width, height, width * 4, skin, profile).Candidates.Count == 0,
            "Allowing small saturation variation admitted a matching pink skin fragment.");
    }

    private static void CheckGlintAndThinMaskTails()
    {
        const int width = 320, height = 240;
        byte[] training = Frame(width, height);
        Tip(training, width, height, 160, 120, 6, 352, .757, .59);
        var profile = ColorTipDetector.Learn(width, height, width * 4, training, new(160, 120));
        byte[] tailed = training.ToArray();
        for (int y = 99; y <= 147; y++) Tip(tailed, width, height, 160, y, 0, 352, .80, .59);
        Require(ColorTipDetector.Detect(width, height, width * 4, tailed, profile).Candidates.Count == 1 &&
            ColorTipDetector.Learn(width, height, width * 4, tailed, new(160, 120)).IsValid,
            "Tiny coloured mask tails turned a small physical pin into a rejected shaft.");
        byte[] glint = Frame(width, height);
        Tip(glint, width, height, 160, 120, 6, 350, .80, .65);
        for (int y = 118; y <= 121; y++)
        for (int x = 158; x <= 161; x++) Tip(glint, width, height, x, y, 0, 350, .60, .90);
        Require(ColorTipDetector.Detect(width, height, width * 4, glint, profile).Candidates.Count == 1,
            "A projector highlight erased a pin despite strongly saturated support around its core.");
        byte[] skin = Frame(width, height);
        Tip(skin, width, height, 160, 120, 6, 350, .60, .90);
        Require(ColorTipDetector.Detect(width, height, width * 4, skin, profile).Candidates.Count == 0,
            "Glint tolerance allowed a uniformly less-saturated skin-colour fragment.");
    }

    private static void CheckRejections()
    {
        byte[] grey = Frame(320, 240);
        bool refused = false;
        try { ColorTipDetector.Learn(320, 240, 1280, grey, new(160, 120)); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused, "A grey board patch was learned as a coloured tip.");
        byte[] original = Frame(320, 240);
        Tip(original, 320, 240, 160, 120, 4, 358, .8, .8);
        var profile = ColorTipDetector.Learn(320, 240, 1280, original, new(160, 120));
        byte[] wrong = Frame(320, 240);
        Tip(wrong, 320, 240, 160, 120, 4, 180, .9, .8);
        Require(ColorTipDetector.Detect(320, 240, 1280, wrong, profile).Candidates.Count == 0,
            "An unrelated hue became the learned marker.");
        byte[] oversized = Frame(320, 240);
        Tip(oversized, 320, 240, 160, 120, 45, 358, .9, .8);
        Require(ColorTipDetector.Detect(320, 240, 1280, oversized, profile).Candidates.Count == 0,
            "An oversized coloured object became the small tip.");
        byte[] duplicates = original.ToArray();
        Tip(duplicates, 320, 240, 230, 120, 4, 2, .8, .8);
        Require(ColorTipDetector.Detect(320, 240, 1280, duplicates, profile).Candidates.Count == 2,
            "Multiple plausible coloured objects were silently reduced to a guessed winner.");
    }

    private static void CheckProjection()
    {
        const int width = 320, height = 240;
        byte[] rendered = Frame(width, height);
        Tip(rendered, width, height, 70, 90, 5, 359, .8, .8);
        byte[] occupied = rendered.ToArray();
        Tip(occupied, width, height, 200, 150, 5, 1, .75, .7);
        var profile = ColorTipDetector.Learn(width, height, width * 4, occupied, new(200, 150));
        var reference = new EyeTipProjectionFrame(width, height, rendered,
            [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1], Epoch);
        var options = new ColorTipDetectionOptions(ProjectionFrames: [reference], FrameTime: Epoch);
        var result = ColorTipDetector.Detect(width, height, width * 4, occupied, profile, options);
        Require(result.Candidates.Count == 1 && Near(result.Candidates[0], 200, 150),
            "The rendered coloured decoration was not excluded while preserving the physical tip.");
        Require(ColorTipDetector.Detect(width, height, width * 4, rendered, profile, options).Candidates.Count == 0,
            "The empty projected board produced a coloured physical tip.");
    }

    private static void CheckGreyTextureCannotVetoColoredPin()
    {
        const int width = 320, height = 240;
        byte[] rendered = Frame(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 4;
            byte level = (byte)(25 + ((x / 3 * 83 + y / 3 * 57) % 110));
            rendered[index] = rendered[index + 1] = rendered[index + 2] = level;
        }
        byte[] occupied = rendered.ToArray();
        Tip(occupied, width, height, 160, 120, 4, 352, .80, .80);
        var profile = ColorTipDetector.Learn(width, height, width * 4, occupied, new(160, 120));
        var plain = ColorTipDetector.Detect(width, height, width * 4, occupied, profile);
        Require(plain.Candidates.Count == 1, "The textured-bed fixture did not contain one coloured pin.");
        var reference = new EyeTipProjectionFrame(width, height, rendered,
            [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1], Epoch);
        using Mat source = Mat.FromPixelData(height, width, MatType.CV_8UC4, occupied);
        using Mat gray = new(); Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        byte[] grayPixels = new byte[width * height];
        System.Runtime.InteropServices.Marshal.Copy(gray.Data, grayPixels, 0, grayPixels.Length);
        var generic = new EyeTipProjectionMatcher([reference], Epoch);
        var pin = plain.Candidates[0];
        Require(!generic.IsPhysicalCandidate(pin.Center, pin.RadiusPixels, grayPixels, width, height, 1, 1),
            "The fixture must reproduce a grayscale match dominated by surrounding texture.");
        var filtered = ColorTipDetector.Detect(width, height, width * 4, occupied, profile,
            new ColorTipDetectionOptions(ProjectionFrames: [reference], FrameTime: Epoch));
        Require(filtered.Candidates.Count == 1 && Near(filtered.Candidates[0], 160, 120),
            "Matching grey background texture erased the physical coloured pin.");
    }

    private static void CheckTracking()
    {
        var tracker = new ColorTipTracker();
        ColorTipObservation marker = new(new(100, 100), 4, 50, .9);
        ColorTipDetectionResult One(ColorTipObservation item) => new([item], "test");
        Require(!tracker.Update(One(marker), Epoch, Epoch).Confirmed, "One frame confirmed a colour tip.");
        Require(!tracker.Update(One(marker), Epoch, Epoch.AddMilliseconds(30)).Confirmed, "Replayed camera frames confirmed a tip.");
        Require(tracker.Update(One(marker), Epoch.AddMilliseconds(70), Epoch.AddMilliseconds(70)).Confirmed,
            "A stationary coloured tip could not confirm.");
        Require(tracker.Update(One(marker with { Center = new(155, 100) }), Epoch.AddMilliseconds(140), Epoch.AddMilliseconds(140)).Confirmed,
            "Ordinary tip movement lost temporal association.");
        Require(tracker.Update(new([], "covered"), Epoch.AddMilliseconds(210), Epoch.AddMilliseconds(210)).Observation is null,
            "Covering the pin retained an extrapolated tip.");
        Require(!tracker.Update(One(marker), Epoch.AddMilliseconds(280), Epoch.AddMilliseconds(280)).Confirmed,
            "A returning tip skipped fresh confirmation.");
        tracker.Reset();
        Require(tracker.Update(new([marker, marker with { Center = new(150, 100) }], "two"), Epoch, Epoch).Observation is null,
            "Ambiguous coloured objects were guessed during acquisition.");
        tracker.Acquire(marker, Epoch);
        Require(!tracker.Update(One(marker), Epoch.AddMilliseconds(70), Epoch.AddSeconds(1)).Confirmed,
            "A stale frame retained a coloured tip.");
        Require(!tracker.Update(One(marker), Epoch.AddSeconds(2), Epoch.AddSeconds(1)).Confirmed,
            "A future camera frame supplied a coloured tip.");
    }

    public static void Replay(string[] args)
    {
        if (args.Length is not (3 or 5)) throw new ArgumentException("Expected camera.png clickX clickY [projection.png matrix.json].");
        using Mat source = Cv2.ImRead(args[0], ImreadModes.Color);
        if (source.Empty()) throw new ArgumentException("Could not read camera image.");
        using Mat bgra = new(); Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        byte[] pixels = BoardDetectionRegression.BytesOf(bgra);
        PixelPoint click = new(double.Parse(args[1], CultureInfo.InvariantCulture), double.Parse(args[2], CultureInfo.InvariantCulture));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var profile = ColorTipDetector.Learn(bgra.Width, bgra.Height, (int)bgra.Step(), pixels, click);
        double learnMilliseconds = timer.Elapsed.TotalMilliseconds;
        ColorTipDetectionOptions options = new(PreferredCenter: click);
        if (args.Length == 5)
        {
            using Mat projected = Cv2.ImRead(args[3], ImreadModes.Color); using Mat projectedBgra = new();
            Cv2.CvtColor(projected, projectedBgra, ColorConversionCodes.BGR2BGRA);
            var now = DateTimeOffset.UtcNow;
            var reference = new EyeTipProjectionFrame(projectedBgra.Width, projectedBgra.Height,
                BoardDetectionRegression.BytesOf(projectedBgra),
                JsonSerializer.Deserialize<double[]>(File.ReadAllText(args[4]))!, now);
            options = options with { ProjectionFrames = [reference], FrameTime = now };
        }
        timer.Restart();
        var detected = ColorTipDetector.Detect(bgra.Width, bgra.Height, (int)bgra.Step(), pixels, profile, options);
        double detectMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var repeated = ColorTipDetector.Detect(bgra.Width, bgra.Height, (int)bgra.Step(), pixels, profile, options);
        Console.WriteLine(JsonSerializer.Serialize(new { profile, learnMilliseconds, detectMilliseconds,
            cachedMilliseconds = timer.Elapsed.TotalMilliseconds, detected.Reason, detected.Candidates,
            repeatCandidates = repeated.Candidates }));
    }

    public static void ReplayProfile(string cameraPath, string profilePath, string? projectionPath = null, string? mapPath = null)
    {
        using Mat source = Cv2.ImRead(cameraPath, ImreadModes.Color);
        if (source.Empty()) throw new ArgumentException("Could not read camera image.");
        using Mat bgra = new(); Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        var profile = JsonSerializer.Deserialize<ColorTipProfile>(File.ReadAllText(profilePath)) ??
            throw new ArgumentException("Could not read colour-tip profile.");
        ColorTipDetectionOptions? options = null;
        if (projectionPath is not null && mapPath is not null)
        {
            using Mat projection = Cv2.ImRead(projectionPath, ImreadModes.Color);
            using Mat projectionBgra = new(); Cv2.CvtColor(projection, projectionBgra, ColorConversionCodes.BGR2BGRA);
            var now = DateTimeOffset.UtcNow;
            options = new(ProjectionFrames: [new(projectionBgra.Width, projectionBgra.Height,
                BoardDetectionRegression.BytesOf(projectionBgra),
                JsonSerializer.Deserialize<double[]>(File.ReadAllText(mapPath))!, now)], FrameTime: now);
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var detected = ColorTipDetector.Detect(bgra.Width, bgra.Height, (int)bgra.Step(),
            BoardDetectionRegression.BytesOf(bgra), profile, options);
        Console.WriteLine(JsonSerializer.Serialize(new { milliseconds = timer.Elapsed.TotalMilliseconds,
            profile, detected.Reason, detected.Candidates }));
    }

    public static void Probe(string path, string profilePath, double x, double y)
    {
        using Mat source = Cv2.ImRead(path, ImreadModes.Color);
        using Mat bgra = new(); Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        var profile = JsonSerializer.Deserialize<ColorTipProfile>(File.ReadAllText(profilePath))!;
        var imageType = typeof(ColorTipDetector).GetNestedType("ColorImage", System.Reflection.BindingFlags.NonPublic)!;
        object image = Activator.CreateInstance(imageType, bgra.Width, bgra.Height, (int)bgra.Step(), BoardDetectionRegression.BytesOf(bgra))!;
        using Mat mask = (Mat)imageType.GetMethod("Mask")!.Invoke(image, [profile])!;
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var component = typeof(ColorTipDetector).GetMethod("Component", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var results = new List<object>();
        foreach (var contour in contours)
        {
            double area = Cv2.ContourArea(contour); if (area < 1) continue;
            Moments m = Cv2.Moments(contour);
            double cx = m.M10 / m.M00, cy = m.M01 / m.M00;
            if (Math.Abs(cx - x) > 30 || Math.Abs(cy - y) > 40) continue;
            Rect bounds = Cv2.BoundingRect(contour);
            double perimeter = Cv2.ArcLength(contour, true);
            object? shapeOnly = component.Invoke(null, [contour, image, profile.NormalizedArea * bgra.Width * bgra.Height, null]);
            object? withCore = component.Invoke(null, [contour, image, profile.NormalizedArea * bgra.Width * bgra.Height, profile]);
            results.Add(new { cx, cy, area, bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                circularity = 4 * Math.PI * area / (perimeter * perimeter),
                fill = area / (bounds.Width * (double)bounds.Height), shapeOnly, withCore });
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
    }

    private static bool Near(ColorTipObservation item, double x, double y) => ColorTipDetector.Distance(item.Center, new(x, y)) < 2;
    private static byte[] Frame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = pixels[i + 1] = pixels[i + 2] = 110; pixels[i + 3] = 255; }
        return pixels;
    }
    private static void Tip(byte[] pixels, int width, int height, int x, int y, int radius, double hue, double saturation, double value)
    {
        using Mat hsv = new(1, 1, MatType.CV_8UC3, new Scalar(hue / 2, saturation * 255, value * 255));
        using Mat bgr = new(); Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        Vec3b color = bgr.At<Vec3b>(0, 0);
        for (int py = y - radius; py <= y + radius; py++)
        for (int px = x - radius; px <= x + radius; px++)
        {
            if (px < 0 || py < 0 || px >= width || py >= height || (px - x) * (px - x) + (py - y) * (py - y) > radius * radius) continue;
            int index = (py * width + px) * 4;
            pixels[index] = color.Item0; pixels[index + 1] = color.Item1; pixels[index + 2] = color.Item2;
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
