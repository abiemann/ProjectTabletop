namespace ProjectTabletop.Calibration;

/// <summary>
/// An immutable planar projective mapping. It maps points on one physical plane only;
/// use a separately measured mapping for a piece raised above the board.
/// </summary>
public sealed class Homography
{
    private readonly double[] _forward;
    private readonly double[] _inverse;

    private Homography(double[] matrix)
    {
        _forward = NormalizeMatrix(matrix);
        _inverse = NormalizeMatrix(InvertMatrix(_forward));
    }

    /// <summary>
    /// Solves the unique homography matching four corresponding quadrilateral corners.
    /// Both lists must use the same corner order, such as top-left, top-right, bottom-right,
    /// bottom-left. Convexity and numerical stability are checked before accepting a result.
    /// </summary>
    public static Homography FromFourPoints(IReadOnlyList<Point2> source, IReadOnlyList<Point2> destination)
    {
        ValidateQuadrilateral(source, nameof(source));
        ValidateQuadrilateral(destination, nameof(destination));

        var (sourceNormalized, sourceToNormalized) = NormalizePoints(source);
        var (destinationNormalized, destinationToNormalized) = NormalizePoints(destination);
        var equations = new double[8, 9];

        for (var index = 0; index < 4; index++)
        {
            var (x, y) = sourceNormalized[index];
            var (u, v) = destinationNormalized[index];
            var row = index * 2;
            equations[row, 0] = x;
            equations[row, 1] = y;
            equations[row, 2] = 1;
            equations[row, 6] = -u * x;
            equations[row, 7] = -u * y;
            equations[row, 8] = u;

            equations[row + 1, 3] = x;
            equations[row + 1, 4] = y;
            equations[row + 1, 5] = 1;
            equations[row + 1, 6] = -v * x;
            equations[row + 1, 7] = -v * y;
            equations[row + 1, 8] = v;
        }

        var solved = SolveEightByEight(equations);
        double[] normalizedMatrix = [
            solved[0], solved[1], solved[2],
            solved[3], solved[4], solved[5],
            solved[6], solved[7], 1
        ];
        var matrix = Multiply(InvertMatrix(destinationToNormalized),
            Multiply(normalizedMatrix, sourceToNormalized));
        var mapping = new Homography(matrix);

        // Reject an ill-conditioned fit even if elimination happened to finish.
        for (var index = 0; index < 4; index++)
        {
            var mapped = mapping.Transform(source[index]);
            var expected = destination[index];
            var scale = Math.Max(1, Math.Max(Math.Abs(expected.X), Math.Abs(expected.Y)));
            if (Math.Abs(mapped.X - expected.X) > scale * 1e-8 ||
                Math.Abs(mapped.Y - expected.Y) > scale * 1e-8)
                throw new ArgumentException("The four point pairs cannot form a stable projective mapping.");
        }
        return mapping;
    }

    /// <summary>Maps a source pixel to its corresponding destination pixel.</summary>
    public Point2 Transform(Point2 point) => Apply(_forward, point);

    /// <summary>Maps a destination pixel back to the source coordinate system.</summary>
    public Point2 InverseTransform(Point2 point) => Apply(_inverse, point);

    /// <summary>Returns the inverse mapping without changing this instance.</summary>
    public Homography Inverse() => new(_inverse);

    /// <summary>Returns a copy of the row-major 3 × 3 matrix, defined up to a common scale.</summary>
    public double[] ToMatrix() => (double[])_forward.Clone();

