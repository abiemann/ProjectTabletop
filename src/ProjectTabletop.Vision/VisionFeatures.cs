using OpenCvSharp;

namespace ProjectTabletop.Vision;

internal sealed class VisionFeatures
{
    public const int AngleBins = 72;
    private const int PatternRings = 3;
    private const int Harmonics = 6;
    private static readonly double[] RingFractions = [0.32, 0.58, 0.82];

    public required PixelPoint Center { get; init; }
    public required double Area { get; init; }
    public required double[] Radial { get; init; }
    public required double[] Pattern { get; init; }
    public required double[] Vector { get; init; }

    public static VisionFeatures Extract(Mat gray, IReadOnlyList<PixelPoint> polygon, int patternThreshold)
    {
        if (polygon.Count < 3)
            throw new ArgumentException("An outline needs at least three points.", nameof(polygon));

        Point[] points = polygon.Select(ToCvPoint).ToArray();
        double area = Math.Abs(Cv2.ContourArea(points));
        if (area < 1)
            throw new ArgumentException("The outline has no measurable area.", nameof(polygon));

        Moments moments = Cv2.Moments(points);
        var center = new PixelPoint(moments.M10 / moments.M00, moments.M01 / moments.M00);
        double radiusScale = Math.Sqrt(area / Math.PI);
        double[] radial = new double[AngleBins];
        double[] pattern = new double[AngleBins * PatternRings];
        for (int a = 0; a < AngleBins; a++)
        {
            double angle = a * 2 * Math.PI / AngleBins;
            double dx = Math.Cos(angle), dy = Math.Sin(angle);
            double radius = RayPolygonRadius(center, dx, dy, polygon);
            radial[a] = radius / radiusScale;
            for (int ring = 0; ring < PatternRings; ring++)
            {
                double x = center.X + dx * radius * RingFractions[ring];
                double y = center.Y + dy * radius * RingFractions[ring];
                pattern[ring * AngleBins + a] = BrightnessAt(gray, x, y, patternThreshold);
            }
        }

        var values = new List<double>(7 + Harmonics + PatternRings * (Harmonics + 1) + 2);
        foreach (double moment in moments.HuMoments())
            values.Add(-Math.Sign(moment) * Math.Log10(Math.Max(Math.Abs(moment), 1e-30)) / 20.0);
        AddSpectrum(values, radial);
        for (int ring = 0; ring < PatternRings; ring++)
            AddSpectrum(values, pattern.AsSpan(ring * AngleBins, AngleBins), includeMean: true);
        values.Add(Math.Log(area / (gray.Width * (double)gray.Height)));
        values.Add(Cv2.ContourArea(points) / Math.Max(Cv2.ContourArea(Cv2.ConvexHull(points)), 1));

        return new VisionFeatures
        {
            Center = center,
            Area = area,
            Radial = radial,
            Pattern = pattern,
            Vector = values.ToArray()
        };
    }

    public static double BestPoseError(VisionFeatures observed, VisionFeatures enrolled,
        double enrolledFrontAngleDegrees, double patternWeight, out double angleDegrees)
    {
        // Enrolled signatures are stored against camera X. Compare every circular shift
        // while retaining the enrolled front direction as the orientation reference.
        int enrolledFrontBin = Mod((int)Math.Round(enrolledFrontAngleDegrees * AngleBins / 360.0), AngleBins);
        double best = double.PositiveInfinity;
        int bestObservedFrontBin = 0;
        for (int observedFrontBin = 0; observedFrontBin < AngleBins; observedFrontBin++)
        {
            double shapeError = 0, patternError = 0;
            for (int a = 0; a < AngleBins; a++)
            {
                int enrolledBin = Mod(enrolledFrontBin + a, AngleBins);
                int observedBin = Mod(observedFrontBin + a, AngleBins);
                double diff = observed.Radial[observedBin] - enrolled.Radial[enrolledBin];
                shapeError += diff * diff;
                for (int ring = 0; ring < PatternRings; ring++)
                {
                    diff = observed.Pattern[ring * AngleBins + observedBin]
                        - enrolled.Pattern[ring * AngleBins + enrolledBin];
                    patternError += diff * diff;
                }
            }
            double error = shapeError / AngleBins + patternWeight * patternError / (AngleBins * PatternRings);
            if (error < best)
            {
                best = error;
                bestObservedFrontBin = observedFrontBin;
            }
        }
        angleDegrees = bestObservedFrontBin * 360.0 / AngleBins;
        return best;
    }

    public static PixelPoint[] FitOutline(IReadOnlyList<PixelPoint> enrolledOutline,
        VisionFeatures enrolled, double enrolledFrontAngleDegrees,
        VisionFeatures observed, double observedFrontAngleDegrees)
    {
        double angle = (observedFrontAngleDegrees - enrolledFrontAngleDegrees) * Math.PI / 180.0;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double scale = Math.Sqrt(observed.Area / enrolled.Area);
        return enrolledOutline.Select(point =>
        {
            double x = (point.X - enrolled.Center.X) * scale;
            double y = (point.Y - enrolled.Center.Y) * scale;
            return new PixelPoint(observed.Center.X + x * cos - y * sin,
                observed.Center.Y + x * sin + y * cos);
        }).ToArray();
    }

    private static void AddSpectrum(List<double> output, ReadOnlySpan<double> signal, bool includeMean = false)
    {
        if (includeMean)
            output.Add(signal.ToArray().Average());
        for (int harmonic = 1; harmonic <= Harmonics; harmonic++)
        {
            double real = 0, imaginary = 0;
            for (int i = 0; i < AngleBins; i++)
            {
                double phase = 2 * Math.PI * harmonic * i / AngleBins;
                real += signal[i] * Math.Cos(phase);
                imaginary += signal[i] * Math.Sin(phase);
            }
            output.Add(Math.Sqrt(real * real + imaginary * imaginary) / AngleBins);
        }
    }

    private static double BrightnessAt(Mat gray, double x, double y, int threshold)
    {
        int cx = (int)Math.Round(x), cy = (int)Math.Round(y);
        int width = gray.Width, height = gray.Height;
        int value = 0;
        // A local maximum keeps small reflective dots observable in coarse radial bins.
        for (int yy = Math.Max(0, cy - 2); yy <= Math.Min(height - 1, cy + 2); yy++)
            for (int xx = Math.Max(0, cx - 2); xx <= Math.Min(width - 1, cx + 2); xx++)
                value = Math.Max(value, gray.At<byte>(yy, xx));
        return Math.Clamp((value - threshold) / (double)Math.Max(1, 255 - threshold), 0, 1);
    }

    private static double RayPolygonRadius(PixelPoint center, double dx, double dy,
        IReadOnlyList<PixelPoint> polygon)
    {
        double furthest = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            PixelPoint a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double denom = Cross(dx, dy, ex, ey);
            if (Math.Abs(denom) < 1e-10)
                continue;
            double ax = a.X - center.X, ay = a.Y - center.Y;
            double t = Cross(ax, ay, ex, ey) / denom;
            double u = Cross(ax, ay, dx, dy) / denom;
            if (t > furthest && u >= -1e-6 && u <= 1 + 1e-6)
                furthest = t;
        }
        return furthest;
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
    private static int Mod(int value, int count) => (value % count + count) % count;
    internal static Point ToCvPoint(PixelPoint p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));
}
