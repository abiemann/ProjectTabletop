using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>Proposes one small projective correction; the caller must validate every rail mark before using it.</summary>
internal sealed class PatternRailRegistration
{
    private const int Columns = PatternRailGeometry.Columns, Marks = Columns * 2;
    private const double Width = PatternRailGeometry.ReferenceWidth, Height = PatternRailGeometry.ReferenceHeight;
    private const int MaximumPatchRadius = 32, PatchCapacity = (MaximumPatchRadius * 2 + 1) * (MaximumPatchRadius * 2 + 1);
    private readonly float[] _patch = new float[PatchCapacity];
    private readonly bool[] _visited = new bool[PatchCapacity];
    private readonly int[] _pending = new int[PatchCapacity];
    private readonly Point2d[] _known = new Point2d[Marks], _found = new Point2d[Marks];
    private static readonly Point2d[] UnitCorners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    public PatternRailRegistration()
    {
        for (int column = 0; column < Columns; column++)
        for (int dot = 0; dot < 2; dot++)
            _known[column * 2 + dot] = new(PatternRailGeometry.ColumnCenter(column) / Width,
                (dot == 0 ? PatternRailGeometry.DotY1ForColumn(column) : PatternRailGeometry.DotY2ForColumn(column)) / Height);
    }

    public bool TryRefine(byte[] bgra, int width, int height, int stride,
        IReadOnlyList<PixelPoint> corners, out PixelPoint[] refined)
    {
        refined = [];
        using var original = Cv2.FindHomography(UnitCorners,
            corners.Select(point => new Point2d(point.X, point.Y)), HomographyMethods.None);
        if (original.Empty()) return false;
        var expected = Cv2.PerspectiveTransform(_known, original);
        double pitch = double.PositiveInfinity, radius = 0;
        foreach (double u in new[] { .01, .5, .99 })
        {
            var samples = Cv2.PerspectiveTransform(new Point2d[]
            {
                new(u, .5), new(u + 1d / Columns, .5),
                new(u + PatternRailGeometry.DotRadius / Width, .5),
                new(u, .5 + PatternRailGeometry.DotRadius / Height)
            }, original);
            pitch = Math.Min(pitch, Distance(samples[0], samples[1]));
            radius = Math.Max(radius, Math.Max(Distance(samples[0], samples[2]), Distance(samples[0], samples[3])));
        }
        // A periodic neighbour must remain outside the correspondence search, even at low resolution.
        double bound = Math.Min(4, pitch * .399);
        int patchRadius = (int)Math.Ceiling(bound + radius * 1.75 + 2);
        if (!double.IsFinite(bound) || patchRadius > MaximumPatchRadius) return false;
        for (int mark = 0; mark < Marks; mark++)
            if (!TryCentre(bgra, width, height, stride, expected[mark], patchRadius, bound, out _found[mark])) return false;

        // Every mark participates. No RANSAC subset, independently moved marks, or learned background.
        using var fit = Cv2.FindHomography(_known, _found, HomographyMethods.None);
        if (fit.Empty()) return false;
        var fittedMarks = Cv2.PerspectiveTransform(_known, fit);
        for (int mark = 0; mark < Marks; mark++)
            if (!Finite(fittedMarks[mark]) || Distance(fittedMarks[mark], expected[mark]) > bound ||
                Distance(fittedMarks[mark], _found[mark]) > .75) return false;
        var fittedCorners = Cv2.PerspectiveTransform(UnitCorners, fit);
        for (int i = 0; i < 4; i++)
            if (!Finite(fittedCorners[i]) || fittedCorners[i].X < 0 || fittedCorners[i].Y < 0 ||
                fittedCorners[i].X >= width - 1 || fittedCorners[i].Y >= height - 1 ||
                Distance(fittedCorners[i], new(corners[i].X, corners[i].Y)) > bound) return false;
        refined = fittedCorners.Select(point => new PixelPoint(point.X, point.Y)).ToArray();
        return true;
    }

    private bool TryCentre(byte[] bgra, int width, int height, int stride,
        Point2d expected, int radius, double bound, out Point2d centre)
    {
        centre = default;
        int left = (int)expected.X - radius, top = (int)expected.Y - radius, side = radius * 2 + 1;
        if (left < 0 || top < 0 || left + side >= width || top + side >= height) return false;
        float minimum = 255, maximum = 0;
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            int offset = (top + y) * stride + (left + x) * 4;
            float value = bgra[offset] * .114f + bgra[offset + 1] * .587f + bgra[offset + 2] * .299f;
            _patch[y * side + x] = value;
            minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
        }
        if (maximum - minimum < 15) return false;
        float threshold = minimum + (maximum - minimum) * .7f;
        int seed = -1;
        double closest = bound * bound;
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            if (_patch[y * side + x] < threshold) continue;
            double dx = left + x - expected.X, dy = top + y - expected.Y, distance = dx * dx + dy * dy;
            if (distance > closest) continue;
            closest = distance; seed = y * side + x;
        }
        if (seed < 0) return false;
        Array.Clear(_visited, 0, side * side);
        int pending = 1, count = 0;
        _pending[0] = seed; _visited[seed] = true;
        double sumX = 0, sumY = 0, weight = 0;
        while (pending > 0)
        {
            int index = _pending[--pending], x = index % side, y = index / side;
            if (x == 0 || y == 0 || x == side - 1 || y == side - 1) return false;
            double value = _patch[index] - minimum;
            sumX += (left + x) * value; sumY += (top + y) * value; weight += value; count++;
            for (int yy = y - 1; yy <= y + 1; yy++)
            for (int xx = x - 1; xx <= x + 1; xx++)
            {
                int next = yy * side + xx;
                if (_visited[next] || _patch[next] < threshold) continue;
                _visited[next] = true; _pending[pending++] = next;
            }
        }
        if (count < 3 || weight <= 0) return false;
        centre = new(sumX / weight, sumY / weight);
        return Distance(centre, expected) <= bound;
    }

    private static bool Finite(Point2d point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static double Distance(Point2d a, Point2d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
