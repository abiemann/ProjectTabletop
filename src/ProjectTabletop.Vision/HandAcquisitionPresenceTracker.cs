namespace ProjectTabletop.Vision;

/// <summary>The exact rendered board and the calibrated native-camera to board-UV homography.</summary>
public sealed record HandAcquisitionSceneImage(int Width, int Height, byte[] Bgra,
    IReadOnlyList<double> CameraToBoard);

/// <summary>Foreground evidence for acquisition, never proof of a hand or a selection.</summary>
public sealed record HandAcquisitionPresenceResult(IReadOnlyList<HandAcquisitionHint> Hints,
    bool BaselineReady, bool? IlluminatedPresence, double ForegroundFraction, string Reason);

/// <summary>
/// Compares the camera with a known rendered scene after robust photometric compensation.
/// A still foreground object remains foreground. Own search lighting is evaluated separately
/// inside its opaque white core and is never learned as the unlit board. Reset on a real scene
/// or calibration change, not when toggling the acquisition light.
/// </summary>
public sealed class HandAcquisitionPresenceTracker
{
    private const int Features = 7;
    private PixelPoint[] _polygon = [];
    private PixelPoint[] _locations = [];
    private bool[] _mask = [];
    private double[]? _baseline;
    private double[]? _expected;
    private bool[]? _templateEdges;
    private HandAcquisitionSceneImage? _scene;
    private int _width, _height, _columns, _rows, _validCount;
    private double _left, _top, _scale;
    private DateTimeOffset _lastTime;

    public HandAcquisitionPresenceResult Update(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<PixelPoint> searchPolygon, HandAcquisitionSceneImage? expectedScene,
        DateTimeOffset frameTime, DateTimeOffset now, HandAcquisitionHint? illuminatedHint = null,
        DateTimeOffset? illuminationStartedAt = null) =>
        Update(width, height, stride, bgra, searchPolygon, frameTime, now, illuminatedHint,
            illuminationStartedAt, expectedScene);

    public HandAcquisitionPresenceResult Update(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<PixelPoint> searchPolygon, DateTimeOffset frameTime, DateTimeOffset now,
        HandAcquisitionHint? illuminatedHint = null, DateTimeOffset? illuminationStartedAt = null,
        HandAcquisitionSceneImage? expectedScene = null)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(searchPolygon);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (frameTime > now + TimeSpan.FromMilliseconds(30) || now - frameTime > TimeSpan.FromMilliseconds(300))
            return Empty("stale-camera-frame");
        if (_lastTime != default && frameTime <= _lastTime) return Empty("old-camera-frame");
        if (width != _width || height != _height || !_polygon.SequenceEqual(searchPolygon))
            if (!Configure(width, height, searchPolygon)) return Empty("invalid-search-polygon");
        _lastTime = frameTime;
        if (_validCount < 48) return Empty("insufficient-board-area");
        double[] current = Sample(stride, bgra);
        if (!ReferenceEquals(_scene, expectedScene)) ConfigureTemplate(expectedScene);

        bool activeLight = IsValidLight(illuminatedHint);
        bool[] allowed = (bool[])_mask.Clone();
        if (activeLight)
            for (int index = 0; index < allowed.Length; index++)
                if (Distance(_locations[index], illuminatedHint!.Center) < illuminatedHint.RadiusPixels * 1.28)
                    allowed[index] = false;

        // The immutable rendered image supports acquisition even when a hand is present in
        // the very first camera frame. A camera reference is only a fallback for callers
        // without a usable rendered template; it cannot identify objects already in it.
        double[]? reference = _expected ?? _baseline;
        bool usingTemplate = _expected is not null;
        if (reference is null)
        {
            if (!activeLight) _baseline = (double[])current.Clone();
            return Empty(activeLight ? "waiting-for-unlit-reference" : "camera-reference-initialized");
        }
        if (usingTemplate)
            for (int index = 0; index < allowed.Length; index++)
                allowed[index] &= !_templateEdges![index];

