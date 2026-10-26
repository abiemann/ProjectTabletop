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
    private const byte ColourMask = 7, ClippedCore = 8, AcceptedCore = 16;
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
                bool red = IsStrongRed(r, g, b);
                bool magenta = r >= 100 && r - g >= 16 && b - g >= 18 && r >= b * .72;
                bool clipped = r >= 245 && g >= 220 && b >= 245 && SmallClippedCore(bgra, stride, x, y);
                _mask[y * width + x] = clipped ? ClippedCore : (byte)0;
                if (clipped)
                {
                    _redFirst[y] = Math.Min(_redFirst[y], x); _redLast[y] = x + 1;
                }
                if (!red && !magenta)
                {
                    if (clipped)
                        _mask[y * width + x] |= 3;
                    else if (r >= 70 && r - g >= 16 && b - g >= 18 && r >= b * .72 && NearCore(bgra, stride, x, y))
                        _mask[y * width + x] |= 4;
                    continue;
                }
                // Colour seeds retain the nonclipped path. Clipped cores are checked first
                // against their local coloured fringe, independently of photo texture.
                _mask[y * width + x] |= red ? (byte)1 : (byte)2;
                _redFirst[y] = Math.Min(_redFirst[y], x); _redLast[y] = x + 1;
            }
        }

        int candidates = FindCores(bgra, stride, out RedDotDetectionResult selected);
        for (int y = _top; y < _bottom; y++)
        for (int x = _redFirst[y]; x < _redLast[y]; x++)
        {
            byte kind = (byte)(_mask[y * width + x] & ColourMask);
            if (kind is 0 or 3 or 4) continue;
            int head = 0, tail = 1, minX = x, maxX = x, minY = y, maxY = y;
            double weightedX = 0, weightedY = 0, weight = 0, redExcess = 0, greenBlue = 0;
            int signalPixels = 0, clippedPixels = 0;
            int peakRedGreen = 0, magentaPixels = 0, colourPixels = 0, seedPixels = 0;
            bool touchesBoundary = false, acceptedCore = false;
            _queue[0] = y * width + x;
            _mask[_queue[0]] &= AcceptedCore;
            while (head < tail)
            {
                int pixel = _queue[head++], py = pixel / width, px = pixel - py * width;
                acceptedCore |= (_mask[pixel] & AcceptedCore) != 0;
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                int source = py * stride + px * 4;
                int excess = bgra[source + 2] - bgra[source + 1];
                bool core = bgra[source + 2] >= 245 && bgra[source + 1] >= 220 && bgra[source] >= 245;
                if (core) clippedPixels++;
                if (!core)
                {
                    if (kind == 1 ? bgra[source + 2] - Math.Max(bgra[source], bgra[source + 1]) >= 90 :
                        bgra[source + 2] >= 175 && bgra[source] >= 200 && excess >= 24) signalPixels++;
                    colourPixels++;
                    greenBlue += bgra[source + 1] - bgra[source];
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
                    int nextKind = _mask[next] & ColourMask;
                    if (nextKind != kind && nextKind != 3 && !(kind == 2 && nextKind == 4)) continue;
                    _mask[next] &= AcceptedCore;
                    _queue[tail++] = next;
                }
            }

            if (acceptedCore) continue;
            // Only a validated core above can bypass colour coherence. A few clipped
            // pixels in projected lettering are not a core and must not promote the
            // surrounding tinted edge into a laser candidate.
            // A partial white centre supports a slightly weaker magenta fringe,
            // but still needs coherent colour across at least a quarter of it.
            int signalScale = kind == 1 ? 5 : clippedPixels > 0 ? 4 : 3;
            if (signalPixels < 2 || signalPixels * signalScale < colourPixels) continue;
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

            // Blue-biased camera white balance can make a real
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
            // Warm photographic highlights can form compact red seeds too. Without
            // a clipped centre, require a coherent near-red hue and at least twice
            // the surrounding red dominance. Check the whole component rather than
            // tightening seed colours, which would split wood texture into tiny dots.
            // Clipped centres and blue-biased magenta evidence keep their own checks.
            if (kind == 1 && (greenBlue * 10 > weight || localRed < surroundingRed)) continue;
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

    private int FindCores(byte[] bgra, int stride, out RedDotDetectionResult selected)
    {
        // Isolate a clipped laser centre before its coloured fringe can join a large
        // photographic feather/foot. Reuse mask bits and the flood queue; retain the
        // colour mask for independent nonclipped dots and genuine two-dot ambiguity.
        int count = 0;
        selected = default;
        for (int y = _top; y < _bottom; y++)
        for (int x = _redFirst[y]; x < _redLast[y]; x++)
        {
            if ((_mask[y * _width + x] & ClippedCore) == 0) continue;
            int head = 0, tail = 1, minX = x, maxX = x, minY = y, maxY = y;
            double sumX = 0, sumY = 0;
            int neutralCorePixels = 0;
            bool touchesBoundary = false;
            _queue[0] = y * _width + x;
            _mask[_queue[0]] &= ColourMask;
            while (head < tail)
            {
                int pixel = _queue[head++], py = pixel / _width, px = pixel - py * _width;
                sumX += px; sumY += py;
                int source = py * stride + px * 4;
                if (Math.Min(bgra[source + 2], bgra[source]) - bgra[source + 1] < 16)
                    neutralCorePixels++;
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                touchesBoundary |= px <= _first[py] || px >= _last[py] - 1 || py <= _top || py >= _bottom - 1;
                for (int ny = Math.Max(_top, py - 1); ny <= Math.Min(_bottom - 1, py + 1); ny++)
                for (int nx = Math.Max(_first[ny], px - 1); nx <= Math.Min(_last[ny] - 1, px + 1); nx++)
                {
                    int next = ny * _width + nx;
                    if ((_mask[next] & ClippedCore) == 0) continue;
                    _mask[next] &= ColourMask;
                    _queue[tail++] = next;
                }
            }

            int boxWidth = maxX - minX + 1, boxHeight = maxY - minY + 1;
            double fill = tail / (double)(boxWidth * boxHeight);
            // Allow a bounded motion streak. Its exposure-average position remains the
            // core centroid; a broad white patch or isolated hot pixel is not a dot.
            // A white core also needs two pixels outside the magenta colour gate.
            // Entirely pink projected highlights must earn the colour checks instead.
            if (touchesBoundary || tail < 4 || neutralCorePixels < 2 ||
                tail > Math.Max(16, _boardArea * .0007) || fill < .1 ||
                boxWidth > _coreProbeDistance * 2.5 || boxHeight > _coreProbeDistance * 2.5 ||
                Math.Min(boxWidth, boxHeight) > _coreProbeDistance) continue;
            int colourSupport = 0;
            for (int py = Math.Max(_top, minY - 3); py <= Math.Min(_bottom - 1, maxY + 3); py++)
            for (int px = Math.Max(_first[py], minX - 3); px <= Math.Min(_last[py] - 1, maxX + 3); px++)
            {
                int source = py * stride + px * 4;
                int red = bgra[source + 2], green = bgra[source + 1], blue = bgra[source];
                if (red >= 245 && green >= 220 && blue >= 245) continue;
                // A bright centre can desaturate its red fringe on dark photographic
                // detail, so local red/green evidence need not satisfy the global intensity
                // gate. A pink fringe retains blue; a pure red fringe has strong red
                // dominance. A weak warm-brown edge can surround a white feather
                // highlight too, so hue purity alone is insufficient evidence.
                if (red >= 100 && red - green >= 24 && IsRedHue(red, green, blue) &&
                    (blue >= green || red - Math.Max(blue, green) >= 55)) colourSupport++;
            }
            if (colourSupport < 4) continue;
            var camera = new PixelPoint(sumX / tail, sumY / tail);
            var board = Map(camera.X, camera.Y);
            if (board.X is < 0 or > 1 || board.Y is < 0 or > 1 ||
                !double.IsFinite(board.X) || !double.IsFinite(board.Y)) continue;
            // The connected coloured fringe belongs to this core, not a second aim.
            for (int i = 0; i < tail; i++) _mask[_queue[i]] |= AcceptedCore;
            count++;
            selected = new(camera, board.X, board.Y, .9, 1);
        }
        return count;
    }

    private bool ContainsStrongRed(byte[] bgra, int stride, int minX, int minY, int maxX, int maxY)
    {
        for (int y = Math.Max(_top, minY - 1); y <= Math.Min(_bottom - 1, maxY + 1); y++)
        for (int x = Math.Max(_first[y], minX - 1); x <= Math.Min(_last[y] - 1, maxX + 1); x++)
        {
            int source = y * stride + x * 4;
            if (IsStrongRed(bgra[source + 2], bgra[source + 1], bgra[source])) return true;
        }
        return false;
    }

    private static bool IsStrongRed(int red, int green, int blue)
    {
        int other = Math.Max(blue, green);
        // Orange/brown photographic details can have a large red/green difference
        // too. A red spot must also stay near the red hue axis, rather than gaining
        // substantial green over blue. Keep the independent magenta path unchanged.
        return red >= 150 && red - other >= 55 && red >= other * 1.45 && IsRedHue(red, green, blue);
    }

    private static bool IsRedHue(int red, int green, int blue) =>
        (green - blue) * 20 <= (red - green) * 7;

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
