using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>Camera-specific colour and apparent component size learned from a clicked physical tip.
/// Hue is circular in degrees; saturation/value and area are normalized, independent of resolution.</summary>
public sealed record ColorTipProfile(int Version, double HueDegrees, double HueToleranceDegrees,
    double Saturation, double Value, double NormalizedArea)
{
    public double NormalizedRadius => Math.Sqrt(NormalizedArea / Math.PI);
    public bool IsValid => Version == 1 && double.IsFinite(HueDegrees) && HueDegrees is >= 0 and < 360 &&
        double.IsFinite(HueToleranceDegrees) && HueToleranceDegrees is >= 8 and <= 35 &&
        double.IsFinite(Saturation) && Saturation is >= .20 and <= 1 &&
        double.IsFinite(Value) && Value is >= .08 and <= 1 &&
        double.IsFinite(NormalizedArea) && NormalizedArea is >= .0000001 and <= .015;
}

/// <summary>A measured coloured component in raw, unmirrored camera pixels, not a predicted contact.</summary>
public sealed record ColorTipObservation(PixelPoint Center, double RadiusPixels, double AreaPixels, double Score);
public sealed record ColorTipDetectionResult(IReadOnlyList<ColorTipObservation> Candidates, string Reason);
public sealed record ColorTipDetectionOptions(PixelPoint? PreferredCenter = null,
    IReadOnlyList<EyeTipProjectionFrame>? ProjectionFrames = null, DateTimeOffset FrameTime = default);

/// <summary>Small saturated-colour tip detection. Learning selects hue and component scale, not
/// a fixed red/pink colour. Hue matching wraps through red's zero-degree boundary. Brightness is
/// allowed to change substantially under projection; grey highlights never become their own tip.</summary>
public static class ColorTipDetector
{
    private const int MaximumDimension = 1920;

    public static ColorTipProfile Learn(int width, int height, int stride, byte[] bgra, PixelPoint click)
    {
        ValidateFrame(width, height, stride, bgra);
        if (!double.IsFinite(click.X) || !double.IsFinite(click.Y) || click.X < 2 || click.Y < 2 ||
            click.X >= width - 2 || click.Y >= height - 2)
            throw new ArgumentException("Click inside the solid coloured tip, away from the camera edge.");
        var image = new ColorImage(width, height, stride, bgra);
        int cx = (int)Math.Round(click.X / image.ScaleX), cy = (int)Math.Round(click.Y / image.ScaleY);
        if (cx < 1 || cy < 1 || cx >= image.Width - 1 || cy >= image.Height - 1)
            throw new ArgumentException("Click the coloured tip away from the camera edge.");
        var patch = new List<(double Hue, double Saturation, double Value)>();
        // A 3x3 patch can learn a real 3-5 pixel radius pin; larger sampling would mix in the shaft.
        for (int y = cy - 1; y <= cy + 1; y++)
        for (int x = cx - 1; x <= cx + 1; x++)
        {
            int index = (y * image.Width + x) * 3;
            double saturation = image.Hsv[index + 1] / 255.0, value = image.Hsv[index + 2] / 255.0;
            if (saturation >= .20 && value >= .08)
                patch.Add((image.Hsv[index] * 2.0, saturation, value));
        }
        if (patch.Count < 5)
            throw new InvalidOperationException("The selected patch is too grey or dark. Click the solid colour near the centre of the tip.");
        double hue = CircularMean(patch.Select(item => (item.Hue, item.Saturation)));
        double spread = patch.Select(item => HueDistance(item.Hue, hue)).Order().ElementAt(patch.Count * 4 / 5);
        if (spread > 22)
            throw new InvalidOperationException("The selected patch contains several colours. Click the solid centre of the tip.");
        var seed = new ColorTipProfile(1, hue, Math.Clamp(spread * 2 + 10, 14, 28),
            patch.Select(item => item.Saturation).Order().ElementAt(patch.Count / 2),
            patch.Select(item => item.Value).Order().ElementAt(patch.Count / 2), .0001);
        using Mat mask = image.Mask(seed);
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var containing = contours.Where(contour => Cv2.PointPolygonTest(contour, new Point2f(cx, cy), false) >= 0)
            .Select(contour => Component(contour, image, null)).Where(item => item is not null).ToArray();
        if (containing.Length != 1)
            throw new InvalidOperationException("The selected colour is not a small separate tip. Keep the coloured end visible and click its centre.");
        var selected = containing[0]!;
        var profile = seed with { NormalizedArea = selected.AreaPixels / (width * (double)height) };
        if (!profile.IsValid) throw new InvalidOperationException("The coloured component is outside the supported tip size.");
        return profile;
    }

