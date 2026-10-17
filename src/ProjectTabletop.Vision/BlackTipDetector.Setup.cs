using OpenCvSharp;

namespace ProjectTabletop.Vision;

public sealed record BlackTipLearningResult(BlackTipProfile? Profile, MarkerTipObservation? Marker, string Reason);

public static partial class BlackTipDetector
{
    /// <summary>Finds exactly one complete physical pair inside a neutral setup target.
    /// The result is a staged measurement; this method never changes a saved profile.</summary>
    public static BlackTipLearningResult LearnPairInTarget(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<PixelPoint> rawTarget, BlackTipLearningOptions? options = null)
    {
        ValidateFrame(width, height, stride, bgra);
        ArgumentNullException.ThrowIfNull(rawTarget);
        if (rawTarget.Count is < 3 or > 16 || rawTarget.Any(point => !double.IsFinite(point.X) ||
            !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height))
            throw new ArgumentException("Supply a finite target polygon inside the camera frame.", nameof(rawTarget));
        var polygon = rawTarget.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray();
        if (!Cv2.IsContourConvex(polygon) || Math.Abs(Cv2.ContourArea(polygon)) < 256)
            throw new ArgumentException("The setup target must be a convex area large enough for the complete marker.", nameof(rawTarget));
        using var polygonInput = InputArray.Create(polygon);
        var seed = new BlackTipProfile(1, .18, 0, .0001, 3);
        using var image = new BlackImage(width, height, stride, bgra, SearchBounds(rawTarget, [seed], width, height));
        EyeTipProjectionMatcher? projection = options?.ProjectionFrames is null ? null :
            new(options.ProjectionFrames, options.FrameTime);
        if (projection is { Ready: false }) return new(null, null, "waiting-for-projected-reference");
        using Mat mask = image.Mask(seed);
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
        var strips = new List<MarkerTipObservation>();
        for (int index = 0; index < contours.Length; index++)
        {
            var contour = contours[index];
            if (hierarchy[index].Parent >= 0 || contour.Any(point => Cv2.PointPolygonTest(polygonInput,
                new Point2f((float)image.ToRawX(point.X), (float)image.ToRawY(point.Y)), true) < 2)) continue;
            var shape = Component(contour, image, seed, null);
            if (shape is null || projection is not null && (!projection.ContainsBoardPoint(shape.Observation.Center) ||
                !Physical(projection, shape.Observation, image))) continue;
            strips.Add(shape.Observation);
            if (strips.Count > 32) return new(null, null, "too-many-marks-in-target");
        }
        var pairs = FindPairs(strips, null);
        pairs.RemoveAll(pair => !image.HasSeparatingGap(strips[pair.First], strips[pair.Second], seed));
        if (pairs.Count != 1) return new(null, null, pairs.Count == 0 ? "show-complete-double-bar-in-target" : "show-only-one-double-bar-in-target");
        var found = pairs[0];
        var first = strips[found.First];
        var second = strips[found.Second];
        Span<double> values = stackalloc double[18];
        Span<double> saturations = stackalloc double[18];
        int count = 0;
        foreach (var strip in new[] { first, second })
        {
            int cx = (int)Math.Round(strip.Center.X), cy = (int)Math.Round(strip.Center.Y);
            for (int y = cy - 1; y <= cy + 1; y++)
            for (int x = cx - 1; x <= cx + 1; x++)
            {
                int offset = y * stride + x * 4;
                int maximum = Math.Max(bgra[offset], Math.Max(bgra[offset + 1], bgra[offset + 2]));
                int minimum = Math.Min(bgra[offset], Math.Min(bgra[offset + 1], bgra[offset + 2]));
                if (maximum > .38 * 255) continue;
                values[count] = maximum / 255.0;
                saturations[count++] = maximum == 0 ? 0 : (maximum - minimum) / (double)maximum;
            }
        }
        if (count < 14) return new(null, null, "show-solid-black-ink-in-target");
        values[..count].Sort();
        saturations[..count].Sort();
        var profile = new BlackTipProfile(1, values[count / 2], Math.Min(.70, saturations[count / 2]),
            (first.AreaPixels + second.AreaPixels) / (2 * width * (double)height),
            (StripAspect(first) + StripAspect(second)) * .5, 2, found.Match.SpacingRatio);
        return profile.IsValid ? new(profile, found.Match.Observation, "double-bar-sample") :
            new(null, null, "marker-size-or-shape-not-supported");
    }
}