        PhotometricFit? fit = Fit(reference, current, allowed);
        var colorOffsets = fit is null ? null : ColorResiduals(reference, current, allowed, fit.Coefficients);
        bool[] foreground = new bool[_mask.Length];
        double threshold = fit is null ? double.PositiveInfinity : Math.Clamp(fit.MedianError * 3.5 + 10, 24, 52);
        int foregroundCount = 0, eligibleCount = 0;
        if (fit is not null)
            for (int index = 0; index < foreground.Length; index++)
            {
                if (!allowed[index]) continue;
                eligibleCount++;
                colorOffsets!.TryGetValue(ColorKey(reference, index), out var colorOffset);
                if (Error(fit.Coefficients, reference, current, index, colorOffset) <= threshold) continue;
                foreground[index] = true;
                foregroundCount++;
            }
        double fraction = foregroundCount / (double)Math.Max(1, eligibleCount);
        bool modelReliable = fit is not null && fit.MedianError <= 18 && fraction < .40;
        if (!modelReliable) Array.Clear(foreground);

        bool? illuminatedPresence = null;
        string reason = modelReliable ? usingTemplate ? "rendered-scene-foreground" : "camera-reference-foreground"
            : "photometric-reference-uncertain";
        if (activeLight)
        {
            if (illuminationStartedAt is null || frameTime - illuminationStartedAt < TimeSpan.FromMilliseconds(220))
                reason = "search-light-settling";
            else
            {
                illuminatedPresence = CheckIlluminatedCore(current, illuminatedHint!, fit, out string lightReason);
                reason = lightReason;
            }
        }

        var hints = modelReliable ? Components(foreground, frameTime) : new List<HandAcquisitionHint>();
        if (illuminatedPresence == true)
        {
            hints.RemoveAll(hint => Distance(hint.Center, illuminatedHint!.Center) < illuminatedHint.RadiusPixels);
            hints.Insert(0, illuminatedHint! with { ObservedAt = frameTime });
        }
        if (hints.Count > 2) hints.RemoveRange(2, hints.Count - 2);

