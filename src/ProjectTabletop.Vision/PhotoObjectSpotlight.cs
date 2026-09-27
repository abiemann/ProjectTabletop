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
/// </summary>
public sealed record PhotoObjectSpotlight(PhotoObjectSpotlightShape Shape, PixelPoint Center,
    double Width, double Height, double RotationRadians, double CornerRadius)
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
        if (corners.Length != 4) return circle;
        RotatedRect core = Cv2.MinAreaRect(contour);
        double rectangleArea = core.Size.Width * core.Size.Height;
        // A disk occupies only pi/4 of its enclosing rectangle. Requiring both
        // a quadrilateral with near-right angles AND high fill avoids labelling
        // circles, triangles and irregular convex outlines as rectangular.
        double coreArea = Math.Abs(Cv2.ContourArea(contour));
        if (rectangleArea < 100 || coreArea / rectangleArea < .87 ||
            coreArea < Cv2.CountNonZero(source) * .75) return circle;
        for (int index = 0; index < 4; index++)
        {
            Point a = corners[(index + 3) % 4] - corners[index];
            Point b = corners[(index + 1) % 4] - corners[index];
            double denominator = Math.Sqrt((double)(a.X * a.X + a.Y * a.Y) * (b.X * b.X + b.Y * b.Y));
            if (denominator < 1 || Math.Abs((double)a.X * b.X + (double)a.Y * b.Y) / denominator > .22)
                return circle;
        }

        double angle = core.Angle * Math.PI / 180;
        while (angle > Math.PI / 4) angle -= Math.PI / 2;
        while (angle <= -Math.PI / 4) angle += Math.PI / 2;
        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        double minU = double.PositiveInfinity, minV = double.PositiveInfinity;
        double maxU = double.NegativeInfinity, maxV = double.NegativeInfinity;
        // Fit the untouched alpha, including narrow elastic loops/bookmarks that
        // were removed only for classifying the body's shape.
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (alpha[y * width + x] == 0) continue;
                double u = x * cosine + y * sine, v = -x * sine + y * cosine;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
        double centerU = (minU + maxU) / 2, centerV = (minV + maxV) / 2;
        return new(PhotoObjectSpotlightShape.RoundedRectangle,
            new(left + centerU * cosine - centerV * sine, top + centerU * sine + centerV * cosine),
            maxU - minU + 1 + 56, maxV - minV + 1 + 56, angle, 8);
    }
}
