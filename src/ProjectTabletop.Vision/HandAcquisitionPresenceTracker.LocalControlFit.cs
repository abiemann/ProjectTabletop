namespace ProjectTabletop.Vision;

public sealed partial class HandAcquisitionPresenceTracker
{
    private sealed record LocalControlEvidence(int Region, bool[] Mask, CaptionReflectanceEvidence? Reflectance);

    private IReadOnlyList<LocalControlEvidence> FitLocalizedControls(double[] reference, double[] current,
        bool[] allowed, bool[] fitAllowed, bool[] measuredForeground,
        IReadOnlyList<HandAcquisitionTextPatterns.Observation> observations,
        IReadOnlyList<CaptionReflectanceEvidence> measuredReflectance,
        IReadOnlyList<CaptionReflectanceEvidence> reflected, List<HandAcquisitionLocalFitResult> diagnostics)
    {
        var result = new List<LocalControlEvidence>();
        if (_scene?.BoardSearchRegions is null || _controlRegions is null || _templateReferenceMask is null ||
            _templateEdges is null || _sampleBoardAreas is null || _textPatterns is null) return result;
        int availableTraining = fitAllowed.Count(value => value);
        var controlTraining = Enumerable.Range(0, fitAllowed.Length).Where(index => fitAllowed[index])
            .GroupBy(index => _controlRegions[index]).ToDictionary(group => group.Key, group => group.Count());
        foreach (var observation in observations)
        {
            if ((!observation.StrongCorruption && !observation.Clean) ||
                reflected.Any(evidence => evidence.Region == observation.Region)) continue;
            int region = observation.Region;
            // Independent reference panels already handle corrupted captions.
            // A readable thin chevron may have too little ink/halo area for the
            // control floor even though actual residuals cover a broad hand.
            // Only that already measured, ink-qualified candidate may request a
            // margin fit; normal idle captions never add whole-reference fits.
            if (availableTraining - controlTraining.GetValueOrDefault(region) >= 80)
            {
                var measured = measuredReflectance.FirstOrDefault(evidence => evidence.Region == region);
                bool thinCaption = ThinCaptionSupport(region);
                if (observation.StrongCorruption || !thinCaption && (measured is null || measured.InkFraction < .5 ||
                    measured.Coverage >= MinimumControlCoverage)) continue;
                double changedArea = 0, changedTriggerArea = 0;
                for (int index = 0; index < measuredForeground.Length; index++)
                    if (measuredForeground[index] && _controlRegions[index] == region)
                    {
                        changedArea += _sampleBoardAreas[index];
                        if (_controlTriggerRegions![index] == region) changedTriggerArea += _sampleBoardAreas[index];
                    }
                if (!thinCaption && (changedArea / _controlBoardAreas![region] < MinimumControlCoverage ||
                    changedTriggerArea / _controlTriggerBoardAreas![region] < MinimumControlCoverage)) continue;
            }
            var control = _scene.BoardSearchRegions[region];
            List<int>[] margins = [[], [], [], []];
            for (int index = 0; index < allowed.Length; index++)
            {
                if (!allowed[index] || _controlRegions[index] != region || _templateEdges[index] ||
                    !BoardPosition(_scene, _locations[index], out double u, out double v) ||
                    _textPatterns.IsGeneratedCaptionSupport(region, u, v, out _)) continue;
                double x = (u - control.X) / control.Width, y = (v - control.Y) / control.Height;
                if (x <= .18) margins[0].Add(index);
                if (x >= .82) margins[1].Add(index);
                if (y <= .18) margins[2].Add(index);
                if (y >= .82) margins[3].Add(index);
            }
            string failure = "insufficient-opposite-margins";
            int trainingCount = 0, witnessCount = 0;
            double witnessCoverage = 0, medianError = 0;
            // Test both axes without adding their evidence areas together. A
            // current hand can occupy the caption while leaving two independent
            // glass margins visible even when this is the only control on screen.
            for (int pair = 0; pair < 4; pair += 2)
            {
                var first = margins[pair]; var second = margins[pair + 1];
                if (first.Count < 6 || second.Count < 6) continue;
                var witness = MatchingMarginSamples(first, second, reference, current);
                witnessCount = witness.Count;
                witnessCoverage = witness.Sum(index => _sampleBoardAreas[index]) / (control.Width * control.Height);
                if (witnessCount < 12 || witnessCoverage < .12)
                { failure = "opposite-margin-response-mismatch"; continue; }
                if (!observation.StrongCorruption && !HasLocalChromaticChange(region, witness, reference, current, allowed))
                { failure = "no-fresh-local-chroma"; continue; }
                var witnessSet = witness.ToHashSet();
                bool[] independent = Enumerable.Range(0, allowed.Length).Select(index =>
                    _templateReferenceMask[index] && !_templateEdges[index] &&
                    (_controlRegions[index] == region ? witnessSet.Contains(index) : fitAllowed[index])).ToArray();
                trainingCount = independent.Count(value => value);
                var fit = Fit(reference, current, independent);
                if (fit is null) { failure = "insufficient-independent-native-samples"; continue; }
                medianError = fit.MedianError;
                if (fit.MedianError > 18) { failure = "uncertain-opposite-margin-fit"; continue; }
                var offsets = ColorResiduals(reference, current, independent, fit.Coefficients);
                double threshold = Math.Clamp(fit.MedianError * 3.5 + 10, 24, 52);
                if (witness.Count(index => Error(fit.Coefficients, reference, current, index) > threshold) > witness.Count * .15)
                { failure = "uncertain-opposite-margin-residual"; continue; }
                bool[] foreground = new bool[allowed.Length];
                for (int index = 0; index < foreground.Length; index++)
                {
                    if (!allowed[index] || _controlRegions[index] != region) continue;
                    offsets.TryGetValue(ColorKey(reference, index), out var offset);
                    double error = Error(fit.Coefficients, reference, current, index, offset);
                    if (error > threshold && _edgeColors?[index] is { } alternatives)
                        error = EdgeError(fit.Coefficients, alternatives, current, index, offsets, error, threshold);
                    foreground[index] = error > threshold;
                }
                if (!observation.StrongCorruption)
                {
                    // A readable projected chevron on skin needs chromatic
                    // residual specifically on its generated ink and halo. A
                    // brightness change or offside panel tint is insufficient.
                    var evidence = LocalizedCaptionReflectance(reference, current, region,
                        foreground, fit, offsets, threshold) ?? LocalReadableControlReflectance(
                            reference, current, region, foreground, fit, offsets);
                    if (evidence is null) { failure = "no-local-caption-reflectance"; continue; }
                    result.Add(new(region, evidence.Mask, evidence));
                }
                else result.Add(new(region, foreground, null));
                failure = "fresh-opposite-margin-fit";
                break;
            }
            diagnostics.Add(new(region, trainingCount, witnessCount, witnessCoverage, medianError, failure));
        }
        return result;
    }

