namespace ProjectTabletop.Vision;

public sealed partial class HandAcquisitionPresenceTracker
{
    private CaptionReflectanceEvidence? LocalizedCaptionReflectance(double[] reference, double[] current,
        int region, bool[] foreground, PhotometricFit fit, Dictionary<int, double[]> offsets, double threshold)
    {
        if (_scene?.BoardSearchRegions is null || _controlRegions is null || _referenceControlRegions is null ||
            _controlReferenceBounds is null ||
            _sampleBoardAreas is null || _textPatterns is null || _templateMask is null) return null;
        var control = _controlReferenceBounds[region];
        List<int>[] margins = [[], [], [], []];
        for (int index = 0; index < foreground.Length; index++)
        {
            // Local witnesses may use opaque candidate pixels even when an
            // explicit global lighting anchor deliberately excludes that plate.
            // This does not add them to the global camera-colour training mask.
            if (!_templateSampleMask![index] || _referenceControlRegions[index] != region || _templateEdges![index] ||
                !BoardPosition(_scene, _locations[index], out double u, out double v) ||
                _textPatterns.IsGeneratedCaptionSupport(region, u, v, out _)) continue;
            double x = (u - control.X) / control.Width, y = (v - control.Y) / control.Height;
            for (int side = 0; side < 4; side++)
            {
                bool margin = side switch { 0 => x <= .2, 1 => x >= .8, 2 => y <= .2, _ => y >= .8 };
                if (margin) margins[side].Add(index);
            }
        }
        // A readable projected word alone cannot distinguish a new reflecting
        // surface from a unique panel color absent from the independent fit.
        // Opposite non-caption margins supply a measured response for matching
        // generated colors. Remove that shared response before accepting a local
        // caption residual. Margins establish locality; they add no qualifying area.
        for (int pair = 0; pair < 4; pair += 2)
        {
            if (margins[pair].Count < 6 || margins[pair + 1].Count < 6) continue;
            var first = margins[pair].GroupBy(index => ColorKey(reference, index)).ToDictionary(group => group.Key, group => group.ToArray());
            var second = margins[pair + 1].GroupBy(index => ColorKey(reference, index)).ToDictionary(group => group.Key, group => group.ToArray());
            var localOffsets = new Dictionary<int, double[]>(offsets);
            var witness = new HashSet<int>();
            foreach (var (key, left) in first)
            {
                if (left.Length < 2) continue;
                var match = second.Where(group => group.Value.Length >= 2)
                    .Select(group => new { group.Key, Samples = group.Value, Distance = ColorDistance(left, group.Value) })
                    .OrderBy(group => group.Distance).FirstOrDefault();
                if (match is null || match.Distance > 16) continue;
                var right = match.Samples;
                double[] a = Residual(left), b = Residual(right);
                if (Math.Sqrt(Enumerable.Range(0, 3).Sum(channel => Math.Pow(a[channel] - b[channel], 2)) / 3) > 12) continue;
                localOffsets[key] = Residual(left.Concat(right));
                localOffsets[match.Key] = localOffsets[key];
                witness.UnionWith(left); witness.UnionWith(right);
            }
            double witnessArea = witness.Sum(index => _sampleBoardAreas[index]);
            if (witnessArea < control.Width * control.Height * .12) continue;
            // A changed reference elsewhere can move the global camera-colour
            // fit without changing this control at all. Require fresh local
            // chroma against the visible opposite margins themselves before a
            // fitted caption residual can claim a new reflecting surface.
            // These witnesses constrain appearance; they supply no hand area.
            if (!HasLocalChromaticChange(region, witness.ToArray(), reference, current, _templateMask)) continue;
            bool[] localized = new bool[foreground.Length];
            for (int index = 0; index < localized.Length; index++)
            {
                if (_controlRegions[index] != region || !foreground[index]) continue;
                localOffsets.TryGetValue(ColorKey(reference, index), out var offset);
                double error = Error(fit.Coefficients, reference, current, index, offset);
                if (error > threshold && _edgeColors?[index] is { } alternatives)
                    error = EdgeError(fit.Coefficients, alternatives, current, index, localOffsets, error, threshold);
                localized[index] = error > threshold;
            }
            var evidence = MeasureCaptionReflectance(reference, current, localized, fit, localOffsets, region).Single();
            if (evidence.Coverage >= MinimumControlCoverage && evidence.TriggerCoverage >= MinimumControlCoverage && evidence.InkFraction >= .5)
                return evidence;
        }
        return null;

        double[] Residual(IEnumerable<int> samples) => Enumerable.Range(0, 3).Select(channel =>
            Median(samples.Select(index => current[index * 3 + channel] - Predict(fit.Coefficients[channel],
                reference[index * 3], reference[index * 3 + 1], reference[index * 3 + 2], index, channel)))).ToArray();

        double ColorDistance(IReadOnlyList<int> first, IReadOnlyList<int> second) => Math.Sqrt(
            Enumerable.Range(0, 3).Sum(channel => Math.Pow(first.Average(index => reference[index * 3 + channel]) -
                second.Average(index => reference[index * 3 + channel]), 2)) / 3);
    }
}
