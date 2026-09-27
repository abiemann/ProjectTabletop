using OpenCvSharp;

namespace ProjectTabletop.Vision;

public static partial class BoardDetector
{
    // The contour search runs at 960 pixels wide to remain quick while scanning.
    // Refine only the selected physical board at camera resolution: a fitted line
    // is much less sensitive to one rounded/occluded corner than ApproxPolyDP.
    private static PixelPoint[] RefinePhysicalBoardCorners(ScanQuad board, ScanFrame scan)
    {
        PixelPoint[] coarse = board.Corners.Select(point => new PixelPoint(
            point.X * scan.SourceWidth / (double)scan.Gray.Width,
            point.Y * scan.SourceHeight / (double)scan.Gray.Height)).ToArray();
        using Mat smooth = new();
        Cv2.GaussianBlur(scan.SourceGray, smooth, new Size(5, 5), 1.1);

        var lines = new RefinedLine[4];
        for (int side = 0; side < 4; side++)
        {
            if (!TryFitPhysicalSide(smooth, coarse[side], coarse[(side + 1) % 4],
                    Math.Sign(board.SideContrasts[side]), out lines[side]))
                return coarse;
        }

        var corners = new PixelPoint[4];
        for (int corner = 0; corner < 4; corner++)
        {
            if (!TryIntersect(lines[(corner + 3) % 4], lines[corner], out corners[corner]) ||
                Distance(corners[corner], coarse[corner]) > 20)
                return coarse;
        }
        return corners;
    }

    private static bool TryFitPhysicalSide(Mat gray, PixelPoint start, PixelPoint end,
        int polarity, out RefinedLine line, int searchRadius = 0)
    {
        line = default;
        if (polarity == 0) return false;
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 80) return false;
        double tx = dx / length, ty = dy / length;
        double nx = -ty, ny = tx;
        double midX = (start.X + end.X) * 0.5;
        double midY = (start.Y + end.Y) * 0.5;
        int count = (int)Math.Clamp(length / 9, 48, 128);
        int radius = searchRadius > 0 ? searchRadius :
            (int)Math.Clamp(Math.Min(gray.Width, gray.Height) * 0.012, 8, 16);
        var samples = new List<EdgeSample>(count);

        for (int sample = 0; sample < count; sample++)
        {
            double along = ((sample + 0.5) / count - 0.5) * length * 0.84;
            double x = midX + tx * along, y = midY + ty * along;
            double bestGradient = double.NegativeInfinity;
            int bestOffset = 0;
            for (int offset = -radius; offset <= radius; offset++)
            {
                double gradient = SignedGradient(gray, x + nx * offset,
                    y + ny * offset, nx, ny, polarity);
                if (gradient > bestGradient)
                {
                    bestGradient = gradient;
                    bestOffset = offset;
                }
            }
            if (bestGradient < 4.5) continue;

            // The parabola locates a blurred intensity step between camera pixels.
            double before = SignedGradient(gray, x + nx * (bestOffset - 1),
                y + ny * (bestOffset - 1), nx, ny, polarity);
            double after = SignedGradient(gray, x + nx * (bestOffset + 1),
                y + ny * (bestOffset + 1), nx, ny, polarity);
            double denominator = before - 2 * bestGradient + after;
            double subpixel = Math.Abs(denominator) > 1e-6
                ? Math.Clamp(0.5 * (before - after) / denominator, -0.5, 0.5) : 0;
            samples.Add(new EdgeSample(along, bestOffset + subpixel));
        }

        if (samples.Count < count * 0.55 || !FitOffsets(samples, out double offsetAtMid,
                out double slope, out double rms) || rms > 4)
            return false;
        line = new RefinedLine(new PixelPoint(midX + nx * offsetAtMid,
            midY + ny * offsetAtMid), new PixelPoint(tx + nx * slope, ty + ny * slope));
        return true;
    }

    private static bool FitOffsets(List<EdgeSample> samples, out double intercept,
        out double slope, out double rms)
    {
        intercept = slope = rms = 0;
        if (!LeastSquares(samples, out double initialIntercept, out double initialSlope)) return false;
        double[] residuals = samples.Select(sample =>
            Math.Abs(sample.Offset - initialIntercept - initialSlope * sample.Along)).Order().ToArray();
        double maxResidual = Math.Max(2.5, residuals[residuals.Length / 2] * 2.8);
        EdgeSample[] inliers = samples.Where(sample =>
            Math.Abs(sample.Offset - initialIntercept - initialSlope * sample.Along) <= maxResidual).ToArray();
        if (inliers.Length < samples.Count * 0.65 ||
            !LeastSquares(inliers, out double fittedIntercept, out double fittedSlope)) return false;
        rms = Math.Sqrt(inliers.Average(sample =>
            Math.Pow(sample.Offset - fittedIntercept - fittedSlope * sample.Along, 2)));
        intercept = fittedIntercept;
        slope = fittedSlope;
        return true;
    }

    private static bool LeastSquares(IReadOnlyList<EdgeSample> samples,
        out double intercept, out double slope)
    {
        intercept = slope = 0;
        if (samples.Count < 3) return false;
        double meanX = samples.Average(sample => sample.Along);
        double meanY = samples.Average(sample => sample.Offset);
        double xx = 0, xy = 0;
        foreach (EdgeSample sample in samples)
        {
            double x = sample.Along - meanX;
            xx += x * x;
            xy += x * (sample.Offset - meanY);
        }
        if (xx < 1e-6) return false;
        slope = xy / xx;
        intercept = meanY - slope * meanX;
        return true;
    }

    private static double SignedGradient(Mat gray, double x, double y,
        double nx, double ny, int polarity) => polarity *
        (SampleGray(gray, x + nx * 2.5, y + ny * 2.5) -
         SampleGray(gray, x - nx * 2.5, y - ny * 2.5));

    private static double SampleGray(Mat gray, double x, double y)
    {
        if (x < 0 || y < 0 || x >= gray.Width - 1 || y >= gray.Height - 1)
            return 0;
        int ix = (int)x, iy = (int)y;
        double fx = x - ix, fy = y - iy;
        return gray.At<byte>(iy, ix) * (1 - fx) * (1 - fy) +
            gray.At<byte>(iy, ix + 1) * fx * (1 - fy) +
            gray.At<byte>(iy + 1, ix) * (1 - fx) * fy +
            gray.At<byte>(iy + 1, ix + 1) * fx * fy;
    }

    private static bool TryIntersect(RefinedLine first, RefinedLine second,
        out PixelPoint intersection)
    {
        intersection = default;
        double cross = first.Direction.X * second.Direction.Y -
            first.Direction.Y * second.Direction.X;
        if (Math.Abs(cross) < 0.15) return false;
        double dx = second.Point.X - first.Point.X;
        double dy = second.Point.Y - first.Point.Y;
        double t = (dx * second.Direction.Y - dy * second.Direction.X) / cross;
        intersection = new PixelPoint(first.Point.X + t * first.Direction.X,
            first.Point.Y + t * first.Direction.Y);
        return true;
    }

    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private readonly record struct EdgeSample(double Along, double Offset);
    private readonly record struct RefinedLine(PixelPoint Point, PixelPoint Direction);
}
