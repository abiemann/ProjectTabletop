using ProjectTabletop.Vision;

internal static class HandAcquisitionPresenceRegression
{
    private const int Width = 640, Height = 360;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Polygon = [new(80, 100), new(560, 100), new(580, 330), new(60, 330)];
    private static readonly double[] Matrix = [1.0 / Width, .0001, -.02, .00002, 1.0 / Height, -.02, .00013, .00006, 1];

    public static void Run()
    {
        StationaryAtStartupAndRemoval();
        ExposureGeometryAndNoise();
        UniqueControlCoveredAtStartup();
        OwnLightPresenceAndRemoval();
        BaselineFallbackAndBarriers();
        Console.WriteLine("Hand acquisition presence regression: stationary foreground present at startup, persistent " +
            "known-render comparison, native projective geometry, photometric/exposure compensation, raster-edge/noise " +
            "rejection, bounded crops, own-white-light exclusion, lit foreground versus empty light, removal, and reset/time barriers passed.");
    }

    private static void StationaryAtStartupAndRemoval()
    {
        var scene = Scene();
        byte[] empty = Camera(scene), hand = Camera(scene);
        Rectangle(hand, 265, 205, 65, 95, 60, 85, 150);
        var tracker = new HandAcquisitionPresenceTracker();
        HandAcquisitionPresenceResult initial = Feed(tracker, hand, scene, 0);
        Require(initial.Hints.Count == 1 && initial.BaselineReady, "A hand already present in the first frame was learned as empty background.");
        HandAcquisitionHint hint = initial.Hints[0];
        Require(Math.Abs(hint.Center.X - 297.5) < 15 && Math.Abs(hint.Center.Y - 252.5) < 15,
            "The known-render comparison located foreground outside its native camera position.");
        CheckCrop(hint);
        for (int frame = 1; frame <= 18; frame++)
        {
            var current = Feed(tracker, hand, scene, frame * 100);
            Require(current.Hints.Count == 1 && current.Hints[0].ObservedAt == Epoch.AddMilliseconds(frame * 100),
                "A stationary foreground object was absorbed or treated as an expired motion hint.");
        }
        Require(Feed(tracker, empty, scene, 1900).Hints.Count == 0, "Removed foreground left a presence ghost.");
        var cleanStartup = Feed(new(), empty, scene, 0);
        Require(cleanStartup.Hints.Count == 0, "The projected controls themselves acquired a spotlight.");
    }

    private static void ExposureGeometryAndNoise()
    {
        var scene = Scene();
        var tracker = new HandAcquisitionPresenceTracker();
        Require(Feed(tracker, Camera(scene), scene, 0).Hints.Count == 0, "The initial projected template did not match.");
        byte[] brighter = Camera(scene, brightness: 23, gain: 1.08);
        Require(Feed(tracker, brighter, scene, 100).Hints.Count == 0, "Auto-exposure changes were mistaken for foreground.");
        byte[] darker = Camera(scene, brightness: -12, gain: .82, noise: 3);
        Require(Feed(tracker, darker, scene, 200).Hints.Count == 0, "Camera noise or darker exposure created a presence hint.");
        byte[] shifted = Camera(scene, shiftX: 2, shiftY: -1);
        Require(Feed(tracker, shifted, scene, 300).Hints.Count == 0,
            "Small calibration/raster edge differences created a hand-sized region.");
        Rectangle(brighter, 275, 215, 60, 85, 70, 95, 160);
        Require(Feed(tracker, brighter, scene, 400).Hints.Count == 1,
            "Exposure compensation erased foreground along with the global camera change.");

        byte[] tiny = Camera(scene);
        Rectangle(tiny, 180, 180, 8, 10, 255, 255, 255);
        Require(Feed(new(), tiny, scene, 0).Hints.Count == 0, "A tiny unmatched decoration became an acquisition region.");
        byte[] outside = Camera(scene);
        Rectangle(outside, 0, 0, 70, 70, 255, 0, 255);
        Require(Feed(new(), outside, scene, 0).Hints.Count == 0, "Foreground outside the calibrated polygon was included.");
        byte[] multiple = Camera(scene);
        Rectangle(multiple, 110, 175, 45, 65, 70, 90, 155);
        Rectangle(multiple, 285, 180, 45, 65, 70, 90, 155);
        Rectangle(multiple, 465, 180, 45, 65, 70, 90, 155);
        var hints = Feed(new(), multiple, scene, 0).Hints;
        Require(hints.Count == 2, "Persistent foreground was not bounded to two useful search regions.");
        foreach (var hint in hints) CheckCrop(hint);
    }

