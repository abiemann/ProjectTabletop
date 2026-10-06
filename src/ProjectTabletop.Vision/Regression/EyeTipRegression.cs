using ProjectTabletop.Vision;

internal static class EyeTipRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckSmallNativeMarker();
        CheckPerspectiveAndIllumination();
        CheckNonMarkers();
        CheckFiniteRingBoundaryAndScaling();
        CheckCandidateBoundAndStride();
        CheckTemporalTracking();
        Console.WriteLine("Eye tip regression: native small pupil, elliptical perspective, illumination, glint, " +
            "hollow/low-contrast rejection, bounded candidates, padded rows, fresh confirmation, loss and ambiguity passed.");
    }

    private static void CheckSmallNativeMarker()
    {
        byte[] frame = Frame(1920, 1080, 180);
        Shaft(frame, 1920, 1080, 540, 589, 7, 70, 100);
        Eye(frame, 1920, 1080, 540, 589, 5, 4.5, .18, 150, 35);
        // The real sticker has a small specular spot inside the dark pupil.
        Ellipse(frame, 1920, 1080, 538, 587, 1, 1, 0, 245);
        var detected = EyeTipDetector.Detect(1920, 1080, 1920 * 4, frame);
        Require(detected.Candidates.Any(item => Distance(item.Center, new(540, 589)) <= 2 &&
            item.RadiusPixels is > 3 and < 7 && item.Contrast > 60),
            "A small pupil with a narrow bright ring and glint was lost at native 1920 resolution.");
    }

    private static void CheckPerspectiveAndIllumination()
    {
        foreach (var (radius, ratio, angle, ring, pupil, background) in new[]
        {
            (6.0, .65, .8, (byte)205, (byte)25, (byte)110),
            (11.0, .50, 1.3, (byte)155, (byte)70, (byte)120),
            (8.0, .85, 2.2, (byte)75, (byte)15, (byte)48),
            (7.0, .85, .4, (byte)90, (byte)18, (byte)8),
            (20.0, .70, .2, (byte)240, (byte)100, (byte)180)
        })
        {
            byte[] frame = Frame(320, 240, background);
            Eye(frame, 320, 240, 155, 117, radius, radius * ratio, angle, ring, pupil);
            Require(EyeTipDetector.Detect(320, 240, 1280, frame).Candidates.Any(item =>
                Distance(item.Center, new(155, 117)) < 2.5),
                $"Marker lost under perspective/illumination: radius={radius}, ratio={ratio}, ring={ring}.");
        }
    }

    private static void CheckNonMarkers()
    {
        byte[] hollow = Frame(240, 180, 180);
        Ellipse(hollow, 240, 180, 120, 90, 12, 12, 0, 20);
        Ellipse(hollow, 240, 180, 120, 90, 8, 8, 0, 180);
        Require(EyeTipDetector.Detect(240, 180, 960, hollow).Candidates.Count == 0,
            "A hollow circle was mistaken for a filled eye pupil.");

        byte[] dim = Frame(240, 180, 35);
        Ellipse(dim, 240, 180, 120, 90, 7, 7, 0, 12);
        Require(EyeTipDetector.Detect(240, 180, 960, dim).Candidates.Count == 0,
            "A dark dot without a substantially bright surround was accepted.");

        byte[] plainDot = Frame(240, 180, 180);
        Ellipse(plainDot, 240, 180, 120, 90, 5, 4.5, .2, 25);
        Require(EyeTipDetector.Detect(240, 180, 960, plainDot).Candidates.Count == 0,
            "A dark mark on a uniformly bright surface was mistaken for a finite white-ring sticker.");

        byte[] elongated = Frame(240, 180, 180);
        Ellipse(elongated, 240, 180, 120, 90, 28, 3, .6, 20);
        Require(EyeTipDetector.Detect(240, 180, 960, elongated).Candidates.Count == 0,
            "A long dark shaft or text stroke was mistaken for a pupil.");

        byte[] blank = Frame(240, 180, 150);
        Require(EyeTipDetector.Detect(240, 180, 960, blank).Candidates.Count == 0,
            "A uniform scene created a marker.");
    }

    private static void CheckCandidateBoundAndStride()
    {
        byte[] crowded = Frame(400, 300, 90);
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 4; column++)
            Eye(crowded, 400, 300, 50 + column * 90, 50 + row * 90, 6, 6, 0, 195, 25);
        Require(EyeTipDetector.Detect(400, 300, 1600, crowded).Candidates.Count == 8,
            "The marker candidate list did not respect its fixed bound.");

        byte[] compact = Frame(100, 80, 100);
        Eye(compact, 100, 80, 45, 37, 5, 5, 0, 190, 20);
        int stride = 100 * 4 + 24;
        byte[] padded = new byte[stride * 80];
        for (int row = 0; row < 80; row++) Array.Copy(compact, row * 400, padded, row * stride, 400);
        Require(EyeTipDetector.Detect(100, 80, stride, padded).Candidates.Any(item =>
            Distance(item.Center, new(45, 37)) < 2), "Padded camera rows shifted the pupil.");
        bool rejected = false;
        try { EyeTipDetector.Detect(100, 80, 399, compact); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "An invalid camera stride was accepted.");
    }

    private static void CheckFiniteRingBoundaryAndScaling()
    {
        foreach (var (x, y) in new[] { (7, 40), (92, 40), (50, 7), (50, 72) })
        {
            byte[] edgeDot = Frame(100, 80, 180);
            Ellipse(edgeDot, 100, 80, x, y, 5, 5, 0, 25);
            Require(EyeTipDetector.Detect(100, 80, 400, edgeDot).Candidates.Count == 0,
                "Missing samples outside the camera frame manufactured a finite white ring.");
        }
        byte[] nearEdge = Frame(100, 80, 100);
        Eye(nearEdge, 100, 80, 18, 18, 5, 5, 0, 190, 25);
        Require(EyeTipDetector.Detect(100, 80, 400, nearEdge).Candidates.Any(item =>
            Distance(item.Center, new(18, 18)) < 2), "A complete ring near a valid frame edge was rejected.");

        byte[] large = Frame(3840, 2160, 100);
        Eye(large, 3840, 2160, 1080, 1178, 10, 9, .2, 190, 25);
        Require(EyeTipDetector.Detect(3840, 2160, 15360, large).Candidates.Any(item =>
            Distance(item.Center, new(1080, 1178)) < 3 && item.RadiusPixels is > 7 and < 12),
            "Reduction from a larger camera did not restore raw camera coordinates and pupil size.");
    }

    private static void CheckTemporalTracking()
    {
        var tracker = new EyeTipTracker();
        EyeTipObservation first = Marker(100, 100);
        var one = Result(first);
        Require(!tracker.Update(one, At(0), At(0)).Confirmed, "One frame confirmed a new marker.");
        Require(!tracker.Update(one, At(0), At(30)).Confirmed, "A replayed frame confirmed a marker.");
        Require(tracker.Update(Result(Marker(164, 100)), At(70), At(70)).Confirmed,
            "A normal small-tip sweep between fresh camera frames did not confirm.");
        var missing = tracker.Update(Result(), At(140), At(140));
        Require(!missing.Confirmed && missing.Observation is null, "Missing input extrapolated a phantom tip.");
        Require(!tracker.Update(one, At(210), At(210)).Confirmed,
            "A marker reappearing after loss skipped fresh confirmation.");
        Require(tracker.Update(one, At(280), At(280)).Confirmed, "Reappearing marker did not recover.");
        var stale = tracker.Update(one, At(290), At(700));
        Require(!stale.Confirmed && stale.Observation is null, "A stale camera frame retained an active tip.");

        tracker.Reset();
        var ambiguous = tracker.Update(Result(first, Marker(200, 100)), At(800), At(800));
        Require(!ambiguous.Confirmed && ambiguous.Observation is null,
            "Equally plausible unselected eyes were silently acquired.");
        ambiguous = tracker.Update(Result(first, Marker(200, 100) with { Score = .65 }), At(805), At(805));
        Require(!ambiguous.Confirmed && ambiguous.Observation is null,
            "A quality-score lead silently chose between multiple plausible unselected eyes.");
        tracker.Acquire(first, At(810));
        Require(tracker.Update(Result(Marker(105, 100), Marker(200, 100)), At(880), At(880)).Confirmed,
            "Explicit selection did not preserve the chosen nearby marker.");
        var jump = tracker.Update(Result(Marker(400, 300)), At(950), At(950));
        Require(!jump.Confirmed && jump.Observation is null, "Tracking jumped to a remote eye immediately.");
        Require(!tracker.Update(Result(Marker(400, 300)), At(1400), At(1400)).Confirmed,
            "A new distant marker after timeout skipped confirmation.");
        Require(tracker.Update(Result(Marker(403, 300)), At(1470), At(1470)).Confirmed,
            "An unambiguous marker could not reacquire after timeout.");
        Require(!tracker.Update(one, At(1550), At(1490)).Confirmed, "A future camera frame was accepted.");
    }

    private static byte[] Frame(int width, int height, byte gray)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = pixels[i + 1] = pixels[i + 2] = gray; pixels[i + 3] = 255; }
        return pixels;
    }

    private static void Eye(byte[] pixels, int width, int height, double cx, double cy,
        double rx, double ry, double angle, byte ring, byte pupil)
    {
        Ellipse(pixels, width, height, cx, cy, rx * 1.85, ry * 1.85, angle, ring);
        Ellipse(pixels, width, height, cx, cy, rx, ry, angle, pupil);
    }

    private static void Shaft(byte[] pixels, int width, int height, int cx, int cy,
        int halfWidth, int length, byte gray)
    {
        for (int y = Math.Max(0, cy); y < Math.Min(height, cy + length); y++)
        for (int x = Math.Max(0, cx - halfWidth); x < Math.Min(width, cx + halfWidth + 1); x++)
        {
            int index = (y * width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = gray;
        }
    }

    private static void Ellipse(byte[] pixels, int width, int height, double cx, double cy,
        double rx, double ry, double angle, byte gray)
    {
        int radius = (int)Math.Ceiling(Math.Max(rx, ry)) + 1;
        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        for (int y = Math.Max(0, (int)cy - radius); y < Math.Min(height, (int)cy + radius + 1); y++)
        for (int x = Math.Max(0, (int)cx - radius); x < Math.Min(width, (int)cx + radius + 1); x++)
        {
            double dx = x - cx, dy = y - cy;
            double ex = (dx * cosine + dy * sine) / rx, ey = (-dx * sine + dy * cosine) / ry;
            if (ex * ex + ey * ey > 1) continue;
            int index = (y * width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = gray;
        }
    }

    private static EyeTipObservation Marker(double x, double y) => new(new(x, y), 5, .9, 90, 1);
    private static EyeTipDetectionResult Result(params EyeTipObservation[] observations) => new(observations, "fixture");
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);
    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
