namespace ProjectTabletop.Vision;

public sealed partial class HandAcquisitionPresenceTracker
{
    // Context pixels can describe a candidate after a control qualifies. They
    // never contribute to its coverage, confirmation, or gesture eligibility.
    internal int LocalContextSampledCellCount { get; private set; }

    private HandAcquisitionHint AttachLocalForegroundCandidate(HandAcquisitionHint hint,
        byte[] bgra, int stride, PhotometricFit fit, Dictionary<int, double[]> offsets)
    {
        if (_scene?.AllowsLocalForegroundContext != true || _expected is null ||
            hint.ObservedAt != _lastTime || hint.ControlCoverage is not >= MinimumControlCoverage ||
            hint.ControlTriggerCoverage is not >= MinimumControlCoverage) return hint;
        int control = ControlRegionAt(hint.Center);
        if (control < 0) return hint;
        int cap = (int)Math.Floor(Math.Min(_width, _height) * .60);
        int side = Math.Min(cap, (int)Math.Ceiling(hint.SearchBounds.Width * 1.8));
        if (side < 48) return hint;
        int left = Math.Clamp((int)Math.Round(hint.Center.X - side / 2.0), 0, _width - side);
        int top = Math.Clamp((int)Math.Round(hint.Center.Y - side / 2.0), 0, _height - side);
        double[] pixels = new double[_expected.Length];
        bool[] residual = new bool[_mask.Length], strong = new bool[_mask.Length], eligible = new bool[_mask.Length];
        double threshold = Math.Clamp(fit.MedianError * 3.5 + 10, 24, 52);
        // Hysteresis is geometry-only: a faint palm may connect to strong finger
        // interference, but cannot seed a component or change control coverage.
        double growthThreshold = Math.Max(12, threshold * .55);
        int eligibleCount = 0;
        Span<double> difference = stackalloc double[3];
        Span<double> minimumColor = stackalloc double[3], maximumColor = stackalloc double[3];
        double[] color = new double[3];
        for (int index = 0; index < _mask.Length; index++)
        {
            var point = _locations[index];
            if (!_mask[index] || point.X < left || point.Y < top ||
                point.X >= left + side || point.Y >= top + side) continue;
            eligible[index] = true;
            eligibleCount++;
            LocalContextSampledCellCount++;
            for (int dy = -1; dy <= 1; dy += 2)
            for (int dx = -1; dx <= 1; dx += 2)
            {
                int x = Math.Clamp((int)(point.X + dx * _scale * .25), 0, _width - 1);
                int y = Math.Clamp((int)(point.Y + dy * _scale * .25), 0, _height - 1);
                int offset = y * stride + x * 4;
                for (int channel = 0; channel < 3; channel++)
                    pixels[index * 3 + channel] += bgra[offset + channel] * .25;
            }
            offsets.TryGetValue(ColorKey(_expected, index), out var correction);
            double error = Error(fit.Coefficients, _expected, pixels, index, correction);
            if (error <= growthThreshold) continue;
            // Tolerate the same bounded glyph/edge registration and antialiased
            // mixtures as the control gate, including fixed artwork nearby.
            var alternatives = new List<double>(75);
            minimumColor.Fill(double.PositiveInfinity); maximumColor.Fill(double.NegativeInfinity);
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (TemplateColor(_scene, new(point.X + dx * (_scale + 1) * .5,
                    point.Y + dy * (_scale + 1) * .5), color))
                {
                    alternatives.AddRange(color);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        minimumColor[channel] = Math.Min(minimumColor[channel], color[channel]);
                        maximumColor[channel] = Math.Max(maximumColor[channel], color[channel]);
                    }
                }
            if (alternatives.Count > 0)
                error = EdgeError(fit.Coefficients, alternatives.ToArray(), pixels, index, offsets, error, growthThreshold);
            if (error <= growthThreshold) continue;
            for (int channel = 0; channel < 3; channel++)
                difference[channel] = pixels[index * 3 + channel] -
                    Predict(fit.Coefficients[channel], _expected[index * 3], _expected[index * 3 + 1],
                        _expected[index * 3 + 2], index, channel) - (correction?[channel] ?? 0);
            double luminance = difference[0] * .114 + difference[1] * .587 + difference[2] * .299;
            double chroma = Math.Sqrt((Math.Pow(difference[0] - luminance, 2) +
                Math.Pow(difference[1] - luminance, 2) + Math.Pow(difference[2] - luminance, 2)) / 3);
            strong[index] = error > threshold && (luminance < -threshold || chroma >= 18);
            bool expectedEdge = maximumColor[0] - minimumColor[0] > 32 ||
                maximumColor[1] - minimumColor[1] > 32 || maximumColor[2] - minimumColor[2] > 32;
            residual[index] = strong[index] || (_controlRegions?[index] < 0 && !expectedEdge &&
                (luminance < -growthThreshold || chroma >= 12));
        }
        if (eligibleCount < 80) return hint;

