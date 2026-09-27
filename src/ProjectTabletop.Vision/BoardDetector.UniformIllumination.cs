using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

public static partial class BoardDetector
{
    /// <summary>
    /// Finds the boundary of a uniformly illuminated projector field. Call only while
    /// the projector displays a full-frame solid white scan image. The returned
    /// camera-pixel corners describe the light field, not the cardboard.
    /// </summary>
    public static BoardDetection? DetectProjectedField(int width, int height, int stride, byte[] bgra)
    {
        using ScanFrame scan = new(width, height, stride, bgra);
        ScanQuad? field = FindDominantBrightQuad(scan);
        return field is null ? null : ToDetection(field, scan, 0.55 + 0.4 * field.EdgeSupport);
    }

    /// <summary>
    /// Finds the physical cardboard by ambient light while the projector output
    /// is completely black. This offers an independent check on the full-white
    /// scan: the same four camera corners should appear in both frames. It
    /// requires the cardboard to reflect more ambient light than the floor and
    /// returns null if no complete bright quadrilateral is visible. Never call
    /// this while a projected image is displayed.
    /// </summary>
    public static BoardDetection? DetectAmbientBoard(int width, int height, int stride, byte[] bgra)
    {
        using ScanFrame scan = new(width, height, stride, bgra);
        ScanQuad? board = FindDominantBrightQuad(scan, requireEverySide: true);
        return board is null ? null : ToDetection(board, scan, 0.55 + 0.4 * board.EdgeSupport,
            refinePhysicalEdges: true);
    }

    /// <summary>
    /// Finds the cardboard as a separate quadrilateral inside a full-frame white
    /// projector field. Its camera-space aspect ratio is unrestricted because the
    /// oblique webcam view can make a physically square board appear wide. A
    /// projector grid, text, or picture must not be displayed during this scan.
    /// Returns null if the field or all four independent cardboard edges cannot be
    /// distinguished. The cardboard can reflect either more or less light than its
    /// surroundings; all four sides must agree on the contrast direction.
    /// </summary>
    public static BoardDetection? DetectUniformIllumination(int width, int height, int stride, byte[] bgra)
    {
        using ScanFrame scan = new(width, height, stride, bgra);
        ScanQuad? field = FindDominantBrightQuad(scan);
        if (field is null) return null;

        ScanQuad? board = scan.Quads
            .Where(candidate => !ReferenceEquals(candidate, field) &&
                candidate.Area >= field.Area * 0.16 && candidate.Area <= field.Area * 0.84 &&
                // The physical sheet can extend a few pixels past the projected
                // field, especially at its near and far edges in an oblique view.
                candidate.Corners.All(corner =>
                    Cv2.PointPolygonTest(field.Corners, corner, true) >= -8) &&
                Cv2.PointPolygonTest(field.Corners,
                    new Point((int)candidate.Corners.Average(p => p.X),
                        (int)candidate.Corners.Average(p => p.Y)), false) > 0 &&
                Math.Abs(candidate.MeanContrast) >= 11 &&
                candidate.SideContrasts.All(side =>
                    Math.Sign(side) == Math.Sign(candidate.MeanContrast) && Math.Abs(side) >= 5) &&
                candidate.EdgeSupport >= 0.55 &&
                candidate.DarkInteriorFraction <= 0.07)
            .OrderByDescending(candidate => candidate.Area)
            .ThenByDescending(candidate => Math.Abs(candidate.MeanContrast))
            .FirstOrDefault();
        return board is null ? null : ToDetection(board, scan,
            0.4 + 0.35 * board.EdgeSupport + Math.Min(0.2, Math.Abs(board.MeanContrast) / 150),
            refinePhysicalEdges: true);
    }

    private static BoardDetection ToDetection(ScanQuad quad, ScanFrame scan, double confidence,
        bool refinePhysicalEdges = false) => new(
        refinePhysicalEdges ? RefinePhysicalBoardCorners(quad, scan) : quad.Corners.Select(point => new PixelPoint(
            point.X * scan.SourceWidth / (double)scan.Gray.Width,
            point.Y * scan.SourceHeight / (double)scan.Gray.Height)).ToArray(),
        Math.Clamp(confidence, 0, 1));

