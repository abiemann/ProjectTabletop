namespace ProjectTabletop.Vision;

public sealed partial class HandAcquisitionPresenceTracker
{
    private bool[]? _outsideControlMask;
    private double[]? _outsideControlAreas;
    private bool[]? _outsideControlResidual;
    private int[]? _outsideControlQueue;
    private bool[]? _outsideControlFitMask;

    private void ResetOutsideControlDisturbance()
    {
        _outsideControlMask = _outsideControlResidual = null;
        _outsideControlAreas = null;
        _outsideControlQueue = null;
        _outsideControlFitMask = null;
    }

    private void ConfigureOutsideControlDisturbance(HandAcquisitionSceneImage scene)
    {
        if (scene.BoardDisturbanceExclusionRegion is not { } exclusion) return;
        _outsideControlMask = new bool[_mask.Length];
        _outsideControlAreas = new double[_mask.Length];
        _outsideControlResidual = new bool[_mask.Length];
        _outsideControlQueue = new int[_mask.Length];
        _edgeColors ??= new double[]?[_mask.Length];
        double[] color = new double[3];
        double radius = _scale * .25 + 1;
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_mask[index]) continue;
            var point = _locations[index];
            if (!TemplateColor(scene, point, color)) continue;
            bool excluded = false;
            for (int corner = 0; corner < 4; corner++)
                if (!BoardPosition(scene, new(point.X + (corner % 2 == 0 ? -radius : radius),
                    point.Y + (corner < 2 ? -radius : radius)), out double u, out double v) ||
                    u >= exclusion.X && u <= exclusion.X + exclusion.Width &&
                    v >= exclusion.Y && v <= exclusion.Y + exclusion.Height)
                { excluded = true; break; }
            if (excluded) continue;
            _outsideControlMask[index] = true;
            _outsideControlAreas[index] = BoardSampleArea(scene, point);
            if (_templateSampleMask is not null) _templateSampleMask[index] = true;
            if (_edgeColors[index] is not null) continue;
            // Use exactly the caption pass's local colour/edge tolerance. The
            // extra samples never enter its candidate, reference or text masks.
            var colors = new List<double>(75);
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (TemplateColor(scene, new(point.X + dx * (_scale + 1) * .5,
                    point.Y + dy * (_scale + 1) * .5), color)) colors.AddRange(color);
            _edgeColors[index] = colors.ToArray();
        }
        // Caption plates do not necessarily contain the colours of menu art.
        // Fit that wider palette separately, from a bounded, distributed set
        // of the samples already read. Keep both caption masks unchanged.
        _outsideControlFitMask = new bool[_mask.Length];
        int spacing = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_outsideControlMask.Count(value => value) / 2048.0)));
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_outsideControlMask[index] || index % _columns % spacing != 0 || index / _columns % spacing != 0) continue;
            var alternatives = _edgeColors[index]!;
            bool flat = true;
            for (int colorIndex = 0; colorIndex < alternatives.Length && flat; colorIndex += 3)
                for (int channel = 0; channel < 3; channel++)
                    if (Math.Abs(alternatives[colorIndex + channel] - _expected![index * 3 + channel]) > 32)
                    { flat = false; break; }
            _outsideControlFitMask[index] = flat;
        }
    }

    private (bool Disturbed, double Fraction) MeasureOutsideControlDisturbance(double[] reference,
        double[] current, PhotometricFit? fit, Dictionary<int, double[]>? colorOffsets,
        double threshold, HandAcquisitionHint? illuminatedHint)
    {
        if (_outsideControlMask is null || fit is null || colorOffsets is null) return (false, 0);
        bool[] fitAllowed = _outsideControlFitMask!;
        if (illuminatedHint is not null)
        {
            fitAllowed = (bool[])fitAllowed.Clone();
            for (int index = 0; index < fitAllowed.Length; index++)
                if (fitAllowed[index] && Distance(_locations[index], illuminatedHint.IlluminationCenter) <
                    illuminatedHint.IlluminationRadiusPixels * 1.28) fitAllowed[index] = false;
        }
        var boardFit = Fit(reference, current, fitAllowed, fullColorResponse: true);
        if (boardFit is null || boardFit.MedianError > 18) return (false, 0);
        fit = boardFit;
        colorOffsets = ColorResiduals(reference, current, fitAllowed, fit.Coefficients);
        threshold = Math.Clamp(fit.MedianError * 3.5 + 10, 24, 52);
        var residual = _outsideControlResidual!;
        Array.Clear(residual);
        double eligibleArea = 0, foregroundArea = 0;
        for (int index = 0; index < residual.Length; index++)
        {
            if (!_outsideControlMask[index] || illuminatedHint is not null &&
                Distance(_locations[index], illuminatedHint.IlluminationCenter) < illuminatedHint.IlluminationRadiusPixels * 1.28) continue;
            eligibleArea += _outsideControlAreas![index];
            colorOffsets.TryGetValue(ColorKey(reference, index), out var offset);
            double error = Error(fit.Coefficients, reference, current, index, offset);
            if (error > threshold && _edgeColors?[index] is { } alternatives)
                error = EdgeError(fit.Coefficients, alternatives, current, index, colorOffsets, error, threshold);
            if (error <= threshold) continue;
            residual[index] = true;
            foregroundArea += _outsideControlAreas![index];
        }
        double fraction = foregroundArea / Math.Max(1e-9, eligibleArea);
        // Broad disagreement is an uncertain optical model, not a permanent
        // obstruction. Tiny noise and thin raster-edge residuals cannot veto.
        if (fraction >= .35 || foregroundArea < .0012) return (false, fraction);
        int[] queue = _outsideControlQueue!;
        for (int seed = 0; seed < residual.Length; seed++)
        {
            if (!residual[seed]) continue;
            int head = 0, tail = 1, minX = seed % _columns, maxX = minX,
                minY = seed / _columns, maxY = minY;
            queue[0] = seed; residual[seed] = false;
            double area = 0;
            while (head < tail)
            {
                int index = queue[head++], x = index % _columns, y = index / _columns;
                area += _outsideControlAreas![index];
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy, neighbor = ny * _columns + nx;
                    if (nx < 0 || nx >= _columns || ny < 0 || ny >= _rows || !residual[neighbor]) continue;
                    residual[neighbor] = false; queue[tail++] = neighbor;
                }
            }
            if (area >= .0012 && tail >= 12 &&
                tail >= (maxX - minX + 1) * (maxY - minY + 1) * .30)
                return (true, fraction);
        }
        return (false, fraction);
    }
}
