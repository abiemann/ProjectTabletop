using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>The learned black crossbar of a cardboard T. Area is relative to the camera frame;
/// aspect ratio uses a rotated rectangle, so rotating the bat does not invalidate the profile.</summary>
public sealed record BlackTipProfile(int Version, double Value, double Saturation,
    double NormalizedArea, double AspectRatio = 1)
{
    public double NormalizedRadius => Math.Sqrt(NormalizedArea / Math.PI);
    public bool IsValid => Version == 1 && double.IsFinite(Value) && Value is >= 0 and <= .38 &&
        double.IsFinite(Saturation) && Saturation is >= 0 and <= .70 &&
        double.IsFinite(NormalizedArea) && NormalizedArea is >= .0000001 and <= .025 &&
        double.IsFinite(AspectRatio) && AspectRatio is >= 1.6 and <= 8;
}

public sealed record BlackTipLearningOptions(IReadOnlyList<EyeTipProjectionFrame>? ProjectionFrames = null,
    DateTimeOffset FrameTime = default);

/// <summary>
/// Detects the solid black top crossbar of a cardboard T, not its uncoloured handle. Learned
/// size and elongation reject round holes; fresh projected-scene references veto the app's
/// own black artwork. Returns the common measured-component contract for ColorTipTracker.
/// Callers must partition identical bats by calibrated player half BEFORE temporal association.
/// </summary>
public static class BlackTipDetector
{
    private const int MaximumDimension = 1920;

    public static BlackTipProfile Learn(int width, int height, int stride, byte[] bgra,
        PixelPoint click, BlackTipLearningOptions? options = null)
    {
        ValidateFrame(width, height, stride, bgra);
        if (!double.IsFinite(click.X) || !double.IsFinite(click.Y) || click.X < 3 || click.Y < 3 ||
            click.X >= width - 3 || click.Y >= height - 3)
            throw new ArgumentException("Click inside the black crossbar, away from the camera edge.");
        var image = new BlackImage(width, height, stride, bgra);
        int cx = (int)Math.Round(click.X / image.ScaleX), cy = (int)Math.Round(click.Y / image.ScaleY);
        if (cx < 2 || cy < 2 || cx >= image.Width - 2 || cy >= image.Height - 2)
            throw new ArgumentException("Click the black crossbar away from the camera edge.");
        var patch = new List<(double Value, double Saturation)>();
        for (int y = cy - 1; y <= cy + 1; y++)
        for (int x = cx - 1; x <= cx + 1; x++)
        {
            int index = (y * image.Width + x) * 3;
            double value = image.Hsv[index + 2] / 255.0, saturation = image.Hsv[index + 1] / 255.0;
            if (value <= .38 && (saturation <= .70 || value <= .12)) patch.Add((value, saturation));
        }
        if (patch.Count < 7)
            throw new InvalidOperationException("Click the solid black middle of the T's crossbar. Keep the handle uncoloured.");
        var seed = new BlackTipProfile(1, patch.Select(item => item.Value).Order().ElementAt(patch.Count / 2),
            Math.Min(.70, patch.Select(item => item.Saturation).Order().ElementAt(patch.Count / 2)), .0001, 3);
        using Mat mask = image.Mask(seed);
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
        var selected = contours.Where((contour, index) => hierarchy[index].Parent < 0 &&
                Cv2.PointPolygonTest(contour, new Point2f(cx, cy), false) >= 0)
            .Select(contour => Component(contour, image, seed, null))
            .Where(item => item is not null).OrderBy(item => item!.Observation.AreaPixels).FirstOrDefault();
        if (selected is null || selected.Aspect < 1.8)
            throw new InvalidOperationException("The selected mark is not a separate black crossbar. Show the whole long bar and click its middle.");
        var result = seed with
        {
            NormalizedArea = selected.Observation.AreaPixels / (width * (double)height),
            AspectRatio = selected.Aspect
        };
        if (!result.IsValid) throw new InvalidOperationException("The crossbar is outside the supported size or shape. Make its width about 2–8 times its thickness.");
        if (options?.ProjectionFrames is { } frames)
        {
            var projection = new EyeTipProjectionMatcher(frames, options.FrameTime);
            if (!projection.Ready)
                throw new InvalidOperationException("Wait for a current projected-board reference, then click the physical crossbar again.");
            if (!projection.ContainsBoardPoint(selected.Observation.Center))
                throw new InvalidOperationException("Place the cardboard T on the projected board, then click its black crossbar again.");
            if (!projection.IsPhysicalCandidate(selected.Observation.Center, selected.Observation.RadiusPixels,
                image.Gray, image.Width, image.Height, image.ScaleX, image.ScaleY))
                throw new InvalidOperationException("That mark is part of the projected picture. Click the black crossbar on your cardboard T.");
        }
        return result;
    }