    public static ColorTipDetectionResult Detect(int width, int height, int stride, byte[] bgra,
        ColorTipProfile profile, ColorTipDetectionOptions? options = null)
    {
        ValidateFrame(width, height, stride, bgra);
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsValid) throw new ArgumentException("Invalid colour-tip profile.");
        if (options?.PreferredCenter is { } preferred && (!double.IsFinite(preferred.X) ||
            !double.IsFinite(preferred.Y) || preferred.X < 0 || preferred.Y < 0))
            throw new ArgumentException("Invalid preferred colour-tip center.");
        EyeTipProjectionMatcher? projection = options?.ProjectionFrames is null ? null :
            new(options.ProjectionFrames, options.FrameTime == default ? DateTimeOffset.UtcNow : options.FrameTime);
        if (projection is { Ready: false }) return new([], "waiting-for-projected-tip-reference");
        var image = new ColorImage(width, height, stride, bgra);
        using Mat mask = image.Mask(profile);
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        double expectedArea = profile.NormalizedArea * width * height;
        var components = contours.Select(contour => Component(contour, image, expectedArea, profile))
            .Where(item => item is not null).Cast<ColorTipObservation>();
        var ranked = options?.PreferredCenter is { } location ?
            components.OrderBy(item => Distance(item.Center, location)).ThenByDescending(item => item.Score) :
            components.OrderByDescending(item => item.Score);
        ColorTipObservation[] result = ranked.Take(128).Where(item => projection is null ||
            projection.IsPhysicalColorCandidate(item, profile, image.Gray, image.Width, image.Height,
                image.ScaleX, image.ScaleY)).Take(8).ToArray();
        return new(result, result.Length == 0 ? "no-colour-tip" : result.Length == 1 ? "colour-tip-candidate" : "multiple-colour-tips");
    }

    private static ColorTipObservation? Component(Point[] contour, ColorImage image, double? expectedArea,
        ColorTipProfile? profile = null)
    {
        double area = Math.Abs(Cv2.ContourArea(contour));
        if (area < 6 || area > image.Width * (double)image.Height * .015) return null;
        Rect bounds = Cv2.BoundingRect(contour);
        if (bounds.X < 1 || bounds.Y < 1 || bounds.Right >= image.Width - 1 || bounds.Bottom >= image.Height - 1 ||
            area / (bounds.Width * (double)bounds.Height) < .32) return null;
        double perimeter = Cv2.ArcLength(contour, true), circularity = 4 * Math.PI * area / (perimeter * perimeter);
        if (circularity < .25) return null;
        Moments moments = Cv2.Moments(contour);
        if (moments.M00 <= 0) return null;
        double xx = moments.Mu20 / moments.M00, yy = moments.Mu02 / moments.M00, xy = moments.Mu11 / moments.M00;
        double spread = Math.Sqrt(Math.Max(0, (xx - yy) * (xx - yy) + 4 * xy * xy));
        double major = Math.Max(0, (xx + yy + spread) / 2), minor = Math.Max(.01, (xx + yy - spread) / 2);
        if (major / minor > 3.5 * 3.5) return null;
        double rawArea = area * image.ScaleX * image.ScaleY;
        if (expectedArea is { } expected && (rawArea < expected * .35 || rawArea > expected * 2.8)) return null;
        double centerX = moments.M10 / moments.M00, centerY = moments.M01 / moments.M00;
        if (profile is not null && !CoreMatches(image, centerX, centerY, Math.Sqrt(area / Math.PI), profile)) return null;
        double sizeScore = expectedArea is { } desired ? Math.Exp(-Math.Abs(Math.Log(rawArea / desired))) : 1;
        double score = .55 * Math.Clamp(circularity, 0, 1) + .45 * sizeScore;
        return new(new(centerX * image.ScaleX, centerY * image.ScaleY),
            Math.Sqrt(rawArea / Math.PI), rawArea, score);
    }

    private static bool CoreMatches(ColorImage image, double centerX, double centerY, double radius,
        ColorTipProfile profile)
    {
        // A permissive edge mask preserves antialiased and illuminated marker boundaries.
        // Its centre must retain the learned colour's saturation: projected pebble shadows
        // can fragment a pink hand into small red-hued blobs, but are not the saturated pin.
        // Saturation is brightness-normalized, so a simple exposure change does not erase it.
        double coreRadius = Math.Clamp(radius * .55, 2, 4);
        int extent = (int)Math.Ceiling(coreRadius), count = 0, matched = 0;
        Span<byte> saturation = stackalloc byte[81];
        int cx = (int)Math.Round(centerX), cy = (int)Math.Round(centerY);
        for (int y = -extent; y <= extent; y++)
        for (int x = -extent; x <= extent; x++)
        {
            if (x * x + y * y > coreRadius * coreRadius) continue;
            int px = cx + x, py = cy + y;
            if (px < 0 || py < 0 || px >= image.Width || py >= image.Height) continue;
            count++;
            int index = (py * image.Width + px) * 3;
            if (HueDistance(image.Hsv[index] * 2, profile.HueDegrees) > profile.HueToleranceDegrees ||
                image.Hsv[index + 1] < 46) continue;
            saturation[matched++] = image.Hsv[index + 1];
        }
        if (matched < count * .65 || matched < 5) return false;
        saturation[..matched].Sort();
        double median = saturation[matched / 2] / 255.0;
        double upperQuartile = saturation[(matched - 1) * 3 / 4] / 255.0;
        // A glossy pin can carry a small pale projector highlight across its centre. Require
        // substantial strongly coloured support around it, rather than lowering the uniform
        // colour requirement until a less-saturated pink hand can pass.
        return median >= profile.Saturation * .85 ||
            (median >= profile.Saturation * .75 && upperQuartile >= profile.Saturation * .95);
    }

    private sealed class ColorImage
    {
        public int Width { get; }
        public int Height { get; }
        public double ScaleX { get; }
        public double ScaleY { get; }
        public byte[] Hsv { get; }
        public byte[] Gray { get; }
        public ColorImage(int width, int height, int stride, byte[] bgra)
        {
            using Mat source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra, stride);
            using Mat reduced = new();
            double reduction = Math.Min(1, MaximumDimension / (double)Math.Max(width, height));
            if (reduction < 1) Cv2.Resize(source, reduced, new Size(Math.Max(1, (int)Math.Round(width * reduction)),
                Math.Max(1, (int)Math.Round(height * reduction))), interpolation: InterpolationFlags.Area);
            else source.CopyTo(reduced);
            Width = reduced.Width; Height = reduced.Height; ScaleX = width / (double)Width; ScaleY = height / (double)Height;
            using Mat bgr = new(), hsv = new(), gray = new();
            Cv2.CvtColor(reduced, bgr, ColorConversionCodes.BGRA2BGR);
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.CvtColor(reduced, gray, ColorConversionCodes.BGRA2GRAY);
            Hsv = new byte[Width * Height * 3]; Gray = new byte[Width * Height];
            Marshal.Copy(hsv.Data, Hsv, 0, Hsv.Length); Marshal.Copy(gray.Data, Gray, 0, Gray.Length);
        }
        public Mat Mask(ColorTipProfile profile)
        {
            byte[] pixels = new byte[Width * Height];
            // Weak same-hue wood/skin must not bridge the pin into a long shaft component.
            // Preserve its saturated body before shape analysis, then verify the stronger core.
            double minimumSaturation = Math.Max(.18, profile.Saturation * .75);
            double minimumValue = Math.Max(.06, Math.Min(.18, profile.Value * .25));
            for (int i = 0; i < pixels.Length; i++)
                if (Hsv[i * 3 + 1] >= minimumSaturation * 255 && Hsv[i * 3 + 2] >= minimumValue * 255 &&
                    HueDistance(Hsv[i * 3] * 2, profile.HueDegrees) <= profile.HueToleranceDegrees)
                    pixels[i] = 255;
            Mat mask = new(Height, Width, MatType.CV_8UC1);
            Marshal.Copy(pixels, 0, mask.Data, pixels.Length);
            // One-pixel colour bridges and tails can join a small pin to the shaft. A minimal
            // cross opening removes those narrow connections while preserving small solid pins.
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Cross, new Size(3, 3));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            return mask;
        }
    }

    private static double CircularMean(IEnumerable<(double Hue, double Weight)> samples)
    {
        double x = 0, y = 0;
        foreach (var sample in samples)
        { x += Math.Cos(sample.Hue * Math.PI / 180) * sample.Weight; y += Math.Sin(sample.Hue * Math.PI / 180) * sample.Weight; }
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }
    internal static double HueDistance(double first, double second) => Math.Min(Math.Abs(first - second), 360 - Math.Abs(first - second));
    internal static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    private static void ValidateFrame(int width, int height, int stride, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
    }
}
