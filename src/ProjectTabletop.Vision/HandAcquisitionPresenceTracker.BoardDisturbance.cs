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
        ResetOutsideControlColorRefinement();
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
        int spacing = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_outsideControlMask.Count(value => value) / (double)OutsideControlFitBudget)));
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_outsideControlMask[index] || index % _columns % spacing != 0 || index / _columns % spacing != 0) continue;
            _outsideControlFitMask[index] = OutsideControlFlatReference(index);
        }
        ConfigureOutsideControlColorRefinement();
    }

    // The caption pass samples only its controls and reference plates. Read the
    // remaining board cells into the same frame sample when the veto is needed.
    private void SampleOutsideControl(int stride, byte[] bgra, double[] current, HandAcquisitionHint? illuminatedHint)
    {
        for (int index = 0; index < _outsideControlMask!.Length; index++)
        {
            if (!_outsideControlMask[index] || SamplesCell(index, illuminatedHint)) continue;
            SampledCellCount++;
            SampleCell(index, stride, bgra, current);
        }
    }

    private (bool Disturbed, double Fraction) MeasureOutsideControlDisturbance(double[] reference,
        double[] current, PhotometricFit? fit, Dictionary<int, double[]>? colorOffsets,
        double threshold, HandAcquisitionHint? illuminatedHint)
    {
        if (_outsideControlMask is null || fit is null || colorOffsets is null) return (false, 0);
        bool[] fitAllowed = _outsideControlFrameFitMask!;
        Array.Copy(_outsideControlFitMask!, fitAllowed, fitAllowed.Length);
        if (illuminatedHint is not null)
        {
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
            double error = OutsideControlError(reference, current, index, fit, colorOffsets, threshold);
            if (error <= threshold) continue;
            residual[index] = true;
            foregroundArea += _outsideControlAreas![index];
        }
        double fraction = foregroundArea / Math.Max(1e-9, eligibleArea);
        // Broad disagreement is an uncertain optical model, not a permanent
        // obstruction. Tiny noise and thin raster-edge residuals cannot veto.
        if (fraction >= .35 || foregroundArea < .0012) return (false, fraction);
        Array.Clear(_outsideControlRefinementRegion!);
        if (!FindOutsideControlObstruction(collectRegions: true)) return (false, fraction);
        return (RefineOutsideControlObstruction(reference, current, illuminatedHint), fraction);
    }

    private double OutsideControlError(double[] reference, double[] current, int index,
        PhotometricFit fit, Dictionary<int, double[]> offsets, double threshold)
    {
        offsets.TryGetValue(ColorKey(reference, index), out var offset);
        double error = Error(fit.Coefficients, reference, current, index, offset);
        if (error > threshold && _edgeColors?[index] is { } alternatives)
        {
            error = EdgeError(fit.Coefficients, alternatives, current, index, offsets, error, threshold);
            if (error > threshold)
                error = BoardColorMixtureError(fit.Coefficients, alternatives, current, index, offsets, error, threshold);
        }
        return error;
    }

    private bool FindOutsideControlObstruction(bool collectRegions)
    {
        bool[] residual = _outsideControlResidual!, visited = _outsideControlVisited!;
        Array.Clear(visited);
        bool found = false;
        int[] queue = _outsideControlQueue!;
        for (int seed = 0; seed < residual.Length; seed++)
        {
            if (!residual[seed] || visited[seed]) continue;
            int head = 0, tail = 1, minX = seed % _columns, maxX = minX,
                minY = seed / _columns, maxY = minY;
            queue[0] = seed; visited[seed] = true;
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
                    if (nx < 0 || nx >= _columns || ny < 0 || ny >= _rows || !residual[neighbor] || visited[neighbor]) continue;
                    visited[neighbor] = true; queue[tail++] = neighbor;
                }
            }
            // A long border can fill a one-row/column box completely. Require
            // thickness in both dimensions before treating it as an obstruction.
            if (area >= .0012 && tail >= 12 && maxX > minX && maxY > minY &&
                tail >= (maxX - minX + 1) * (maxY - minY + 1) * .30)
            {
                if (!collectRegions) return true;
                found = true;
                int padX = Math.Max(4, (maxX - minX + 2) / 2), padY = Math.Max(4, (maxY - minY + 2) / 2);
                for (int y = Math.Max(0, minY - padY); y <= Math.Min(_rows - 1, maxY + padY); y++)
                for (int x = Math.Max(0, minX - padX); x <= Math.Min(_columns - 1, maxX + padX); x++)
                    _outsideControlRefinementRegion![y * _columns + x] = true;
            }
        }
        return found;
    }

    private double BoardColorMixtureError(double[][] coefficients, double[] colors, double[] current, int index,
        Dictionary<int, double[]> offsets, double best, double threshold)
    {
        // Camera filtering can mix two neighbouring hues, not just the darkest
        // and brightest colour. Use only this cell's existing local palette;
        // caption corruption keeps its separate, stricter comparison unchanged.
        Span<double> predictions = stackalloc double[colors.Length];
        Span<double> minimum = stackalloc double[3] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        Span<double> maximum = stackalloc double[3] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (int color = 0; color < colors.Length; color += 3)
        {
            offsets.TryGetValue(ColorKey(colors, color / 3), out var offset);
            for (int channel = 0; channel < 3; channel++)
            {
                predictions[color + channel] = Predict(coefficients[channel], colors[color], colors[color + 1],
                    colors[color + 2], index, channel) + (offset?[channel] ?? 0);
                minimum[channel] = Math.Min(minimum[channel], predictions[color + channel]);
                maximum[channel] = Math.Max(maximum[channel], predictions[color + channel]);
            }
        }
        double bestSquared = best * best * 3, thresholdSquared = threshold * threshold * 3;
        double lowerBoundSquared = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double value = current[index * 3 + channel];
            double distance = Math.Max(minimum[channel] - value, Math.Max(0, value - maximum[channel]));
            lowerBoundSquared += distance * distance;
        }
        // Every segment lies inside these bounds. Reject impossible mixtures
        // cheaply; being inside the bounds never accepts a camera colour.
        if (lowerBoundSquared > thresholdSquared) return best;
        for (int first = 0; first < predictions.Length; first += 3)
        for (int second = first + 3; second < predictions.Length; second += 3)
        {
            double numerator = 0, denominator = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                double delta = predictions[second + channel] - predictions[first + channel];
                numerator += (current[index * 3 + channel] - predictions[first + channel]) * delta;
                denominator += delta * delta;
            }
            double amount = denominator > 1e-10 ? Math.Clamp(numerator / denominator, 0, 1) : 0;
            double squared = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                double error = current[index * 3 + channel] - predictions[first + channel] -
                    amount * (predictions[second + channel] - predictions[first + channel]);
                squared += error * error;
            }
            bestSquared = Math.Min(bestSquared, squared);
            if (bestSquared <= thresholdSquared) return Math.Sqrt(bestSquared / 3);
        }
        return Math.Sqrt(bestSquared / 3);
    }
}