    public static ColorTipDetectionResult Detect(int width, int height, int stride, byte[] bgra,
        BlackTipProfile profile, ColorTipDetectionOptions? options = null) =>
        DetectEach(width, height, stride, bgra, [profile], options)[0];

    /// <summary>Detects each learned bar in one camera frame, in profile order. The colour
    /// conversion and projected-scene reference are shared; only the dark threshold and the
    /// learned size and shape differ per profile.</summary>
    public static IReadOnlyList<ColorTipDetectionResult> DetectEach(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<BlackTipProfile> profiles, ColorTipDetectionOptions? options = null)
    {
        ValidateFrame(width, height, stride, bgra);
        ArgumentNullException.ThrowIfNull(profiles);
        foreach (var profile in profiles)
        {
            ArgumentNullException.ThrowIfNull(profile, nameof(profiles));
            if (!profile.IsValid) throw new ArgumentException("Invalid black-crossbar profile.", nameof(profiles));
        }
        if (options?.PreferredCenter is { } preferred && (!double.IsFinite(preferred.X) ||
            !double.IsFinite(preferred.Y) || preferred.X < 0 || preferred.Y < 0))
            throw new ArgumentException("Invalid preferred black-crossbar center.", nameof(options));
        EyeTipProjectionMatcher? projection = options?.ProjectionFrames is null ? null :
            new(options.ProjectionFrames, options.FrameTime);
        if (projection is { Ready: false })
            return profiles.Select(_ => new ColorTipDetectionResult([], "waiting-for-projected-bat-reference")).ToArray();
        var image = new BlackImage(width, height, stride, bgra);
        return profiles.Select(profile => Detect(image, profile, width * (double)height,
            options?.PreferredCenter, projection)).ToArray();
    }

    private static ColorTipDetectionResult Detect(BlackImage image, BlackTipProfile profile, double framePixels,
        PixelPoint? preferred, EyeTipProjectionMatcher? projection)
    {
        using Mat mask = image.Mask(profile);
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
        double expectedArea = profile.NormalizedArea * framePixels;
        var candidates = contours.Where((_, index) => hierarchy[index].Parent < 0)
            .Select(contour => Component(contour, image, profile, expectedArea))
            .Where(item => item is not null).Select(item => item!.Observation);
        var ranked = preferred is { } point
            ? candidates.OrderBy(item => ColorTipDetector.Distance(item.Center, point)).ThenByDescending(item => item.Score)
            : candidates.OrderByDescending(item => item.Score);
        var accepted = ranked.Take(128).Where(item => projection is null ||
            projection.IsPhysicalCandidate(item.Center, item.RadiusPixels, image.Gray, image.Width, image.Height,
                image.ScaleX, image.ScaleY)).Take(16).ToArray();
        return new(accepted, accepted.Length == 0 ? "no-black-crossbar" :
            accepted.Length == 1 ? "black-crossbar-candidate" : "multiple-black-crossbars");
    }

    private sealed record ComponentShape(ColorTipObservation Observation, double Aspect);

    private static ComponentShape? Component(Point[] contour, BlackImage image, BlackTipProfile profile,
        double? expectedArea)
    {
        double area = Math.Abs(Cv2.ContourArea(contour));
        if (area < 12 || area > image.Width * (double)image.Height * .025) return null;
        Rect bounds = Cv2.BoundingRect(contour);
        if (bounds.X < 1 || bounds.Y < 1 || bounds.Right >= image.Width - 1 || bounds.Bottom >= image.Height - 1)
            return null;
        RotatedRect rotated = Cv2.MinAreaRect(contour);
        double longSide = Math.Max(rotated.Size.Width, rotated.Size.Height),
            shortSide = Math.Min(rotated.Size.Width, rotated.Size.Height);
        if (shortSide < 2.4 || longSide / shortSide > 8.4) return null;
        double aspect = longSide / shortSide;
        double fill = area / Math.Max(1, longSide * shortSide);
        double hullArea = Math.Abs(Cv2.ContourArea(Cv2.ConvexHull(contour)));
        if (fill < .54 || area / Math.Max(1, hullArea) < .74) return null;
        if (expectedArea is not null && (aspect < Math.Max(1.45, profile.AspectRatio / 1.8) ||
            aspect > profile.AspectRatio * 1.8)) return null;
        double rawArea = area * image.ScaleX * image.ScaleY;
        if (expectedArea is { } expected && (rawArea < expected * .45 || rawArea > expected * 2.2)) return null;
        Moments moments = Cv2.Moments(contour);
        if (moments.M00 <= 0) return null;
        double cx = moments.M10 / moments.M00, cy = moments.M01 / moments.M00;
        // Hole contours are children; this explicit dark-core check also rejects an enclosing
        // ring whose geometric centroid falls in its light interior.
        if (!image.DarkCore(cx, cy, profile)) return null;
        double contrast = image.Contrast(contour, bounds);
        if (contrast < 13) return null;
        double sizeScore = expectedArea is { } desired ? Math.Exp(-Math.Abs(Math.Log(rawArea / desired))) : 1;
        double shapeScore = expectedArea is null ? fill : Math.Exp(-Math.Abs(Math.Log(aspect / profile.AspectRatio))) * fill;
        double score = Math.Clamp(.45 * sizeScore + .40 * shapeScore + .15 * Math.Min(1, contrast / 55), 0, 1);
        return new(new(new(cx * image.ScaleX, cy * image.ScaleY), Math.Sqrt(rawArea / Math.PI), rawArea, score), aspect);
    }

