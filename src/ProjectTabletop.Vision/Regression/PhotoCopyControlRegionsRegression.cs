using ProjectTabletop.Vision;

internal static class PhotoCopyControlRegionsRegression
{
    private const int Width = 640, Height = 480, SceneSize = 400;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly double[] Matrix = [1.0 / 500, 0, -.14, 0, 1.0 / 400, -.10, 0, 0, 1];
    private static readonly PixelPoint[] Polygon = [new(82.5, 366), new(557.5, 366), new(557.5, 438), new(82.5, 438)];
    private static readonly HandTrackingBounds[] Regions =
        [new(.092, .847, .236, .081), new(.382, .847, .236, .081), new(.672, .847, .236, .081)];

    public static void Run()
    {
        var scene = Scene();
        for (int button = 0; button < Regions.Length; button++)
        {
            var tracker = new HandAcquisitionPresenceTracker();
            var hand = Camera(scene, coveredButton: button);
            var first = Feed(tracker, hand, scene, 0);
            Require(first.BaselineReady && first.Hints.Count == 1,
                $"A stationary hand covering Photo Copy button {button} on the first frame was not located: {first.Reason}.");
            var hint = first.Hints[0];
            var point = BoardPosition(hint.Center.X, hint.Center.Y);
            Require(In(point, Regions[button]) && hint.SearchBounds.Width == hint.SearchBounds.Height,
                "The masked control comparison lost camera geometry or stretched the hand search crop.");
            for (int frame = 1; frame <= 15; frame++)
                Require(Feed(tracker, hand, scene, frame * 1000).Hints.Count == 1,
                    "A held hand over a Photo Copy label became part of the baseline.");
            Require(Feed(tracker, Camera(scene), scene, 16000).Hints.Count == 0,
                "Removing the hand from a Photo Copy control left an acquisition ghost.");
        }

        var empty = new HandAcquisitionPresenceTracker();
        for (int frame = 0; frame < 5; frame++)
        {
            var result = Feed(empty, Camera(scene, outsidePhase: frame + 1,
                brightness: frame * 3, gain: 1 - frame * .025, noise: 2), scene, frame * 100);
            Require(result.Hints.Count == 0 && result.BaselineReady,
                "Animated swirl gaps, status text, exposure or noise acquired a false Photo Copy spotlight.");
        }
        Require(Feed(new(), Camera(scene, coveredButton: 1, outsidePhase: 7), scene, 0).Hints.Count == 1,
            "Ignoring animated content also erased a real hand over a static control.");

        // A search light extends beyond the small control mask. Evaluate its entire
        // white core so fingers over the button edge keep the light alive.
        var light = new HandAcquisitionHint(new(240, 300, 160, 160), new(320, 395), 44, Epoch, .05);
        var trackerWithLight = new HandAcquisitionPresenceTracker();
        var litHand = Camera(scene, outsidePhase: 9);
        PaintCircle(litHand, light.Center, light.RadiusPixels, 220, 228, 222);
        PaintRectangle(litHand, 305, 378, 25, 31, 65, 105, 165);
        Require(Feed(trackerWithLight, litHand, scene, 350, light, 100).IlluminatedPresence == true,
            "A motionless hand under the control search light was not retained.");
        var litEmpty = Camera(scene, outsidePhase: 10);
        PaintCircle(litEmpty, light.Center, light.RadiusPixels, 220, 228, 222);
        var removal = Feed(trackerWithLight, litEmpty, scene, 500, light, 100);
        Require(removal.IlluminatedPresence == false && removal.Hints.Count == 0,
            "The empty projected light sustained its own presence over Photo Copy controls.");

        foreach (var invalid in new[]
        {
            scene with { BoardSearchRegions = [] },
            scene with { BoardSearchRegions = [new(double.NaN, .8, .2, .1)] },
            scene with { BoardSearchRegions = [new(.9, .8, .2, .1)] },
            scene with { BoardReferenceRegions = [] },
            scene with { BoardReferenceRegions = [new(.4, double.PositiveInfinity, .3, .1)] },
            scene with { BoardReferenceRegions = [new(.4, .8, .3, -.1)] },
            scene with { CameraToBoard = new double[9] }
        })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            var result = Feed(tracker, Camera(scene), invalid, 0);
            Require(!result.BaselineReady && result.Reason == "invalid-rendered-scene",
                "Invalid restricted scene geometry silently initialized a camera baseline.");
            Require(Feed(tracker, Camera(scene, coveredButton: 0), invalid, 100).Hints.Count == 0,
                "An invalid render fell back to unrelated camera motion.");
        }
        SingleControlIndependentReference(scene);
        DenseControlLettering(scene);
        PerControlAreaFloor(scene);
        Console.WriteLine("Photo Copy control-region regression: stationary first-frame hands on Exit/Clear/Save, " +
            "persistent masks, animated swirl/status exclusion, exposure/noise, square native crops, " +
            "own-light removal, majority-covered single control with separate reference and invalid-template barriers passed.");
    }

    private static void PerControlAreaFloor(HandAcquisitionSceneImage scene)
    {
        // A real hand can leave a broad but shallow band of residuals where its
        // other pixels resemble projected colors. This band is narrower than
        // 3% of the webcam height but occupies a useful part of one control.
        var narrow = Camera(scene);
        PaintRectangle(narrow, 293, 397, 48, 11, 62, 94, 158);
        var result = Feed(new(), narrow, scene, 0);
        Require(result.Hints.Count == 1 && result.Hints[0].ControlCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage,
            "A meaningful narrow residual band was rejected using whole-camera dimensions.");
        Require(result.Hints[0].MotionFraction < HandAcquisitionPresenceTracker.MinimumControlCoverage,
            "The per-control fixture no longer distinguishes a control denominator from the union of all controls.");

        // Both bands exceed the independent minimum sample count and span three
        // analysis rows; only the measured per-control area floor rejects this one.
        var small = Camera(scene);
        PaintRectangle(small, 307, 397, 20, 11, 62, 94, 158);
        Require(Feed(new(), small, scene, 0).Hints.Count == 0,
            "A residual fragment below 7% of its control acquired a spotlight.");
    }

    private static void DenseControlLettering(HandAcquisitionSceneImage original)
    {
        var scene = original with { Bgra = (byte[])original.Bgra.Clone() };
        for (int y = 0; y < SceneSize; y++)
        for (int x = 0; x < SceneSize; x++)
        {
            var point = new PixelPoint(x / (double)(SceneSize - 1), y / (double)(SceneSize - 1));
            if (!In(point, Regions[1]) || x % 6 >= 2) continue;
            int offset = (y * SceneSize + x) * 4;
            scene.Bgra[offset] = scene.Bgra[offset + 1] = scene.Bgra[offset + 2] = 220;
        }
        var hand = Feed(new(), Camera(scene, coveredButton: 1), scene, 0);
        Require(hand.Hints.Count == 1 && In(BoardPosition(hand.Hints[0].Center.X, hand.Hints[0].Center.Y), Regions[1]),
            "Dense lettering edges erased coherent fingers covering a compact control.");
        for (int shift = -2; shift <= 2; shift++)
        {
            var empty = Feed(new(), Camera(scene, shiftX: shift, shiftY: -shift,
                noise: 1, brightness: 8, gain: .93), scene, 0);
            Require(empty.Hints.Count == 0,
                "Dense lettering or small expected-scene raster shifts became false foreground: " +
                $"shift {shift}, fraction {empty.ForegroundFraction:F4}, hints {string.Join(';', empty.Hints.Select(hint => hint.Center))}.");
        }
    }

    private static void SingleControlIndependentReference(HandAcquisitionSceneImage original)
    {
        // Only the left control may acquire a hand. The other static panels and
        // their surrounding known color supply an independent photometric fit.
        var scene = original with
        {
            BoardSearchRegions = [Regions[0]],
            BoardReferenceRegions = [new(.35, .817, .57, .135)]
        };
        var tracker = new HandAcquisitionPresenceTracker();
        var hand = Camera(scene, coveredButton: 0, coverHalfWidth: .106);
        var first = Feed(tracker, hand, scene, 0);
        Require(first.Hints.Count == 1 && first.BaselineReady && first.ForegroundFraction > .50,
            "A single control mostly occluded on the first frame was rejected despite its independent reference: " +
            first.Reason + $" ({first.ForegroundFraction:F3}).");
        Require(In(BoardPosition(first.Hints[0].Center.X, first.Hints[0].Center.Y), Regions[0]),
            "The separate reference panel became an acquisition candidate.");
        for (int frame = 1; frame <= 5; frame++)
        {
            Require(Feed(tracker, hand, scene, frame * 200).Hints.Count == 1,
                "A still hand covering most of a lone Back button was lost.");
            Require(Feed(new(), Camera(scene, brightness: frame * 4, gain: 1 - frame * .035, noise: 2),
                scene, 0).Hints.Count == 0,
                "Independent reference compensation created foreground under common exposure/noise changes.");
        }
        Require(Feed(tracker, Camera(scene), scene, 1200).Hints.Count == 0,
            "A removed hand over a lone control left a foreground ghost.");

        // Even real foreground on another visible control is not selectable here.
        var elsewhere = Feed(new(), Camera(scene, coveredButton: 2), scene, 0);
        Require(elsewhere.Hints.Count == 0,
            "Foreground inside the reference area but outside the candidate control triggered a spotlight.");

        // A fixed header strip can be substantially smaller than the covered
        // button, especially after excluding text edges. Occupancy of the large
        // candidate must not be divided by this small reference's sample count.
        var smallReferenceScene = scene with { BoardReferenceRegions = [new(.39, .905, .50, .018)] };
        var smallReference = new HandAcquisitionPresenceTracker();
        for (int frame = 0; frame < 5; frame++)
        {
            var observation = Feed(smallReference, Camera(smallReferenceScene, coveredButton: 0,
                coverHalfWidth: .106, brightness: frame * 3, gain: 1 - frame * .02, noise: 1),
                smallReferenceScene, frame * 100);
            Require(observation.Hints.Count == 1 && observation.ForegroundFraction > .50,
                "A small clean reference was rejected because the separate control was mostly covered: " +
                observation.Reason + $" ({observation.ForegroundFraction:F3}).");
            Require(Feed(new(), Camera(smallReferenceScene, brightness: frame * 3,
                gain: 1 - frame * .02, noise: 1), smallReferenceScene, 0).Hints.Count == 0,
                "A small clean reference produced foreground on an unoccluded control.");
        }
        Require(Feed(smallReference, Camera(smallReferenceScene), smallReferenceScene, 600).Hints.Count == 0,
            "A small independent reference retained foreground after hand removal.");
    }

    private static HandAcquisitionSceneImage Scene()
    {
        var pixels = new byte[SceneSize * SceneSize * 4];
        for (int y = 0; y < SceneSize; y++)
        for (int x = 0; x < SceneSize; x++)
        {
            double u = x / (double)(SceneSize - 1), v = y / (double)(SceneSize - 1);
            byte b = 98, g = 98, r = 98;
            foreach (var region in Regions)
                if (u >= region.X - .012 && u <= region.X + region.Width + .012 &&
                    v >= .835 && v <= .940)
                {
                    b = (byte)(30 + (v - .835) * 130); g = (byte)(25 + (v - .835) * 100); r = 17;
                    // Repeated narrow label strokes exercise the edge-rejection mask.
                    if (v is > .863 and < .885 && Math.Abs(u - region.X - region.Width / 2) < .045 && x % 6 < 3)
                        b = g = r = 220;
                }
            int offset = (y * SceneSize + x) * 4;
            pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255;
        }
        return new(SceneSize, SceneSize, pixels, Matrix, Regions);
    }

    private static byte[] Camera(HandAcquisitionSceneImage scene, int? coveredButton = null,
        int outsidePhase = 0, double brightness = 0, double gain = 1, int noise = 0, double coverHalfWidth = .038,
        int shiftX = 0, int shiftY = 0)
    {
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var point = BoardPosition(x + shiftX, y + shiftY);
            double tx = Math.Clamp(point.X, 0, 1) * (SceneSize - 1), ty = Math.Clamp(point.Y, 0, 1) * (SceneSize - 1);
            int ix = (int)tx, iy = (int)ty, rx = Math.Min(SceneSize - 1, ix + 1), by = Math.Min(SceneSize - 1, iy + 1);
            double fx = tx - ix, fy = ty - iy;
            for (int channel = 0; channel < 3; channel++)
            {
                double expected = scene.Bgra[(iy * SceneSize + ix) * 4 + channel] * (1 - fx) * (1 - fy) +
                    scene.Bgra[(iy * SceneSize + rx) * 4 + channel] * fx * (1 - fy) +
                    scene.Bgra[(by * SceneSize + ix) * 4 + channel] * (1 - fx) * fy +
                    scene.Bgra[(by * SceneSize + rx) * 4 + channel] * fx * fy;
                double actual = 15 + channel * 5 + .78 * expected + expected * expected * .0007 + x * .006;
                pixels[(y * Width + x) * 4 + channel] = (byte)Math.Clamp(Math.Round(actual * gain + brightness +
                    (noise == 0 ? 0 : (x * 17 + y * 31) % (noise * 2 + 1) - noise)), 0, 255);
            }
            pixels[(y * Width + x) * 4 + 3] = 255;
            if (outsidePhase != 0 && !Regions.Any(region => In(point, region)))
                Set(pixels, x, y, (byte)((x * 7 + outsidePhase * 37) % 256),
                    (byte)((y * 13 + outsidePhase * 61) % 256), (byte)((x + y * 3 + outsidePhase * 19) % 256));
            if (coveredButton is { } button && Math.Abs(point.X - Regions[button].X - Regions[button].Width / 2) < coverHalfWidth &&
                point.Y is > .850 and < .925) Set(pixels, x, y, 62, 94, 158);
        }
        return pixels;
    }

    private static PixelPoint BoardPosition(double x, double y) => new((x - 70) / 500, (y - 40) / 400);
    private static bool In(PixelPoint point, HandTrackingBounds region) => point.X >= region.X &&
        point.X <= region.X + region.Width && point.Y >= region.Y && point.Y <= region.Y + region.Height;
    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels,
        HandAcquisitionSceneImage scene, int milliseconds, HandAcquisitionHint? light = null, int? lightStarted = null) =>
        tracker.Update(Width, Height, Width * 4, pixels, Polygon, scene, Epoch.AddMilliseconds(milliseconds),
            Epoch.AddMilliseconds(milliseconds), light, lightStarted is null ? null : Epoch.AddMilliseconds(lightStarted.Value));
    private static void PaintCircle(byte[] pixels, PixelPoint center, double radius, byte b, byte g, byte r)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            if (Math.Pow(x - center.X, 2) + Math.Pow(y - center.Y, 2) <= radius * radius) Set(pixels, x, y, b, g, r);
    }
    private static void PaintRectangle(byte[] pixels, int left, int top, int width, int height, byte b, byte g, byte r)
    {
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++) Set(pixels, x, y, b, g, r);
    }
    private static void Set(byte[] pixels, int x, int y, byte b, byte g, byte r)
    {
        int offset = (y * Width + x) * 4;
        pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
