namespace ProjectTabletop.Calibration;

/// <summary>Keeps the board's viewing direction in projector space, independent of camera roll.</summary>
public static class BoardOrientation
{
    /// <summary>
    /// Reorders a convex perimeter without moving any corner. The board's logical top edge
    /// follows the saved projector-pixel heading as closely as its four physical edges allow.
    /// Screen Y increases downward, so returned corners have clockwise winding.
    /// </summary>
    public static Point2[] Orient(IReadOnlyList<Point2> corners, double headingDegrees, double displayAspect)
    {
        if (!double.IsFinite(headingDegrees))
            throw new ArgumentOutOfRangeException(nameof(headingDegrees), "Use a finite heading.");
        ValidateAspect(displayAspect);
        var clockwise = Clockwise(corners);
        double reference = NormalizeDegrees(headingDegrees) * Math.PI / 180;
        int selected = 0;
        double best = double.NegativeInfinity;
        for (int index = 0; index < 4; index++)
        {
            double angle = EdgeAngle(clockwise[index], clockwise[(index + 1) % 4], displayAspect);
            double agreement = Math.Cos(angle - reference);
            // Geometry, rather than incoming array order, breaks an exact diagonal tie.
            bool canonicalTie = Math.Abs(agreement - best) <= 1e-12 &&
                (clockwise[index].Y < clockwise[selected].Y ||
                 clockwise[index].Y == clockwise[selected].Y && clockwise[index].X < clockwise[selected].X);
            if (agreement > best + 1e-12 || canonicalTie)
            {
                selected = index;
                best = agreement;
            }
        }
        return Enumerable.Range(0, 4).Select(index => clockwise[(selected + index) % 4]).ToArray();
    }

    /// <summary>
    /// Gets the logical top edge's heading in [0, 360), accounting for projector pixel aspect.
    /// Reversed perimeter winding is normalized while preserving the first logical corner.
    /// </summary>
    public static double Heading(IReadOnlyList<Point2> corners, double displayAspect)
    {
        ValidateAspect(displayAspect);
        var clockwise = Clockwise(corners);
        return NormalizeDegrees(EdgeAngle(clockwise[0], clockwise[1], displayAspect) * 180 / Math.PI);
    }

    private static double EdgeAngle(Point2 first, Point2 second, double displayAspect)
    {
        double dx = second.X - first.X, dy = second.Y - first.Y;
        double scale = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return Math.Atan2(dy / scale, dx / scale * displayAspect);
    }

    private static double NormalizeDegrees(double degrees)
    {
        double result = degrees % 360;
        return result < 0 ? result + 360 : result == 0 ? 0 : result;
    }

    private static void ValidateAspect(double displayAspect)
    {
        if (!double.IsFinite(displayAspect) || displayAspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayAspect), "Use a finite positive display aspect.");
    }

    private static Point2[] Clockwise(IReadOnlyList<Point2> corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        if (corners.Count != 4 || corners.Any(point => !point.IsFinite))
            throw new ArgumentException("Provide four finite corners in convex perimeter order.", nameof(corners));
        double scale = Math.Max(corners.Max(point => point.X) - corners.Min(point => point.X),
            corners.Max(point => point.Y) - corners.Min(point => point.Y));
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("The board corners have no usable extent.", nameof(corners));

        int winding = 0;
        for (int index = 0; index < 4; index++)
        {
            var first = corners[index];
            var second = corners[(index + 1) % 4];
            var third = corners[(index + 2) % 4];
            double dx = (second.X - first.X) / scale, dy = (second.Y - first.Y) / scale;
            double nextX = (third.X - second.X) / scale, nextY = (third.Y - second.Y) / scale;
            double cross = dx * nextY - dy * nextX;
            if (dx * dx + dy * dy < 1e-10 || Math.Abs(cross) < 1e-8)
                throw new ArgumentException("Board corners are duplicated or nearly collinear.", nameof(corners));
            int sign = Math.Sign(cross);
            if (winding != 0 && sign != winding)
                throw new ArgumentException("Board corners must form a convex, noncrossing perimeter.", nameof(corners));
            winding = sign;
        }
        return winding > 0 ? corners.ToArray() : [corners[0], corners[3], corners[2], corners[1]];
    }
}
