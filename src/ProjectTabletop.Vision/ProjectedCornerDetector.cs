namespace ProjectTabletop.Vision;

/// <summary>
/// Camera-space intersection of the two short orange bracket arms projected at
/// one physical board corner. A null result means either arm was missing or its
/// fitted line was not reliable enough to guide a projector correction.
/// </summary>
public sealed record CornerMarkerDetection(
    int CornerIndex,
    PixelPoint Intersection,
    double FirstArmRmsPixels,
    double SecondArmRmsPixels,
    double Confidence);

/// <summary>
/// Measures the projected orange brackets against an independently detected
/// cardboard outline. The board corners are a search prior, not the answer:
/// this class uses only orange pixels in small, separate regions around them.
/// </summary>
public static class ProjectedCornerDetector
{
    public static CornerMarkerDetection?[] Detect(int width, int height, int stride,
        byte[] bgra, IReadOnlyList<PixelPoint> expectedBoardCorners)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(expectedBoardCorners);
        if (expectedBoardCorners.Count != 4)
            throw new ArgumentException("Four clockwise corners are required.", nameof(expectedBoardCorners));
        if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
            bgra.Length < (long)(height - 1) * stride + width * 4)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");

        var detections = new CornerMarkerDetection?[4];
        for (int index = 0; index < 4; index++)
            detections[index] = DetectCorner(width, height, stride, bgra,
                expectedBoardCorners, index);
        return detections;
    }

    private static CornerMarkerDetection? DetectCorner(int width, int height, int stride,
        byte[] bgra, IReadOnlyList<PixelPoint> corners, int index)
    {
        PixelPoint corner = corners[index];
        PixelPoint next = corners[(index + 1) % 4];
        PixelPoint previous = corners[(index + 3) % 4];
        double firstLength = Distance(corner, next);
        double secondLength = Distance(corner, previous);
        if (firstLength < 80 || secondLength < 80) return null;
        double ax = (next.X - corner.X) / firstLength;
        double ay = (next.Y - corner.Y) / firstLength;
        double bx = (previous.X - corner.X) / secondLength;
        double by = (previous.Y - corner.Y) / secondLength;
        double determinant = ax * by - ay * bx;
        if (Math.Abs(determinant) < 0.4) return null;

        // The compositor draws arms about 7% of the shortest projected edge.
        // The board-based bound deliberately reaches a little beyond them.
        double reach = Math.Clamp(Math.Min(firstLength, secondLength) * 0.085, 38, 90);
        int radius = (int)Math.Ceiling(reach * 1.45 + 24);
        int minX = Math.Max(0, (int)Math.Floor(corner.X) - radius);
        int maxX = Math.Min(width - 1, (int)Math.Ceiling(corner.X) + radius);
        int minY = Math.Max(0, (int)Math.Floor(corner.Y) - radius);
        int maxY = Math.Min(height - 1, (int)Math.Ceiling(corner.Y) + radius);
        var first = new List<ArmPixel>(400);
        var second = new List<ArmPixel>(400);

        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                int offset = y * stride + x * 4;
                int blue = bgra[offset], green = bgra[offset + 1], red = bgra[offset + 2];
                // Red excess survives the webcam's blue projector cast. The
                // white grid and brown floor are not selected by luminance.
                int excess = red - Math.Max(green, blue);
                if (red < 115 || excess < 36) continue;
                double dx = x - corner.X, dy = y - corner.Y;
                double u = (dx * by - dy * bx) / determinant;
                double v = (ax * dy - ay * dx) / determinant;
                if (u >= 11 && u <= reach && Math.Abs(v) <= 17)
                    first.Add(new ArmPixel(u, v));
                if (v >= 11 && v <= reach && Math.Abs(u) <= 17)
                    second.Add(new ArmPixel(v, u));
            }

        if (!TryFitArm(first, reach, out ArmLine firstLine) ||
            !TryFitArm(second, reach, out ArmLine secondLine)) return null;

        // v = first.Intercept + first.Slope*u;
        // u = second.Intercept + second.Slope*v.
        double divisor = 1 - firstLine.Slope * secondLine.Slope;
        if (Math.Abs(divisor) < 0.7) return null;
        double uAtJoint = (secondLine.Intercept +
            secondLine.Slope * firstLine.Intercept) / divisor;
        double vAtJoint = firstLine.Intercept + firstLine.Slope * uAtJoint;
        PixelPoint intersection = new(corner.X + ax * uAtJoint + bx * vAtJoint,
            corner.Y + ay * uAtJoint + by * vAtJoint);
        if (Distance(intersection, corner) > 18) return null;

        double confidence = Math.Clamp(
            Math.Min(firstLine.Coverage, secondLine.Coverage) *
            (1 - Math.Max(firstLine.Rms, secondLine.Rms) / 8), 0, 1);
        return new CornerMarkerDetection(index, intersection,
            firstLine.Rms, secondLine.Rms, confidence);
    }

    private static bool TryFitArm(List<ArmPixel> pixels, double reach, out ArmLine line)
    {
        line = default;
        if (pixels.Count < 40) return false;
        if (!LeastSquares(pixels, out double intercept, out double slope)) return false;
        double[] residuals = pixels.Select(pixel =>
            Math.Abs(pixel.Across - intercept - slope * pixel.Along)).Order().ToArray();
        double threshold = Math.Max(3.5, 2.8 * residuals[residuals.Length / 2]);
        ArmPixel[] inliers = pixels.Where(pixel =>
            Math.Abs(pixel.Across - intercept - slope * pixel.Along) <= threshold).ToArray();
        if (inliers.Length < 36 || inliers.Length < pixels.Count * 0.65 ||
            !LeastSquares(inliers, out intercept, out slope)) return false;

        // Every part of an arm must be present. A red smudge at a corner, or
        // only one unoccluded section, cannot supply alignment feedback.
        int occupiedBins = 0;
        for (int bin = 0; bin < 5; bin++)
        {
            double from = 11 + (reach - 11) * bin / 5;
            double to = 11 + (reach - 11) * (bin + 1) / 5;
            if (inliers.Count(pixel => pixel.Along >= from && pixel.Along < to) >= 5)
                occupiedBins++;
        }
        double spread = inliers.Max(pixel => pixel.Along) -
            inliers.Min(pixel => pixel.Along);
        double rms = Math.Sqrt(inliers.Average(pixel =>
            Math.Pow(pixel.Across - intercept - slope * pixel.Along, 2)));
        // The projector clips the lowest horizontal arm to one camera pixel
        // on this installation. Three populated bins still span a long enough
        // line for a stable intersection; confidence records the shorter arm.
        if (occupiedBins < 3 || spread < reach * 0.50 || rms > 4.5 ||
            Math.Abs(slope) > 0.20 || Math.Abs(intercept) > 15) return false;
        line = new ArmLine(intercept, slope, rms,
            Math.Min(1, occupiedBins / 5.0) * Math.Min(1, spread / (reach * 0.7)));
        return true;
    }

    private static bool LeastSquares(IReadOnlyList<ArmPixel> pixels,
        out double intercept, out double slope)
    {
        intercept = slope = 0;
        if (pixels.Count < 3) return false;
        double meanAlong = pixels.Average(pixel => pixel.Along);
        double meanAcross = pixels.Average(pixel => pixel.Across);
        double variance = 0, covariance = 0;
        foreach (ArmPixel pixel in pixels)
        {
            double delta = pixel.Along - meanAlong;
            variance += delta * delta;
            covariance += delta * (pixel.Across - meanAcross);
        }
        if (variance < 1) return false;
        slope = covariance / variance;
        intercept = meanAcross - slope * meanAlong;
        return true;
    }

    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private readonly record struct ArmPixel(double Along, double Across);
    private readonly record struct ArmLine(double Intercept, double Slope,
        double Rms, double Coverage);
}