    private sealed class BlackImage
    {
        public int Width { get; }
        public int Height { get; }
        public double ScaleX { get; }
        public double ScaleY { get; }
        public byte[] Hsv { get; }
        public byte[] Gray { get; }

        public BlackImage(int width, int height, int stride, byte[] bgra)
        {
            using Mat source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra, stride);
            using Mat reduced = new();
            double reduction = Math.Min(1, MaximumDimension / (double)Math.Max(width, height));
            if (reduction < 1) Cv2.Resize(source, reduced, new Size(Math.Max(1, (int)Math.Round(width * reduction)),
                Math.Max(1, (int)Math.Round(height * reduction))), interpolation: InterpolationFlags.Area);
            else source.CopyTo(reduced);
            Width = reduced.Width; Height = reduced.Height;
            ScaleX = width / (double)Width; ScaleY = height / (double)Height;
            using Mat bgr = new(), hsv = new(), gray = new();
            Cv2.CvtColor(reduced, bgr, ColorConversionCodes.BGRA2BGR);
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.CvtColor(reduced, gray, ColorConversionCodes.BGRA2GRAY);
            Hsv = new byte[Width * Height * 3]; Gray = new byte[Width * Height];
            Marshal.Copy(hsv.Data, Hsv, 0, Hsv.Length);
            Marshal.Copy(gray.Data, Gray, 0, Gray.Length);
        }

        private bool IsDark(int pixel, BlackTipProfile profile)
        {
            double value = Hsv[pixel * 3 + 2] / 255.0, saturation = Hsv[pixel * 3 + 1] / 255.0;
            double maximumValue = Math.Clamp(profile.Value * 1.7 + .07, .18, .42);
            double maximumSaturation = Math.Clamp(profile.Saturation + .30, .36, .72);
            return value <= maximumValue && (saturation <= maximumSaturation || value <= .12);
        }

        public Mat Mask(BlackTipProfile profile)
        {
            byte[] pixels = new byte[Width * Height];
            for (int i = 0; i < pixels.Length; i++) if (IsDark(i, profile)) pixels[i] = 255;
            Mat mask = new(Height, Width, MatType.CV_8UC1);
            Marshal.Copy(pixels, 0, mask.Data, pixels.Length);
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Cross, new Size(3, 3));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            return mask;
        }

        public bool DarkCore(double x, double y, BlackTipProfile profile)
        {
            int cx = (int)Math.Round(x), cy = (int)Math.Round(y), count = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (IsDark((cy + dy) * Width + cx + dx, profile)) count++;
            return count >= 7;
        }

        public double Contrast(Point[] contour, Rect bounds)
        {
            double core = 0, surround = 0;
            int coreCount = 0, surroundCount = 0;
            int margin = Math.Clamp(Math.Min(bounds.Width, bounds.Height) / 2, 3, 10);
            int left = Math.Max(0, bounds.Left - margin), right = Math.Min(Width - 1, bounds.Right + margin);
            int top = Math.Max(0, bounds.Top - margin), bottom = Math.Min(Height - 1, bounds.Bottom + margin);
            // Sampling is bounded even for a large camera frame. The surrounding ring supplies
            // a local contrast test, avoiding acceptance of a whole uniformly dark patch.
            int step = Math.Max(1, Math.Max(right - left, bottom - top) / 70);
            for (int y = top; y <= bottom; y += step)
            for (int x = left; x <= right; x += step)
            {
                double distance = Cv2.PointPolygonTest(contour, new Point2f(x, y), true);
                if (distance >= 1) { core += Gray[y * Width + x]; coreCount++; }
                else if (distance < -1 && distance > -margin)
                { surround += Gray[y * Width + x]; surroundCount++; }
            }
            return coreCount < 3 || surroundCount < 8 ? 0 : surround / surroundCount - core / coreCount;
        }
    }

    private static void ValidateFrame(int width, int height, int stride, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
    }
}
