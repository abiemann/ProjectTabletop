using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>A dark pupil and bright surround in raw, unmirrored camera pixels.
/// Score measures the visual match, not probability, contact or depth.</summary>
public sealed record EyeTipObservation(PixelPoint Center, double RadiusPixels,
    double Score, double Contrast, double BrightRingCoverage);

public sealed record EyeTipDetectionResult(IReadOnlyList<EyeTipObservation> Candidates, string Reason);

/// <summary>Finds small eye stickers without a learned model. A selected marker still needs
/// temporal association: another printed dot on a bright surface can have the same appearance.</summary>
public static class EyeTipDetector
{
    private const int MaximumDimension = 1920;
    private const int RingSamples = 24;
    private static readonly int[] Thresholds = [40, 70, 105, 145, 185];

    public static EyeTipDetectionResult Detect(int width, int height, int stride, byte[] bgra,
        EyeTipDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        options?.Validate();
        EyeTipProjectionMatcher? projection = options?.ProjectionFrames is null ? null :
            new(options.ProjectionFrames, options.FrameTime);
        if (projection is { Ready: false }) return new([], "waiting-for-projected-eye-reference");

        using Mat source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra, stride);
        using Mat reduced = new();
        double reduction = Math.Min(1, MaximumDimension / (double)Math.Max(width, height));
        if (reduction < 1)
            Cv2.Resize(source, reduced, new Size(Math.Max(1, (int)Math.Round(width * reduction)),
                Math.Max(1, (int)Math.Round(height * reduction))), interpolation: InterpolationFlags.Area);
        else source.CopyTo(reduced);
        using Mat gray = new();
        Cv2.CvtColor(reduced, gray, ColorConversionCodes.BGRA2GRAY);
        int workingWidth = gray.Width, workingHeight = gray.Height;
        byte[] pixels = new byte[workingWidth * workingHeight];
        Marshal.Copy(gray.Data, pixels, 0, pixels.Length);
        using Mat mask = new();
        var candidates = new List<EyeTipObservation>();
        double scaleX = width / (double)workingWidth, scaleY = height / (double)workingHeight;

