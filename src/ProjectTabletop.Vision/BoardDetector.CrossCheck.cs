namespace ProjectTabletop.Vision;

public static partial class BoardDetector
{
    /// <summary>
    /// Largest disagreement, in camera pixels, between the ambient (black-projection) and the
    /// white-lit cardboard corners. Where the cardboard nearly fills the projected light, its
    /// white-lit edge merges with the edge of the light and is measured several pixels off,
    /// while the ambient scan sees that edge against the table. A corner within
    /// <paramref name="boundaryMargin"/> of one edge of the light is therefore compared only
    /// along that edge, and a corner near two edges not at all.
    /// </summary>
    /// <param name="boundaryCorners">Corners compared partly or not at all.</param>
    public static double CrossCheckError(BoardDetection ambient, BoardDetection white, BoardDetection field,
        double boundaryMargin, out int boundaryCorners)
    {
        ArgumentNullException.ThrowIfNull(ambient);
        ArgumentNullException.ThrowIfNull(white);
        ArgumentNullException.ThrowIfNull(field);
        if (ambient.Corners.Length != 4 || white.Corners.Length != 4 || field.Corners.Length < 3)
            throw new ArgumentException("Cross-checking needs four board corners and a projected field.");
        boundaryCorners = 0;
        double error = 0;
        for (int index = 0; index < 4; index++)
        {
            PixelPoint trusted = ambient.Corners[index], lit = white.Corners[index];
            double dx = lit.X - trusted.X, dy = lit.Y - trusted.Y;
            var near = Enumerable.Range(0, field.Corners.Length)
                .Select(edge => (Start: field.Corners[edge], End: field.Corners[(edge + 1) % field.Corners.Length]))
                .Where(edge => SegmentDistance(trusted, edge.Start, edge.End) <= boundaryMargin)
                .ToArray();
            if (near.Length > 0) boundaryCorners++;
            double offset = near.Length switch
            {
                0 => Math.Sqrt(dx * dx + dy * dy),
                1 => AlongEdge(dx, dy, near[0].Start, near[0].End),
                _ => 0
            };
            error = Math.Max(error, offset);
        }
        return error;
    }

    private static double AlongEdge(double dx, double dy, PixelPoint start, PixelPoint end)
    {
        double ex = end.X - start.X, ey = end.Y - start.Y, length = Math.Sqrt(ex * ex + ey * ey);
        return length > 0 ? Math.Abs((dx * ex + dy * ey) / length) : Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SegmentDistance(PixelPoint point, PixelPoint start, PixelPoint end)
    {
        double ex = end.X - start.X, ey = end.Y - start.Y, lengthSquared = ex * ex + ey * ey;
        double t = lengthSquared > 0
            ? Math.Clamp(((point.X - start.X) * ex + (point.Y - start.Y) * ey) / lengthSquared, 0, 1) : 0;
        double x = start.X + t * ex - point.X, y = start.Y + t * ey - point.Y;
        return Math.Sqrt(x * x + y * y);
    }
}
