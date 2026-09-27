namespace ProjectTabletop.Calibration;

/// <summary>An estimate of board side lengths from supplied full-image geometry, not a measured calibration.</summary>
public sealed record BoardSizeEstimate(double ShortSideCentimeters, double LongSideCentimeters)
{
    /// <summary>
    /// Estimates size from board corners in the full, unmodified projector image. The lens must
    /// point perpendicular to the board, so height equals throw distance. The supplied throw
    /// ratio and image aspect must match the optics, with no digital zoom or keystone correction.
    /// Corners must trace the convex perimeter in either direction. Small extrapolation beyond
    /// the image is allowed; coordinates are deliberately not clipped to the canvas.
    /// </summary>
    public static BoardSizeEstimate FromProjectorCorners(IReadOnlyList<Point2> normalizedCorners,
        double lensHeightCentimeters, double throwRatio, double projectorAspect)
    {
        ValidatePositive(lensHeightCentimeters, nameof(lensHeightCentimeters));
        ValidatePositive(throwRatio, nameof(throwRatio));
        ValidatePositive(projectorAspect, nameof(projectorAspect));
        double fullWidth = lensHeightCentimeters / throwRatio;
        double fullHeight = fullWidth / projectorAspect;
        if (!double.IsFinite(fullWidth) || fullWidth <= 0 || !double.IsFinite(fullHeight) || fullHeight <= 0)
            throw new ArgumentException("The supplied optics do not produce a finite, positive image size.");
        return FromImageDimensions(normalizedCorners, fullWidth, fullHeight);
    }

    /// <summary>
    /// Estimates board size using the physical width and height of the full output image,
    /// not the board. The image must form a rectangular field on the board's plane, with
    /// uniform, unwarped scaling along each image axis. The measurements must correspond
    /// to the same full image coordinate system as the normalized corners; do not substitute
    /// a content crop or the board dimensions. This method does not infer or correct tilt,
    /// keystone, or lens distortion, and makes no assumption about projector model or aspect.
    /// Corners may trace either convex perimeter direction and are not clipped to the image.
    /// </summary>
    public static BoardSizeEstimate FromImageDimensions(IReadOnlyList<Point2> normalizedCorners,
        double imageWidthCentimeters, double imageHeightCentimeters)
    {
        ValidatePositive(imageWidthCentimeters, nameof(imageWidthCentimeters));
        ValidatePositive(imageHeightCentimeters, nameof(imageHeightCentimeters));
        ValidateCorners(normalizedCorners);

        var edges = new double[4];
        for (int index = 0; index < 4; index++)
        {
            var first = normalizedCorners[index];
            var second = normalizedCorners[(index + 1) % 4];
            double dx = (second.X - first.X) * imageWidthCentimeters;
            double dy = (second.Y - first.Y) * imageHeightCentimeters;
            edges[index] = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(edges[index]) || edges[index] <= 0)
                throw new ArgumentException("The corners do not produce finite, positive board sides.", nameof(normalizedCorners));
        }

        // Independent edge observations may differ slightly; retain both measurements.
        double firstSide = edges[0] / 2 + edges[2] / 2;
        double secondSide = edges[1] / 2 + edges[3] / 2;
        return new(Math.Min(firstSide, secondSide), Math.Max(firstSide, secondSide));
    }

    private static void ValidatePositive(double value, string parameter)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameter, "Use a finite value greater than zero.");
    }

    private static void ValidateCorners(IReadOnlyList<Point2>? points)
    {
        ArgumentNullException.ThrowIfNull(points, "normalizedCorners");
        if (points.Count != 4 || points.Any(point => !point.IsFinite))
            throw new ArgumentException("Provide four finite corners in convex perimeter order.", "normalizedCorners");
        double scale = Math.Max(points.Max(point => point.X) - points.Min(point => point.X),
            points.Max(point => point.Y) - points.Min(point => point.Y));
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("The board corners have no usable extent.", "normalizedCorners");

        int orientation = 0;
        for (int index = 0; index < 4; index++)
        {
            var first = points[index];
            var second = points[(index + 1) % 4];
            var third = points[(index + 2) % 4];
            double dx = (second.X - first.X) / scale, dy = (second.Y - first.Y) / scale;
            double nextX = (third.X - second.X) / scale, nextY = (third.Y - second.Y) / scale;
            double cross = dx * nextY - dy * nextX;
            if (dx * dx + dy * dy < 1e-10 || Math.Abs(cross) < 1e-8)
                throw new ArgumentException("The board corners are duplicated or nearly collinear.", "normalizedCorners");
            int sign = Math.Sign(cross);
            if (orientation != 0 && sign != orientation)
                throw new ArgumentException("The board perimeter must be convex and must not cross itself.", "normalizedCorners");
            orientation = sign;
        }
    }
}
