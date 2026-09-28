using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoObjectPerforatedShapeRegression
{
    private const int Size = 400, BoardSize = PhotoHandCutout.BoardPixels, Left = 280, Top = 350;
    private static readonly double[] BoardMap = [1.0 / BoardSize, 0, 0, 0, 1.0 / BoardSize, 0, 0, 0, 1];

    public static void Run()
    {
        foreach (var transform in new[] { new Transform(0, 1, 1), new Transform(27, 1, 1),
                     new Transform(-35, 1.12, .82) })
        foreach (Shape shape in Enum.GetValues<Shape>())
        {
            using Mat mask = MakeMask(shape, transform);
            byte[] alpha = Bytes(mask);
            byte[] original = (byte[])alpha.Clone();
            var target = new PhotoObjectTarget(Left, Top, Size, Size, alpha, alpha.Count(value => value >= 200));
            Require(alpha.SequenceEqual(original) && target.Alpha.SequenceEqual(original),
                "Shape classification modified the original photograph alpha.");

            if (shape == Shape.Rectangle)
            {
                CheckOldOpeningLosesCore(mask);
                Require(target.Spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
                    $"Dense openings in a rectangular face still erased the body during shape classification ({transform}).");
                CheckAxes(target.Spotlight, transform);
                Require(target.Spotlight.Width * target.Spotlight.Height <
                        Math.PI * target.SpotlightRadius * target.SpotlightRadius * .75,
                    "The perforated rectangle's light flooded the old circular footprint.");
                CheckCoverage(target);
                CheckCopy(target, original);
            }
            else
                Require(target.Spotlight.Shape == PhotoObjectSpotlightShape.Circle,
                    $"Filling holes solely for classification turned a {shape} into a rectangle ({transform}).");
        }
        Console.WriteLine("Perforated object-shape regression: dense holes, rotated and non-square rectangles with " +
            "a small corner shadow, complete light coverage and unchanged copied alpha/RGB; circle, ellipse and concave fallbacks passed.");
    }

    private static void CheckOldOpeningLosesCore(Mat alpha)
    {
        using Mat binary = new(), opened = new();
        Cv2.Threshold(alpha, binary, 199, 255, ThresholdTypes.Binary);
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(29, 29));
        Cv2.MorphologyEx(binary, opened, MorphTypes.Open, kernel);
        Cv2.FindContours(opened, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        double coreArea = contours.Select(contour => Math.Abs(Cv2.ContourArea(contour))).DefaultIfEmpty().Max();
        Require(coreArea < Cv2.CountNonZero(binary) * .75,
            "The fixture no longer reproduces the old opening step erasing the rectangular body.");
    }

    private static Mat MakeMask(Shape shape, Transform transform)
    {
        using Mat plain = new(Size, Size, MatType.CV_8UC1, Scalar.Black);
        switch (shape)
        {
            case Shape.Rectangle:
                Cv2.Rectangle(plain, new Rect(70, 105, 260, 190), Scalar.White, -1);
                // A small offset attached corner represents the carton shadow;
                // it is retained in alpha and light coverage, not cropped away.
                Cv2.FillConvexPoly(plain, [new(70, 105), new(72, 81), new(89, 105)], Scalar.White);
                break;
            case Shape.Circle:
                Cv2.Circle(plain, new Point(200, 200), 110, Scalar.White, -1);
                break;
            case Shape.Ellipse:
                Cv2.Ellipse(plain, new Point(200, 200), new Size(145, 75), 0, 0, 360, Scalar.White, -1);
                break;
            case Shape.Concave:
                Cv2.FillPoly(plain, [[new(70, 105), new(155, 105), new(155, 215),
                    new(330, 215), new(330, 295), new(70, 295)]], Scalar.White);
                break;
        }
        using Mat interior = new();
        using Mat guard = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(35, 35));
        Cv2.Erode(plain, interior, guard);
        int holeCount = 0;
        for (int y = 80; y <= 320; y += 24)
        for (int x = 80; x <= 320; x += 24)
        {
            if (interior.At<byte>(y, x) == 0) continue;
            Cv2.Circle(plain, new Point(x, y), 7, Scalar.Black, -1);
            holeCount++;
        }
        Require(holeCount >= 20, "The fixture no longer stresses shape opening with dense internal holes.");
        using Mat affine = new(2, 3, MatType.CV_64FC1);
        Point2d u = transform.Direction(1, 0), v = transform.Direction(0, 1);
        affine.Set(0, 0, u.X); affine.Set(0, 1, v.X);
        affine.Set(1, 0, u.Y); affine.Set(1, 1, v.Y);
        affine.Set(0, 2, 200 - 200 * u.X - 200 * v.X);
        affine.Set(1, 2, 200 - 200 * u.Y - 200 * v.Y);
        Mat result = new();
        Cv2.WarpAffine(plain, result, affine, new Size(Size, Size), InterpolationFlags.Linear,
            BorderTypes.Constant, Scalar.Black);
        return result;
    }

    private static void CheckCopy(PhotoObjectTarget target, byte[] originalAlpha)
    {
        byte[] camera = new byte[BoardSize * BoardSize * 4];
        for (int i = 0; i < camera.Length; i += 4)
        {
            camera[i] = camera[i + 1] = camera[i + 2] = 235;
            camera[i + 3] = 255;
        }
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (originalAlpha[y * Size + x] == 0) continue;
            int i = ((Top + y) * BoardSize + Left + x) * 4;
            camera[i] = (byte)(30 + x % 37); camera[i + 1] = (byte)(55 + y % 29); camera[i + 2] = 100;
        }
        PhotoHandCutout? copied = PhotoObjectExtractor.ExtractTarget(BoardSize, BoardSize, BoardSize * 4,
            camera, null, BoardMap, target, out var failure);
        Require(copied is not null && failure is null, "The fitted light could not verify its perforated object: " + failure);
        Require(copied!.Width == Size && copied.Height == Size && copied.BoardOrigin == new PixelPoint(Left, Top),
            "Fitting the spotlight stretched or recentered the copied image.");
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int index = y * Size + x, destination = index * 4;
            Require(copied.BgraPixels[destination + 3] == originalAlpha[index],
                "Shape-only hole filling leaked into the photograph's alpha.");
            for (int channel = 0; channel < 3; channel++)
            {
                byte expected = originalAlpha[index] == 0 ? (byte)0 :
                    camera[((Top + y) * BoardSize + Left + x) * 4 + channel];
                Require(copied.BgraPixels[destination + channel] == expected,
                    "Shape fitting changed photographed RGB or retained background inside a real hole.");
            }
        }
    }

    private static void CheckAxes(PhotoObjectSpotlight light, Transform transform)
    {
        double c = Math.Cos(light.RotationRadians), s = Math.Sin(light.RotationRadians);
        Point2d u = new(c, s), v = new(light.Shear * c - s, light.Shear * s + c);
        Point2d expectedU = transform.Direction(1, 0), expectedV = transform.Direction(0, 1);
        static double Error(Point2d a, Point2d b) => Math.Abs(a.X * b.Y - a.Y * b.X) /
            Math.Sqrt((a.X * a.X + a.Y * a.Y) * (b.X * b.X + b.Y * b.Y));
        Require(Math.Min(Math.Max(Error(u, expectedU), Error(v, expectedV)),
                Math.Max(Error(u, expectedV), Error(v, expectedU))) < .10,
            "The light followed the holes or corner shadow instead of the rectangular face's edges.");
    }

    private static void CheckCoverage(PhotoObjectTarget target)
    {
        PhotoObjectSpotlight light = target.Spotlight;
        for (int y = 0; y < Size; y += 3)
        for (int x = 0; x < Size; x += 3)
        {
            if (target.Alpha[y * Size + x] == 0) continue;
            for (int step = 0; step < 8; step++)
            {
                double dx = Left + x + 20 * Math.Cos(step * Math.PI / 4) - light.Center.X;
                double dy = Top + y + 20 * Math.Sin(step * Math.PI / 4) - light.Center.Y;
                double v = -dx * Math.Sin(light.RotationRadians) + dy * Math.Cos(light.RotationRadians);
                double u = dx * Math.Cos(light.RotationRadians) + dy * Math.Sin(light.RotationRadians) - light.Shear * v;
                double qx = Math.Abs(u) - light.Width / 2 + light.CornerRadius;
                double qy = Math.Abs(v) - light.Height / 2 + light.CornerRadius;
                Require(Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) +
                        Math.Min(Math.Max(qx, qy), 0) <= light.CornerRadius,
                    "The fitted rectangle clipped the original alpha or its white presence-sampling band.");
            }
        }
    }

    private enum Shape { Rectangle, Circle, Ellipse, Concave }
    private readonly record struct Transform(double Angle, double ScaleX, double ScaleY)
    {
        public Point2d Direction(double x, double y)
        {
            double radians = Angle * Math.PI / 180;
            return new(ScaleX * (x * Math.Cos(radians) - y * Math.Sin(radians)),
                ScaleY * (x * Math.Sin(radians) + y * Math.Cos(radians)));
        }
    }
    private static byte[] Bytes(Mat mask)
    {
        byte[] result = new byte[Size * Size]; Marshal.Copy(mask.Data, result, 0, result.Length); return result;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
