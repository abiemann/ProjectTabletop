using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoSingleComponentBoxRegression
{
    private const int CameraWidth = 800;
    private static readonly Scalar Grey = new(115, 115, 115, 255), Ink = new(35, 35, 35, 255);

    public static void Run()
    {
        foreach (int height in new[] { 800, 640 })
        foreach (double angle in new[] { 0.0, 27, 71, 118 })
        foreach (bool twoBevels in new[] { false, true })
        {
            double[] map = Mapping(height);
            using Mat photo = Scene(height, angle, twoBevels);
            PhotoObjectTarget target = Locate(photo, map);
            Require(target.HasRecoveredSurface && target.ContrastEvidence is not null,
                $"The connected C-shaped printing lost its camera-supported pale face ({height}px, {angle} degrees, two bevels {twoBevels}).");
            foreach (Point point in new[] { new Point(370, 350), new Point(450, 350),
                         new Point(450, 430), new Point(360, 440) })
            {
                Point2f camera = Rotate(point, angle);
                Require(Alpha(target, camera, height) == 255,
                    "A blank part of the clipped box face was left transparent.");
                Require(Sample(target, target.ContrastEvidence!, camera, height) == 0,
                    "Completing a connected pale face invented contrast in its blank pixels.");
            }
            Require(Alpha(target, Rotate(new Point(400, 285), angle), height) == 0 &&
                    Alpha(target, Rotate(new Point(535, 400), angle), height) == 0,
                "Recovering the actual clipped face included exterior board pixels.");
            Require(target.Spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
                "A recovered clipped rectangular box received circular light.");
            CheckCoverage(target);
            CheckNativeCopy(target, height, angle, twoBevels, map);

            // All dark printing belongs to one open component in both cases.
            // Only the positive frame photographs an edge across the gap.
            using Mat open = Scene(height, angle, twoBevels, missingEdge: true);
            PhotoObjectTarget openTarget = Locate(open, map);
            Require(!openTarget.HasRecoveredSurface && Alpha(openTarget, Rotate(new Point(450, 350), angle), height) == 0,
                "A genuine open C-shaped object had its missing face manufactured from the convex hull.");
        }

        foreach (int height in new[] { 800, 640 })
        {
            using Mat frame = Scene(height, 27, false, frame: true);
            PhotoObjectTarget frameTarget = Locate(frame, Mapping(height));
            Require(!frameTarget.HasRecoveredSurface && Alpha(frameTarget, Rotate(new Point(450, 350), 27), height) == 0,
                "A closed frame lost its real transparent opening during single-component recovery.");
            foreach (string shape in new[] { "circle", "ellipse", "hexagon", "concave" })
            {
                using Mat irregular = Irregular(height, shape);
                Require(Locate(irregular, Mapping(height)).Spotlight.Shape == PhotoObjectSpotlightShape.Circle,
                    $"The clipped-box fit forced a {shape} into a rectangular spotlight.");
            }
        }
        Console.WriteLine("Single-component box regression: camera-backed clipped five/six-edge faces, " +
            "rotation and non-square mapping, complete alpha and native RGB/proportions; open C shapes, frame holes and irregular-shape safeguards passed.");
    }

    private static Point[] Outline(bool twoBevels) => twoBevels
        ? [new(360, 310), new(510, 310), new(510, 450), new(470, 490), new(290, 490), new(290, 380)]
        : [new(360, 310), new(510, 310), new(510, 490), new(290, 490), new(290, 380)];

    private static Mat Scene(int height, double angle, bool twoBevels, bool missingEdge = false,
        bool frame = false, bool lit = false)
    {
        Scalar background = lit ? new(245, 245, 245, 255) : Grey;
        Scalar dark = lit ? new(65, 76, 92, 255) : Ink;
        using Mat original = new(height, CameraWidth, MatType.CV_8UC4, background);
        using Mat mask = new(height, CameraWidth, MatType.CV_8UC1, Scalar.Black);
        Cv2.FillConvexPoly(mask, Outline(twoBevels), Scalar.White);
        using Mat inside = new(), rim = new();
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(29, 29));
        Cv2.Erode(mask, inside, kernel);
        Cv2.Subtract(mask, inside, rim);
        original.SetTo(dark, rim);
        if (!frame)
        {
            Cv2.Rectangle(original, new Rect(383, 302, 107, 30), background, -1);
            if (!missingEdge)
                Cv2.Line(original, new Point(383, 310), new Point(490, 310),
                    lit ? new Scalar(220, 220, 220, 255) : new Scalar(103, 103, 103, 255), 3);
        }
        Cv2.Line(original, new Point(400, 482), new Point(400, 385), dark, 4);
        Cv2.Rectangle(original, new Rect(380, 365, 40, 24), dark, -1);
        Cv2.Rectangle(original, new Rect(399, 420, 40, 18), dark, -1);
        using Mat rotation = Cv2.GetRotationMatrix2D(new(400, 400), angle, 1);
        Mat result = new();
        Cv2.WarpAffine(original, result, rotation, new Size(CameraWidth, height), InterpolationFlags.Linear,
            BorderTypes.Constant, background);
        return result;
    }

    private static void CheckNativeCopy(PhotoObjectTarget target, int height, double angle,
        bool twoBevels, double[] map)
    {
        using Mat lit = Scene(height, angle, twoBevels, lit: true);
        byte[] photo = Bytes(lit);
        PhotoHandCutout? segmented = PhotoObjectExtractor.ExtractTarget(CameraWidth, height, CameraWidth * 4,
            photo, null, map, target, out var failure);
        Require(segmented is not null, "The recovered current face could not be verified for capture: " + failure);
        PhotoHandCutout? native = PhotoCopyCameraImage.Capture(CameraWidth, height, CameraWidth * 4,
            photo, map, segmented!);
        Require(native is not null && native.CameraGeometry is not null && native.BoardOrigin is null,
            "The recovered photograph was not converted to native camera geometry.");
        double cameraAnchorX = (target.Left + segmented!.PalmAnchor.X) * CameraWidth / 1000.0;
        double cameraAnchorY = (target.Top + segmented.PalmAnchor.Y + 250) * height / 1000.0;
        int left = (int)Math.Round(cameraAnchorX - native!.PalmAnchor.X);
        int top = (int)Math.Round(cameraAnchorY - native.PalmAnchor.Y);
        Point2f[] outline = Outline(twoBevels).Select(point => Rotate(point, angle)).ToArray();
        double width = outline.Max(point => point.X) - outline.Min(point => point.X);
        double actualHeight = outline.Max(point => point.Y) - outline.Min(point => point.Y);
        Require(Math.Abs(native.Width - width) < 13 && Math.Abs(native.Height - actualHeight) < 13,
            "The saved photograph retained square-board stretching instead of the camera's proportions.");
        for (int y = 0; y < native.Height; y++)
        for (int x = 0; x < native.Width; x++)
        {
            int index = (y * native.Width + x) * 4;
            if (native.BgraPixels[index + 3] == 0)
            {
                Require(native.BgraPixels.AsSpan(index, 3).SequenceEqual(new byte[3]),
                    "Transparent copied pixels retained background RGB.");
                continue;
            }
            int source = ((top + y) * CameraWidth + left + x) * 4;
            Require(native.BgraPixels.AsSpan(index, 3).SequenceEqual(photo.AsSpan(source, 3)),
                "The native photograph's RGB was resized, replaced or recolored.");
        }
    }

    private static Mat Irregular(int height, string shape)
    {
        Mat result = new(height, CameraWidth, MatType.CV_8UC4, Grey);
        switch (shape)
        {
            case "circle": Cv2.Circle(result, new Point(400, 400), 90, Ink, -1); break;
            case "ellipse": Cv2.Ellipse(result, new Point(400, 400), new Size(120, 65), 27, 0, 360, Ink, -1); break;
            case "hexagon":
                Cv2.FillConvexPoly(result, Enumerable.Range(0, 6).Select(i => new Point(
                    400 + (int)Math.Round(110 * Math.Cos(i * Math.PI / 3)),
                    400 + (int)Math.Round(110 * Math.Sin(i * Math.PI / 3)))).ToArray(), Ink);
                break;
            default:
                Cv2.FillPoly(result, [[new(290, 310), new(360, 310), new(360, 420),
                    new(510, 420), new(510, 490), new(290, 490)]], Ink);
                break;
        }
        return result;
    }

    private static void CheckCoverage(PhotoObjectTarget target)
    {
        PhotoObjectSpotlight light = target.Spotlight;
        for (int y = 0; y < target.Height; y += 3)
        for (int x = 0; x < target.Width; x += 3)
        {
            if (target.Alpha[y * target.Width + x] == 0) continue;
            for (int i = 0; i < 8; i++)
            {
                double dx = target.Left + x + 20 * Math.Cos(i * Math.PI / 4) - light.Center.X;
                double dy = target.Top + y + 20 * Math.Sin(i * Math.PI / 4) - light.Center.Y;
                double v = -dx * Math.Sin(light.RotationRadians) + dy * Math.Cos(light.RotationRadians);
                double u = dx * Math.Cos(light.RotationRadians) + dy * Math.Sin(light.RotationRadians) - light.Shear * v;
                double qx = Math.Abs(u) - light.Width / 2 + light.CornerRadius;
                double qy = Math.Abs(v) - light.Height / 2 + light.CornerRadius;
                Require(Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) +
                        Math.Min(Math.Max(qx, qy), 0) <= light.CornerRadius,
                    "The clipped rectangular light trimmed recovered alpha or its white verification margin.");
            }
        }
    }

    private static Point2f Rotate(Point point, double angle)
    {
        double radians = angle * Math.PI / 180, x = point.X - 400, y = point.Y - 400;
        return new((float)(400 + x * Math.Cos(radians) + y * Math.Sin(radians)),
            (float)(400 - x * Math.Sin(radians) + y * Math.Cos(radians)));
    }
    // Calibration shifts the existing camera fixture into the upper capture field.
    private static double[] Mapping(int height) => [1.0 / CameraWidth, 0, 0, 0, 1.0 / height, -.25, 0, 0, 1];
    private static byte Alpha(PhotoObjectTarget target, Point2f camera, int height) => Sample(target, target.Alpha, camera, height);
    private static byte Sample(PhotoObjectTarget target, IReadOnlyList<byte> values, Point2f camera, int height)
    {
        int x = (int)Math.Round(camera.X * 1000 / CameraWidth) - target.Left;
        int y = (int)Math.Round(camera.Y * 1000 / height) - 250 - target.Top;
        return x < 0 || y < 0 || x >= target.Width || y >= target.Height ? (byte)0 : values[y * target.Width + x];
    }
    private static PhotoObjectTarget Locate(Mat scene, double[] map) =>
        PhotoObjectLocator.Locate(scene.Width, scene.Height, scene.Width * 4, Bytes(scene), map, out var failure) ??
        throw new InvalidOperationException("Single-component fixture could not acquire its visible object: " + failure);
    private static byte[] Bytes(Mat photo)
    {
        byte[] result = new byte[photo.Width * photo.Height * 4]; Marshal.Copy(photo.Data, result, 0, result.Length); return result;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