    private static ScanQuad? FindDominantBrightQuad(ScanFrame scan, bool requireEverySide = false) => scan.Quads
        .Where(candidate => candidate.Area >= scan.Gray.Width * scan.Gray.Height * 0.20 &&
            candidate.Area <= scan.Gray.Width * scan.Gray.Height * 0.93 &&
            candidate.MeanContrast >= 12 &&
            candidate.SideContrasts.All(side => side >= 5) &&
            candidate.EdgeSupport >= 0.55 &&
            // A floor seam can extend one ambient contour corner beyond the
            // cardboard. Three sound edges must not hide an unsupported fourth.
            (!requireEverySide || EdgeSupport(scan.Edges, candidate.Corners, weakestSide: true) >= 0.55))
        .OrderByDescending(candidate => candidate.Area)
        .ThenByDescending(candidate => candidate.EdgeSupport)
        .FirstOrDefault();

    private sealed class ScanFrame : IDisposable
    {
        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public Mat SourceGray { get; } = new();
        public Mat Gray { get; } = new();
        public Mat Edges { get; } = new();
        public List<ScanQuad> Quads { get; } = [];

        public ScanFrame(int width, int height, int stride, byte[] bgra)
        {
            ArgumentNullException.ThrowIfNull(bgra);
            if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
                bgra.Length < (long)(height - 1) * stride + width * 4)
                throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
            SourceWidth = width;
            SourceHeight = height;

            using Mat frame = new(height, width, MatType.CV_8UC4);
            int rowBytes = checked(width * 4);
            for (int row = 0; row < height; row++)
                Marshal.Copy(bgra, row * stride, IntPtr.Add(frame.Data, row * rowBytes), rowBytes);
            Cv2.CvtColor(frame, SourceGray, ColorConversionCodes.BGRA2GRAY);
            double scale = Math.Min(1.0, WorkingWidth / (double)Math.Max(width, height));
            using Mat reduced = new();
            if (scale < 1.0)
                Cv2.Resize(frame, reduced,
                    new Size(Math.Max(1, (int)Math.Round(width * scale)),
                        Math.Max(1, (int)Math.Round(height * scale))),
                    interpolation: InterpolationFlags.Area);
            else
                frame.CopyTo(reduced);
            Cv2.CvtColor(reduced, Gray, ColorConversionCodes.BGRA2GRAY);

            using Mat blurred = new();
            using Mat joined = new();
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.GaussianBlur(Gray, blurred, new Size(7, 7), 0);
            Cv2.Canny(blurred, Edges, 20, 65);
            Cv2.MorphologyEx(Edges, joined, MorphTypes.Close, kernel);
            AddContours(joined, Edges);

            // Threshold contours recover a boundary when its gradient is too soft
            // for Canny, as can happen with a defocused projector or matte board.
            using Mat otsu = new();
            Cv2.Threshold(blurred, otsu, 0, 255,
                ThresholdTypes.Binary | ThresholdTypes.Otsu);
            using Mat smoothMask = new();
            Cv2.MorphologyEx(otsu, smoothMask, MorphTypes.Close, kernel);
            AddContours(smoothMask, Edges);
        }