    private static void OwnLightPresenceAndRemoval()
    {
        var scene = Scene();
        var tracker = new HandAcquisitionPresenceTracker();
        var hint = new HandAcquisitionHint(new(200, 150, 200, 200), new(300, 250), 65, Epoch, .04);
        byte[] empty = Camera(scene), lit = Camera(scene), occupied = Camera(scene);
        Circle(lit, hint.Center, hint.RadiusPixels, 215, 225, 220);
        Circle(occupied, hint.Center, hint.RadiusPixels, 215, 225, 220);
        Rectangle(occupied, 283, 220, 34, 60, 85, 115, 165);
        Feed(tracker, empty, scene, 0);
        Require(Feed(tracker, occupied, scene, 100, hint, 100).IlluminatedPresence is null,
            "The first projector/camera light transition was judged as a physical object.");
        var present = Feed(tracker, occupied, scene, 350, hint, 100);
        Require(present.IlluminatedPresence == true && present.Hints.Count > 0,
            "An illuminated stationary hand failed to keep fresh foreground evidence.");
        for (int frame = 4; frame <= 14; frame++)
            Require(Feed(tracker, occupied, scene, frame * 100, hint, 100).IlluminatedPresence == true,
                "Own lighting absorbed stationary foreground after the initial illumination.");
        var removed = Feed(tracker, lit, scene, 1500, hint, 100);
        Require(removed.IlluminatedPresence == false && removed.Hints.Count == 0,
            "An empty white search light kept itself on or became foreground.");
        Require(Feed(tracker, empty, scene, 1600).Hints.Count == 0,
            "Turning the search light off created a persistent false acquisition.");

        byte[] occluded = Camera(scene);
        Circle(occluded, hint.Center, hint.RadiusPixels, 40, 50, 90);
        var uncertain = Feed(tracker, occluded, scene, 1700, hint, 100);
        Require(uncertain.IlluminatedPresence is not false,
            "A fully dark, uniformly occluded light was confidently called empty.");
    }

