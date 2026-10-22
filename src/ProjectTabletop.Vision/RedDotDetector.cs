namespace ProjectTabletop.Vision;

/// <summary>One current-frame red dot. BoardX/BoardY are normalized board coordinates.
/// Confidence is a relative optical score, not a probability. Two plausible dots are ambiguous.</summary>
public readonly record struct RedDotDetectionResult(PixelPoint? CameraPosition, double? BoardX,
    double? BoardY, double Confidence, int CandidateCount)
{
    public bool Detected => CameraPosition.HasValue && BoardX.HasValue && BoardY.HasValue;
    public bool Ambiguous => CandidateCount > 1;
}

/// <summary>Finds a small, compact red spot on the calibrated board, including a magenta camera
/// response under blue-biased white balance. A clipped white
/// centre is allowed when its red fringe remains visible. One worker owns this reusable detector;
/// the caller stamps observations and decides freshness. No previous aim is retained. Avoid tiny
/// saturated-red projected decorations: identical physical and projected dots cannot be separated
/// by colour alone.</summary>
public sealed class RedDotDetector
{
    private readonly double[] _map = new double[9];
    private byte[] _mask = [];
    private int[] _queue = [], _first = [], _last = [], _redFirst = [], _redLast = [];
    private int _width, _height, _top, _bottom, _coreProbeDistance;
    private double _boardArea;

    public RedDotDetectionResult Detect(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<double> cameraToBoard)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(cameraToBoard);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (cameraToBoard.Count != 9)
            throw new ArgumentException("Provide a row-major camera-to-board homography.", nameof(cameraToBoard));
        if (width != _width || height != _height || !SameMap(cameraToBoard))
            Configure(width, height, cameraToBoard);

        for (int y = _top; y < _bottom; y++)
        {
            _redFirst[y] = _last[y]; _redLast[y] = _first[y];
            for (int x = _first[y]; x < _last[y]; x++)
            {
                int source = y * stride + x * 4;
                int r = bgra[source + 2], g = bgra[source + 1], b = bgra[source];
                int other = Math.Max(b, g);
                bool red = r >= 150 && r - other >= 55 && r >= other * 1.45;
                bool magenta = r >= 100 && r - g >= 16 && b - g >= 18 && r >= b * .72;
                _mask[y * width + x] = 0;
                if (!red && !magenta)
                {
                    if (r >= 245 && g >= 220 && b >= 245 && SmallClippedCore(bgra, stride, x, y))
                        _mask[y * width + x] = 3;
                    else if (r >= 70 && r - g >= 16 && b - g >= 18 && r >= b * .72 && NearCore(bgra, stride, x, y))
                        _mask[y * width + x] = 4;
                    continue;
                }
                // Only red pixels seed candidates. Clipped white cores and their immediate
                // dim magenta fringe connect a spot, but cannot create a candidate alone.
                _mask[y * width + x] = red ? (byte)1 : (byte)2;
                _redFirst[y] = Math.Min(_redFirst[y], x); _redLast[y] = x + 1;
            }
        }

