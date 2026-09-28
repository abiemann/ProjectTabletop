namespace ProjectTabletop.Vision;

public sealed partial class HandAcquisitionPresenceTracker
{
    private sealed record IlluminatedWhiteSample(double X, double Y, double[] Color);

    private double[][]? FitPeripheralIlluminatedWhite(double[] current, int[] core, int[] brightCore,
        HandAcquisitionHint hint)
    {
        double radius = hint.RadiusPixels * .64;
        List<int>[] sectors = Enumerable.Range(0, 8).Select(_ => new List<int>()).ToArray();
        foreach (int index in core)
        {
            if (_textPatterns is not null && _scene is not null && _controlRegions is not null && _controlRegions[index] >= 0 &&
                BoardPosition(_scene, _locations[index], out double u, out double v) &&
                _textPatterns.IsGeneratedCaptionSupport(_controlRegions[index], u, v, out _)) continue;
            double x = (_locations[index].X - hint.Center.X) / radius;
            double y = (_locations[index].Y - hint.Center.Y) / radius;
            if (x * x + y * y < .49) continue;
            int sector = Math.Clamp((int)((Math.Atan2(y, x) + Math.PI) * 4 / Math.PI), 0, 7);
            sectors[sector].Add(index);
        }
        var samples = new List<IlluminatedWhiteSample>(8);
        foreach (var sector in sectors.Where(sector => sector.Count >= 3))
        {
            // Bright samples at the perimeter describe the visible lit surface.
            // The center never trains this response, and a localized finger at
            // the perimeter cannot move the other independent angular sectors.
            int[] bright = sector.OrderBy(index => Luminance(current, index)).Skip((int)(sector.Count * .6)).ToArray();
            samples.Add(new(bright.Average(index => (_locations[index].X - hint.Center.X) / radius),
                bright.Average(index => (_locations[index].Y - hint.Center.Y) / radius),
                Enumerable.Range(0, 3).Select(channel => bright.Average(index => current[index * 3 + channel])).ToArray()));
        }
        if (samples.Count < 6) return null;
        int[] best = [];
        double bestError = double.PositiveInfinity;
        for (int a = 0; a < samples.Count - 2; a++)
        for (int b = a + 1; b < samples.Count - 1; b++)
        for (int c = b + 1; c < samples.Count; c++)
        {
            var first = samples[a]; var second = samples[b]; var third = samples[c];
            double dx1 = second.X - first.X, dy1 = second.Y - first.Y;
            double dx2 = third.X - first.X, dy2 = third.Y - first.Y;
            double determinant = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(determinant) < .08) continue;
            var field = Enumerable.Range(0, 3).Select(channel =>
            {
                double d1 = second.Color[channel] - first.Color[channel], d2 = third.Color[channel] - first.Color[channel];
                double x = (d1 * dy2 - d2 * dy1) / determinant;
                double y = (dx1 * d2 - dx2 * d1) / determinant;
                return new[] { first.Color[channel] - x * first.X - y * first.Y, x, y };
            }).ToArray();
            double[] errors = samples.Select(sample => Math.Sqrt(Enumerable.Range(0, 3).Sum(channel =>
                Math.Pow(sample.Color[channel] - Math.Clamp(field[channel][0] + field[channel][1] * sample.X +
                    field[channel][2] * sample.Y, 0, 255), 2)) / 3)).ToArray();
            int[] inliers = Enumerable.Range(0, errors.Length).Where(index => errors[index] <= 15).ToArray();
            double error = inliers.Length == 0 ? double.PositiveInfinity : inliers.Average(index => errors[index]);
            if (inliers.Length > best.Length || inliers.Length == best.Length && error < bestError)
            { best = inliers; bestError = error; }
        }
        if (best.Length < 6) return null;
        // Refit only independently agreeing peripheral sectors. The resulting
        // field compensates spatial illumination, not a measured center object.
        var result = new double[3][];
        for (int channel = 0; channel < 3; channel++)
        {
            double[,] system = new double[3, 4];
            foreach (int index in best)
            {
                var sample = samples[index];
                double[] features = [1, sample.X, sample.Y];
                for (int row = 0; row < 3; row++)
                {
                    system[row, 3] += features[row] * sample.Color[channel];
                    for (int column = 0; column < 3; column++) system[row, column] += features[row] * features[column];
                }
            }
            for (int pivot = 0; pivot < 3; pivot++)
            {
                if (Math.Abs(system[pivot, pivot]) < 1e-8) return null;
                double divisor = system[pivot, pivot];
                for (int column = pivot; column < 4; column++) system[pivot, column] /= divisor;
                for (int row = 0; row < 3; row++)
                {
                    if (row == pivot) continue;
                    double amount = system[row, pivot];
                    for (int column = pivot; column < 4; column++) system[row, column] -= amount * system[pivot, column];
                }
            }
            result[channel] = [system[0, 3], system[1, 3], system[2, 3]];
            if (result[channel].Any(value => !double.IsFinite(value))) return null;
        }
        // A broad dark/colored peripheral hand must not become the fitted lit
        // surface while visible white paper elsewhere remains unexplained.
        // Validate at the witnesses' own coordinates, preserving a true gradient.
        int agreeingBright = brightCore.Count(index => Math.Sqrt(Enumerable.Range(0, 3).Sum(channel =>
            Math.Pow(current[index * 3 + channel] - IlluminatedWhiteAt(result[channel], _locations[index], hint), 2)) / 3) <= 20);
        if (agreeingBright < brightCore.Length * .75) return null;
        return result;
    }

    private static double IlluminatedWhiteAt(double[] field, PixelPoint point, HandAcquisitionHint hint) =>
        Math.Clamp(field[0] + field[1] * (point.X - hint.Center.X) / (hint.RadiusPixels * .64) +
            field[2] * (point.Y - hint.Center.Y) / (hint.RadiusPixels * .64), 0, 255);
}
