using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>
/// The four visible outer corners of the white board in camera pixels, clockwise
/// from the top left. Confidence describes geometric/edge evidence, not a
/// calibrated probability.
/// </summary>
public sealed record BoardDetection(PixelPoint[] Corners, double Confidence);

/// <summary>
/// Finds the outer board during grid setup. The projected grid itself is a
/// prominent inner quadrilateral, so the detector accepts only a larger,
/// complete quadrilateral containing it. This deliberately returns null when
/// an outer board edge is clipped or obscured instead of calling the grid the
/// physical board.
/// </summary>
public static class BoardDetector
{
    private const int WorkingWidth = 960;
    private const double MinimumAreaFraction = 0.12;

    public static BoardDetection? Detect(int width, int height, int stride, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
            bgra.Length < (long)(height - 1) * stride + width * 4)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");

        using Mat frame = new(height, width, MatType.CV_8UC4);
        int rowBytes = checked(width * 4);
        for (int row = 0; row < height; row++)
            Marshal.Copy(bgra, row * stride, IntPtr.Add(frame.Data, row * rowBytes), rowBytes);

        double scale = Math.Min(1.0, WorkingWidth / (double)Math.Max(width, height));
        using Mat reduced = new();
        if (scale < 1.0)
            Cv2.Resize(frame, reduced, new Size(Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale))), interpolation: InterpolationFlags.Area);
        else
            frame.CopyTo(reduced);
        using Mat gray = new();
        using Mat blurred = new();
        using Mat edges = new();
        using Mat joined = new();
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.CvtColor(reduced, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.GaussianBlur(gray, blurred, new Size(7, 7), 0);
        Cv2.Canny(blurred, edges, 18, 55);
        Cv2.MorphologyEx(edges, joined, MorphTypes.Close, kernel);
        Cv2.FindContours(joined, out Point[][] contours, out _,
            RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        var candidates = new List<Quad>();
        double frameArea = reduced.Width * (double)reduced.Height;
        foreach (Point[] contour in contours)
        {
            double contourArea = Math.Abs(Cv2.ContourArea(contour));
            if (contourArea < frameArea * MinimumAreaFraction) continue;
            double perimeter = Cv2.ArcLength(contour, true);
            Point[] polygon = Cv2.ApproxPolyDP(contour, Math.Max(3, perimeter * 0.018), true);
            if (polygon.Length != 4 || !Cv2.IsContourConvex(polygon)) continue;
            Point[] ordered = OrderCorners(polygon);
            double area = Math.Abs(Cv2.ContourArea(ordered));
            if (area < frameArea * MinimumAreaFraction || area > frameArea * 0.92) continue;
            if (ordered.Any(point => point.X < reduced.Width * 0.015 ||
                point.Y < reduced.Height * 0.015 || point.X > reduced.Width * 0.985 ||
                point.Y > reduced.Height * 0.985)) continue;
            double top = Distance(ordered[0], ordered[1]);
            double right = Distance(ordered[1], ordered[2]);
            double bottom = Distance(ordered[2], ordered[3]);
            double left = Distance(ordered[3], ordered[0]);
            double aspect = (top + bottom) / (right + left);
            if (aspect < 0.55 || aspect > 1.8 ||
                Math.Min(Math.Min(top, right), Math.Min(bottom, left)) <
                    Math.Min(reduced.Width, reduced.Height) * 0.25)
                continue;
            double edgeSupport = EdgeSupport(edges, ordered);
            if (edgeSupport < 0.55) continue;
            if (candidates.Any(existing => Similar(existing.Corners, ordered))) continue;
            candidates.Add(new Quad(ordered, area, edgeSupport));
        }

        // The board surrounds the grid in this installation. A single bright
        // rectangle is insufficient evidence: it could be only the projection.
        Quad? board = candidates
            .Where(outer => candidates.Any(inner =>
                !ReferenceEquals(inner, outer) &&
                inner.Area >= outer.Area * 0.20 && inner.Area <= outer.Area * 0.86 &&
                inner.Corners.All(corner => Cv2.PointPolygonTest(outer.Corners, corner, false) > 0)))
            .OrderByDescending(quad => quad.Area)
            .ThenByDescending(quad => quad.EdgeSupport)
            .FirstOrDefault();
        if (board is null) return null;
        double confidence = Math.Clamp(0.55 + 0.4 * board.EdgeSupport, 0, 1);
        PixelPoint[] corners = board.Corners.Select(point =>
            new PixelPoint(point.X * width / (double)reduced.Width,
                point.Y * height / (double)reduced.Height)).ToArray();
        return new BoardDetection(corners, confidence);
    }

    private static Point[] OrderCorners(Point[] polygon)
    {
        Point[] byY = polygon.OrderBy(point => point.Y).ToArray();
        Point[] upper = byY[..2].OrderBy(point => point.X).ToArray();
        Point[] lower = byY[2..].OrderBy(point => point.X).ToArray();
        return [upper[0], upper[1], lower[1], lower[0]];
    }

    private static double EdgeSupport(Mat edges, Point[] corners)
    {
        int hits = 0, total = 0;
        for (int side = 0; side < 4; side++)
        {
            Point first = corners[side], last = corners[(side + 1) % 4];
            for (int step = 1; step <= 24; step++)
            {
                double t = step / 25.0;
                int x = (int)Math.Round(first.X + (last.X - first.X) * t);
                int y = (int)Math.Round(first.Y + (last.Y - first.Y) * t);
                total++;
                bool found = false;
                for (int dy = -3; dy <= 3 && !found; dy++)
                    for (int dx = -3; dx <= 3 && !found; dx++)
                        if (x + dx >= 0 && x + dx < edges.Width && y + dy >= 0 && y + dy < edges.Height &&
                            edges.At<byte>(y + dy, x + dx) != 0)
                            found = true;
                if (found) hits++;
            }
        }
        return hits / (double)total;
    }

    private static bool Similar(Point[] first, Point[] second) =>
        Enumerable.Range(0, 4).All(i => Distance(first[i], second[i]) < 10);

    private static double Distance(Point first, Point second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private sealed record Quad(Point[] Corners, double Area, double EdgeSupport);
}