        int candidates = 0;
        RedDotDetectionResult selected = default;
        for (int y = _top; y < _bottom; y++)
        for (int x = _redFirst[y]; x < _redLast[y]; x++)
        {
            byte kind = _mask[y * width + x];
            if (kind is 0 or 3 or 4) continue;
            int head = 0, tail = 1, minX = x, maxX = x, minY = y, maxY = y;
            double weightedX = 0, weightedY = 0, weight = 0, redExcess = 0;
            int peakRedGreen = 0, magentaPixels = 0, colourPixels = 0, seedPixels = 0;
            bool touchesBoundary = false;
            _queue[0] = y * width + x;
            _mask[_queue[0]] = 0;
            while (head < tail)
            {
                int pixel = _queue[head++], py = pixel / width, px = pixel - py * width;
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                int source = py * stride + px * 4;
                int excess = bgra[source + 2] - bgra[source + 1];
                bool core = bgra[source + 2] >= 245 && bgra[source + 1] >= 220 && bgra[source] >= 245;
                if (!core)
                {
                    colourPixels++;
                    if (bgra[source + 2] >= 100) seedPixels++;
                    weightedX += px * excess; weightedY += py * excess; weight += excess;
                    peakRedGreen = Math.Max(peakRedGreen, excess);
                    redExcess += bgra[source + 2] - Math.Max(bgra[source], bgra[source + 1]);
                    if (bgra[source] - bgra[source + 1] >= 18 && bgra[source + 2] - bgra[source] < 55)
                        magentaPixels++;
                }
                if (px <= _first[py] || px >= _last[py] - 1 || py <= _top || py >= _bottom - 1)
                    touchesBoundary = true;
                for (int ny = Math.Max(_top, py - 1); ny <= Math.Min(_bottom - 1, py + 1); ny++)
                for (int nx = Math.Max(_first[ny], px - 1); nx <= Math.Min(_last[ny] - 1, px + 1); nx++)
                {
                    int next = ny * width + nx;
                    // Keep strong red separate from a tinted backdrop. Magenta fragments may
                    // connect through a clipped core and its three-pixel dim fringe only.
                    if (_mask[next] != kind && _mask[next] != 3 && !(kind == 2 && _mask[next] == 4)) continue;
                    _mask[next] = 0;
                    _queue[tail++] = next;
                }
            }

            int boxWidth = maxX - minX + 1, boxHeight = maxY - minY + 1;
            double fill = tail / (double)(boxWidth * boxHeight);
            if (touchesBoundary || seedPixels < 4 || tail > Math.Max(16, _boardArea * .0007) ||
                fill < .18 || boxWidth > boxHeight * 2.5 || boxHeight > boxWidth * 2.5) continue;
            PixelPoint a = Map(minX, minY), b = Map(maxX, minY), c = Map(maxX, maxY), d = Map(minX, maxY);
            double uSpan = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) -
                Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
            double vSpan = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) -
                Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
            if (!double.IsFinite(uSpan) || !double.IsFinite(vSpan) || uSpan > .035 || vSpan > .035) continue;
            // The weak fringe of an already-strong dot is part of that same dot, not a
            // second ambiguous aim. Inspect raw colours because its flood may be cleared.
            if (kind == 2 && ContainsStrongRed(bgra, stride, minX, minY, maxX, maxY)) continue;

            // Preserve the strong-red path. Blue-biased camera white balance can make a real
            // red spot magenta, with blue exceeding red even in its brightest pixels. Require
            // both a local red/green peak and mean contrast so lavender cardboard, blue duck
            // feathers and small chromatic fringes around projected lettering remain clear.
            SurroundingExcess(bgra, stride, minX, minY, maxX, maxY,
                out double surroundingRed, out double surroundingRedGreen);
            double localRed = redExcess / colourPixels - surroundingRed;
            double localRedGreen = weight / colourPixels - surroundingRedGreen;
            bool strongRed = localRed >= 35;
            if (!strongRed && (magentaPixels * 2 < colourPixels || peakRedGreen < 24 || localRedGreen < 8 ||
                peakRedGreen - surroundingRedGreen < 14)) continue;
            var camera = new PixelPoint(weightedX / weight, weightedY / weight);
            var board = Map(camera.X, camera.Y);
            if (board.X is < 0 or > 1 || board.Y is < 0 or > 1 ||
                !double.IsFinite(board.X) || !double.IsFinite(board.Y)) continue;
            candidates++;
            double contrast = strongRed ? localRed / 180 : localRedGreen / 40;
            selected = new(camera, board.X, board.Y,
                Math.Clamp(.35 + .45 * contrast + .2 * Math.Min(1, fill / .6), 0, 1), 1);
        }
        return candidates == 1 ? selected : new(null, null, null, 0, candidates);
    }

    private bool ContainsStrongRed(byte[] bgra, int stride, int minX, int minY, int maxX, int maxY)
    {
        for (int y = Math.Max(_top, minY - 1); y <= Math.Min(_bottom - 1, maxY + 1); y++)
        for (int x = Math.Max(_first[y], minX - 1); x <= Math.Min(_last[y] - 1, maxX + 1); x++)
        {
            int source = y * stride + x * 4;
            int r = bgra[source + 2], other = Math.Max(bgra[source], bgra[source + 1]);
            if (r >= 150 && r - other >= 55 && r >= other * 1.45) return true;
        }
        return false;
    }

    private bool NearCore(byte[] bgra, int stride, int cx, int cy)
    {
        for (int y = Math.Max(_top, cy - 3); y <= Math.Min(_bottom - 1, cy + 3); y++)
        for (int x = Math.Max(_first[y], cx - 3); x <= Math.Min(_last[y] - 1, cx + 3); x++)
        {
            int source = y * stride + x * 4;
            if (bgra[source + 2] >= 245 && bgra[source + 1] >= 220 && bgra[source] >= 245 &&
                SmallClippedCore(bgra, stride, x, y)) return true;
        }
        return false;
    }

    private bool SmallClippedCore(byte[] bgra, int stride, int x, int y)
    {
        // Clipped cardboard or a broad white highlight is not a laser core. Probe beyond
        // the largest accepted spot so a red dot on white can still use its coloured edge.
        int distance = _coreProbeDistance;
        return !Clipped(x - distance, y) && !Clipped(x + distance, y) &&
            !Clipped(x, y - distance) && !Clipped(x, y + distance);

        bool Clipped(int px, int py)
        {
            if (py < _top || py >= _bottom || px < _first[py] || px >= _last[py]) return false;
            int source = py * stride + px * 4;
            return bgra[source + 2] >= 245 && bgra[source + 1] >= 220 && bgra[source] >= 245;
        }
    }

    private void SurroundingExcess(byte[] bgra, int stride, int minX, int minY, int maxX, int maxY,
        out double red, out double redGreen)
    {
        int padding = Math.Max(3, Math.Max(maxX - minX + 1, maxY - minY + 1) / 2);
        int count = 0;
        double redSum = 0, redGreenSum = 0;
        for (int y = Math.Max(_top, minY - padding); y <= Math.Min(_bottom - 1, maxY + padding); y++)
        for (int x = Math.Max(_first[y], minX - padding); x <= Math.Min(_last[y] - 1, maxX + padding); x++)
        {
            if (x >= minX - 1 && x <= maxX + 1 && y >= minY - 1 && y <= maxY + 1) continue;
            int source = y * stride + x * 4;
            redSum += Math.Max(0, bgra[source + 2] - Math.Max(bgra[source], bgra[source + 1]));
            redGreenSum += Math.Max(0, bgra[source + 2] - bgra[source + 1]);
            count++;
        }
        red = count == 0 ? 255 : redSum / count;
        redGreen = count == 0 ? 255 : redGreenSum / count;
    }

    private bool SameMap(IReadOnlyList<double> map)
    {
        for (int i = 0; i < 9; i++) if (map[i] != _map[i]) return false;
        return true;
    }

    private PixelPoint Map(double x, double y)
    {
        double w = _map[6] * x + _map[7] * y + _map[8];
        return new((_map[0] * x + _map[1] * y + _map[2]) / w,
            (_map[3] * x + _map[4] * y + _map[5]) / w);
    }

    private void Configure(int width, int height, IReadOnlyList<double> map)
    {
        // Reuse the existing Vision homography validation. Only calibration changes allocate.
        if (!PhotoObjectExtractor.TryMatrix(map, out var h))
            throw new ArgumentException("Invalid camera-to-board homography.", nameof(map));
        double[] inverse = [
            h[4] * h[8] - h[5] * h[7], h[2] * h[7] - h[1] * h[8], h[1] * h[5] - h[2] * h[4],
            h[5] * h[6] - h[3] * h[8], h[0] * h[8] - h[2] * h[6], h[2] * h[3] - h[0] * h[5],
            h[3] * h[7] - h[4] * h[6], h[1] * h[6] - h[0] * h[7], h[0] * h[4] - h[1] * h[3]];
        PixelPoint[] corners = new PixelPoint[4];
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            double u = i is 1 or 2 ? 1 : 0, v = i >= 2 ? 1 : 0;
            double w = inverse[6] * u + inverse[7] * v + inverse[8];
            if (w == 0 || !double.IsFinite(w) || sign != 0 && sign != Math.Sign(w))
                throw new ArgumentException("The board crosses the projective horizon.", nameof(map));
            sign = Math.Sign(w);
            corners[i] = new((inverse[0] * u + inverse[1] * v + inverse[2]) / w,
                (inverse[3] * u + inverse[4] * v + inverse[5]) / w);
            if (!double.IsFinite(corners[i].X) || !double.IsFinite(corners[i].Y))
                throw new ArgumentException("Invalid board extent.", nameof(map));
        }
        double area = 0;
        for (int i = 0; i < 4; i++)
            area += corners[i].X * corners[(i + 1) % 4].Y - corners[(i + 1) % 4].X * corners[i].Y;
        _boardArea = Math.Abs(area) * .5;
        _coreProbeDistance = Math.Clamp((int)Math.Ceiling(Math.Sqrt(_boardArea) * .035), 4, 64);
        _top = (int)Math.Clamp(Math.Ceiling(corners.Min(p => p.Y)), 0, height);
        _bottom = (int)Math.Clamp(Math.Floor(corners.Max(p => p.Y)) + 1, 0, height);
        _first = new int[height]; _last = new int[height];
        _redFirst = new int[height]; _redLast = new int[height];
        int covered = 0;
        for (int y = _top; y < _bottom; y++)
        {
            double left = double.PositiveInfinity, right = double.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                var a = corners[i]; var b = corners[(i + 1) % 4];
                if (a.Y == b.Y || y < Math.Min(a.Y, b.Y) || y > Math.Max(a.Y, b.Y)) continue;
                double x = a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                left = Math.Min(left, x); right = Math.Max(right, x);
            }
            if (!double.IsFinite(left)) continue;
            _first[y] = (int)Math.Clamp(Math.Ceiling(left), 0, width);
            _last[y] = (int)Math.Clamp(Math.Floor(right) + 1, 0, width);
            covered += Math.Max(0, _last[y] - _first[y]);
        }
        _mask = new byte[checked(width * height)];
        _queue = new int[covered];
        for (int i = 0; i < 9; i++) _map[i] = map[i];
        _width = width; _height = height;
    }
}
