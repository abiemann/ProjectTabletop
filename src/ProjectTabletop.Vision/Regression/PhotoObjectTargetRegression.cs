using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoObjectTargetRegression
{
    private const int Size = 500;
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, 0, 0, 0, 1];
    private static readonly Rect Subject = new(90, 185, 140, 150);
    private static readonly Scalar ObjectColor = new(40, 110, 170, 255);
    private static readonly HandDetection Shutter = CreateShutter();

    public static void Run()
    {
        foreach (var color in new[] { new Scalar(20, 20, 20, 255), new Scalar(65, 65, 65, 255),
            new Scalar(220, 220, 220, 255), ObjectColor })
        {
            using Mat grey = Scene(); DrawSubject(grey, color);
            var target = Locate(grey);
            Require(target.Width is > 275 and < 295 && target.Height is > 295 and < 315,
                "Grey acquisition lost a dark, light, neutral or colored object.");
            Require(AlphaAt(target, 120, 220) == 255 && AlphaAt(target, 160, 260) == 0,
                "Grey acquisition filled a real hole or lost the object's interior.");
            double outlineRadius = Math.Sqrt(target.Width * target.Width + target.Height * target.Height) / 2;
            Require(target.SpotlightRadius >= outlineRadius + 24 && target.SpotlightRadius <= outlineRadius + 30,
                "The object spotlight must cover its outline and local sampling band without excess spill.");
            Require(target.Alpha is not byte[], "The locked alpha exposes its mutable backing array.");
        }
        CheckCurrentPixelsAndPresence();
        CheckRejectionsAndPadding();
        CheckPerspectiveAndGradient();
        CheckGlareAndSmallObjects();
        CheckCaptureBoundary();
        CheckSpotlightShape();
        Console.WriteLine("Photo object target regression: grey dark/light/color acquisition, holes and immutable mask, " +
            "current illuminated pixels, stationary/moved/removed/occluded checks, ambiguous/edge/invalid rejection, " +
            "perspective, gradients, local glare without discarding small real objects, padded rows, " +
            "rotated rectangular lights with protrusion coverage and circular/irregular fallbacks passed.");
    }

    private static void CheckCurrentPixelsAndPresence()
    {
        using Mat grey = Scene(); DrawSubject(grey, ObjectColor);
        var target = Locate(grey);
        using Mat lit = LitScene(target);
        Scalar currentColor = new(70, 150, 205, 255);
        DrawSubject(lit, currentColor, lit: true);
        Require(Observe(lit, target) == PhotoObjectTargetState.Present,
            "A stationary object under its own white light was rejected.");
        var cutout = PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, Bytes(lit), Shutter,
            BoardMap, target, out var failure);
        Require(cutout is not null, "Current target capture failed: " + failure);
        Require(cutout!.Width == target.Width && cutout.Height == target.Height, "Target capture changed its locked geometry.");
        for (int index = 0; index < target.Alpha.Count; index++)
        {
            Require(cutout.BgraPixels[index * 4 + 3] == target.Alpha[index], "White illumination changed the acquired alpha.");
            if (target.Alpha[index] == 0)
                Require(cutout.BgraPixels[index * 4] == 0 && cutout.BgraPixels[index * 4 + 1] == 0 &&
                    cutout.BgraPixels[index * 4 + 2] == 0, "Exterior pixels retained photographed background.");
        }
        int sample = (((220 * 2 - target.Top) * target.Width) + 120 * 2 - target.Left) * 4;
        Require(cutout.BgraPixels[sample] == 70 && cutout.BgraPixels[sample + 1] == 150 &&
            cutout.BgraPixels[sample + 2] == 205, "Capture reused acquisition RGB instead of current photographed pixels.");
        Require(AlphaAt(target, 160, 260) == 0, "The current spotlight filled the stored hole.");

        using Mat removed = LitScene(target);
        Require(Observe(removed, target) == PhotoObjectTargetState.MissingOrMoved,
            "The projected light alone kept a removed object locked.");
        using Mat moved = LitScene(target); DrawSubject(moved, currentColor, lit: true, offsetX: 20);
        var movedState = PhotoObjectLocator.ObserveTarget(Size, Size, Size * 4, Bytes(moved), BoardMap, target, out var movedFailure);
        Require(movedState == PhotoObjectTargetState.MissingOrMoved,
            "A shifted object passed its old boundary check: " + movedState + "; " + movedFailure);
        Require(PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, Bytes(moved), Shutter,
            BoardMap, target, out failure) is null && failure is not null,
            "A moved object's old silhouette was used to photograph background.");
        var overlap = Shutter with { Landmarks = Shutter.Landmarks.Select(p => new PixelPoint(p.X - 230, p.Y)).ToArray() };
        Require(Observe(lit, target, [overlap]) == PhotoObjectTargetState.Occluded,
            "A shutter hand over the target was accepted.");
        Require(PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, Bytes(lit), Shutter,
            BoardMap, target, out failure, [overlap]) is null, "An overlapping second hand was ignored.");
        using Mat noContrast = LitScene(target); DrawSubject(noContrast, new Scalar(235, 235, 235, 255), lit: true);
        Require(Observe(noContrast, target) == PhotoObjectTargetState.MissingOrMoved,
            "An unverifiable white-on-white subject was silently accepted.");
    }

    private static void CheckRejectionsAndPadding()
    {
        using Mat empty = Scene();
        Require(PhotoObjectLocator.Locate(Size, Size, Size * 4, Bytes(empty), BoardMap, out var reason) is null && reason is not null,
            "An empty grey field acquired a subject.");
        using Mat two = Scene(); DrawSubject(two, ObjectColor);
        Cv2.Rectangle(two, new Rect(300, 190, 60, 70), new Scalar(25, 25, 25, 255), -1);
        Require(PhotoObjectLocator.Locate(Size, Size, Size * 4, Bytes(two), BoardMap, out reason) is null,
            "Ambiguous objects were silently reduced to one.");
        using Mat edge = Scene(); Cv2.Rectangle(edge, new Rect(5, 180, 100, 80), ObjectColor, -1);
        Require(PhotoObjectLocator.Locate(Size, Size, Size * 4, Bytes(edge), BoardMap, out reason) is null,
            "An edge-clipped subject was locked.");
        using Mat scene = Scene(); DrawSubject(scene, ObjectColor);
        var target = Locate(scene); byte[] pixels = Bytes(scene);
        int stride = Size * 4 + 37;
        byte[] padded = new byte[stride * Size];
        for (int y = 0; y < Size; y++) Array.Copy(pixels, y * Size * 4, padded, y * stride, Size * 4);
        var paddedTarget = PhotoObjectLocator.Locate(Size, Size, stride, padded, BoardMap, out reason);
        Require(paddedTarget is not null && paddedTarget.Alpha.SequenceEqual(target.Alpha) && paddedTarget.Center == target.Center,
            "Camera row padding changed the locked geometry.");
        foreach (double[] map in new[] { new double[9], new double[8], new[] { double.NaN, 0, 0, 0, 1, 0, 0, 0, 1 } })
            Require(PhotoObjectLocator.Locate(Size, Size, Size * 4, pixels, map, out reason) is null,
                "Invalid calibration was accepted.");
        try { PhotoObjectLocator.Locate(Size, Size, Size * 4, pixels[..^1], BoardMap, out _); }
        catch (ArgumentException) { return; }
        throw new Exception("A truncated camera buffer was accepted.");
    }

    private static void CheckPerspectiveAndGradient()
    {
        using Mat scene = Scene(gradient: true); DrawSubject(scene, ObjectColor, gradient: true);
        var original = Locate(scene);
        Point2f[] corners = [new(0, 0), new(Size, 0), new(Size, Size), new(0, Size)];
        Point2f[] observed = [new(50, 20), new(470, 50), new(440, 480), new(30, 450)];
        using Mat forward = Cv2.GetPerspectiveTransform(corners, observed);
        using Mat inverse = Cv2.GetPerspectiveTransform(observed, [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        using Mat camera = new();
        Cv2.WarpPerspective(scene, camera, forward, new Size(Size, Size), InterpolationFlags.Linear,
            BorderTypes.Constant, new Scalar(110, 110, 110, 255));
        double[] map = Enumerable.Range(0, 9).Select(i => inverse.At<double>(i / 3, i % 3)).ToArray();
        var target = PhotoObjectLocator.Locate(Size, Size, Size * 4, Bytes(camera), map, out var failure);
        Require(target is not null && Math.Abs(target.Center.X - original.Center.X) < 3 &&
            Math.Abs(target.Center.Y - original.Center.Y) < 3 && AlphaAt(target, 160, 260) == 0,
            "Perspective or a gentle grey gradient changed the subject: " + failure);
        using Mat lit = LitScene(original, gradient: true); DrawSubject(lit, ObjectColor, lit: true, gradient: true);
        Require(Observe(lit, original) == PhotoObjectTargetState.Present,
            "A gentle gradient across the illuminated margin prevented presence verification.");
    }

    private static void CheckGlareAndSmallObjects()
    {
        // A saturated lavender projector field with a broad brighter patch,
        // modelled after the live false-positive pattern without storing a photo.
        using Mat empty = Scene();
        for (int y = 115; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double glow = 29 * Math.Exp(-.5 * (Math.Pow((x - 320) / 28.0, 2) + Math.Pow((y - 210) / 42.0, 2)));
                double noise = ((x * 13 + y * 7) % 7 - 3) * .5;
                empty.Set(y, x, new Vec4b(254, (byte)(190 + glow + noise), (byte)(212 + glow + noise), 255));
            }
        Require(PhotoObjectLocator.Locate(Size, Size, Size * 4, Bytes(empty), BoardMap, out _) is null,
            "A broad illumination hotspot on an empty board became an object.");
        using Mat book = empty.Clone();
        Cv2.Rectangle(book, Subject, ObjectColor, -1);
        var target = Locate(book);
        Require(target.Width is > 275 and < 295 && target.Height is > 295 and < 315,
            "Local illumination correction distorted a contrasting subject.");
        using Mat multiple = book.Clone();
        // Similar area to either live false glare component, but a real abrupt
        // boundary and color contrast: size alone must never discard this object.
        Cv2.Rectangle(multiple, new Rect(355, 235, 17, 18), new Scalar(230, 175, 190, 255), -1);
        var ambiguous = PhotoObjectLocator.Locate(Size, Size, Size * 4, Bytes(multiple), BoardMap, out var failure);
        Require(ambiguous is null && failure?.Contains("one object") == true,
            "A small real second object near the glare was discarded as illumination.");
        using Mat lowContrast = Scene();
        Cv2.Rectangle(lowContrast, new Rect(300, 340, 25, 25), new Scalar(130, 130, 130, 255), -1);
        var small = Locate(lowContrast);
        Require(small.Width is > 45 and < 65 && small.Height is > 45 and < 65,
            "Background smoothing erased a small subject above the contrast threshold.");
    }

    private static void CheckCaptureBoundary()
    {
        foreach (Rect bounds in new[] { new Rect(180, 415, 140, 75), new Rect(8, 200, 60, 70) })
        {
            using Mat scene = Scene(); Cv2.Rectangle(scene, bounds, ObjectColor, -1);
            var target = Locate(scene);
            using Mat lit = LitScene(target); Cv2.Rectangle(lit, bounds, ObjectColor, -1);
            Require(Observe(lit, target) == PhotoObjectTargetState.Present,
                "An object wholly inside the one-percent guard could not be acquired and verified.");
        }
        using Mat clipped = Scene();
        Cv2.Rectangle(clipped, new Rect(180, 470, 80, 45), ObjectColor, -1);
        Require(PhotoObjectLocator.Locate(Size, Size, Size * 4, Bytes(clipped), BoardMap, out var failure) is null &&
            failure?.Contains("edges") == true,
            "Expanding the capture area accepted an object clipped by the physical board edge.");
    }

    private static void CheckSpotlightShape()
    {
        using Mat rectangle = Scene(); DrawSubject(rectangle, ObjectColor);
        var straight = Locate(rectangle);
        Require(straight.Spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
            "A rectangular subject with a real hole did not receive rectangular light.");
        CheckSpotlightCoverage(straight);

        using Mat rotated = Scene(); DrawRotatedBook(rotated);
        var book = Locate(rotated);
        Require(book.Spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle &&
            Math.Abs(Math.Sin(2 * (book.Spotlight.RotationRadians - 27 * Math.PI / 180))) < .1,
            "The rectangular spotlight did not follow the body's rotation through its narrow protrusions.");
        Require(book.Spotlight.Width * book.Spotlight.Height < Math.PI * book.SpotlightRadius * book.SpotlightRadius * .9,
            "The rectangle did not reduce the circular light's excess footprint.");
        CheckSpotlightCoverage(book);
        using Mat illuminated = LitScene(book); DrawRotatedBook(illuminated);
        Require(Observe(illuminated, book) == PhotoObjectTargetState.Present,
            "A rotated book and its protrusions failed verification in the actual rectangular light.");
        Require(PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, Bytes(illuminated), Shutter,
            BoardMap, book, out _) is not null, "Rectangular illumination prevented current-frame capture.");
        using Mat moved = LitScene(book); DrawRotatedBook(moved, offsetX: 20);
        Require(Observe(moved, book) == PhotoObjectTargetState.MissingOrMoved,
            "A rotated object moved beyond its stored silhouette but was accepted.");

        using Mat disk = Scene(); Cv2.Circle(disk, new Point(210, 290), 60, ObjectColor, -1);
        var round = Locate(disk);
        Require(round.Spotlight.Shape == PhotoObjectSpotlightShape.Circle,
            "A circular object was classified as rectangular from occupancy alone.");
        CheckSpotlightCoverage(round);
        using Mat irregular = Scene();
        Cv2.Rectangle(irregular, new Rect(120, 200, 50, 145), ObjectColor, -1);
        Cv2.Rectangle(irregular, new Rect(120, 300, 140, 45), ObjectColor, -1);
        var bent = Locate(irregular);
        Require(bent.Spotlight.Shape == PhotoObjectSpotlightShape.Circle,
            "An irregular concave subject was forced into a rectangular classification.");
        CheckSpotlightCoverage(bent);
    }

    private static void DrawRotatedBook(Mat scene, int offsetX = 0)
    {
        double angle = 27 * Math.PI / 180, cosine = Math.Cos(angle), sine = Math.Sin(angle);
        Point P(double x, double y) => new((int)Math.Round(200 + offsetX + x * cosine - y * sine),
            (int)Math.Round(295 + x * sine + y * cosine));
        Cv2.FillConvexPoly(scene, [P(-60, -42), P(60, -42), P(60, 42), P(-60, 42)], ObjectColor);
        Cv2.FillConvexPoly(scene, [P(-79, -31), P(-59, -31), P(-59, -24), P(-79, -24)], ObjectColor);
        Cv2.Line(scene, P(10, 41), P(26, 57), ObjectColor, 3);
        Cv2.Line(scene, P(26, 57), P(42, 41), ObjectColor, 3);
    }

    private static void CheckSpotlightCoverage(PhotoObjectTarget target)
    {
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
            {
                if (target.Alpha[y * target.Width + x] == 0) continue;
                double bx = target.Left + x, by = target.Top + y;
                Require(InsideSpotlight(target.Spotlight, bx, by),
                    "Shape classification trimmed a photographed protrusion from the white core.");
                if (x % 9 != 0 || y % 9 != 0) continue;
                for (int angle = 0; angle < 8; angle++)
                    Require(InsideSpotlight(target.Spotlight, bx + 20 * Math.Cos(angle * Math.PI / 4),
                        by + 20 * Math.Sin(angle * Math.PI / 4)),
                        "The rectangular white core leaves part of the verification sampling band unlit.");
            }
    }

    private static bool InsideSpotlight(PhotoObjectSpotlight spotlight, double x, double y)
    {
        double dx = x - spotlight.Center.X, dy = y - spotlight.Center.Y;
        if (spotlight.Shape == PhotoObjectSpotlightShape.Circle)
            return dx * dx + dy * dy <= spotlight.Width * spotlight.Width / 4;
        double cosine = Math.Cos(spotlight.RotationRadians), sine = Math.Sin(spotlight.RotationRadians);
        double u = Math.Abs(dx * cosine + dy * sine), v = Math.Abs(-dx * sine + dy * cosine);
        double qx = u - (spotlight.Width / 2 - spotlight.CornerRadius);
        double qy = v - (spotlight.Height / 2 - spotlight.CornerRadius);
        return Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) +
            Math.Min(Math.Max(qx, qy), 0) <= spotlight.CornerRadius;
    }

    private static PhotoObjectTarget Locate(Mat scene) => PhotoObjectLocator.Locate(Size, Size, Size * 4,
        Bytes(scene), BoardMap, out var failure) ?? throw new Exception("Grey object acquisition failed: " + failure);
    private static PhotoObjectTargetState Observe(Mat scene, PhotoObjectTarget target, IReadOnlyList<HandDetection>? hands = null) =>
        PhotoObjectLocator.ObserveTarget(Size, Size, Size * 4, Bytes(scene), BoardMap, target, out _, hands);
    private static int AlphaAt(PhotoObjectTarget target, int x, int y)
    {
        int tx = x * 2 - target.Left, ty = y * 2 - target.Top;
        return tx < 0 || ty < 0 || tx >= target.Width || ty >= target.Height ? 0 : target.Alpha[ty * target.Width + tx];
    }
    private static Mat Scene(bool gradient = false)
    {
        Mat result = new(Size, Size, MatType.CV_8UC4);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++) result.Set(y, x, Background(x, y, false, gradient));
        Cv2.Rectangle(result, new Rect(0, 0, Size, 105), new Scalar(25, 55, 35, 255), -1);
        return result;
    }
    private static Mat LitScene(PhotoObjectTarget target, bool gradient = false)
    {
        Mat result = Scene(gradient);
        for (int y = PhotoObjectTarget.CaptureTop / 2; y < PhotoObjectTarget.CaptureBottom / 2; y++)
            for (int x = PhotoObjectTarget.CaptureLeft / 2; x < PhotoObjectTarget.CaptureRight / 2; x++)
                if (InsideSpotlight(target.Spotlight, x * 2, y * 2))
                    result.Set(y, x, Background(x, y, true, gradient));
        return result;
    }
    private static void DrawSubject(Mat scene, Scalar color, bool lit = false, bool gradient = false, int offsetX = 0)
    {
        Cv2.Rectangle(scene, new Rect(Subject.X + offsetX, Subject.Y, Subject.Width, Subject.Height), color, -1);
        for (int y = 236; y <= 284; y++)
            for (int x = 136; x <= 184; x++)
                if ((x - 160) * (x - 160) + (y - 260) * (y - 260) <= 24 * 24)
                    scene.Set(y, x + offsetX, Background(x + offsetX, y, lit, gradient));
    }
    private static Vec4b Background(int x, int y, bool lit, bool gradient)
    {
        int value = (lit ? 235 : 110) + (gradient ? (int)(10.0 * x / Size + 6.0 * y / Size) : 0);
        return new((byte)value, (byte)value, (byte)value, 255);
    }
    private static HandDetection CreateShutter()
    {
        PixelPoint[] points = [new(427, 337), new(412, 328), new(400, 318), new(389, 308), new(410, 270),
            new(412, 304), new(410, 283), new(409, 270), new(410, 270), new(427, 299), new(427, 276),
            new(427, 259), new(427, 246), new(440, 302), new(442, 283), new(443, 269), new(443, 258),
            new(448, 308), new(455, 295), new(458, 284), new(460, 274)];
        return new(points, .99, .5);
    }
    private static byte[] Bytes(Mat scene)
    {
        byte[] result = new byte[Size * Size * 4]; Marshal.Copy(scene.Data, result, 0, result.Length); return result;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
