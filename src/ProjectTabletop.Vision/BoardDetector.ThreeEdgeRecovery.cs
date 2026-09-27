using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

public static partial class BoardDetector
{
    /// <summary>
    /// Recovers a small movement of the same flat cardboard when one ambient-lit
    /// edge is hidden. Three image lines alone do not locate the fourth line;
    /// the prior four-corner pose supplies the board's fixed size and plane.
    /// Returns null when fewer than three edges fit or the rigid plane model is
    /// inconsistent. The caller must use a genuine prior cardboard detection,
    /// and the projector must be black for this ambient-light measurement.
    /// </summary>
    public static BoardDetection? RecoverAmbientBoardWithPrior(int width, int height,
        int stride, byte[] bgra, BoardDetection prior, int allowedSidesMask = 0b1111)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(prior);
        if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
            bgra.Length < (long)(height - 1) * stride + width * 4 ||
            prior.Corners.Length != 4 || (allowedSidesMask & ~0b1111) != 0)
            throw new ArgumentException("Invalid frame, prior corners, or side mask.");
        if (int.PopCount(allowedSidesMask) < 3 ||
            Enumerable.Range(0, 4).Any(side =>
                Distance(prior.Corners[side], prior.Corners[(side + 1) % 4]) < 80))
            return null;

        if (!TryBoardHomography(prior.Corners, out double[] homography)) return null;
        using Mat color = new(height, width, MatType.CV_8UC4);
        int rowBytes = checked(width * 4);
        for (int row = 0; row < height; row++)
            Marshal.Copy(bgra, row * stride, IntPtr.Add(color.Data, row * rowBytes), rowBytes);
        using Mat gray = new();
        using Mat smooth = new();
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.GaussianBlur(gray, smooth, new Size(5, 5), 1.1);

        var fitted = new RefinedLine?[4];
        int radius = (int)Math.Clamp(Math.Min(width, height) * 0.044, 24, 52);
        for (int side = 0; side < 4; side++)
            if ((allowedSidesMask & (1 << side)) != 0 &&
                TryFitPhysicalSide(smooth, prior.Corners[side],
                    prior.Corners[(side + 1) % 4], +1, out RefinedLine line, radius))
                fitted[side] = line;
        if (fitted.Count(line => line.HasValue) < 3) return null;

        BoardDetection? best = null;
        double bestError = double.PositiveInfinity;
        for (int omitted = 0; omitted < 4; omitted++)
        {
            int[] used = Enumerable.Range(0, 4).Where(side => side != omitted).ToArray();
            if (used.Any(side => !fitted[side].HasValue) ||
                !TryFitRigidBoardPose(homography, fitted, used,
                    out PixelPoint[] corners, out double error))
                continue;
            double maxDisplacement = Enumerable.Range(0, 4).Max(i =>
                Distance(corners[i], prior.Corners[i]));
            // The prior constraint is only trustworthy for a modest movement in
            // the same plane. A large move needs another complete four-edge scan.
            if (error > 3.0 || maxDisplacement > Math.Min(width, height) * 0.075 ||
                corners.Any(point => point.X < 0 || point.Y < 0 ||
                    point.X >= width || point.Y >= height))
                continue;

            // If four lines were found, the omitted line can break near ties.
            // A false line from a shadow may disagree, so it cannot veto three
            // mutually consistent physical edges.
            double score = error;
            if (fitted[omitted] is RefinedLine fourth)
                score += Math.Min(5, SideLineError(fourth, corners[omitted],
                    corners[(omitted + 1) % 4])) * 0.04;
            if (score >= bestError) continue;
            bestError = score;
            best = new BoardDetection(corners,
                Math.Clamp(0.64 - error * 0.05, 0.35, 0.64));
        }
        return best;
    }

    private static bool TryBoardHomography(PixelPoint[] prior, out double[] homography)
    {
        homography = [];
        double[] u = [0, 1, 1, 0], v = [0, 0, 1, 1];
        var matrix = new double[8, 8];
        var values = new double[8];
        for (int i = 0; i < 4; i++)
        {
            int xRow = i * 2, yRow = xRow + 1;
            matrix[xRow, 0] = u[i]; matrix[xRow, 1] = v[i]; matrix[xRow, 2] = 1;
            matrix[xRow, 6] = -u[i] * prior[i].X;
            matrix[xRow, 7] = -v[i] * prior[i].X;
            values[xRow] = prior[i].X;
            matrix[yRow, 3] = u[i]; matrix[yRow, 4] = v[i]; matrix[yRow, 5] = 1;
            matrix[yRow, 6] = -u[i] * prior[i].Y;
            matrix[yRow, 7] = -v[i] * prior[i].Y;
            values[yRow] = prior[i].Y;
        }
        if (!TrySolveLinearSystem(matrix, values, out homography)) return false;
        return homography.All(double.IsFinite);
    }

    private static bool TryFitRigidBoardPose(double[] h, RefinedLine?[] lines,
        int[] usedSides, out PixelPoint[] corners, out double rms)
    {
        corners = [];
        rms = double.PositiveInfinity;
        double[] pose = [0, 0, 0]; // translation in board widths, then rotation
        for (int iteration = 0; iteration < 12; iteration++)
        {
            if (!TryLineResiduals(h, pose, lines, usedSides, out double[] residuals,
                    out _)) return false;
            var jacobian = new double[residuals.Length, 3];
            for (int parameter = 0; parameter < 3; parameter++)
            {
                double[] perturbed = (double[])pose.Clone();
                const double step = 1e-5;
                perturbed[parameter] += step;
                if (!TryLineResiduals(h, perturbed, lines, usedSides,
                        out double[] shifted, out _)) return false;
                for (int row = 0; row < residuals.Length; row++)
                    jacobian[row, parameter] = (shifted[row] - residuals[row]) / step;
            }
            var normal = new double[3, 3];
            var target = new double[3];
            for (int a = 0; a < 3; a++)
            {
                for (int row = 0; row < residuals.Length; row++)
                    target[a] -= jacobian[row, a] * residuals[row];
                for (int b = 0; b < 3; b++)
                    for (int row = 0; row < residuals.Length; row++)
                        normal[a, b] += jacobian[row, a] * jacobian[row, b];
            }
            if (!TrySolveLinearSystem(normal, target, out double[] delta)) return false;
            for (int parameter = 0; parameter < 3; parameter++)
                pose[parameter] += delta[parameter];
            if (Math.Abs(pose[0]) > 0.10 || Math.Abs(pose[1]) > 0.10 ||
                Math.Abs(pose[2]) > 0.10) return false;
            if (Math.Sqrt(delta.Sum(value => value * value)) < 1e-7) break;
        }
        if (!TryLineResiduals(h, pose, lines, usedSides,
                out double[] finalResiduals, out corners)) return false;
        rms = Math.Sqrt(finalResiduals.Average(value => value * value));
        return double.IsFinite(rms);
    }

    private static bool TryLineResiduals(double[] h, double[] pose,
        RefinedLine?[] lines, int[] usedSides,
        out double[] residuals, out PixelPoint[] corners)
    {
        residuals = new double[usedSides.Length * 2];
        corners = new PixelPoint[4];
        double cos = Math.Cos(pose[2]), sin = Math.Sin(pose[2]);
        double[] us = [0, 1, 1, 0], vs = [0, 0, 1, 1];
        for (int i = 0; i < 4; i++)
        {
            double u = us[i] - 0.5, v = vs[i] - 0.5;
            double x = cos * u - sin * v + 0.5 + pose[0];
            double y = sin * u + cos * v + 0.5 + pose[1];
            double denominator = h[6] * x + h[7] * y + 1;
            if (Math.Abs(denominator) < 1e-7) return false;
            corners[i] = new PixelPoint(
                (h[0] * x + h[1] * y + h[2]) / denominator,
                (h[3] * x + h[4] * y + h[5]) / denominator);
            if (!double.IsFinite(corners[i].X) || !double.IsFinite(corners[i].Y))
                return false;
        }
        for (int index = 0; index < usedSides.Length; index++)
        {
            int side = usedSides[index];
            RefinedLine line = lines[side]!.Value;
            residuals[index * 2] = SignedLineDistance(line, corners[side]);
            residuals[index * 2 + 1] = SignedLineDistance(line,
                corners[(side + 1) % 4]);
        }
        return true;
    }

    private static double SideLineError(RefinedLine line, PixelPoint first,
        PixelPoint last) =>
        (Math.Abs(SignedLineDistance(line, first)) +
         Math.Abs(SignedLineDistance(line, last))) / 2;

    private static double SignedLineDistance(RefinedLine line, PixelPoint point)
    {
        double dx = line.Direction.X, dy = line.Direction.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        return ((point.X - line.Point.X) * -dy +
                (point.Y - line.Point.Y) * dx) / length;
    }

    private static bool TrySolveLinearSystem(double[,] matrix, double[] values,
        out double[] solution)
    {
        solution = [];
        int n = values.Length;
        var augmented = new double[n, n + 1];
        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++)
                augmented[row, col] = matrix[row, col];
            augmented[row, n] = values[row];
        }
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(augmented[row, col]) > Math.Abs(augmented[pivot, col]))
                    pivot = row;
            if (Math.Abs(augmented[pivot, col]) < 1e-9) return false;
            for (int x = col; x <= n; x++)
                (augmented[col, x], augmented[pivot, x]) =
                    (augmented[pivot, x], augmented[col, x]);
            double scale = augmented[col, col];
            for (int x = col; x <= n; x++) augmented[col, x] /= scale;
            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double factor = augmented[row, col];
                for (int x = col; x <= n; x++)
                    augmented[row, x] -= factor * augmented[col, x];
            }
        }
        solution = Enumerable.Range(0, n).Select(row => augmented[row, n]).ToArray();
        return solution.All(double.IsFinite);
    }
}