    private bool ThinCaptionSupport(int region)
    {
        if (_thinCaptionSupport.TryGetValue(region, out bool cached)) return cached;
        if (_scene?.BoardTriggerRegions is null || _textPatterns is null || _controlBoardAreas is null) return false;
        var trigger = _scene.BoardTriggerRegions[region];
        int support = 0;
        for (int y = (int)Math.Floor(trigger.Y * 1000) - 8; y <= (trigger.Y + trigger.Height) * 1000 + 8; y++)
        for (int x = (int)Math.Floor(trigger.X * 1000) - 8; x <= (trigger.X + trigger.Width) * 1000 + 8; x++)
            if (_textPatterns.IsGeneratedCaptionSupport(region, x / 1000.0, y / 1000.0, out _)) support++;
        if (support == 0) return false;
        bool thin = support / 1_000_000.0 / _controlBoardAreas[region] < MinimumControlCoverage;
        _thinCaptionSupport[region] = thin;
        return thin;
    }

    private bool HasLocalChromaticChange(int region, IReadOnlyList<int> witnesses,
        double[] reference, double[] current, bool[] allowed)
    {
        // This inexpensive current-frame palette comparison only proposes a
        // bounded fit. Match the generated background color on opposite margins;
        // a global color shift has the same response there and cannot propose.
        var palette = witnesses.GroupBy(index => ColorKey(reference, index)).Where(group => group.Count() >= 2)
            .Select(group => new
            {
                Expected = Enumerable.Range(0, 3).Select(channel => Median(group.Select(index => reference[index * 3 + channel]))).ToArray(),
                Observed = Enumerable.Range(0, 3).Select(channel => Median(group.Select(index => current[index * 3 + channel]))).ToArray()
            }).ToArray();
        double area = 0, triggerArea = 0;
        for (int index = 0; index < allowed.Length; index++)
        {
            if (!allowed[index] || _controlRegions![index] != region) continue;
            var nearest = palette.Select(color => new { Color = color,
                Distance = Math.Sqrt(Enumerable.Range(0, 3).Sum(channel => Math.Pow(reference[index * 3 + channel] - color.Expected[channel], 2)) / 3) })
                .OrderBy(color => color.Distance).FirstOrDefault();
            if (nearest is null || nearest.Distance > 16) continue;
            double b = current[index * 3] - nearest.Color.Observed[0], g = current[index * 3 + 1] - nearest.Color.Observed[1],
                r = current[index * 3 + 2] - nearest.Color.Observed[2];
            double luminance = b * .114 + g * .587 + r * .299;
            if (Math.Sqrt((b * b + g * g + r * r) / 3) <= 24 ||
                Math.Sqrt((Math.Pow(b - luminance, 2) + Math.Pow(g - luminance, 2) + Math.Pow(r - luminance, 2)) / 3) < 18) continue;
            area += _sampleBoardAreas![index];
            if (_controlTriggerRegions![index] == region) triggerArea += _sampleBoardAreas[index];
        }
        return area / _controlBoardAreas![region] >= MinimumControlCoverage &&
            triggerArea / _controlTriggerBoardAreas![region] >= MinimumControlCoverage;
    }