    private static void UniqueControlCoveredAtStartup()
    {
        var scene = Scene();
        for (int y = 77; y <= 105; y++)
        for (int x = 77; x <= 106; x++)
        {
            int offset = (y * scene.Width + x) * 4;
            scene.Bgra[offset] = 25; scene.Bgra[offset + 1] = 180; scene.Bgra[offset + 2] = 235;
        }
        byte[] covered = Camera(scene);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double divisor = Matrix[6] * x + Matrix[7] * y + Matrix[8];
            double u = (Matrix[0] * x + Matrix[1] * y + Matrix[2]) / divisor;
            double v = (Matrix[3] * x + Matrix[4] * y + Matrix[5]) / divisor;
            // A hand covering the control also occupies the neighboring felt. A perfectly
            // uniform occluder exactly matching a unique control's footprint is inherently
            // ambiguous for a first-frame photometric fit; fixed palm searches cover that case.
            if (u * (scene.Width - 1) is >= 72 and <= 111 && v * (scene.Height - 1) is >= 72 and <= 110)
                Set(covered, x, y, 40, 65, 110);
        }
        Require(Feed(new(), covered, scene, 0).Hints.Count > 0,
            "A hand already covering a uniquely colored control supplied its own false empty-color reference.");
    }

    private static void BaselineFallbackAndBarriers()
    {
        var scene = Scene();
        byte[] empty = Camera(scene), hand = Camera(scene);
        Rectangle(hand, 265, 205, 65, 95, 60, 85, 150);
        var tracker = new HandAcquisitionPresenceTracker();
        Require(Feed(tracker, empty, null, 0).Hints.Count == 0, "Fallback camera initialization invented an object.");
        Require(Feed(tracker, hand, null, 100).Hints.Count == 1, "Foreground failed against a previously empty camera baseline.");
        Require(Feed(tracker, hand, null, 5000).Hints.Count == 1, "The fallback baseline absorbed a motionless hand.");
        Require(Feed(tracker, empty, null, 5100).Hints.Count == 0, "Fallback removal left an acquisition ghost.");
        Require(Feed(tracker, hand, scene, 5050).Hints.Count == 0, "An out-of-order camera frame supplied presence evidence.");
        Require(Feed(tracker, hand, scene, 5200, now: 5600).Hints.Count == 0, "Stale camera frames supplied presence evidence.");
        Require(Feed(tracker, hand, scene, 5800, now: 5700).Hints.Count == 0, "Future camera frames supplied presence evidence.");
        tracker.Reset();
        Require(Feed(tracker, hand, scene, 0).Hints.Count == 1,
            "Reset lost known-render acquisition for an already present hand.");
        tracker.Reset();
        Require(Feed(tracker, hand, null, 0).Hints.Count == 0,
            "A camera-only baseline claimed it could identify what was already present at startup.");

        var invalid = scene with { CameraToBoard = new double[9] };
        Require(Feed(new(), empty, invalid, 0).Hints.Count == 0, "An invalid projective mapping generated foreground.");
        var shiftedPolygon = Polygon.Select(point => new PixelPoint(point.X + 5, point.Y)).ToArray();
        Require(tracker.Update(Width, Height, Width * 4, hand, shiftedPolygon, null,
            Epoch.AddMilliseconds(100), Epoch.AddMilliseconds(100)).Hints.Count == 0,
            "A changed calibration reused an unrelated fallback baseline.");
    }

    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels,
        HandAcquisitionSceneImage? scene, int milliseconds, HandAcquisitionHint? light = null,
        int? lightStarted = null, int? now = null) => tracker.Update(Width, Height, Width * 4, pixels, Polygon,
            scene, Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(now ?? milliseconds), light,
            lightStarted is null ? null : Epoch.AddMilliseconds(lightStarted.Value));

    private static HandAcquisitionSceneImage Scene()
    {
        const int width = 192, height = 128;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(25 + x * .12);
            pixels[offset + 1] = (byte)(70 + x * .16 + y * .09);
            pixels[offset + 2] = (byte)(20 + y * .18);
            if (y is > 82 and < 113 && x % 42 is > 4 and < 38)
            {
                int button = x / 42;
                pixels[offset] = (byte)(20 + button * 14);
                pixels[offset + 1] = (byte)(25 + button * 17);
                pixels[offset + 2] = (byte)(35 + button * 24);
                if (y is 91 or 92 && x % 7 < 4)
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 225;
            }
            pixels[offset + 3] = 255;
        }
        return new(width, height, pixels, Matrix);
    }

    private static byte[] Camera(HandAcquisitionSceneImage scene, double brightness = 0, double gain = 1,
        int noise = 0, int shiftX = 0, int shiftY = 0)
    {
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double px = x + shiftX, py = y + shiftY;
            double divisor = Matrix[6] * px + Matrix[7] * py + Matrix[8];
            double u = Math.Clamp((Matrix[0] * px + Matrix[1] * py + Matrix[2]) / divisor, 0, 1);
            double v = Math.Clamp((Matrix[3] * px + Matrix[4] * py + Matrix[5]) / divisor, 0, 1);
            double tx = u * (scene.Width - 1), ty = v * (scene.Height - 1);
            int ix = (int)tx, iy = (int)ty, rx = Math.Min(scene.Width - 1, ix + 1), by = Math.Min(scene.Height - 1, iy + 1);
            double fx = tx - ix, fy = ty - iy;
            for (int channel = 0; channel < 3; channel++)
            {
                double expected = scene.Bgra[(iy * scene.Width + ix) * 4 + channel] * (1 - fx) * (1 - fy) +
                    scene.Bgra[(iy * scene.Width + rx) * 4 + channel] * fx * (1 - fy) +
                    scene.Bgra[(by * scene.Width + ix) * 4 + channel] * (1 - fx) * fy +
                    scene.Bgra[(by * scene.Width + rx) * 4 + channel] * fx * fy;
                double actual = 12 + channel * 6 + (.65 + channel * .08) * expected + .0008 * expected * expected +
                    x * 12.0 / Width + y * 8.0 / Height;
                actual = actual * gain + brightness + (noise == 0 ? 0 : (x * 17 + y * 31) % (noise * 2 + 1) - noise);
                pixels[(y * Width + x) * 4 + channel] = (byte)Math.Clamp(Math.Round(actual), 0, 255);
            }
            pixels[(y * Width + x) * 4 + 3] = 255;
        }
        return pixels;
    }

    private static void Rectangle(byte[] pixels, int x, int y, int width, int height, byte b, byte g, byte r)
    {
        for (int py = y; py < y + height; py++)
        for (int px = x; px < x + width; px++) Set(pixels, px, py, b, g, r);
    }
    private static void Circle(byte[] pixels, PixelPoint center, double radius, byte b, byte g, byte r)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            if (Math.Pow(x - center.X, 2) + Math.Pow(y - center.Y, 2) <= radius * radius) Set(pixels, x, y, b, g, r);
    }
    private static void Set(byte[] pixels, int x, int y, byte b, byte g, byte r)
    {
        int offset = (y * Width + x) * 4;
        pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r;
    }
    private static void CheckCrop(HandAcquisitionHint hint) => Require(hint.SearchBounds.Width == hint.SearchBounds.Height &&
        hint.SearchBounds.X >= 0 && hint.SearchBounds.Y >= 0 && hint.SearchBounds.X + hint.SearchBounds.Width <= Width &&
        hint.SearchBounds.Y + hint.SearchBounds.Height <= Height, "A presence crop stretched or exceeded the native camera.");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