    private static Point2 Apply(double[] matrix, Point2 point)
    {
        if (!point.IsFinite) throw new ArgumentOutOfRangeException(nameof(point), "Coordinates must be finite.");
        var denominator = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12)
            throw new InvalidOperationException("This point maps to the projective horizon.");
        var x = (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / denominator;
        var y = (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / denominator;
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new InvalidOperationException("The transformed point is outside the finite projective plane.");
        return new(x, y);
    }

    private static void ValidateQuadrilateral(IReadOnlyList<Point2>? points, string parameter)
    {
        ArgumentNullException.ThrowIfNull(points, parameter);
        if (points.Count != 4)
            throw new ArgumentException("Provide exactly four corresponding corners.", parameter);
        if (points.Any(point => !point.IsFinite))
            throw new ArgumentException("All corner coordinates must be finite.", parameter);

        var width = points.Max(point => point.X) - points.Min(point => point.X);
        var height = points.Max(point => point.Y) - points.Min(point => point.Y);
        var scale = Math.Max(width, height);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("The corners have no usable extent.", parameter);

        var orientation = 0;
        for (var index = 0; index < 4; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % 4];
            var after = points[(index + 2) % 4];
            var edgeX = (next.X - current.X) / scale;
            var edgeY = (next.Y - current.Y) / scale;
            if (edgeX * edgeX + edgeY * edgeY < 1e-10)
                throw new ArgumentException("Two adjacent corners are too close.", parameter);
            var nextX = (after.X - next.X) / scale;
            var nextY = (after.Y - next.Y) / scale;
            var cross = edgeX * nextY - edgeY * nextX;
            if (Math.Abs(cross) < 1e-8)
                throw new ArgumentException("Three adjacent corners are nearly collinear.", parameter);
            var sign = Math.Sign(cross);
            if (orientation != 0 && sign != orientation)
                throw new ArgumentException("Corners must trace a convex quadrilateral without crossing.", parameter);
            orientation = sign;
        }
    }

    private static (Point2[] Points, double[] Transform) NormalizePoints(IReadOnlyList<Point2> points)
    {
        var centerX = points.Average(point => point.X);
        var centerY = points.Average(point => point.Y);
        var meanDistance = points.Average(point =>
            Math.Sqrt(Math.Pow(point.X - centerX, 2) + Math.Pow(point.Y - centerY, 2)));
        if (!double.IsFinite(meanDistance) || meanDistance <= 0)
            throw new ArgumentException("The corners cannot be normalized.");
        var scale = Math.Sqrt(2) / meanDistance;
        double[] transform = [scale, 0, -scale * centerX, 0, scale, -scale * centerY, 0, 0, 1];
        return (points.Select(point => Apply(transform, point)).ToArray(), transform);
    }

    private static double[] SolveEightByEight(double[,] equations)
    {
        for (var column = 0; column < 8; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 8; row++)
                if (Math.Abs(equations[row, column]) > Math.Abs(equations[pivot, column])) pivot = row;
            if (Math.Abs(equations[pivot, column]) < 1e-11)
                throw new ArgumentException("The four point pairs are numerically degenerate.");
            for (var cell = column; cell < 9; cell++)
                (equations[column, cell], equations[pivot, cell]) =
                    (equations[pivot, cell], equations[column, cell]);
            var divisor = equations[column, column];
            for (var cell = column; cell < 9; cell++) equations[column, cell] /= divisor;
            for (var row = 0; row < 8; row++)
            {
                if (row == column) continue;
                var factor = equations[row, column];
                for (var cell = column; cell < 9; cell++) equations[row, cell] -= factor * equations[column, cell];
            }
        }
        return Enumerable.Range(0, 8).Select(index => equations[index, 8]).ToArray();
    }

    private static double[] Multiply(double[] left, double[] right)
    {
        var result = new double[9];
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        for (var middle = 0; middle < 3; middle++)
            result[row * 3 + column] += left[row * 3 + middle] * right[middle * 3 + column];
        return result;
    }

    private static double[] NormalizeMatrix(double[] matrix)
    {
        if (matrix.Length != 9 || matrix.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("A homography must contain nine finite coefficients.", nameof(matrix));
        var divisor = Math.Abs(matrix[8]) > 1e-12 ? matrix[8] : matrix.Max(Math.Abs);
        if (!double.IsFinite(divisor) || Math.Abs(divisor) < 1e-12)
            throw new ArgumentException("The homography matrix is singular.", nameof(matrix));
        return matrix.Select(value => value / divisor).ToArray();
    }

    private static double[] InvertMatrix(double[] matrix)
    {
        var a = matrix[0]; var b = matrix[1]; var c = matrix[2];
        var d = matrix[3]; var e = matrix[4]; var f = matrix[5];
        var g = matrix[6]; var h = matrix[7]; var i = matrix[8];
        double[] adjugate = [
            e * i - f * h, c * h - b * i, b * f - c * e,
            f * g - d * i, a * i - c * g, c * d - a * f,
            d * h - e * g, b * g - a * h, a * e - b * d
        ];
        var determinant = a * adjugate[0] + b * adjugate[3] + c * adjugate[6];
        var scale = matrix.Max(value => Math.Abs(value));
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-13 * scale * scale * scale)
            throw new ArgumentException("The homography matrix is singular or unstable.");
        return adjugate.Select(value => value / determinant).ToArray();
    }
}