    private CaptionReflectanceEvidence? LocalReadableControlReflectance(double[] reference, double[] current,
        int region, bool[] foreground, PhotometricFit fit, Dictionary<int, double[]> offsets)
    {
        var ink = MeasureCaptionReflectance(reference, current, foreground, fit, offsets, region).Single();
        if (ink.InkFraction < .5) return null;
        // The very small arrow's ink/halo can cover less than 7% of its plate.
        // Having proved fresh chroma on its generated ink and independent clean
        // margins, count only actually changed chromatic cells across the plate.
        // No glyph expansion, NCC-derived area or margin witness counts as hand.
        bool[] mask = new bool[foreground.Length];
        double area = 0, triggerArea = 0;
        Span<double> residual = stackalloc double[3];
        for (int index = 0; index < mask.Length; index++)
        {
            if (_controlRegions![index] != region || !foreground[index]) continue;
            offsets.TryGetValue(ColorKey(reference, index), out var offset);
            for (int channel = 0; channel < 3; channel++)
                residual[channel] = current[index * 3 + channel] - Predict(fit.Coefficients[channel],
                    reference[index * 3], reference[index * 3 + 1], reference[index * 3 + 2], index, channel) - (offset?[channel] ?? 0);
            double luminance = residual[0] * .114 + residual[1] * .587 + residual[2] * .299;
            double chroma = Math.Sqrt((Math.Pow(residual[0] - luminance, 2) +
                Math.Pow(residual[1] - luminance, 2) + Math.Pow(residual[2] - luminance, 2)) / 3);
            if (chroma < 18) continue;
            mask[index] = true;
            area += _sampleBoardAreas![index];
            if (_controlTriggerRegions![index] == region) triggerArea += _sampleBoardAreas[index];
        }
        double coverage = area / _controlBoardAreas![region], triggerCoverage = triggerArea / _controlTriggerBoardAreas![region];
        return coverage >= MinimumControlCoverage && triggerCoverage >= MinimumControlCoverage
            ? new(region, mask, coverage, triggerCoverage, ink.InkFraction) : null;
    }

    private static List<int> MatchingMarginSamples(IReadOnlyList<int> first, IReadOnlyList<int> second,
        double[] reference, double[] current)
    {
        var a = first.GroupBy(index => ColorKey(reference, index)).ToArray();
        var b = second.GroupBy(index => ColorKey(reference, index)).ToArray();
        var witness = new HashSet<int>();
        foreach (var left in a.Where(group => group.Count() >= 2))
        {
            var right = b.Where(group => group.Count() >= 2).OrderBy(group => Distance(left, group, reference)).FirstOrDefault();
            if (right is null || Distance(left, right, reference) > 16 || Distance(left, right, current) > 12) continue;
            witness.UnionWith(left); witness.UnionWith(right);
        }
        return witness.ToList();

        static double Distance(IEnumerable<int> left, IEnumerable<int> right, double[] pixels) => Math.Sqrt(
            Enumerable.Range(0, 3).Sum(channel => Math.Pow(Median(left.Select(index => pixels[index * 3 + channel])) -
                Median(right.Select(index => pixels[index * 3 + channel])), 2)) / 3);
    }
}