        // Several fixed luminance cuts separate a dark pupil from its own light ring even
        // when the ring is darker than the projected background. A local adaptive cut can
        // instead join that whole sticker to the shaft. Cost is bounded by image size and
        // these five passes; expensive ring checks are capped per pass.
        foreach (int threshold in Thresholds)
        {
            Cv2.Threshold(gray, mask, threshold, 255, ThresholdTypes.BinaryInv);
            // Keep nested contours: on a dark scene the white annulus is a hole in
            // the background component, and the real dark pupil sits inside that hole.
            Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.List,
                ContourApproximationModes.ApproxSimple);
            int checkedShapes = 0;
            foreach (Point[] contour in contours)
            {
                double area = Math.Abs(Cv2.ContourArea(contour));
                if (area < 10 || area > 20000) continue;
                double rawRadius = Math.Sqrt(area / Math.PI * scaleX * scaleY);
                if (options?.ExpectedRadiusPixels is { } expectedRadius &&
                    (rawRadius < expectedRadius * .60 || rawRadius > expectedRadius * 1.65)) continue;
                Rect bounds = Cv2.BoundingRect(contour);
                if (bounds.X <= 1 || bounds.Y <= 1 || bounds.Right >= workingWidth - 1 ||
                    bounds.Bottom >= workingHeight - 1 || bounds.Width > 200 || bounds.Height > 200) continue;
                double perimeter = Cv2.ArcLength(contour, true);
                double circularity = 4 * Math.PI * area / Math.Max(1, perimeter * perimeter);
                if (circularity < .52 || area / (bounds.Width * (double)bounds.Height) < .43) continue;
                Moments moments = Cv2.Moments(contour);
                if (moments.M00 <= 0) continue;
                double cx = moments.M10 / moments.M00, cy = moments.M01 / moments.M00;
                double xx = moments.Mu20 / moments.M00, yy = moments.Mu02 / moments.M00;
                double xy = moments.Mu11 / moments.M00;
                double spread = Math.Sqrt(Math.Max(0, (xx - yy) * (xx - yy) + 4 * xy * xy));
                double major = 2 * Math.Sqrt(Math.Max(0, (xx + yy + spread) / 2));
                double minor = 2 * Math.Sqrt(Math.Max(0, (xx + yy - spread) / 2));
                if (minor < 1.4 || major > 85 || major / minor > 2.6) continue;
                if (++checkedShapes > 1024) break;
                double angle = .5 * Math.Atan2(2 * xy, xx - yy);
                double cosine = Math.Cos(angle), sine = Math.Sin(angle);

                double At(double radial, double theta)
                {
                    double x = Math.Cos(theta) * major * radial;
                    double y = Math.Sin(theta) * minor * radial;
                    return Sample(pixels, workingWidth, workingHeight,
                        cx + x * cosine - y * sine, cy + x * sine + y * cosine);
                }

                // Median interior intensity tolerates a small shiny highlight. Most of the
                // disk must still be dark; this rejects hollow circles and ordinary lettering.
                var interior = new double[25];
                interior[0] = Sample(pixels, workingWidth, workingHeight, cx, cy);
                for (int i = 0; i < 24; i++) interior[i + 1] = At(i < 8 ? .35 : .72, i * Math.PI / 8);
                Array.Sort(interior);
                double pupil = interior[interior.Length / 2];
                var ring = new double[RingSamples];
                var exterior = new double[RingSamples];
                bool offFrame = false;
                for (int i = 0; i < RingSamples; i++)
                {
                    double theta = i * Math.PI * 2 / RingSamples;
                    double inner = At(1.30, theta), middle = At(1.65, theta), outer = At(2.0, theta);
                    double beyond = At(2.5, theta), far = At(3.0, theta);
                    if (inner < 0 || middle < 0 || outer < 0 || beyond < 0 || far < 0)
                    { offFrame = true; break; }
                    // A narrow annulus need not extend across all three radii, but one isolated
                    // white pixel is insufficient to explain this angular sector.
                    ring[i] = Math.Max((inner + middle) / 2, (middle + outer) / 2);
                    exterior[i] = (beyond + far) / 2;
                }
                if (offFrame) continue;
                double[] orderedRing = ring.Order().ToArray();
                double light = orderedRing[RingSamples / 2], contrast = light - pupil;
                if (light < 45 || contrast < 28) continue;
                double ringCoverage = ring.Count(value => value - pupil >= Math.Max(22, contrast * .45)) /
                    (double)RingSamples;
                double darkFill = interior.Count(value => value <= pupil + contrast * .45) / (double)interior.Length;
                if (ringCoverage < .80 || darkFill < .76) continue;
                // A pupil on any bright floor/paper is insufficient. The sticker's light
                // annulus must end against a darker exterior in several angular sectors.
                // Most of its outline may merge into a bright board; the exposed edge over
                // the shaft still supplies evidence. Never infer that edge from score alone.
                int finiteRingSectors = Enumerable.Range(0, RingSamples).Count(i =>
                    ring[i] - exterior[i] >= Math.Max(12, contrast * .14));
                if (finiteRingSectors < 4) continue;
                double score = .30 * Math.Clamp(contrast / 115, 0, 1) + .25 * ringCoverage +
                    .25 * Math.Clamp(circularity, 0, 1) + .20 * darkFill;
                var observation = new EyeTipObservation(new(cx * scaleX, cy * scaleY),
                    rawRadius, score, contrast, ringCoverage);
                int duplicate = candidates.FindIndex(previous =>
                    Distance(previous.Center, observation.Center) <= Math.Max(3 * scaleX,
                        Math.Min(previous.RadiusPixels, observation.RadiusPixels) * .8));
                if (duplicate < 0) candidates.Add(observation);
                else if (observation.Score > candidates[duplicate].Score) candidates[duplicate] = observation;
            }
        }
        // Preserve the clicked/associated marker before bounding work. Projection clutter can
        // score higher than a real pupil, so the rendered-scene veto precedes the final top eight.
        IEnumerable<EyeTipObservation> ranked = options?.PreferredCenter is { } preferred ?
            candidates.OrderBy(item => Distance(item.Center, preferred)).ThenByDescending(item => item.Score) :
            candidates.OrderByDescending(item => item.Score);
        EyeTipObservation[] best = ranked.Take(options is null ? 8 : 128)
            .Where(item => projection is null || projection.IsPhysicalCandidate(item, pixels,
                workingWidth, workingHeight, scaleX, scaleY)).Take(8).ToArray();
        return new(best, best.Length == 0 ? "no-eye-marker" : best.Length == 1 ? "eye-candidate" : "multiple-eye-candidates");
    }

    private static double Sample(byte[] pixels, int width, int height, double x, double y)
    {
        if (x < 0 || y < 0 || x >= width - 1 || y >= height - 1) return -1;
        int left = (int)x, top = (int)y;
        double dx = x - left, dy = y - top;
        return pixels[top * width + left] * (1 - dx) * (1 - dy) +
            pixels[top * width + left + 1] * dx * (1 - dy) +
            pixels[(top + 1) * width + left] * (1 - dx) * dy +
            pixels[(top + 1) * width + left + 1] * dx * dy;
    }

    internal static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
