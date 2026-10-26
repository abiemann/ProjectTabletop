namespace ProjectTabletop.Vision;

public sealed partial class HandAcquisitionPresenceTracker
{
    private const int OutsideControlFitBudget = 2048;
    private bool[]? _outsideControlFrameFitMask, _outsideControlVisited, _outsideControlRefinementRegion;
    private int[]? _outsideControlColorWitnesses;

    private void ResetOutsideControlColorRefinement()
    {
        _outsideControlFrameFitMask = _outsideControlVisited = _outsideControlRefinementRegion = null;
        _outsideControlColorWitnesses = null;
    }

    private void ConfigureOutsideControlColorRefinement()
    {
        _outsideControlFrameFitMask = new bool[_mask.Length];
        _outsideControlVisited = new bool[_mask.Length];
        _outsideControlRefinementRegion = new bool[_mask.Length];
        Span<bool> represented = stackalloc bool[4096];
        represented.Clear();
        int fitCount = 0;
        for (int index = 0; index < _mask.Length; index++)
            if (_outsideControlFitMask![index]) { represented[ColorKey(_expected!, index)] = true; fitCount++; }

        // The regular grid can miss every sample of a small artwork hue. Select
        // flat supplementary witnesses from the rendered image only, preferring
        // surrounding detail that can be checked before trusting their colour.
        Span<int> witnesses = stackalloc int[4096];
        witnesses.Fill(-1);
        Span<double> variances = stackalloc double[4096];
        variances.Clear();
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_outsideControlMask![index]) continue;
            int key = ColorKey(_expected!, index);
            if (represented[key] || !OutsideControlFlatReference(index) ||
                !OutsideControlWindowMoments(_expected!, null, index, 1, null,
                    out double variance, out _, out _) || variance < 16 || variance <= variances[key]) continue;
            witnesses[key] = index;
            variances[key] = variance;
        }
        var additional = new List<int>(Math.Max(0, OutsideControlFitBudget - fitCount));
        for (int key = 0; key < witnesses.Length && additional.Count < OutsideControlFitBudget - fitCount; key++)
            if (witnesses[key] >= 0) additional.Add(witnesses[key]);
        _outsideControlColorWitnesses = additional.ToArray();
    }

    private bool OutsideControlFlatReference(int index)
    {
        var alternatives = _edgeColors![index]!;
        for (int colorIndex = 0; colorIndex < alternatives.Length; colorIndex += 3)
        for (int channel = 0; channel < 3; channel++)
            if (Math.Abs(alternatives[colorIndex + channel] - _expected![index * 3 + channel]) > 32) return false;
        return true;
    }

    private bool RefineOutsideControlObstruction(double[] reference, double[] current, HandAcquisitionHint? illuminatedHint)
    {
        bool[] allowed = _outsideControlFrameFitMask!, region = _outsideControlRefinementRegion!;
        bool supplemented = false;
        foreach (int index in _outsideControlColorWitnesses!)
            if (OutsideControlTextureIntact(reference, current, index, 1, illuminatedHint) &&
                OutsideControlTextureIntact(reference, current, index, 2, illuminatedHint))
            { allowed[index] = true; supplemented = true; }
        if (!supplemented) return true;
        // Never let a suspected object, including its original grid samples,
        // teach the colour model what that same object should look like.
        for (int index = 0; index < allowed.Length; index++)
            if (region[index]) allowed[index] = false;
        var fit = Fit(reference, current, allowed, fullColorResponse: true);
        if (fit is null || fit.MedianError > 18) return true;
        var offsets = ColorResiduals(reference, current, allowed, fit.Coefficients);
        double threshold = Math.Clamp(fit.MedianError * 3.5 + 10, 24, 52);
        for (int index = 0; index < region.Length; index++)
        {
            if (!region[index] || !_outsideControlMask![index] || illuminatedHint is not null &&
                Distance(_locations[index], illuminatedHint.IlluminationCenter) < illuminatedHint.IlluminationRadiusPixels * 1.28) continue;
            _outsideControlResidual![index] = OutsideControlError(reference, current, index, fit, offsets, threshold) > threshold;
        }
        // Only suspect neighbourhoods change; all other original evidence stays.
        return FindOutsideControlObstruction(collectRegions: false);
    }

    private bool OutsideControlTextureIntact(double[] reference, double[] current, int index,
        int radius, HandAcquisitionHint? illuminatedHint)
    {
        if (!OutsideControlWindowMoments(reference, current, index, radius, illuminatedHint,
            out double expectedVariance, out double observedVariance, out double covariance)) return false;
        return expectedVariance >= 16 && observedVariance >= 4 &&
            observedVariance >= expectedVariance * .0625 && covariance > 0 &&
            covariance * covariance >= .64 * expectedVariance * observedVariance;
    }

    private bool OutsideControlWindowMoments(double[] reference, double[]? current, int index,
        int radius, HandAcquisitionHint? illuminatedHint, out double expectedVariance,
        out double observedVariance, out double covariance)
    {
        expectedVariance = observedVariance = covariance = 0;
        int x = index % _columns, y = index / _columns;
        if (x < radius || y < radius || x + radius >= _columns || y + radius >= _rows) return false;
        double expectedSum = 0, observedSum = 0, expectedSquared = 0, observedSquared = 0, product = 0;
        int count = 0;
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            int neighbor = (y + dy) * _columns + x + dx;
            if (!_outsideControlMask![neighbor] || current is not null && _outsideControlRefinementRegion![neighbor] ||
                illuminatedHint is not null && Distance(_locations[neighbor], illuminatedHint.IlluminationCenter) <
                    illuminatedHint.IlluminationRadiusPixels * 1.28) return false;
            double expected = Luminance(reference, neighbor), observed = current is null ? 0 : Luminance(current, neighbor);
            expectedSum += expected; observedSum += observed;
            expectedSquared += expected * expected; observedSquared += observed * observed;
            product += expected * observed;
            count++;
        }
        expectedVariance = Math.Max(0, expectedSquared / count - Math.Pow(expectedSum / count, 2));
        observedVariance = Math.Max(0, observedSquared / count - Math.Pow(observedSum / count, 2));
        covariance = product / count - expectedSum * observedSum / (count * count);
        return true;
    }
}
