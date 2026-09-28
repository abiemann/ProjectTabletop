using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoPrintedSurfaceTwoFaintEdgesRegression
{
    private const int Size = PhotoHandCutout.BoardPixels;
    // Keep fixture camera pixels unchanged while placing the carton above the controls.
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, -.25, 0, 0, 1];
    private static readonly Scalar Grey = new(115, 115, 115, 255), Ink = new(35, 35, 35, 255);
    private static readonly Scalar Faint = new(103, 103, 103, 255);

    public static void Run()
    {
        // Both faint edges are below segmentation contrast, unlike the earlier
        // fixture's darker top seam. The dominant binary component is an L;
        // only the actual camera edges can justify completing its pale face.
        foreach (double angle in new[] { 0.0, 10, 30, 45, 60, 90 })
        foreach (bool nonSquare in new[] { false, true })
        {
            var transform = new Transform(angle, nonSquare ? 1.18 : 1, nonSquare ? .82 : 1);
            using Mat original = Scene(top: true, right: true);
            using Mat camera = transform.Apply(original);
            PhotoObjectTarget? target = Locate(camera, out var failure);
            Require(target is not null && failure is null && target.HasRecoveredSurface,
                $"Two camera-supported faint sides did not complete the printed carton ({transform}): {failure}");
            double expectedArea = 235 * 276 * transform.ScaleX * transform.ScaleY;
            Require(target!.ForegroundArea > expectedArea * .90 && target.ForegroundArea < expectedArea * 1.07,
                "Completing the L-shaped rim lost the blank face or included excessive surrounding board.");
            foreach (var point in new[] { new Point2f(400, 520), new Point2f(400, 640),
                         new Point2f(550, 520), new Point2f(575, 615), new Point2f(560, 685) })
            {
                Point2f mapped = transform.Map(point);
                Require(Sample(target, target.Alpha, mapped) == 255,
                    "The upper/right blank face stayed transparent beyond the L rim's triangular hull.");
                Require(target.ContrastEvidence is not null && Sample(target, target.ContrastEvidence, mapped) == 0,
                    "Completing the pale face invented measured contrast in blank pixels.");
            }
            foreach (var point in new[] { new Point2f(345, 615), new Point2f(611, 615),
                         new Point2f(479, 465), new Point2f(479, 773) })
                Require(Sample(target, target.Alpha, transform.Map(point)) == 0,
                    "Completing the printed face filled exterior board pixels.");
            CheckSpotlight(target.Spotlight, transform);

            foreach (bool missingTop in new[] { false, true })
            {
                using Mat open = Scene(top: !missingTop, right: missingTop);
                using Mat openCamera = transform.Apply(open);
                Require(Locate(openCamera, out failure) is null && failure is not null,
                    $"A missing {(missingTop ? "top" : "right")} side was inferred from the L rim and inner printing ({transform}).");
            }

            if (angle != 0 && angle != 45) continue;
            using Mat two = Scene(top: true, right: true);
            Cv2.Rectangle(two, new Rect(240, 490, 55, 95), Ink, -1);
            using Mat twoCamera = transform.Apply(two);
            Require(Locate(twoCamera, out failure) is null,
                "An unrelated outside object was discarded while completing an L-shaped carton rim.");
            using Mat frame = new(Size, Size, MatType.CV_8UC4, Grey);
            Cv2.Rectangle(frame, new Rect(362, 481, 235, 276), Ink, 14);
            Cv2.Rectangle(frame, new Rect(437, 565, 70, 90), Ink, -1);
            using Mat frameCamera = transform.Apply(frame);
            Require(Locate(frameCamera, out failure) is null,
                "A closed frame and its separate inner object lost their actual opening.");
        }
        Console.WriteLine("Two-faint-edge acquisition regression: L-shaped rims, supported top/right camera edges, " +
            "rotation and non-square mapping, complete opaque face and fitted lights; each missing side, outside objects and closed frames rejected.");
    }

    private static Mat Scene(bool top, bool right)
    {
        Mat scene = new(Size, Size, MatType.CV_8UC4, Grey);
        Cv2.Rectangle(scene, new Rect(362, 481, 18, 276), Ink, -1);
        Cv2.Rectangle(scene, new Rect(362, 712, 235, 45), Ink, -1);
        if (top) Cv2.Line(scene, new Point(380, 481), new Point(596, 481), Faint, 3);
        if (right) Cv2.Line(scene, new Point(596, 481), new Point(596, 712), Faint, 3);
        Cv2.Rectangle(scene, new Rect(437, 514, 91, 10), Ink, -1);
        Cv2.Rectangle(scene, new Rect(458, 530, 66, 10), Ink, -1);
        Cv2.Ellipse(scene, new Point(470, 625), new Size(22, 41), -10, 0, 360, Ink, -1);
        Cv2.Ellipse(scene, new Point(528, 575), new Size(28, 20), -25, 0, 360, Ink, -1);
        Cv2.Line(scene, new Point(475, 590), new Point(517, 579), Ink, 15);
        Cv2.Rectangle(scene, new Rect(461, 680, 43, 11), Ink, -1);
        return scene;
    }

    private static void CheckSpotlight(PhotoObjectSpotlight light, Transform transform)
    {
        Require(light.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
            "The completed rectangular face received circular light.");
        double cosine = Math.Cos(light.RotationRadians), sine = Math.Sin(light.RotationRadians);
        Point2d u = new(cosine, sine), v = new(light.Shear * cosine - sine, light.Shear * sine + cosine);
        Point2d expectedU = transform.Direction(1, 0), expectedV = transform.Direction(0, 1);
        static double Error(Point2d first, Point2d second) => Math.Abs(first.X * second.Y - first.Y * second.X) /
            Math.Sqrt((first.X * first.X + first.Y * first.Y) * (second.X * second.X + second.Y * second.Y));
        Require(Math.Min(Math.Max(Error(u, expectedU), Error(v, expectedV)),
                Math.Max(Error(u, expectedV), Error(v, expectedU))) < .10,
            "The completed spotlight followed the enclosing right-angle rectangle instead of the actual carton edges.");
    }

    private readonly record struct Transform(double Angle, double ScaleX, double ScaleY)
    {
        public Point2d Direction(double x, double y)
        {
            double radians = Angle * Math.PI / 180;
            return new(ScaleX * (x * Math.Cos(radians) - y * Math.Sin(radians)),
                ScaleY * (x * Math.Sin(radians) + y * Math.Cos(radians)));
        }
        public Point2f Map(Point2f point)
        {
            Point2d delta = Direction(point.X - 479, point.Y - 620);
            return new((float)(479 + delta.X), (float)(620 + delta.Y));
        }
        public Mat Apply(Mat scene)
        {
            Point2d u = Direction(1, 0), v = Direction(0, 1);
            using Mat affine = new(2, 3, MatType.CV_64FC1);
            affine.Set(0, 0, u.X); affine.Set(0, 1, v.X);
            affine.Set(1, 0, u.Y); affine.Set(1, 1, v.Y);
            affine.Set(0, 2, 479 - 479 * u.X - 620 * v.X);
            affine.Set(1, 2, 620 - 479 * u.Y - 620 * v.Y);
            Mat result = new();
            Cv2.WarpAffine(scene, result, affine, new Size(Size, Size), InterpolationFlags.Linear,
                BorderTypes.Constant, Grey);
            return result;
        }
    }
    private static PhotoObjectTarget? Locate(Mat scene, out string? failure)
    {
        byte[] photo = new byte[Size * Size * 4]; Marshal.Copy(scene.Data, photo, 0, photo.Length);
        return PhotoObjectLocator.Locate(Size, Size, Size * 4, photo, BoardMap, out failure);
    }
    private static byte Sample(PhotoObjectTarget target, IReadOnlyList<byte> values, Point2f point)
    {
        int x = (int)Math.Round(point.X) - target.Left, y = (int)Math.Round(point.Y) - 250 - target.Top;
        return x < 0 || y < 0 || x >= target.Width || y >= target.Height ? (byte)0 : values[y * target.Width + x];
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
