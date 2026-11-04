namespace ProjectTabletop.Vision;

public readonly record struct CameraWhiteScanExposureResult(bool IsUsable, int SampleCount,
    double BrightnessP90, double ClippedFraction, int Direction, double? SuggestedExposureLog2Seconds);

/// <summary>
/// Coarse exposure headroom before board geometry is available. Call only with a fresh, settled
/// full-white scan frame; pixel brightness alone cannot establish which projection is displayed.
/// Recommendations use existing driver steps and never lengthen exposure beyond one camera frame.
/// </summary>
public static class CameraWhiteScanExposure
{
    public static CameraWhiteScanExposureResult Evaluate(int width, int height, int stride, byte[] bgra,
        double currentExposure, double minimumExposure, double maximumExposure, double exposureStep,
        double framesPerSecond, IReadOnlyList<PixelPoint>? ambientBoardCorners = null)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || stride < (long)width * 4 ||
            bgra.Length < (long)(height - 1) * stride + (long)width * 4)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (!double.IsFinite(currentExposure) || !double.IsFinite(minimumExposure) ||
            !double.IsFinite(maximumExposure) || !double.IsFinite(exposureStep) || exposureStep <= 0 ||
            minimumExposure > maximumExposure || currentExposure < minimumExposure || currentExposure > maximumExposure ||
            !double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
            return default;
        double steps = (currentExposure - minimumExposure) / exposureStep;
        double maximumSteps = (Math.Min(maximumExposure, Math.Log2(1 / framesPerSecond)) - minimumExposure) / exposureStep;
        if (!double.IsFinite(steps) || !double.IsFinite(maximumSteps) || maximumSteps < 0 ||
            Math.Abs(steps - Math.Round(steps)) > 1e-6) return default;
        double allowedMaximum = minimumExposure + Math.Floor(maximumSteps + 1e-9) * exposureStep;

        Span<PixelPoint> corners = stackalloc PixelPoint[4];
        if (ambientBoardCorners is null)
        {
            // The central field avoids unlit room margins. A substantial illuminated region is
            // still required, so an isolated lamp or hot pixel cannot request more exposure.
            corners[0] = new(width * .2, height * .2);
            corners[1] = new(width * .8, height * .2);
            corners[2] = new(width * .8, height * .8);
            corners[3] = new(width * .2, height * .8);
        }
        else
        {
            if (ambientBoardCorners.Count != 4) return default;
            for (int i = 0; i < 4; i++) corners[i] = ambientBoardCorners[i];
            double sign = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = corners[i]; var b = corners[(i + 1) % 4]; var c = corners[(i + 2) % 4];
                if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || a.X < 0 || a.X >= width || a.Y < 0 || a.Y >= height)
                    return default;
                double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                if (!double.IsFinite(cross) || Math.Abs(cross) < 1 || sign != 0 && Math.Sign(cross) != sign) return default;
                sign = Math.Sign(cross);
            }
        }

        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        int columns = Math.Min(32, width), rows = Math.Min(24, height), count = 0, clipped = 0;
        double inset = ambientBoardCorners is null ? 0 : .15;
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            double u = inset + (1 - 2 * inset) * (column + .5) / columns;
            double v = inset + (1 - 2 * inset) * (row + .5) / rows;
            double x = (1 - v) * ((1 - u) * corners[0].X + u * corners[1].X) +
                v * ((1 - u) * corners[3].X + u * corners[2].X);
            double y = (1 - v) * ((1 - u) * corners[0].Y + u * corners[1].Y) +
                v * ((1 - u) * corners[3].Y + u * corners[2].Y);
            int offset = (int)y * stride + (int)x * 4;
            int level = Math.Max(bgra[offset], Math.Max(bgra[offset + 1], bgra[offset + 2]));
            if (level < 32) continue;
            histogram[level]++;
            count++;
            if (level >= 250) clipped++;
        }
        if (count < Math.Max(48, columns * rows / 5)) return new(false, count, 0, 0, 0, null);
        int cumulative = 0, percentile = 0, rank = (int)Math.Ceiling(count * .9);
        for (; percentile < 255; percentile++)
        {
            cumulative += histogram[percentile];
            if (cumulative >= rank) break;
        }
        double clippedFraction = clipped / (double)count;
        int direction = percentile > 235 || clippedFraction >= .08 ? -1 : percentile < 180 ? 1 : 0;
        if (direction == 0) return new(true, count, percentile, clippedFraction, 0, null);
        double target = Math.Clamp(currentExposure + direction * exposureStep, minimumExposure, allowedMaximum);
        int actualDirection = Math.Sign(target - currentExposure);
        // A darker frame must never request a darker setting merely because its current exposure
        // exceeds the timing cap. Leave that incompatible state for the caller to resolve explicitly.
        if (actualDirection != direction) return new(true, count, percentile, clippedFraction, 0, null);
        return new(true, count, percentile, clippedFraction, actualDirection, target);
    }
}