        // Join small optical gaps, then flood only the component touching fresh
        // measured caption pixels. Disconnected artwork cannot extend the fit.
        bool[] joined = new bool[residual.Length];
        for (int index = 0; index < residual.Length; index++)
        {
            if (!residual[index]) continue;
            int x = index % _columns, y = index / _columns;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < _columns && ny < _rows && eligible[ny * _columns + nx])
                    joined[ny * _columns + nx] = true;
            }
        }
        int[] seeds = Enumerable.Range(0, residual.Length).Where(index => strong[index] &&
            _controlTriggerRegions?[index] == control).ToArray();
        if (seeds.Length == 0) return hint;
        int[] queue = new int[joined.Length];
        var measured = new List<int>();
        foreach (int seed in seeds)
        {
            if (!joined[seed]) continue;
            int count = 1, read = 0;
            queue[0] = seed; joined[seed] = false;
            var component = new List<int>();
            while (read < count)
            {
                int index = queue[read++], x = index % _columns, y = index / _columns;
                if (residual[index]) component.Add(index);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= _columns || ny >= _rows) continue;
                    int adjacent = ny * _columns + nx;
                    if (!joined[adjacent]) continue;
                    joined[adjacent] = false; queue[count++] = adjacent;
                }
            }
            if (component.Count > measured.Count) measured = component;
        }
        // Broad misregistration or photometric drift is not a fitted object.
        if (measured.Count < 20 || measured.Count > eligibleCount * .45) return hint;
        int trim = (int)(measured.Count * .05);
        var byX = measured.OrderBy(index => _locations[index].X).ToArray();
        var byY = measured.OrderBy(index => _locations[index].Y).ToArray();
        // Keep every strongly changed caption/control pixel (including narrow
        // fingertips), without retaining unrelated strong artwork reached only
        // through a weak optical fringe outside the qualified control.
        var strongMeasured = measured.Where(index => strong[index] && _controlRegions?[index] == control).ToArray();
        double minX = Math.Min(_locations[byX[trim]].X, strongMeasured.Min(index => _locations[index].X)) - _scale / 2;
        double minY = Math.Min(_locations[byY[trim]].Y, strongMeasured.Min(index => _locations[index].Y)) - _scale / 2;
        double maxX = Math.Max(_locations[byX[^(trim + 1)]].X, strongMeasured.Max(index => _locations[index].X)) + _scale / 2;
        double maxY = Math.Max(_locations[byY[^(trim + 1)]].Y, strongMeasured.Max(index => _locations[index].Y)) + _scale / 2;
        minX = Math.Clamp(minX, left, left + side); minY = Math.Clamp(minY, top, top + side);
        maxX = Math.Clamp(maxX, left, left + side); maxY = Math.Clamp(maxY, top, top + side);
        if (maxX - minX < _scale * 3 || maxY - minY < _scale * 3 ||
            (maxX - minX) * (maxY - minY) > side * (double)side * .65) return hint;
        return hint with { CandidateBounds = new(minX, minY, maxX - minX, maxY - minY) };
    }
}
