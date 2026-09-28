namespace ProjectTabletop.Vision;

/// <summary>A bounded copy of an actually projected Paint frame, in board UV coordinates.</summary>
public sealed record PaintExpectedFrame(int Width, int Height, byte[] Bgra, DateTimeOffset PresentedAt);

/// <summary>Immutable input for one Paint comparison. Revision changes on navigation or calibration,
/// not while paint animates. ExpectedHistory must include recently presented animation frames.</summary>
public sealed record PaintDisturbanceScene(long Revision, IReadOnlyList<double> CameraToBoard,
    IReadOnlyList<PaintExpectedFrame> ExpectedHistory, HandTrackingBounds PaintBounds,
    IReadOnlyList<HandTrackingBounds>? IgnoredRegions = null);

public sealed record PaintDisturbanceObservation(PixelPoint BoardCenter, double RadiusUv,
    double ForegroundBoardArea, DateTimeOffset ObservedAt);

public sealed record PaintDisturbanceResult(IReadOnlyList<PaintDisturbanceObservation> Drops,
    int CandidateCount, double ForegroundBoardArea, bool ReferenceReady, string Reason,
    double? EstimatedDelayMilliseconds = null);

/// <summary>
/// Finds physical obstructions of a moving projected painting. It compares native camera samples
/// with a short history of known projected images; paint spreading by itself cannot create another
/// drop. This is intentionally an obstruction detector, not a hand or gesture classifier.
/// Single inference-worker ownership is required, as for the shared acquisition trackers.
/// </summary>
public sealed class PaintDisturbanceTracker
{
    // Preserve the measured four-finger noise floor in board area. Seven percent
    // of the whole canvas would require an obstruction much larger than a hand.
    public const double MinimumBoardArea = HandAcquisitionPresenceTracker.MinimumControlCoverage * .236 * .081;
    private const int Features = 7;
    private readonly List<Track> _tracks = [];
    private readonly Dictionary<PaintExpectedFrame, double[]> _expectedSamples = [];
    private long _revision = long.MinValue;
    private double[] _homography = [];
    private HandTrackingBounds? _paintBounds;
    private HandTrackingBounds[] _ignored = [];
    private int _width, _height, _columns, _rows;
    private double _scale, _left, _top;
    private PixelPoint[] _cameraPoints = [], _boardPoints = [];
    private double[] _areas = [];
    private bool[] _mask = [];
    private DateTimeOffset _lastFrame;

