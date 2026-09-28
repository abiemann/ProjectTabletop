using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

public static partial class PhotoObjectLocator
{
    // A pale printed box can contribute one open outer rim and several separate
    // ink components. Only recover its enclosed face when the actual camera
    // image supports its complete outline; containment alone would fill an
    // open U around an unrelated object or a real hole in a picture frame.
    private static bool TryRecoverPrintedSurface(byte[] photo, byte[] pixels, int[] ids,
        int[] candidates, int[] area, bool[] touchesEdge, out byte[] recoveredMask,
        out byte[] contrastEvidence)
    {
        recoveredMask = [];
        contrastEvidence = [];
        if (candidates.Length is < 1 or > 12 || candidates.Any(id => touchesEdge[id])) return false;
        int outerId = candidates.MaxBy(id => area[id]);
        if (area[outerId] < candidates.Where(id => id != outerId).Sum(id => area[id])) return false;
        byte[] outerPixels = new byte[BoardSize * BoardSize];
        for (int i = 0; i < outerPixels.Length; i++)
            if (ids[i] == outerId) outerPixels[i] = 255;
        using Mat outer = Mat.FromPixelData(BoardSize, BoardSize, MatType.CV_8UC1, outerPixels);
        Cv2.FindContours(outer, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.Tree, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return false;
        int boundary = Enumerable.Range(0, contours.Length).Where(i => hierarchy[i].Parent < 0)
            .MaxBy(i => Cv2.ContourArea(contours[i]));
        Point[] hull = Cv2.ConvexHull(contours[boundary]);
        double hullArea = Cv2.ContourArea(hull);
        var rectangle = Cv2.MinAreaRect(hull);
        double rectangleArea = rectangle.Size.Width * rectangle.Size.Height;
        if (hullArea < 1200 || hullArea > Capture.Width * Capture.Height * .45 ||
            rectangleArea <= 0 ||
            Math.Min(rectangle.Size.Width, rectangle.Size.Height) < 40 ||
            area[outerId] / hullArea is < .12 or > .72) return false;
        // Rectifying a non-square board into square coordinates shears a rotated
        // box. Follow its actual parallel sides instead of the empty corners of
        // an enclosing rectangle. The existing rectangle fit remains useful for
        // small folds or shadows that perturb an otherwise rectangular outline.
        Point[] quad = Cv2.ApproxPolyDP(hull, Cv2.ArcLength(hull, true) * .025, true);
        bool parallel = Math.Abs(Cv2.ContourArea(quad)) >= hullArea * .88 &&
            PhotoObjectSpotlight.TryParallelEdges(quad, hull, hullArea, out _, out _);
        bool rectangular = hullArea / rectangleArea >= .88;
        // A frame-sized opening is evidence against an opaque face. Very narrow
        // rim slivers can close during rectification, however, so let those tiny
        // near-boundary gaps survive as transparent holes in the recovered mask.
        // This also protects genuine openings when the rim and printing happen
        // to form one connected component.
        for (int i = 0; i < contours.Length; i++)
        {
            if (hierarchy[i].Parent < 0) continue;
            double holeArea = Cv2.ContourArea(contours[i]);
            if (holeArea < 36) continue;
            var hole = Cv2.MinAreaRect(contours[i]);
            if (holeArea > Math.Max(36, hullArea * .005) ||
                Math.Min(hole.Size.Width, hole.Size.Height) > 6 ||
                Cv2.PointPolygonTest(hull, hole.Center, true) >
                    Math.Max(6, Math.Min(rectangle.Size.Width, rectangle.Size.Height) * .1)) return false;
        }

        using Mat camera = Mat.FromPixelData(BoardSize, BoardSize, MatType.CV_8UC4, photo);
        using Mat smoothed = new();
        Cv2.GaussianBlur(camera, smoothed, new Size(3, 3), .8);
        var outlines = new List<(Point[] Mask, Point2f[] Edges)>();
        if (parallel) outlines.Add((hull, quad.Select(point => new Point2f(point.X, point.Y)).ToArray()));
        if (rectangular) outlines.Add((hull, rectangle.Points()));
        // Two strong adjacent sides can leave only an L-shaped contrast mask.
        // Its convex hull cuts through the pale face. Complete a bounded outline
        // only when the actual image supports every proposed side, including
        // both faint ones, and all separate printing lies inside that outline.
        foreach (var corners in PrintedSurfaceCompletions(hull).OrderBy(points => Math.Abs(Cv2.ContourArea(points))))
            outlines.Add((corners.Select(point => new Point((int)Math.Round(point.X),
                (int)Math.Round(point.Y))).ToArray(), corners));
        using Mat filled = new(BoardSize, BoardSize, MatType.CV_8UC1, Scalar.Black);
        byte[] filledPixels = new byte[BoardSize * BoardSize];
        var candidateIds = candidates.ToHashSet();
        bool accepted = false;
        foreach (var outline in outlines)
        {
            double proposedArea = Math.Abs(Cv2.ContourArea(outline.Mask));
            if (proposedArea < hullArea * .98 || proposedArea > hullArea * 2.05 ||
                proposedArea > Capture.Width * Capture.Height * .45 ||
                outline.Mask.Any(point => point.X <= Capture.Left + 2 || point.X >= Capture.Right - 3 ||
                    point.Y <= Capture.Top + 2 || point.Y >= Capture.Bottom - 3) ||
                !PrintedSurfaceEdges(smoothed, outline.Edges)) continue;
            filled.SetTo(Scalar.Black);
            Cv2.FillConvexPoly(filled, outline.Mask, Scalar.White);
            Marshal.Copy(filled.Data, filledPixels, 0, filledPixels.Length);
            bool containsAll = true;
            for (int i = 0; i < ids.Length && containsAll; i++)
                if (candidateIds.Contains(ids[i]) && filledPixels[i] == 0) containsAll = false;
            if (containsAll) { accepted = true; break; }
        }
        if (!accepted) return false;

        // Retain even tiny enclosed holes instead of manufacturing opaque
        // pixels there. The larger-hole veto above prevents merging a frame
        // and an item placed inside it into one surface.
        for (int i = 0; i < contours.Length; i++)
            if (hierarchy[i].Parent >= 0) Cv2.DrawContours(filled, contours, i, Scalar.Black, -1);
        Marshal.Copy(filled.Data, filledPixels, 0, filledPixels.Length);
        contrastEvidence = new byte[filledPixels.Length];
        for (int i = 0; i < filledPixels.Length; i++)
            if (filledPixels[i] != 0 && pixels[i] != 0) contrastEvidence[i] = 255;
        recoveredMask = filledPixels;
        return true;
    }

    private static bool PrintedSurfaceEdges(Mat photo, Point2f[] corners)
    {
        int strongSides = 0;
        for (int side = 0; side < corners.Length; side++)
        {
            if (!PrintedSurfaceEdge(photo, corners[side], corners[(side + 1) % corners.Length], out bool strong))
                return false;
            if (strong) strongSides++;
        }
        return strongSides >= 2;
    }

    private static IEnumerable<Point2f[]> PrintedSurfaceCompletions(Point[] hull)
    {
        var rectangle = Cv2.MinAreaRect(hull);
        double minimumSide = Math.Max(40, Math.Min(rectangle.Size.Width, rectangle.Size.Height) * .4);
        Point[] outline = Cv2.ApproxPolyDP(hull, Cv2.ArcLength(hull, true) * .01, true);
        static double Length(Point edge) => Math.Sqrt((double)edge.X * edge.X + (double)edge.Y * edge.Y);
        var sides = Enumerable.Range(0, outline.Length)
            .Select(i => outline[(i + 1) % outline.Length] - outline[i])
            .Where(edge => Length(edge) >= minimumSide).OrderByDescending(Length).Take(8).ToArray();
        // Use the measured side directions rather than forcing right angles in
        // square board coordinates. A rotated physical rectangle becomes skewed
        // there when the physical board has unequal width and height.
        for (int first = 0; first < sides.Length; first++)
            for (int second = first + 1; second < sides.Length; second++)
            {
                Point a = sides[first], b = sides[second];
                double ux = a.X / Length(a), uy = a.Y / Length(a);
                double vx = b.X / Length(b), vy = b.Y / Length(b);
                double determinant = ux * vy - uy * vx;
                if (Math.Abs(ux * vx + uy * vy) > .65) continue;
                var projected = hull.Select(point => (U: (point.X * vy - point.Y * vx) / determinant,
                    V: (ux * point.Y - uy * point.X) / determinant)).ToArray();
                double minU = projected.Min(point => point.U), maxU = projected.Max(point => point.U);
                double minV = projected.Min(point => point.V), maxV = projected.Max(point => point.V);
                if (Math.Min(maxU - minU, maxV - minV) < 40) continue;
                Point2f P(double u, double v) => new((float)(u * ux + v * vx), (float)(u * uy + v * vy));
                yield return [P(minU, minV), P(maxU, minV), P(maxU, maxV), P(minU, maxV)];
            }
        yield return rectangle.Points();
    }

    private static bool PrintedSurfaceEdge(Mat photo, Point2f first, Point2f second, out bool strong)
    {
        strong = false;
        double dx = second.X - first.X, dy = second.Y - first.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 40) return false;
        double nx = -dy / length, ny = dx / length;
        bool supported = false;
        // A tiny tab, fold or attached shadow can move the enclosing rectangle
        // a few pixels outside the face. Seek one coherent parallel edge nearby,
        // never a different offset for every individual sample.
        double search = Math.Min(8, length * .03);
        for (double offset = -search; offset <= search + .1; offset += 2)
        {
            var contrasts = new List<double>();
            for (double t = .05; t <= .951; t += .02)
            {
                double x = first.X + t * dx + nx * offset, y = first.Y + t * dy + ny * offset;
                if (!Sample(x + nx * 6, y + ny * 6, out var inside) ||
                    !Sample(x - nx * 6, y - ny * 6, out var outside)) return false;
                contrasts.Add(Math.Sqrt(Enumerable.Range(0, 3)
                    .Sum(channel => Math.Pow(inside[channel] - outside[channel], 2)) / 3));
            }
            contrasts.Sort();
            double median = contrasts[contrasts.Count / 2];
            strong |= median >= 12 && contrasts.Count(value => value >= 6) >= contrasts.Count * .75;
            supported |= median >= 4 && contrasts.Count(value => value >= 3) >= contrasts.Count * .75;
        }
        return supported;

        bool Sample(double x, double y, out double[] color)
        {
            color = new double[3];
            int centerX = (int)Math.Round(x), centerY = (int)Math.Round(y);
            if (centerX < 1 || centerY < 1 || centerX >= BoardSize - 1 || centerY >= BoardSize - 1) return false;
            for (int v = -1; v <= 1; v++)
                for (int u = -1; u <= 1; u++)
                {
                    var pixel = photo.At<Vec4b>(centerY + v, centerX + u);
                    for (int channel = 0; channel < 3; channel++) color[channel] += pixel[channel] / 9.0;
                }
            return true;
        }
    }
}