        private void AddContours(Mat binary, Mat edgeMap)
        {
            Cv2.FindContours(binary, out Point[][] contours, out _,
                RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            double frameArea = Gray.Width * (double)Gray.Height;
            foreach (Point[] contour in contours)
            {
                double area = Math.Abs(Cv2.ContourArea(contour));
                if (area < frameArea * 0.08 || area > frameArea * 0.94) continue;
                double perimeter = Cv2.ArcLength(contour, true);
                foreach (double epsilon in new[] { 0.012, 0.02, 0.035 })
                {
                    Point[] polygon = Cv2.ApproxPolyDP(contour, Math.Max(2, perimeter * epsilon), true);
                    if (polygon.Length != 4 || !Cv2.IsContourConvex(polygon)) continue;
                    Point[] ordered = OrderCorners(polygon);
                    if (ordered.Any(point => point.X < Gray.Width * 0.008 ||
                        point.Y < Gray.Height * 0.008 || point.X > Gray.Width * 0.992 ||
                        point.Y > Gray.Height * 0.992)) continue;
                    double polygonArea = Math.Abs(Cv2.ContourArea(ordered));
                    if (polygonArea < frameArea * 0.08 || polygonArea > frameArea * 0.94) continue;
                    double shortest = Enumerable.Range(0, 4)
                        .Min(i => Distance(ordered[i], ordered[(i + 1) % 4]));
                    if (shortest < Math.Min(Gray.Width, Gray.Height) * 0.20) continue;
                    if (Quads.Any(existing => Similar(existing.Corners, ordered))) continue;
                    double support = EdgeSupport(edgeMap, ordered);
                    double[] contrasts = SideContrasts(Gray, ordered);
                    if (contrasts.Any(double.IsNaN)) continue;
                    Quads.Add(new ScanQuad(ordered, polygonArea, support, contrasts,
                        DarkInteriorFraction(Gray, ordered)));
                    break;
                }
            }
        }

        public void Dispose()
        {
            SourceGray.Dispose();
            Gray.Dispose();
            Edges.Dispose();
        }
    }

    private static double[] SideContrasts(Mat gray, Point[] corners)
    {
        double centerX = corners.Average(point => point.X);
        double centerY = corners.Average(point => point.Y);
        double[] values = new double[4];
        for (int side = 0; side < 4; side++)
        {
            Point first = corners[side], last = corners[(side + 1) % 4];
            double dx = last.X - first.X, dy = last.Y - first.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double nx = -dy / length, ny = dx / length;
            double midX = (first.X + last.X) * 0.5;
            double midY = (first.Y + last.Y) * 0.5;
            if ((centerX - midX) * nx + (centerY - midY) * ny < 0)
            {
                nx = -nx;
                ny = -ny;
            }
            double sum = 0;
            int samples = 0;
            for (int i = 3; i <= 17; i++)
            {
                double t = i / 20.0;
                double x = first.X + dx * t;
                double y = first.Y + dy * t;
                foreach (int offset in new[] { 7, 13 })
                {
                    int inX = (int)Math.Round(x + nx * offset);
                    int inY = (int)Math.Round(y + ny * offset);
                    int outX = (int)Math.Round(x - nx * offset);
                    int outY = (int)Math.Round(y - ny * offset);
                    if (inX < 0 || inY < 0 || outX < 0 || outY < 0 ||
                        inX >= gray.Width || outX >= gray.Width ||
                        inY >= gray.Height || outY >= gray.Height) continue;
                    sum += gray.At<byte>(inY, inX) - gray.At<byte>(outY, outX);
                    samples++;
                }
            }
            values[side] = samples == 0 ? double.NaN : sum / samples;
        }
        return values;
    }

    private static double DarkInteriorFraction(Mat gray, Point[] corners)
    {
        using Mat mask = new(gray.Height, gray.Width, MatType.CV_8UC1, Scalar.Black);
        Cv2.FillConvexPoly(mask, corners, Scalar.White);
        using Mat inset = new();
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(25, 25));
        Cv2.Erode(mask, inset, kernel);
        var pixels = new List<byte>();
        int height = gray.Height, width = gray.Width;
        for (int y = 0; y < height; y += 4)
            for (int x = 0; x < width; x += 4)
                if (inset.At<byte>(y, x) != 0) pixels.Add(gray.At<byte>(y, x));
        if (pixels.Count == 0) return 1;
        pixels.Sort();
        int median = pixels[pixels.Count / 2];
        return pixels.Count(pixel => pixel < median - 32) / (double)pixels.Count;
    }

    private sealed record ScanQuad(Point[] Corners, double Area, double EdgeSupport,
        double[] SideContrasts, double DarkInteriorFraction)
    {
        public double MeanContrast => SideContrasts.Average();
    }
}
