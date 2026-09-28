namespace ProjectTabletop.Vision;

/// <summary>A camera motion hint for a fresh palm search, never a detected hand or a gesture.</summary>
/// <param name="ControlCoverage">For rendered-control foreground hints, residual area divided
/// by that control's configured search-interior area in board coordinates; null for motion-only hints.</param>
public sealed record HandAcquisitionHint(HandTrackingBounds SearchBounds, PixelPoint Center,
    double RadiusPixels, DateTimeOffset ObservedAt, double MotionFraction, double? ControlCoverage = null);

/// <summary>
/// Finds bounded local disturbances within a calibrated camera polygon. All returned geometry
/// stays in native camera pixels; the square crops provide surrounding palm context without
/// stretching the camera image. Call Reset when the projected scene or its lights change.
/// </summary>
public sealed class HandAcquisitionMotionTracker
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Retention = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(180);
    private byte[]? _previous;
    private bool[] _mask = [];
    private PixelPoint[] _polygon = [];
    private int _width, _height, _columns, _rows, _validCount;
    private double _left, _top, _scale;
    private DateTimeOffset _previousTime, _quietUntil;
    private IReadOnlyList<HandAcquisitionHint> _hints = [];

    public IReadOnlyList<HandAcquisitionHint> Update(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<PixelPoint> searchPolygon, DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(searchPolygon);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (frameTime > now + TimeSpan.FromMilliseconds(30) || now - frameTime > Freshness)
        {
            Reset();
            return [];
        }
        if (_previous is not null && frameTime <= _previousTime) return [];
        bool changedGeometry = width != _width || height != _height ||
            !_polygon.SequenceEqual(searchPolygon);
        if (changedGeometry && !Configure(width, height, searchPolygon)) return [];
        if (_validCount < 24) return [];

        byte[] current = Sample(width, height, stride, bgra);
        if (_previous is null || frameTime - _previousTime > Retention)
        {
            _previous = current;
            _previousTime = frameTime;
            _quietUntil = frameTime + QuietPeriod;
            _hints = [];
            return [];
        }

        // Uniform auto-exposure changes are not a moving object. Estimate the signed global
        // change robustly so a small moving hand cannot set the compensation itself.
        int[] histogram = new int[511];
        for (int index = 0; index < current.Length; index++)
            if (_mask[index]) histogram[current[index] - _previous[index] + 255]++;
        int globalShift = Median(histogram, _validCount) - 255;
        int[] noiseHistogram = new int[511];
        for (int index = 0; index < current.Length; index++)
            if (_mask[index]) noiseHistogram[Math.Abs(current[index] - _previous[index] - globalShift)]++;
        int threshold = Math.Clamp(Median(noiseHistogram, _validCount) * 4 + 8, 16, 60);
        bool[] moving = new bool[current.Length];
        int changed = 0;
        for (int index = 0; index < current.Length; index++)
        {
            if (!_mask[index] || Math.Abs(current[index] - _previous[index] - globalShift) < threshold) continue;
            moving[index] = true;
            changed++;
        }
        _previous = current;
        _previousTime = frameTime;

        if (Math.Abs(globalShift) >= 18 || changed > _validCount * .42)
        {
            _hints = [];
            _quietUntil = frameTime + QuietPeriod;
            return [];
        }
        if (frameTime < _quietUntil) return [];

        // Join the two moving edges of a small finger without promoting isolated sensor specks.
        bool[] joined = new bool[moving.Length];
        for (int y = 1; y < _rows - 1; y++)
        for (int x = 1; x < _columns - 1; x++)
        {
            int index = y * _columns + x;
            if (!moving[index]) continue;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int adjacent = index + dy * _columns + dx;
                if (_mask[adjacent]) joined[adjacent] = true;
            }
        }

        var candidates = new List<(HandAcquisitionHint Hint, int Evidence)>();
        var queue = new int[joined.Length];
        int minimumEvidence = Math.Max(6, (int)Math.Ceiling(_validCount * .0025));
        for (int start = 0; start < joined.Length; start++)
        {
            if (!joined[start]) continue;
            joined[start] = false;
            int read = 0, count = 1, evidence = 0;
            int minX = _columns, minY = _rows, maxX = 0, maxY = 0;
            queue[0] = start;
            while (read < count)
            {
                int index = queue[read++], x = index % _columns, y = index / _columns;
                if (moving[index])
                {
                    evidence++;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= _columns || ny >= _rows) continue;
                    int adjacent = ny * _columns + nx;
                    if (!joined[adjacent]) continue;
                    joined[adjacent] = false;
                    queue[count++] = adjacent;
                }
            }
            if (evidence < minimumEvidence) continue;
            double motionWidth = (maxX - minX + 1) * _scale;
            double motionHeight = (maxY - minY + 1) * _scale;
            // A row or column of compression noise has area but no useful hand-sized extent.
            if (Math.Min(motionWidth, motionHeight) < Math.Max(3 * _scale, Math.Min(width, height) * .009)) continue;
            var center = new PixelPoint(_left + (minX + maxX + 1) * _scale / 2,
                _top + (minY + maxY + 1) * _scale / 2);
            if (!Inside(center, _polygon)) continue;
            double shortSide = Math.Min(width, height), extent = Math.Max(motionWidth, motionHeight);
            double maximumSide = shortSide * .60;
            double minimumSide = Math.Min(maximumSide, Math.Max(48, shortSide * .26));
            int side = (int)Math.Ceiling(Math.Clamp(extent * 1.7 + shortSide * .10,
                minimumSide, maximumSide));
            side = Math.Min(side, (int)shortSide);
            int cropX = Math.Clamp((int)Math.Round(center.X - side / 2.0), 0, width - side);
            int cropY = Math.Clamp((int)Math.Round(center.Y - side / 2.0), 0, height - side);
            double radius = Math.Clamp(extent * .55 + shortSide * .025, shortSide * .055, shortSide * .13);
            candidates.Add((new(new(cropX, cropY, side, side), center, radius, frameTime,
                evidence / (double)_validCount), evidence));
        }

        var selected = new List<HandAcquisitionHint>(2);
        foreach (var candidate in candidates.OrderByDescending(item => item.Evidence))
        {
            if (selected.Any(existing => Distance(existing.Center, candidate.Hint.Center) <
                Math.Min(existing.SearchBounds.Width, candidate.Hint.SearchBounds.Width) * .45)) continue;
            selected.Add(candidate.Hint);
            if (selected.Count == 2) break;
        }
        if (selected.Count > 0) _hints = selected;
        else _hints = _hints.Where(hint => now - hint.ObservedAt <= Retention).ToArray();
        return _hints;
    }

    public void Reset()
    {
        _previous = null;
        _mask = [];
        _polygon = [];
        _width = _height = _columns = _rows = _validCount = 0;
        _previousTime = _quietUntil = default;
        _hints = [];
    }

    private bool Configure(int width, int height, IReadOnlyList<PixelPoint> polygon)
    {
        Reset();
        if (polygon.Count is < 3 or > 16 || polygon.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            return false;
        _left = Math.Clamp(polygon.Min(point => point.X), 0, width);
        _top = Math.Clamp(polygon.Min(point => point.Y), 0, height);
        double right = Math.Clamp(polygon.Max(point => point.X), 0, width);
        double bottom = Math.Clamp(polygon.Max(point => point.Y), 0, height);
        if (right - _left < 8 || bottom - _top < 8) return false;
        _width = width; _height = height;
        _polygon = polygon.ToArray();
        _scale = Math.Max(1, Math.Max(right - _left, bottom - _top) / 192);
        _columns = (int)Math.Ceiling((right - _left) / _scale);
        _rows = (int)Math.Ceiling((bottom - _top) / _scale);
        _mask = new bool[_columns * _rows];
        for (int y = 0; y < _rows; y++)
        for (int x = 0; x < _columns; x++)
        {
            bool valid = Inside(new(_left + (x + .5) * _scale, _top + (y + .5) * _scale), _polygon);
            _mask[y * _columns + x] = valid;
            if (valid) _validCount++;
        }
        return true;
    }

    private byte[] Sample(int width, int height, int stride, byte[] bgra)
    {
        var sampled = new byte[_mask.Length];
        for (int y = 0; y < _rows; y++)
        for (int x = 0; x < _columns; x++)
        {
            int index = y * _columns + x;
            if (!_mask[index]) continue;
            int luminance = 0;
            // Four native samples per cell reduce pixel/compression noise. Both axes use the
            // same sampling scale; neither the diagnostic image nor the camera is transformed.
            for (int sampleY = 0; sampleY < 2; sampleY++)
            for (int sampleX = 0; sampleX < 2; sampleX++)
            {
                int px = Math.Clamp((int)(_left + (x + .25 + sampleX * .5) * _scale), 0, width - 1);
                int py = Math.Clamp((int)(_top + (y + .25 + sampleY * .5) * _scale), 0, height - 1);
                int offset = py * stride + px * 4;
                luminance += (bgra[offset] * 29 + bgra[offset + 1] * 150 + bgra[offset + 2] * 77 + 128) >> 8;
            }
            sampled[index] = (byte)(luminance / 4);
        }
        return sampled;
    }

    private static int Median(int[] histogram, int count)
    {
        int total = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            total += histogram[value];
            if (total > count / 2) return value;
        }
        return 0;
    }

    private static bool Inside(PixelPoint point, IReadOnlyList<PixelPoint> polygon)
    {
        bool inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            PixelPoint a = polygon[current], b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
}