        // Keep a fixed reference instead of gradually absorbing a stationary hand. A new
        // rendered scene explicitly resets it; exposure drift is fitted on every fresh frame.
        if (_baseline is null && !activeLight && foregroundCount == 0 && modelReliable)
            _baseline = (double[])current.Clone();
        return new(hints, _baseline is not null || _expected is not null, illuminatedPresence, fraction, reason);
    }

    public void Reset()
    {
        _polygon = []; _locations = []; _mask = [];
        _baseline = _expected = null; _templateEdges = null; _scene = null;
        _width = _height = _columns = _rows = _validCount = 0;
        _lastTime = default;
    }

    private HandAcquisitionPresenceResult Empty(string reason) =>
        new([], _baseline is not null || _expected is not null, null, 0, reason);

    private bool Configure(int width, int height, IReadOnlyList<PixelPoint> polygon)
    {
        Reset();
        if (polygon.Count is < 3 or > 16 || polygon.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return false;
        _left = Math.Clamp(polygon.Min(point => point.X), 0, width);
        _top = Math.Clamp(polygon.Min(point => point.Y), 0, height);
        double right = Math.Clamp(polygon.Max(point => point.X), 0, width);
        double bottom = Math.Clamp(polygon.Max(point => point.Y), 0, height);
        if (right - _left < 8 || bottom - _top < 8) return false;
        _width = width; _height = height; _polygon = polygon.ToArray();
        _scale = Math.Max(1, Math.Max(right - _left, bottom - _top) / 192);
        _columns = (int)Math.Ceiling((right - _left) / _scale);
        _rows = (int)Math.Ceiling((bottom - _top) / _scale);
        _mask = new bool[_columns * _rows];
        _locations = new PixelPoint[_mask.Length];
        for (int y = 0; y < _rows; y++)
        for (int x = 0; x < _columns; x++)
        {
            int index = y * _columns + x;
            _locations[index] = new(_left + (x + .5) * _scale, _top + (y + .5) * _scale);
            _mask[index] = Inside(_locations[index], _polygon);
            if (_mask[index]) _validCount++;
        }
        return true;
    }

    private double[] Sample(int stride, byte[] bgra)
    {
        double[] result = new double[_mask.Length * 3];
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_mask[index]) continue;
            PixelPoint point = _locations[index];
            for (int dy = -1; dy <= 1; dy += 2)
            for (int dx = -1; dx <= 1; dx += 2)
            {
                int x = Math.Clamp((int)(point.X + dx * _scale * .25), 0, _width - 1);
                int y = Math.Clamp((int)(point.Y + dy * _scale * .25), 0, _height - 1);
                int offset = y * stride + x * 4;
                for (int channel = 0; channel < 3; channel++) result[index * 3 + channel] += bgra[offset + channel] * .25;
            }
        }
        return result;
    }

    private void ConfigureTemplate(HandAcquisitionSceneImage? scene)
    {
        _scene = scene; _expected = null; _templateEdges = null;
        if (scene is null || scene.Width is <= 1 or > 16384 || scene.Height is <= 1 or > 16384 ||
            scene.Bgra is null || scene.Bgra.Length < scene.Width * (long)scene.Height * 4 ||
            scene.CameraToBoard is not { Count: 9 } || scene.CameraToBoard.Any(value => !double.IsFinite(value))) return;
        var expected = new double[_mask.Length * 3];
        var edges = new bool[_mask.Length];
        int mapped = 0;
        double[] neighbor = new double[3];
        PixelPoint[] offsets = [new(-_scale * 2.5, 0), new(_scale * 2.5, 0),
            new(0, -_scale * 2.5), new(0, _scale * 2.5)];
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_mask[index]) { edges[index] = true; continue; }
            PixelPoint point = _locations[index];
            if (!TemplateColor(scene, point, expected.AsSpan(index * 3, 3))) { edges[index] = true; continue; }
            mapped++;
            foreach (var offset in offsets)
            {
                if (!TemplateColor(scene, new(point.X + offset.X, point.Y + offset.Y), neighbor)) { edges[index] = true; break; }
                if (Enumerable.Range(0, 3).Any(channel => Math.Abs(expected[index * 3 + channel] - neighbor[channel]) > 32))
                { edges[index] = true; break; }
            }
        }
        if (mapped < _validCount * .75) return;
        _expected = expected; _templateEdges = edges;
    }

    private static bool TemplateColor(HandAcquisitionSceneImage scene, PixelPoint point, Span<double> result)
    {
        IReadOnlyList<double> matrix = scene.CameraToBoard;
        double divisor = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        if (!double.IsFinite(divisor) || Math.Abs(divisor) < 1e-10) return false;
        double u = (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / divisor;
        double v = (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / divisor;
        if (!double.IsFinite(u) || !double.IsFinite(v) || u < 0 || v < 0 || u > 1 || v > 1) return false;
        double x = u * (scene.Width - 1), y = v * (scene.Height - 1);
        int ix = (int)x, iy = (int)y, rx = Math.Min(scene.Width - 1, ix + 1), by = Math.Min(scene.Height - 1, iy + 1);
        double fx = x - ix, fy = y - iy;
        for (int channel = 0; channel < 3; channel++)
            result[channel] = scene.Bgra[(iy * scene.Width + ix) * 4 + channel] * (1 - fx) * (1 - fy) +
                scene.Bgra[(iy * scene.Width + rx) * 4 + channel] * fx * (1 - fy) +
                scene.Bgra[(by * scene.Width + ix) * 4 + channel] * (1 - fx) * fy +
                scene.Bgra[(by * scene.Width + rx) * 4 + channel] * fx * fy;
        return true;
    }

    private sealed record PhotometricFit(double[][] Coefficients, double MedianError);

    private PhotometricFit? Fit(double[] reference, double[] current, bool[] allowed)
    {
        int[] training = Enumerable.Range(0, allowed.Length).Where(index => allowed[index]).ToArray();
        if (training.Length < 80) return null;
        int[] retained = training;
        double[][] coefficients = new double[3][];
        double median = 0;
        double[] features = new double[Features];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double[,] normal = new double[Features, Features];
                double[] values = new double[Features];
                foreach (int index in retained)
                {
                    FeatureVector(reference, index, channel, features);
                    double value = current[index * 3 + channel];
                    for (int row = 0; row < Features; row++)
                    {
                        values[row] += features[row] * value;
                        for (int column = 0; column < Features; column++) normal[row, column] += features[row] * features[column];
                    }
                }
                // Regularize degenerate flat-color scenes and prefer a linear response when
                // the template lacks enough brightness range to estimate a gamma curve.
                for (int diagonal = 0; diagonal < Features; diagonal++) normal[diagonal, diagonal] += retained.Length * (diagonal == 4 ? .0003 : .00001);
                coefficients[channel] = Solve(normal, values);
                if (coefficients[channel].Any(value => !double.IsFinite(value))) return null;
            }
            var errors = training.Select(index => (Index: index, Error: Error(coefficients, reference, current, index)))
                .OrderBy(item => item.Error).ToArray();
            median = errors[errors.Length / 2].Error;
            // Keep the fit representative of dark controls as well as the much larger felt
            // area. A global trim can discard every button pixel and then classify those
            // very buttons as foreground; trim outliers within known rendered-color groups.
            retained = errors.GroupBy(item => ColorKey(reference, item.Index))
                .SelectMany(group => group.Take(Math.Max(1, (int)(group.Count() * .70))))
                .Select(item => item.Index).ToArray();
        }
        return new(coefficients, median);
    }

    private void FeatureVector(double[] reference, int index, int channel, double[] features)
    {
        double normalized = reference[index * 3 + channel] / 255;
        features[0] = 1;
        for (int color = 0; color < 3; color++) features[color + 1] = reference[index * 3 + color] / 255;
        features[4] = normalized * normalized;
        features[5] = (index % _columns + .5) / _columns - .5;
        features[6] = (index / _columns + .5) / _rows - .5;
    }

    private double Predict(double[] coefficients, double blue, double green, double red, int index, int channel)
    {
        double normalized = (channel == 0 ? blue : channel == 1 ? green : red) / 255;
        return Math.Clamp(coefficients[0] + coefficients[1] * blue / 255 + coefficients[2] * green / 255 +
            coefficients[3] * red / 255 + coefficients[4] * normalized * normalized +
            coefficients[5] * ((index % _columns + .5) / _columns - .5) +
            coefficients[6] * ((index / _columns + .5) / _rows - .5), 0, 255);
    }

    private double Error(double[][] coefficients, double[] reference, double[] current, int index, double[]? colorOffset = null)
    {
        double squared = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double prediction = Predict(coefficients[channel], reference[index * 3], reference[index * 3 + 1], reference[index * 3 + 2], index, channel);
            squared += Math.Pow(current[index * 3 + channel] - prediction - (colorOffset?[channel] ?? 0), 2);
        }
        return Math.Sqrt(squared / 3);
    }

    private static int ColorKey(double[] reference, int index) => (int)reference[index * 3] / 16 +
        ((int)reference[index * 3 + 1] / 16) * 16 + ((int)reference[index * 3 + 2] / 16) * 256;

    private Dictionary<int, double[]> ColorResiduals(double[] reference, double[] current, bool[] allowed, double[][] coefficients)
    {
        var offsets = new Dictionary<int, double[]>();
        foreach (var group in Enumerable.Range(0, allowed.Length).Where(index => allowed[index]).GroupBy(index => ColorKey(reference, index)))
        {
            // A single uniquely colored control might be covered by the arriving hand.
            // Its pixels cannot establish their own expected appearance: require matching
            // rendered colors at separated board locations before applying a correction.
            if (!Distributed(group)) continue;
            double[] correction = new double[3];
            for (int channel = 0; channel < 3; channel++)
                correction[channel] = Median(group.Select(index => current[index * 3 + channel] -
                    Predict(coefficients[channel], reference[index * 3], reference[index * 3 + 1], reference[index * 3 + 2], index, channel)));
            offsets[group.Key] = correction;
        }
        return offsets;
    }

    private bool Distributed(IEnumerable<int> group) => group.Count() >= 40 &&
        (group.Max(index => index % _columns) - group.Min(index => index % _columns) >= _columns * .25 ||
         group.Max(index => index / _columns) - group.Min(index => index / _columns) >= _rows * .50);

    private static double[] Solve(double[,] matrix, double[] values)
    {
        for (int pivot = 0; pivot < Features; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < Features; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (Math.Abs(matrix[best, pivot]) < 1e-12) return Enumerable.Repeat(double.NaN, Features).ToArray();
            if (best != pivot)
            {
                for (int column = 0; column < Features; column++)
                    (matrix[best, column], matrix[pivot, column]) = (matrix[pivot, column], matrix[best, column]);
                (values[best], values[pivot]) = (values[pivot], values[best]);
            }
            double divisor = matrix[pivot, pivot];
            for (int column = pivot; column < Features; column++) matrix[pivot, column] /= divisor;
            values[pivot] /= divisor;
            for (int row = 0; row < Features; row++)
            {
                if (row == pivot) continue;
                double amount = matrix[row, pivot];
                for (int column = pivot; column < Features; column++) matrix[row, column] -= amount * matrix[pivot, column];
                values[row] -= amount * values[pivot];
            }
        }
        return values;
    }

    private bool? CheckIlluminatedCore(double[] current, HandAcquisitionHint hint, PhotometricFit? fit, out string reason)
    {
        int[] core = Enumerable.Range(0, _mask.Length).Where(index => _mask[index] &&
            Distance(_locations[index], hint.Center) < hint.RadiusPixels * .64).ToArray();
        if (core.Length < 24) { reason = "insufficient-white-core"; return null; }
        int[] bright = core.OrderBy(index => Luminance(current, index)).Skip((int)(core.Length * .72)).ToArray();
        double[] reference = Enumerable.Range(0, 3).Select(channel => Median(bright.Select(index => current[index * 3 + channel]))).ToArray();
        double referenceLuminance = reference[0] * .114 + reference[1] * .587 + reference[2] * .299;
        bool[] foreground = new bool[_mask.Length];
        int changed = 0;
        foreach (int index in core)
        {
            double brightnessDrop = referenceLuminance - Luminance(current, index);
            double colorError = Math.Sqrt(Enumerable.Range(0, 3).Sum(channel =>
                Math.Pow(current[index * 3 + channel] - reference[channel] + brightnessDrop, 2)) / 3);
            if (brightnessDrop <= 20 && colorError <= 22) continue;
            foreground[index] = true; changed++;
        }
        if (changed >= Math.Max(8, core.Length * .035) &&
            Components(foreground, _lastTime, Math.Max(6, (int)(core.Length * .025))).Count > 0)
        { reason = "foreground-under-search-light"; return true; }

        // A uniformly dark core might be entirely occluded. Do not call it empty unless
        // the visible color is a plausible lit board, or agrees with the fitted white response.
        bool plausibleWhite = referenceLuminance >= 115 && reference.Max() - reference.Min() <= 48;
        if (!plausibleWhite && fit is not null)
        {
            int centerIndex = core.OrderBy(index => Distance(_locations[index], hint.Center)).First();
            double colorError = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                double prediction = Predict(fit.Coefficients[channel], 255, 255, 255, centerIndex, channel);
                colorError += Math.Pow(reference[channel] - prediction, 2);
            }
            plausibleWhite = Math.Sqrt(colorError / 3) <= 32 && referenceLuminance >= 75;
        }
        reason = plausibleWhite ? "empty-search-light" : "white-core-appearance-uncertain";
        return plausibleWhite ? false : null;
    }

    private List<HandAcquisitionHint> Components(bool[] foreground, DateTimeOffset observedAt, int? minimumEvidence = null)
    {
        bool[] joined = new bool[foreground.Length];
        for (int index = 0; index < foreground.Length; index++)
        {
            if (!foreground[index]) continue;
            int x = index % _columns, y = index / _columns;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < _columns && ny < _rows && _mask[ny * _columns + nx]) joined[ny * _columns + nx] = true;
            }
        }
        // A few misregistered text/chip pixels are not enough to illuminate the board.
        // Motion remains a separate, more sensitive path for small arriving fingertips.
        int minimum = minimumEvidence ?? Math.Max(12, (int)(_validCount * .006));
        var candidates = new List<(HandAcquisitionHint Hint, int Count)>();
        int[] queue = new int[joined.Length];
        for (int start = 0; start < joined.Length; start++)
        {
            if (!joined[start]) continue;
            joined[start] = false;
            int count = 1, read = 0, evidence = 0, left = _columns, top = _rows, right = 0, bottom = 0;
            queue[0] = start;
            while (read < count)
            {
                int index = queue[read++], x = index % _columns, y = index / _columns;
                if (foreground[index])
                {
                    evidence++; left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
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
            if (evidence < minimum || Math.Min(right - left, bottom - top) * _scale <
                Math.Max(3 * _scale, Math.Min(_width, _height) * .03)) continue;
            var center = new PixelPoint(_left + (left + right + 1) * _scale / 2, _top + (top + bottom + 1) * _scale / 2);
            if (!Inside(center, _polygon)) continue;
            double extent = Math.Max(right - left + 1, bottom - top + 1) * _scale;
            double shortSide = Math.Min(_width, _height), maxSide = shortSide * .60;
            int side = (int)Math.Ceiling(Math.Clamp(extent * 1.7 + shortSide * .10, Math.Min(maxSide, Math.Max(48, shortSide * .26)), maxSide));
            int cropX = Math.Clamp((int)Math.Round(center.X - side / 2.0), 0, _width - side);
            int cropY = Math.Clamp((int)Math.Round(center.Y - side / 2.0), 0, _height - side);
            double radius = Math.Clamp(extent * .55 + shortSide * .025, shortSide * .055, shortSide * .13);
            candidates.Add((new(new(cropX, cropY, side, side), center, radius, observedAt, evidence / (double)_validCount), evidence));
        }
        var result = new List<HandAcquisitionHint>();
        foreach (var item in candidates.OrderByDescending(item => item.Count))
        {
            if (result.Any(existing => Distance(existing.Center, item.Hint.Center) <
                Math.Min(existing.SearchBounds.Width, item.Hint.SearchBounds.Width) * .45)) continue;
            result.Add(item.Hint);
            if (result.Count == 2) break;
        }
        return result;
    }

    private static bool IsValidLight(HandAcquisitionHint? hint) => hint is not null &&
        double.IsFinite(hint.Center.X) && double.IsFinite(hint.Center.Y) && double.IsFinite(hint.RadiusPixels) && hint.RadiusPixels > 0;
    private static double Luminance(double[] pixels, int index) =>
        pixels[index * 3] * .114 + pixels[index * 3 + 1] * .587 + pixels[index * 3 + 2] * .299;
    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    private static bool Inside(PixelPoint point, IReadOnlyList<PixelPoint> polygon)
    {
        bool inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            PixelPoint a = polygon[current], b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