    public PaintDisturbanceResult Update(int width, int height, int stride, byte[] bgra,
        PaintDisturbanceScene scene, DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(scene);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (frameTime > now + TimeSpan.FromMilliseconds(30) || now - frameTime > TimeSpan.FromMilliseconds(300))
        { Reset(); return Empty("stale-camera-frame"); }
        if (_lastFrame != default && frameTime <= _lastFrame) return Empty("old-camera-frame");
        if (scene.Revision != _revision || width != _width || height != _height ||
            !_homography.SequenceEqual(scene.CameraToBoard) || _paintBounds != scene.PaintBounds ||
            !_ignored.SequenceEqual(scene.IgnoredRegions ?? []))
            if (!Configure(width, height, scene)) return Empty("invalid-paint-geometry");
        if (_lastFrame != default && frameTime - _lastFrame > TimeSpan.FromMilliseconds(450)) _tracks.Clear();
        _lastFrame = frameTime;

        var history = scene.ExpectedHistory?.Where(item => ValidFrame(item) &&
            item.PresentedAt <= frameTime + TimeSpan.FromMilliseconds(30) &&
            frameTime - item.PresentedAt <= TimeSpan.FromMilliseconds(1000))
            .OrderByDescending(item => item.PresentedAt).Take(10).ToArray() ?? [];
        if (history.Length < 2 || history[0].PresentedAt - history[^1].PresentedAt < TimeSpan.FromMilliseconds(100) ||
            frameTime - history[0].PresentedAt > TimeSpan.FromMilliseconds(350))
        { _tracks.Clear(); return Empty("waiting-for-current-paint-render"); }

        double[] current = SampleCamera(stride, bgra);
        foreach (var expired in _expectedSamples.Keys.Where(item => !history.Contains(item)).ToArray())
            _expectedSamples.Remove(expired);
        double[][] expected = history.Select(item =>
        {
            if (!_expectedSamples.TryGetValue(item, out var samples))
                _expectedSamples[item] = samples = SampleExpected(item);
            return samples;
        }).ToArray();
        Fit? bestFit = null;
        int bestIndex = 0;
        for (int reference = 0; reference < expected.Length; reference++)
        {
            Fit? fit = FitCamera(expected[reference], current);
            if (fit is not null && (bestFit is null || fit.MatchError < bestFit.MatchError))
            { bestFit = fit; bestIndex = reference; }
        }
        if (bestFit is null || bestFit.MedianError > 20)
        { _tracks.Clear(); return Empty("paint-reference-uncertain"); }
        double threshold = Math.Clamp(bestFit.MedianError * 3.5 + 10, 25, 48);
        // Estimate one shared projection/camera delay from the unobstructed scene.
        // Only that render and its immediate neighbors may explain a pixel; a
        // much older unrelated paint color cannot conceal a new physical object.
        double[][] matchingHistory = expected.Skip(Math.Max(0, bestIndex - 1)).Take(bestIndex == 0 ? 2 : 3).ToArray();
        double estimatedDelay = Math.Max(0, (frameTime - history[bestIndex].PresentedAt).TotalMilliseconds);
        bool[] foreground = new bool[_mask.Length];
        double foregroundArea = 0, eligibleArea = 0;
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_mask[index]) continue;
            eligibleArea += _areas[index];
            if (MatchesHistory(bestFit, matchingHistory, current, index, threshold)) continue;
            foreground[index] = true;
            foregroundArea += _areas[index];
        }
        if (foregroundArea > eligibleArea * .40)
        { _tracks.Clear(); return new([], 0, foregroundArea, false, "paint-scene-change"); }

        var candidates = Components(foreground, frameTime);
        var drops = new List<PaintDisturbanceObservation>(2);
        _tracks.RemoveAll(track => frameTime - track.LastSeen > TimeSpan.FromMilliseconds(450));
        var used = new HashSet<Track>();
        foreach (var candidate in candidates)
        {
            Track? track = _tracks.Where(item => !used.Contains(item) &&
                Distance(item.Center, candidate.BoardCenter) < .085)
                .OrderBy(item => Distance(item.Center, candidate.BoardCenter)).FirstOrDefault();
            if (track is null)
            {
                track = new() { Center = candidate.BoardCenter, FirstSeen = frameTime, LastSeen = frameTime };
                _tracks.Add(track);
            }
            else
            {
                track.Center = candidate.BoardCenter;
                track.LastSeen = frameTime;
                track.Observations++;
            }
            used.Add(track);
            if (track.Observations < 2 || frameTime - track.FirstSeen < TimeSpan.FromMilliseconds(60)) continue;
            bool moved = track.LastDropCenter is { } previous && Distance(previous, candidate.BoardCenter) >= .04;
            TimeSpan interval = TimeSpan.FromMilliseconds(moved ? 150 : 900);
            if (track.LastDrop != default && frameTime - track.LastDrop < interval) continue;
            track.LastDrop = frameTime;
            track.LastDropCenter = candidate.BoardCenter;
            drops.Add(candidate);
        }
        return new(drops, candidates.Count, foregroundArea, true,
            drops.Count > 0 ? "physical-disturbance" : candidates.Count > 0 ? "disturbance-held" : "projected-paint-only",
            estimatedDelay);
    }

    public void Reset()
    {
        _tracks.Clear(); _expectedSamples.Clear(); _revision = long.MinValue; _homography = []; _paintBounds = null; _ignored = [];
        _width = _height = _columns = _rows = 0; _cameraPoints = []; _boardPoints = []; _areas = []; _mask = [];
        _lastFrame = default;
    }

    private static PaintDisturbanceResult Empty(string reason) => new([], 0, 0, false, reason);
    private sealed class Track
    {
        public PixelPoint Center;
        public PixelPoint? LastDropCenter;
        public DateTimeOffset FirstSeen, LastSeen, LastDrop;
        public int Observations = 1;
    }

    private bool Configure(int width, int height, PaintDisturbanceScene scene)
    {
        Reset();
        if (scene.CameraToBoard is not { Count: 9 } || scene.CameraToBoard.Any(value => !double.IsFinite(value)) ||
            !ValidBounds(scene.PaintBounds) || scene.IgnoredRegions is { Count: > 32 } ||
            scene.IgnoredRegions?.Any(bounds => !ValidBounds(bounds)) == true) return false;
        double[]? inverse = Invert(scene.CameraToBoard);
        if (inverse is null) return false;
        PixelPoint[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var native = corners.Select(point => Map(inverse, point.X, point.Y)).ToArray();
        if (native.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return false;
        _left = Math.Clamp(native.Min(point => point.X), 0, width - 1);
        _top = Math.Clamp(native.Min(point => point.Y), 0, height - 1);
        double right = Math.Clamp(native.Max(point => point.X), 0, width);
        double bottom = Math.Clamp(native.Max(point => point.Y), 0, height);
        if (right - _left < 32 || bottom - _top < 32) return false;
        _scale = Math.Max(1, Math.Max(right - _left, bottom - _top) / 256);
        _columns = (int)Math.Ceiling((right - _left) / _scale);
        _rows = (int)Math.Ceiling((bottom - _top) / _scale);
        _cameraPoints = new PixelPoint[_columns * _rows]; _boardPoints = new PixelPoint[_cameraPoints.Length];
        _areas = new double[_cameraPoints.Length]; _mask = new bool[_cameraPoints.Length];
        _homography = scene.CameraToBoard.ToArray();
        _paintBounds = scene.PaintBounds; _ignored = scene.IgnoredRegions?.ToArray() ?? [];
        int valid = 0;
        for (int index = 0; index < _mask.Length; index++)
        {
            PixelPoint point = new(_left + (index % _columns + .5) * _scale,
                _top + (index / _columns + .5) * _scale);
            _cameraPoints[index] = point;
            _boardPoints[index] = Map(_homography, point.X, point.Y);
            var cell = new PixelPoint[4];
            bool allowed = true;
            for (int corner = 0; corner < 4; corner++)
            {
                cell[corner] = Map(_homography, point.X + (corner is 0 or 3 ? -.5 : .5) * _scale,
                    point.Y + (corner < 2 ? -.5 : .5) * _scale);
                allowed &= Inside(cell[corner], scene.PaintBounds) && !_ignored.Any(bounds => Inside(cell[corner], bounds));
            }
            if (!allowed) continue;
            double area = 0;
            for (int corner = 0; corner < 4; corner++)
                area += cell[corner].X * cell[(corner + 1) % 4].Y - cell[(corner + 1) % 4].X * cell[corner].Y;
            _areas[index] = Math.Abs(area) / 2;
            _mask[index] = true; valid++;
        }
        if (valid < 100) { Reset(); return false; }
        _revision = scene.Revision; _width = width; _height = height;
        return true;
    }

    private double[] SampleCamera(int stride, byte[] bgra)
    {
        var result = new double[_mask.Length * 3];
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_mask[index]) continue;
            for (int dy = -1; dy <= 1; dy += 2)
            for (int dx = -1; dx <= 1; dx += 2)
            {
                int x = Math.Clamp((int)(_cameraPoints[index].X + dx * _scale * .25), 0, _width - 1);
                int y = Math.Clamp((int)(_cameraPoints[index].Y + dy * _scale * .25), 0, _height - 1);
                int offset = y * stride + x * 4;
                for (int channel = 0; channel < 3; channel++) result[index * 3 + channel] += bgra[offset + channel] * .25;
            }
        }
        return result;
    }

    private double[] SampleExpected(PaintExpectedFrame frame)
    {
        var result = new double[_mask.Length * 3];
        for (int index = 0; index < _mask.Length; index++)
        {
            if (!_mask[index]) continue;
            double x = Math.Clamp(_boardPoints[index].X * (frame.Width - 1), 0, frame.Width - 1);
            double y = Math.Clamp(_boardPoints[index].Y * (frame.Height - 1), 0, frame.Height - 1);
            int ix = (int)x, iy = (int)y, rx = Math.Min(frame.Width - 1, ix + 1), by = Math.Min(frame.Height - 1, iy + 1);
            double fx = x - ix, fy = y - iy;
            for (int channel = 0; channel < 3; channel++)
                result[index * 3 + channel] = frame.Bgra[(iy * frame.Width + ix) * 4 + channel] * (1 - fx) * (1 - fy) +
                    frame.Bgra[(iy * frame.Width + rx) * 4 + channel] * fx * (1 - fy) +
                    frame.Bgra[(by * frame.Width + ix) * 4 + channel] * (1 - fx) * fy +
                    frame.Bgra[(by * frame.Width + rx) * 4 + channel] * fx * fy;
        }
        return result;
    }

    private sealed record Fit(double[][] Coefficients, double MedianError, double MatchError);

    private Fit? FitCamera(double[] expected, double[] current)
    {
        int[] training = Enumerable.Range(0, _mask.Length).Where(index => _mask[index] && index % 11 == 0).ToArray();
        if (training.Length < 80) return null;
        int[] retained = training;
        double[][] coefficients = new double[3][];
        double[] features = new double[Features];
        double median = 0, matchingError = 0;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double[,] matrix = new double[Features, Features]; double[] values = new double[Features];
                foreach (int index in retained)
                {
                    FeatureVector(expected, index, channel, features);
                    for (int row = 0; row < Features; row++)
                    {
                        values[row] += features[row] * current[index * 3 + channel];
                        for (int column = 0; column < Features; column++) matrix[row, column] += features[row] * features[column];
                    }
                }
                for (int diagonal = 0; diagonal < Features; diagonal++)
                    matrix[diagonal, diagonal] += retained.Length * (diagonal == 4 ? .0003 : .00001);
                coefficients[channel] = Solve(matrix, values);
                if (coefficients[channel].Any(value => !double.IsFinite(value))) return null;
            }
            var errors = training.Select(index => (Index: index, Error: Error(coefficients, expected, index, current, index)))
                .OrderBy(item => item.Error).ToArray();
            median = errors[errors.Length / 2].Error;
            matchingError = errors.Average(item => Math.Min(item.Error, 50));
            retained = errors.GroupBy(item => ColorKey(expected, item.Index))
                .SelectMany(group => group.Take(Math.Max(1, (int)(group.Count() * .7))))
                .Select(item => item.Index).ToArray();
        }
        return new(coefficients, median, matchingError);
    }

    private void FeatureVector(double[] expected, int index, int channel, double[] features)
    {
        features[0] = 1;
        for (int color = 0; color < 3; color++) features[color + 1] = expected[index * 3 + color] / 255;
        features[4] = Math.Pow(expected[index * 3 + channel] / 255, 2);
        features[5] = _boardPoints[index].X - .5; features[6] = _boardPoints[index].Y - .5;
    }

    private double Predict(double[] coefficients, double[] expected, int colorIndex, int positionIndex, int channel) =>
        Math.Clamp(coefficients[0] + coefficients[1] * expected[colorIndex * 3] / 255 +
            coefficients[2] * expected[colorIndex * 3 + 1] / 255 + coefficients[3] * expected[colorIndex * 3 + 2] / 255 +
            coefficients[4] * Math.Pow(expected[colorIndex * 3 + channel] / 255, 2) +
            coefficients[5] * (_boardPoints[positionIndex].X - .5) + coefficients[6] * (_boardPoints[positionIndex].Y - .5), 0, 255);

    private double Error(double[][] coefficients, double[] expected, int colorIndex, double[] current, int index)
    {
        double sum = 0;
        for (int channel = 0; channel < 3; channel++)
            sum += Math.Pow(current[index * 3 + channel] - Predict(coefficients[channel], expected, colorIndex, index, channel), 2);
        return Math.Sqrt(sum / 3);
    }

    private bool MatchesHistory(Fit fit, double[][] history, double[] current, int index, double threshold)
    {
        foreach (double[] expected in history)
            if (Error(fit.Coefficients, expected, index, current, index) <= threshold) return true;
        // Native camera sample spacing is identical on both axes. This only allows a
        // small local calibration/raster tolerance, never a stretched camera image.
        int x = index % _columns, y = index / _columns;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= _columns || ny >= _rows) continue;
            int adjacent = ny * _columns + nx;
            if (!_mask[adjacent]) continue;
            foreach (double[] expected in history)
                if (Error(fit.Coefficients, expected, adjacent, current, index) <= threshold) return true;
        }
        // Projector/camera exposure can straddle two animation frames. Accept their
        // interpolated color, rather than treating the app's own blending as input.
        Span<double> first = stackalloc double[3], second = stackalloc double[3];
        for (int frame = 1; frame < history.Length; frame++)
        {
            double numerator = 0, denominator = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                first[channel] = Predict(fit.Coefficients[channel], history[frame - 1], index, index, channel);
                second[channel] = Predict(fit.Coefficients[channel], history[frame], index, index, channel);
                double delta = second[channel] - first[channel];
                numerator += (current[index * 3 + channel] - first[channel]) * delta;
                denominator += delta * delta;
            }
            double amount = denominator > 1e-8 ? Math.Clamp(numerator / denominator, 0, 1) : 0;
            double error = 0;
            for (int channel = 0; channel < 3; channel++)
                error += Math.Pow(current[index * 3 + channel] - first[channel] - amount * (second[channel] - first[channel]), 2);
            if (error / 3 <= threshold * threshold) return true;
        }
        return false;
    }

    private List<PaintDisturbanceObservation> Components(bool[] foreground, DateTimeOffset time)
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
        var result = new List<PaintDisturbanceObservation>();
        int[] queue = new int[joined.Length];
        for (int start = 0; start < joined.Length; start++)
        {
            if (!joined[start]) continue;
            joined[start] = false; queue[0] = start;
            int read = 0, count = 1, evidence = 0;
            double area = 0, u = 0, v = 0, left = 1, right = 0, top = 1, bottom = 0;
            while (read < count)
            {
                int index = queue[read++], x = index % _columns, y = index / _columns;
                if (foreground[index])
                {
                    evidence++; area += _areas[index];
                    u += _boardPoints[index].X * _areas[index]; v += _boardPoints[index].Y * _areas[index];
                    left = Math.Min(left, _boardPoints[index].X); right = Math.Max(right, _boardPoints[index].X);
                    top = Math.Min(top, _boardPoints[index].Y); bottom = Math.Max(bottom, _boardPoints[index].Y);
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
            if (evidence < 12 || area < MinimumBoardArea || Math.Min(right - left, bottom - top) < .009) continue;
            result.Add(new(new(u / area, v / area), Math.Clamp(Math.Sqrt(area / Math.PI), .018, .065), area, time));
        }
        return result.OrderByDescending(item => item.ForegroundBoardArea).Take(2).ToList();
    }

    private static bool ValidFrame(PaintExpectedFrame frame) => frame.Width is > 1 and <= 2048 &&
        frame.Height is > 1 and <= 2048 && frame.Bgra is not null && frame.Bgra.Length >= frame.Width * (long)frame.Height * 4;
    private static bool ValidBounds(HandTrackingBounds bounds) => double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.X >= 0 && bounds.Y >= 0 &&
        bounds.Width > 0 && bounds.Height > 0 && bounds.X + bounds.Width <= 1 && bounds.Y + bounds.Height <= 1;
    private static bool Inside(PixelPoint point, HandTrackingBounds bounds) => point.X >= bounds.X && point.Y >= bounds.Y &&
        point.X <= bounds.X + bounds.Width && point.Y <= bounds.Y + bounds.Height;
    private static int ColorKey(double[] colors, int index) => (int)colors[index * 3] / 32 +
        ((int)colors[index * 3 + 1] / 32) * 8 + ((int)colors[index * 3 + 2] / 32) * 64;
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static PixelPoint Map(IReadOnlyList<double> matrix, double x, double y)
    {
        double denominator = matrix[6] * x + matrix[7] * y + matrix[8];
        return Math.Abs(denominator) < 1e-10 ? new(double.NaN, double.NaN) :
            new((matrix[0] * x + matrix[1] * y + matrix[2]) / denominator,
                (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator);
    }
    private static double[]? Invert(IReadOnlyList<double> m)
    {
        double[] cofactors = [m[4] * m[8] - m[5] * m[7], m[2] * m[7] - m[1] * m[8], m[1] * m[5] - m[2] * m[4],
            m[5] * m[6] - m[3] * m[8], m[0] * m[8] - m[2] * m[6], m[2] * m[3] - m[0] * m[5],
            m[3] * m[7] - m[4] * m[6], m[1] * m[6] - m[0] * m[7], m[0] * m[4] - m[1] * m[3]];
        double determinant = m[0] * cofactors[0] + m[1] * cofactors[3] + m[2] * cofactors[6];
        return !double.IsFinite(determinant) || Math.Abs(determinant) < 1e-15 ? null : cofactors.Select(value => value / determinant).ToArray();
    }
    private static double[] Solve(double[,] matrix, double[] values)
    {
        for (int pivot = 0; pivot < Features; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < Features; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (Math.Abs(matrix[best, pivot]) < 1e-12) return Enumerable.Repeat(double.NaN, Features).ToArray();
            for (int column = 0; column < Features; column++)
                (matrix[best, column], matrix[pivot, column]) = (matrix[pivot, column], matrix[best, column]);
            (values[best], values[pivot]) = (values[pivot], values[best]);
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
}
