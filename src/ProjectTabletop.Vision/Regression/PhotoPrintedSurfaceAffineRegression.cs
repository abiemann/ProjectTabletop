using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoPrintedSurfaceAffineRegression
{
    private const int Size = PhotoHandCutout.BoardPixels;
    // Keep fixture camera pixels unchanged while placing the carton above the controls.
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, -.25, 0, 0, 1];
    private static readonly Scalar Grey = new(115, 115, 115, 255);
    private static readonly Scalar Ink = new(35, 35, 35, 255);
    private static readonly Point2d Center = new(479, 620);

    public static void Run()
    {
        // A physical rectangle on a non-square board is an oblique
        // parallelogram in normalized board coordinates after rotation.
        foreach (var transform in new[] { new Affine(45, 1.25, .78),
                     new Affine(-35, 1.25, .78), new Affine(28, 1.18, .84, .12) })
        {
            Point2f[] corners = [transform.Map(362, 481), transform.Map(596, 481),
                transform.Map(596, 756), transform.Map(362, 756)];
            RotatedRect enclosing = Cv2.MinAreaRect(corners);
            double polygonArea = Math.Abs(Cv2.ContourArea(corners));
            Require(polygonArea / (enclosing.Size.Width * enclosing.Size.Height) < .88,
                "The affine fixture no longer exercises the old right-angle rectangle gate.");

            using Mat plain = Scene(faintClosure: true);
            using Mat camera = transform.Apply(plain);
            PhotoObjectTarget? target = Locate(camera, out var failure);
            Require(target is not null && target.HasRecoveredSurface && failure is null,
                $"A printed carton on an anisotropically normalized board was rejected ({transform}): {failure}");
            Require(target!.ForegroundArea > polygonArea * .95 && target.ForegroundArea < polygonArea * 1.04,
                "Affine surface recovery lost its face or filled its enclosing rectangle instead of its actual outline.");

            // Blank face, including close to the weak edge, must remain opaque
            // even though its color exactly matches the surrounding field.
            foreach (var sample in new[] { (400.0, 520.0), (400.0, 640.0),
                         (550.0, 520.0), (585.0, 605.0), (580.0, 690.0) })
            {
                Point2f point = transform.Map(sample.Item1, sample.Item2);
                Require(Alpha(target, point) == 255,
                    "An affine carton's blank or near-edge face became transparent.");
                Require(Evidence(target, point) == 0,
                    "Recovering the face invented contrast evidence in its blank region.");
            }
            foreach (var sample in new[] { (350.0, 615.0), (607.0, 615.0),
                         (479.0, 469.0), (479.0, 768.0) })
                Require(Alpha(target, transform.Map(sample.Item1, sample.Item2)) == 0,
                    "An affine carton's silhouette retained pixels outside an actual edge.");

            CheckSpotlightAxes(target.Spotlight, transform);

            using Mat open = Scene(faintClosure: false);
            using Mat openCamera = transform.Apply(open);
            Require(Locate(openCamera, out failure) is null && failure is not null,
                "An affine open U/C shape was filled without camera evidence for its fourth edge.");
        }
        Console.WriteLine("Printed-surface affine regression: rotated boxes under non-square board normalization " +
            "and mild shear, complete face alpha, accurate sheared rectangular lights and missing-edge rejection passed.");
    }

    private static void CheckSpotlightAxes(PhotoObjectSpotlight spotlight, Affine transform)
    {
        Require(spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
            "An affine rectangular carton was illuminated with a circle.");
        double cosine = Math.Cos(spotlight.RotationRadians), sine = Math.Sin(spotlight.RotationRadians);
        Point2d lightU = new(cosine, sine);
        Point2d lightV = new(spotlight.Shear * cosine - sine, spotlight.Shear * sine + cosine);
        Point2d trueU = transform.Direction(1, 0), trueV = transform.Direction(0, 1);
        bool sameOrder = Parallel(lightU, trueU) && Parallel(lightV, trueV);
        bool exchanged = Parallel(lightU, trueV) && Parallel(lightV, trueU);
        Require(sameOrder || exchanged,
            "The spotlight used a right-angle bounding box instead of the carton's observed oblique edges.");
        Require(Math.Abs(spotlight.Shear) > .10,
            "The non-square board fixture lost its required spotlight shear.");
    }

    private static bool Parallel(Point2d a, Point2d b) =>
        Math.Abs(a.X * b.X + a.Y * b.Y) /
        Math.Sqrt((a.X * a.X + a.Y * a.Y) * (b.X * b.X + b.Y * b.Y)) > .99;

    private static Mat Scene(bool faintClosure)
    {
        Mat scene = new(Size, Size, MatType.CV_8UC4, Grey);
        Cv2.Rectangle(scene, new Rect(362, 481, 18, 276), Ink, -1);
        Cv2.Rectangle(scene, new Rect(362, 481, 235, 11), Ink, -1);
        Cv2.Rectangle(scene, new Rect(362, 712, 232, 45), Ink, -1);
        if (faintClosure)
            Cv2.Line(scene, new Point(594, 492), new Point(594, 712), new Scalar(103, 103, 103, 255), 2);
        Cv2.Rectangle(scene, new Rect(437, 514, 91, 10), Ink, -1);
        Cv2.Rectangle(scene, new Rect(458, 530, 66, 10), Ink, -1);
        Cv2.Ellipse(scene, new Point(470, 625), new Size(22, 41), -10, 0, 360, Ink, -1);
        Cv2.Ellipse(scene, new Point(528, 575), new Size(28, 20), -25, 0, 360, Ink, -1);
        Cv2.Line(scene, new Point(475, 590), new Point(517, 579), Ink, 15);
        Cv2.Rectangle(scene, new Rect(461, 680, 43, 11), Ink, -1);
        return scene;
    }

    private readonly record struct Affine(double Angle, double ScaleX, double ScaleY, double Shear = 0)
    {
        public Point2d Direction(double x, double y)
        {
            double radians = Angle * Math.PI / 180;
            double rotatedX = x * Math.Cos(radians) - y * Math.Sin(radians);
            double rotatedY = x * Math.Sin(radians) + y * Math.Cos(radians);
            return new(ScaleX * rotatedX + Shear * rotatedY, ScaleY * rotatedY);
        }
        public Point2f Map(double x, double y)
        {
            Point2d delta = Direction(x - Center.X, y - Center.Y);
            return new((float)(Center.X + delta.X), (float)(Center.Y + delta.Y));
        }
        public Mat Apply(Mat original)
        {
            Point2d u = Direction(1, 0), v = Direction(0, 1);
            using Mat matrix = new(2, 3, MatType.CV_64FC1);
            matrix.Set(0, 0, u.X); matrix.Set(0, 1, v.X);
            matrix.Set(1, 0, u.Y); matrix.Set(1, 1, v.Y);
            matrix.Set(0, 2, Center.X - u.X * Center.X - v.X * Center.Y);
            matrix.Set(1, 2, Center.Y - u.Y * Center.X - v.Y * Center.Y);
            Mat transformed = new();
            Cv2.WarpAffine(original, transformed, matrix, new Size(Size, Size),
                InterpolationFlags.Linear, BorderTypes.Constant, Grey);
            return transformed;
        }
    }

    private static PhotoObjectTarget? Locate(Mat scene, out string? failure)
    {
        byte[] bytes = new byte[Size * Size * 4];
        Marshal.Copy(scene.Data, bytes, 0, bytes.Length);
        return PhotoObjectLocator.Locate(Size, Size, Size * 4, bytes, BoardMap, out failure);
    }
    private static byte Alpha(PhotoObjectTarget target, Point2f point) => Sample(target, target.Alpha, point);
    private static byte Evidence(PhotoObjectTarget target, Point2f point) => target.ContrastEvidence is null
        ? (byte)0 : Sample(target, target.ContrastEvidence, point);
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
