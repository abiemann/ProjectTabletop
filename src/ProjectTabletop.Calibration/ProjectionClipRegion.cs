using System.Collections.ObjectModel;

namespace ProjectTabletop.Calibration;

/// <summary>
/// The detected board's safe projection area in normalized projector coordinates.
/// Corners run around the perimeter in screen-coordinate clockwise order:
/// top-left, top-right, bottom-right, bottom-left.
/// </summary>
public sealed class ProjectionClipRegion
{
    private const double GeometryTolerance = 1e-8;
    private const double BoundaryTolerance = 1e-10;
    private readonly ReadOnlyCollection<Point2> _corners;

    private ProjectionClipRegion(Point2[] corners)
    {
        _corners = Array.AsReadOnly(corners);
        MinX = corners.Min(point => point.X);
        MinY = corners.Min(point => point.Y);
        MaxX = corners.Max(point => point.X);
        MaxY = corners.Max(point => point.Y);
    }

    public IReadOnlyList<Point2> Corners => _corners;
    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }

    public static ProjectionClipRegion FromCorners(IReadOnlyList<Point2> normalizedInsetCorners)
    {
        ArgumentNullException.ThrowIfNull(normalizedInsetCorners);
        if (normalizedInsetCorners.Count != 4)
            throw new ArgumentException("Provide four board corners in perimeter order.",
                nameof(normalizedInsetCorners));

        var corners = normalizedInsetCorners.ToArray();
        if (corners.Any(point => !point.IsFinite || point.X is < 0 or > 1 ||
                                              point.Y is < 0 or > 1))
            throw new ArgumentException("Board corners must be finite and inside the projector field.",
                nameof(normalizedInsetCorners));

        // With Y increasing down the screen, TL -> TR -> BR -> BL has a positive
        // cross product. A positive turn at every corner also rejects concave and
        // self-crossing polygons before they reach the GPU clip layer.
        for (var index = 0; index < 4; index++)
        {
            var current = corners[index];
            var next = corners[(index + 1) % 4];
            var after = corners[(index + 2) % 4];
            var firstX = next.X - current.X;
            var firstY = next.Y - current.Y;
            var secondX = after.X - next.X;
            var secondY = after.Y - next.Y;
            if (firstX * firstX + firstY * firstY <= GeometryTolerance * GeometryTolerance ||
                firstX * secondY - firstY * secondX <= GeometryTolerance)
                throw new ArgumentException("Board corners must form a nondegenerate clockwise quadrilateral.",
                    nameof(normalizedInsetCorners));
        }

        return new ProjectionClipRegion(corners);
    }

    /// <summary>Tests the convex board area, including its four edges.</summary>
    public bool Contains(Point2 point)
    {
        if (!point.IsFinite || point.X < MinX - BoundaryTolerance ||
            point.X > MaxX + BoundaryTolerance || point.Y < MinY - BoundaryTolerance ||
            point.Y > MaxY + BoundaryTolerance)
            return false;

        for (var index = 0; index < 4; index++)
        {
            var current = _corners[index];
            var next = _corners[(index + 1) % 4];
            var cross = (next.X - current.X) * (point.Y - current.Y) -
                        (next.Y - current.Y) * (point.X - current.X);
            if (cross < -BoundaryTolerance) return false;
        }
        return true;
    }
}
