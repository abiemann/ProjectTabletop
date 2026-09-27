using OpenCvSharp;

namespace ProjectTabletop.Vision;

public enum PhotoObjectSpotlightShape
{
    Circle,
    RoundedRectangle
}

/// <summary>
/// Solid illumination geometry in 1000-pixel board coordinates. Positive rotation
/// follows the board's downward Y axis. Width/height exclude the renderer's feather.
/// Shape classification uses the main outline; coverage includes every alpha pixel.
/// A non-square physical board becomes a square here. Shear preserves its oblique
/// object edges: local (u, v) maps to Rotate(u + Shear * v, v) + Center.
/// </summary>
public sealed record PhotoObjectSpotlight(PhotoObjectSpotlightShape Shape, PixelPoint Center,
    double Width, double Height, double RotationRadians, double CornerRadius, double Shear = 0)
{
    internal static PhotoObjectSpotlight Create(int left, int top, int width, int height,
        byte[] alpha, PixelPoint circleCenter, double circleRadius)
    {
        var circle = new PhotoObjectSpotlight(PhotoObjectSpotlightShape.Circle, circleCenter,
            circleRadius * 2, circleRadius * 2, 0, 0);
        byte[] mask = alpha.Select(value => value >= 200 ? (byte)255 : (byte)0).ToArray();
        using Mat source = Mat.FromPixelData(height, width, MatType.CV_8UC1, mask);
        int openingRadius = Math.Clamp((int)Math.Round(Math.Min(width, height) * .055), 2, 14);
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse,
            new Size(openingRadius * 2 + 1, openingRadius * 2 + 1));
        using Mat opened = new();
        Cv2.MorphologyEx(source, opened, MorphTypes.Open, kernel);
        Cv2.FindContours(opened, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        Point[]? contour = contours.OrderByDescending(points => Math.Abs(Cv2.ContourArea(points))).FirstOrDefault();
        if (contour is null || contour.Length < 4) return circle;
        Point[] hull = Cv2.ConvexHull(contour);
        Point[] corners = Cv2.ApproxPolyDP(hull, Cv2.ArcLength(hull, true) * .025, true);
        RotatedRect core = Cv2.MinAreaRect(contour);
        double rectangleArea = core.Size.Width * core.Size.Height;
        double coreArea = Math.Abs(Cv2.ContourArea(contour));
        if (rectangleArea < 100 || coreArea < Cv2.CountNonZero(source) * .75) return circle;
        double angle, shear;
        if (!TryParallelEdges(corners, hull, coreArea, out angle, out shear))
        {
            // Rounded bodies can lack four clear corners. Keep the remote fit's
            // occupancy and long-side evidence; circles/ellipses must not pass.
            if (coreArea / rectangleArea < .84 ||
                (!(coreArea / rectangleArea >= .87 && HasSquareCorners(corners)) &&
                 !HasLongParallelSides(hull, core))) return circle;
            angle = core.Angle * Math.PI / 180;
            while (angle > Math.PI / 4) angle -= Math.PI / 2;
            while (angle <= -Math.PI / 4) angle += Math.PI / 2;
            shear = 0;
        }
        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        double minU = double.PositiveInfinity, minV = double.PositiveInfinity;
        double maxU = double.NegativeInfinity, maxV = double.NegativeInfinity;
        // Fit the untouched alpha, including narrow elastic loops/bookmarks that
        // were removed only for classifying the body's shape.
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (alpha[y * width + x] == 0) continue;
                double v = -x * sine + y * cosine;
                double u = x * cosine + y * sine - shear * v;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
        double centerU = (minU + maxU) / 2, centerV = (minV + maxV) / 2;
        // Scale the U padding by the inverse transform's row norm: every alpha
        // pixel still has at least the 20px white presence-sampling band around it.
        return new(PhotoObjectSpotlightShape.RoundedRectangle,
            new(left + (centerU + shear * centerV) * cosine - centerV * sine,
                top + (centerU + shear * centerV) * sine + centerV * cosine),
            maxU - minU + 1 + 56 * Math.Sqrt(1 + shear * shear), maxV - minV + 1 + 56, angle, 8, shear);
    }

    internal static bool TryParallelEdges(Point[] corners, Point[] hull, double area,
        out double angle, out double shear)
    {
        angle = shear = 0;
        if (corners.Length != 4) return false;
        double quadArea = Math.Abs(Cv2.ContourArea(corners));
        double hullArea = Math.Abs(Cv2.ContourArea(hull));
        // Require four sides supported by the actual outline. A four-point
        // approximation inside an ellipse, or a concave L, must not count.
        if (quadArea < 100 || quadArea < hullArea * .87 || area < quadArea * .9) return false;
        Point[] edges = Enumerable.Range(0, 4).Select(i => corners[(i + 1) % 4] - corners[i]).ToArray();
        static double Length(Point p) => Math.Sqrt((double)p.X * p.X + (double)p.Y * p.Y);
        for (int i = 0; i < 2; i++)
        {
            Point a = edges[i], b = edges[i + 2];
            double first = Length(a), second = Length(b), denominator = first * second;
            if (denominator < 1 || Math.Min(first, second) < Math.Max(first, second) * .75 ||
                (double)a.X * b.X + (double)a.Y * b.Y >= 0 ||
                Math.Abs((double)a.X * b.Y - (double)a.Y * b.X) > denominator * .12) return false;
        }
        double ux = edges[0].X - edges[2].X, uy = edges[0].Y - edges[2].Y;
        double vx = edges[1].X - edges[3].X, vy = edges[1].Y - edges[3].Y;
        double cross = ux * vy - uy * vx, dot = ux * vx + uy * vy;
        double norm = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        if (norm < 1 || Math.Abs(dot) > norm * .65 || Math.Abs(cross) < 1) return false;
        angle = Math.Atan2(uy, ux);
        shear = dot / cross;
        return true;
    }

    private static bool HasSquareCorners(Point[] corners)
    {
        if (corners.Length != 4) return false;
        for (int index = 0; index < 4; index++)
        {
            Point a = corners[(index + 3) % 4] - corners[index];
            Point b = corners[(index + 1) % 4] - corners[index];
            double denominator = Math.Sqrt((double)(a.X * a.X + a.Y * a.Y) * (b.X * b.X + b.Y * b.Y));
            if (denominator < 1 || Math.Abs((double)a.X * b.X + (double)a.Y * b.Y) / denominator > .22)
                return false;
        }
        return true;
    }

    private static bool HasLongParallelSides(Point[] hull, RotatedRect core)
    {
        double major = Math.Max(core.Size.Width, core.Size.Height);
        double minor = Math.Min(core.Size.Width, core.Size.Height);
        if (minor < 1 || major / minor < 1.8) return false;
        // Remote controls often have rounded or slightly tapered ends, so their
        // outline simplifies to more than four corners. Look for the dominant
        // straight sides instead. Normalize both axes to prevent a very long
        // ellipse from appearing straight merely because it is narrow.
        double angle = core.Angle * Math.PI / 180;
        if (core.Size.Height > core.Size.Width) angle += Math.PI / 2;
        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        (double U, double V) Project(Point point)
        {
            double x = point.X - core.Center.X, y = point.Y - core.Center.Y;
            return ((x * cosine + y * sine) / major, (-x * sine + y * cosine) / minor);
        }
        double firstSide = 0, secondSide = 0;
        for (int index = 0; index < hull.Length; index++)
        {
            var a = Project(hull[index]);
            var b = Project(hull[(index + 1) % hull.Length]);
            double length = Math.Abs(b.U - a.U), side = (a.V + b.V) / 2;
            if (length < .001 || Math.Abs(b.V - a.V) > length * .16 || Math.Abs(side) < .32) continue;
            if (side < 0) firstSide += length;
            else secondSide += length;
        }
        return firstSide >= .5 && secondSide >= .5;
    }
}
