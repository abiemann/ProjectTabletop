namespace ProjectTabletop.Vision;

/// <summary>A fresh, compact disturbance in normalized band coordinates. TrackingId is diagnostic only and may be zero.</summary>
public readonly record struct BoardMotionObservation(long TrackingId, double U, double V);

/// <summary>
/// Tracks occlusion of a calibrated control band without recognizing hands. The four corners
/// are the band's (0,0), (1,0), (1,1), (0,1) in camera pixels. Returned observations are reused
/// until the next call; consume them synchronously. This instance belongs to one frame worker.
/// </summary>
public sealed class BoardDisturbanceTracker
{
    private const int Columns = 192, Rows = 48, Cells = Columns * Rows;
    private static readonly TimeSpan Freshness = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan Warmup = TimeSpan.FromMilliseconds(250);
    private readonly int[] _offsets = new int[Cells], _queue = new int[Cells], _componentStart = new int[Cells];
    private readonly byte[] _samples = new byte[Cells * 3], _previous = new byte[Cells * 3];
    private readonly float[] _background = new float[Cells * 3], _noise = new float[Cells], _foregroundAge = new float[Cells];
    private readonly float[] _detailBackground = new float[Cells], _detailOnsetBackground = new float[Cells];
    private readonly byte[] _detailArmed = new byte[Cells];
    private readonly byte[] _foreground = new byte[Cells], _connected = new byte[Cells], _protected = new byte[Cells];
    private readonly byte[] _componentProtection = new byte[Cells];
    private readonly int[] _redHistogram = new int[511], _greenHistogram = new int[511], _blueHistogram = new int[511];
    private readonly PixelPoint[] _corners = new PixelPoint[4];
    private readonly DetailLoss[] _detailColumns = new DetailLoss[Columns];
    private readonly Candidate[] _candidates = new Candidate[4];
    private readonly Track[] _tracks = new Track[2];
    private readonly bool[] _matchedTracks = new bool[2], _matchedCandidates = new bool[4];
    private readonly long[] _candidateTrackingIds = new long[4];
    private readonly List<BoardMotionObservation> _observations = new(4);
    private int _width, _height, _stride, _validCells, _candidateCount;
    private bool _initialized, _configured;
    private long _nextTrackingId;
    private DateTimeOffset _lastFrame, _warmUntil;

    public IReadOnlyList<BoardMotionObservation> Update(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<PixelPoint> boardCorners, DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(boardCorners);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        _observations.Clear();
        if (frameTime > now + TimeSpan.FromMilliseconds(30) || now - frameTime > Freshness ||
            (_initialized && frameTime <= _lastFrame))
        {
            Reset();
            return _observations;
        }
        if (GeometryChanged(width, height, stride, boardCorners))
        {
            Reset();
            if (!Configure(width, height, stride, boardCorners)) return _observations;
        }
        Sample(bgra);
        if (!_initialized || frameTime - _lastFrame > MaximumGap)
        {
            Initialize(frameTime);
            return _observations;
        }

        double elapsed = (frameTime - _lastFrame).TotalSeconds;
        _lastFrame = frameTime;
        Array.Clear(_redHistogram);
        Array.Clear(_greenHistogram);
        Array.Clear(_blueHistogram);
        for (int i = 0; i < Cells; i++)
        {
            if (_offsets[i] < 0) continue;
            int c = i * 3;
            _redHistogram[Math.Clamp((int)(_samples[c] - _background[c]) + 255, 0, 510)]++;
            _greenHistogram[Math.Clamp((int)(_samples[c + 1] - _background[c + 1]) + 255, 0, 510)]++;
            _blueHistogram[Math.Clamp((int)(_samples[c + 2] - _background[c + 2]) + 255, 0, 510)]++;
        }
        // The median ignores small foreground objects while compensating camera exposure/white balance.
        int redShift = Median(_redHistogram), greenShift = Median(_greenHistogram), blueShift = Median(_blueHistogram);
        float luminanceShift = (redShift + greenShift + blueShift) / 3f;
        bool warming = frameTime < _warmUntil;
        int foregroundCount = 0;
        Array.Clear(_protected);
        for (int i = 0; i < Cells; i++)
        {
            _foreground[i] = 0;
            if (_offsets[i] < 0) continue;
            int c = i * 3;
            float red = _samples[c] - _background[c] - redShift;
            float green = _samples[c + 1] - _background[c + 1] - greenShift;
            float blue = _samples[c + 2] - _background[c + 2] - blueShift;
            float chroma = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
            float luminance = Math.Abs((red + green + blue) / 3);
            // A dark projected surface cannot lose 40 levels when a hand occludes it.
            // Retain the bright-surface threshold and the measured camera-noise floor.
            float backgroundLuminance = (_background[c] + redShift + _background[c + 1] + greenShift +
                _background[c + 2] + blueShift) / 3;
            float luminanceFloor = Math.Clamp(backgroundLuminance * .60f, 14, 40);
            // Require a recent, abrupt loss before retaining sparse detail.
            // A slow fade across the scenery must not become a moving hand
            // merely because its older residuals fall out of the reference.
            float detailThreshold = Math.Max(luminanceFloor, _noise[i] * 5 + 14);
            float sampleLuminance = (_samples[c] + _samples[c + 1] + _samples[c + 2]) / 3f;
            if (_detailBackground[i] + luminanceShift - sampleLuminance <= detailThreshold) _detailArmed[i] = 0;
            else if (_detailOnsetBackground[i] + luminanceShift - sampleLuminance > detailThreshold) _detailArmed[i] = 1;
            bool foreground = !warming && (chroma > Math.Max(24, _noise[i] * 4 + 8) ||
                luminance > Math.Max(luminanceFloor, _noise[i] * 5 + 14));
            if (foreground) { _foreground[i] = 1; foregroundCount++; }
        }
        // A scene redraw is a new baseline, never a very large moving hand.
        if (foregroundCount > _validCells * .40)
        {
            Initialize(frameTime);
            return _observations;
        }
        BoardMotionObservation? sparseDetail = FindComponents(luminanceShift);
        MatchComponents(frameTime);
        if (_observations.Count == 0 && sparseDetail is { } detail) _observations.Add(detail);
        float backgroundRate = (float)(1 - Math.Exp(-elapsed / (warming ? .12 : 2.5)));
        float stationaryRate = (float)(1 - Math.Exp(-elapsed / .10));
        float noiseRate = (float)(1 - Math.Exp(-elapsed / .7));
        float detailRate = (float)(1 - Math.Exp(-elapsed / .5));
        float onsetRate = (float)(1 - Math.Exp(-elapsed / .2));
        for (int i = 0; i < Cells; i++)
        {
            if (_offsets[i] < 0) continue;
            int c = i * 3;
            byte protection = _componentProtection[_componentStart[i]];
            // Sparse detail uses a recent reference as well as the original
            // background: a slowly fading star must not remain an occlusion.
            // Update after classification, preserving the first hand arrival.
            float luminance = (_samples[c] + _samples[c + 1] + _samples[c + 2]) / 3f;
            _detailBackground[i] += (luminance - luminanceShift - _detailBackground[i]) * detailRate;
            _detailOnsetBackground[i] += (luminance - luminanceShift - _detailOnsetBackground[i]) * onsetRate;
            _foregroundAge[i] = _protected[i] == 0 || protection == 1 ? 0 : _foregroundAge[i] + (float)elapsed;
            if (_protected[i] == 0 || protection == 2 || _foregroundAge[i] > .65)
            {
                // Coherent movement protects the whole component, including its trailing edge.
                // Bound protection for a stationary residual: a hand present during initialization
                // must not leave a permanent ghost
                // after lifting, or occupy a tracking slot forever after a controls transition.
                float rate = _protected[i] == 0 ? backgroundRate : stationaryRate;
                float change = (Math.Abs(_samples[c] - _previous[c]) +
                    Math.Abs(_samples[c + 1] - _previous[c + 1]) + Math.Abs(_samples[c + 2] - _previous[c + 2])) / 3f;
                _noise[i] += (Math.Min(40, change) - _noise[i]) * noiseRate;
                _background[c] += (_samples[c] - redShift - _background[c]) * rate;
                _background[c + 1] += (_samples[c + 1] - greenShift - _background[c + 1]) * rate;
                _background[c + 2] += (_samples[c + 2] - blueShift - _background[c + 2]) * rate;
            }
        }
        _samples.CopyTo(_previous, 0);
        return _observations;
    }

    public void Reset()
    {
        _initialized = false;
        _observations.Clear();
        Array.Clear(_tracks);
    }

    private bool GeometryChanged(int width, int height, int stride, IReadOnlyList<PixelPoint> corners)
    {
        if (!_configured || width != _width || height != _height || stride != _stride || corners.Count != 4) return true;
        for (int i = 0; i < 4; i++) if (corners[i] != _corners[i]) return true;
        return false;
    }

    private bool Configure(int width, int height, int stride, IReadOnlyList<PixelPoint> corners)
    {
        _configured = false;
        if (corners.Count != 4) return false;
        double winding = 0;
        for (int i = 0; i < 4; i++)
        {
            PixelPoint a = corners[i], b = corners[(i + 1) % 4], c = corners[(i + 2) % 4];
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y)) return false;
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (!double.IsFinite(cross) || Math.Abs(cross) < 1 || (i > 0 && Math.Sign(cross) != Math.Sign(winding))) return false;
            winding = cross;
        }
        PixelPoint p0 = corners[0], p1 = corners[1], p2 = corners[2], p3 = corners[3];
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dx3 = p0.X - p1.X + p2.X - p3.X;
        double dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;
        double determinant = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(determinant) < 1e-8) return false;
        double g = (dx3 * dy2 - dx2 * dy3) / determinant, h = (dx1 * dy3 - dx3 * dy1) / determinant;
        double a0 = p1.X - p0.X + g * p1.X, a1 = p3.X - p0.X + h * p3.X;
        double b0 = p1.Y - p0.Y + g * p1.Y, b1 = p3.Y - p0.Y + h * p3.Y;
        _validCells = 0;
        for (int y = 0; y < Rows; y++)
        {
            double v = (y + .5) / Rows;
            for (int x = 0; x < Columns; x++)
            {
                double u = (x + .5) / Columns, divisor = g * u + h * v + 1;
                double cameraX = (a0 * u + a1 * v + p0.X) / divisor;
                double cameraY = (b0 * u + b1 * v + p0.Y) / divisor;
                int index = y * Columns + x;
                if (!double.IsFinite(cameraX) || !double.IsFinite(cameraY) || cameraX < 0 || cameraY < 0 ||
                    cameraX > width - 1 || cameraY > height - 1)
                {
                    _offsets[index] = -1;
                    continue;
                }
                _offsets[index] = (int)Math.Round(cameraY) * stride + (int)Math.Round(cameraX) * 4;
                _validCells++;
            }
        }
        if (_validCells < Cells / 2) return false;
        for (int i = 0; i < 4; i++) _corners[i] = corners[i];
        (_width, _height, _stride, _configured) = (width, height, stride, true);
        return true;
    }

    private void Sample(byte[] bgra)
    {
        for (int i = 0; i < Cells; i++)
        {
            int offset = _offsets[i];
            if (offset < 0) continue;
            int c = i * 3;
            // Fine projected stars can fall between the band's sparse samples.
            // Preserve the brightest real pixel in a bounded local footprint,
            // keeping its RGB channels together instead of inventing a colour.
            int x = offset % _stride / 4, y = offset / _stride;
            int peak = offset, maximum = -1;
            for (int dy = Math.Max(-1, -y); dy <= Math.Min(1, _height - 1 - y); dy++)
            for (int dx = Math.Max(-2, -x); dx <= Math.Min(2, _width - 1 - x); dx++)
            {
                int adjacent = offset + dy * _stride + dx * 4;
                int brightness = bgra[adjacent] + bgra[adjacent + 1] + bgra[adjacent + 2];
                if (brightness > maximum) { maximum = brightness; peak = adjacent; }
            }
            offset = peak;
            _samples[c] = bgra[offset + 2];
            _samples[c + 1] = bgra[offset + 1];
            _samples[c + 2] = bgra[offset];
        }
    }

    private void Initialize(DateTimeOffset frameTime)
    {
        for (int i = 0; i < _samples.Length; i++) _background[i] = _samples[i];
        for (int i = 0; i < Cells; i++)
            _detailBackground[i] = (_samples[i * 3] + _samples[i * 3 + 1] + _samples[i * 3 + 2]) / 3f;
        _detailBackground.CopyTo(_detailOnsetBackground, 0);
        Array.Clear(_detailArmed);
        _samples.CopyTo(_previous, 0);
        Array.Fill(_noise, 4);
        Array.Clear(_foregroundAge);
        Array.Clear(_tracks);
        _initialized = true;
        _lastFrame = frameTime;
        _warmUntil = frameTime + Warmup;
    }

    private int Median(int[] histogram)
    {
        int count = 0;
        for (int i = 0; i < histogram.Length; i++)
            if ((count += histogram[i]) > _validCells / 2) return i - 255;
        return 0;
    }

    private BoardMotionObservation? FindComponents(float luminanceShift)
    {
        _candidateCount = 0;
        DetailLoss details = default;
        Array.Clear(_detailColumns);
        Array.Clear(_connected);
        Array.Clear(_componentProtection);
        // Remove isolated sampling noise without dilating separate ripples into a hand-sized blob.
        for (int y = 0; y < Rows; y++)
        for (int x = 0; x < Columns; x++)
        {
            int index = y * Columns + x;
            if (_foreground[index] == 0) continue;
            int neighbors = 0;
            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(Rows - 1, y + 1); yy++)
            for (int xx = Math.Max(0, x - 1); xx <= Math.Min(Columns - 1, x + 1); xx++)
                neighbors += _foreground[yy * Columns + xx];
            if (neighbors >= 3) _connected[index] = 1;
        }
        for (int start = 0; start < Cells; start++)
        {
            if (_connected[start] != 1) continue;
            int read = 0, count = 1, left = Columns, right = 0, top = Rows, bottom = 0;
            long sumX = 0, sumY = 0;
            _queue[0] = start;
            _connected[start] = 2;
            while (read < count)
            {
                int index = _queue[read++], x = index % Columns, y = index / Columns;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                sumX += x; sumY += y;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(Rows - 1, y + 1); yy++)
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(Columns - 1, x + 1); xx++)
                {
                    int adjacent = yy * Columns + xx;
                    if (_connected[adjacent] != 1) continue;
                    _connected[adjacent] = 2;
                    _queue[count++] = adjacent;
                }
            }
            int width = right - left + 1, height = bottom - top + 1;
            if (count < _validCells * .018 || count > _validCells * .30 ||
                width < Columns * .035 || width > Columns * .34 || height < Rows * .12 ||
                count < width * height * .42)
            {
                // On a dark surface an occluding hand may only damage a few stars
                // or letters. Use losses of those existing details, not arbitrary
                // small moving objects. The gesture stage supplies the successive
                // positions and direction; this does not need a hand-sized blob.
                if (count >= 8 && width >= 2 && height >= 2 && width <= Columns * .34 &&
                    count <= _validCells * .018 &&
                    LostBrightDetail(count, left, right, top, bottom, luminanceShift) is { } losses)
                {
                    for (int i = 0; i < count; i++) _connected[_queue[i]] = 3;
                    details.Merge(losses);
                    _detailColumns[(int)(losses.X / losses.Count)].Merge(losses);
                }
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                _protected[_queue[i]] = 1;
                _componentStart[_queue[i]] = start;
            }
            Candidate candidate = new((sumX / (double)count + .5) / Columns,
                (sumY / (double)count + .5) / Rows, count, start);
            int insert = _candidateCount;
            if (insert == _candidates.Length)
            {
                insert--;
                if (candidate.Area <= _candidates[insert].Area) continue;
            }
            else _candidateCount++;
            while (insert > 0 && candidate.Area > _candidates[insert - 1].Area)
            {
                _candidates[insert] = _candidates[insert - 1];
                insert--;
            }
            _candidates[insert] = candidate;
        }
        if (_candidateCount != 0) return null;
        // Fine stars can disappear as isolated samples. Preserve the existing
        // component-sized surround for larger features; count only additional
        // qualified losses here, including those removed by the noise filter.
        for (int index = 0; index < Cells; index++)
        {
            if (_foreground[index] == 0 || _connected[index] == 3) continue;
            int x = index % Columns, y = index / Columns;
            if (LostBrightDetail(1, x, x, y, y, luminanceShift, index) is { } loss)
            {
                details.Merge(loss);
                _detailColumns[x].Merge(loss);
            }
        }
        // One independently drifting fine star must not veto a much larger
        // local occlusion. Keep all evidence unless one compact cluster owns
        // at least 80%, with only a single small sampling footprint elsewhere.
        // Comparable clusters and scattered/global flicker still fail below.
        if (details.Count >= 8 && details.Right - details.Left + 1 > Columns * .34)
        {
            int window = (int)(Columns * .34), sum = 0, maximum = 0, start = 0;
            for (int x = 0; x < Columns; x++)
            {
                sum += _detailColumns[x].Count;
                if (x >= window) sum -= _detailColumns[x - window].Count;
                if (sum > maximum) { maximum = sum; start = Math.Max(0, x - window + 1); }
            }
            if (maximum >= 8 && maximum >= details.Count * .80 && details.Count - maximum <= 4)
            {
                DetailLoss local = default, remote = default;
                for (int x = 0; x < Columns; x++)
                {
                    if (_detailColumns[x].Count == 0) continue;
                    if (x >= start && x < start + window) local.Merge(_detailColumns[x]);
                    else remote.Merge(_detailColumns[x]);
                }
                if (remote.Right - remote.Left + 1 <= 3 && remote.Bottom - remote.Top + 1 <= 3)
                    details = local;
            }
        }
        // Scattered flicker or a scene-wide exposure change is not one local
        // disturbance. Keep the normal filled-component path unchanged.
        return details.Count >= 2 && details.Right - details.Left + 1 <= Columns * .34 &&
            details.Bottom - details.Top + 1 <= Rows * .85
            ? new BoardMotionObservation(0, (details.X / (double)details.Count + .5) / Columns,
                (details.Y / (double)details.Count + .5) / Rows) : null;
    }

    private DetailLoss? LostBrightDetail(int count, int left, int right, int top, int bottom, float luminanceShift,
        int isolatedSample = -1)
    {
        // A narrow ring measures the original local surface without reading or
        // copying the camera again. Bright canvases retain their original gate.
        // Neighboring samples can include the same fine star's footprint.
        int padding = isolatedSample >= 0 ? 1 : 2;
        if (isolatedSample >= 0) { left--; right++; top--; bottom++; }
        float surround = 0;
        int surroundingCells = 0;
        for (int y = Math.Max(0, top - padding); y <= Math.Min(Rows - 1, bottom + padding); y++)
        for (int x = Math.Max(0, left - padding); x <= Math.Min(Columns - 1, right + padding); x++)
        {
            if (x >= left && x <= right && y >= top && y <= bottom) continue;
            int index = y * Columns + x;
            if (_offsets[index] < 0) continue;
            int c = index * 3;
            surround += (_background[c] + _background[c + 1] + _background[c + 2]) / 3 + luminanceShift;
            surroundingCells++;
        }
        if (surroundingCells == 0 || (surround /= surroundingCells) > 48) return null;
        DetailLoss loss = default;
        for (int i = 0; i < count; i++)
        {
            int index = isolatedSample >= 0 ? isolatedSample : _queue[i], c = index * 3;
            float background = (_background[c] + _background[c + 1] + _background[c + 2]) / 3 + luminanceShift;
            float sample = (_samples[c] + _samples[c + 1] + _samples[c + 2]) / 3f;
            float threshold = Math.Max(Math.Clamp(background * .60f, 14, 40), _noise[index] * 5 + 14);
            if (_noise[index] <= 8 && background - surround >= 18 && background - sample > threshold &&
                _detailArmed[index] != 0 && _detailBackground[index] + luminanceShift - sample > threshold)
                loss.Merge(new DetailLoss { Count = 1, X = index % Columns, Y = index / Columns,
                    Left = index % Columns, Right = index % Columns, Top = index / Columns, Bottom = index / Columns });
        }
        return loss.Count >= (isolatedSample >= 0 ? 1 : 6) && loss.Count >= count * .60 ? loss : null;
    }

    private void MatchComponents(DateTimeOffset frameTime)
    {
        Array.Clear(_matchedTracks);
        Array.Clear(_matchedCandidates);
        Array.Clear(_candidateTrackingIds);
        for (int i = 0; i < _tracks.Length; i++)
            if (frameTime - _tracks[i].LastSeen > TimeSpan.FromMilliseconds(180)) _tracks[i] = default;
        for (int match = 0; match < _tracks.Length; match++)
        {
            int bestTrack = -1, bestCandidate = -1;
            double bestDistance = double.MaxValue;
            for (int t = 0; t < _tracks.Length; t++)
            {
                Track track = _tracks[t];
                if (track.Id == 0 || _matchedTracks[t]) continue;
                double maximumDistance = Math.Min(.24, .055 + (frameTime - track.LastSeen).TotalSeconds * 1.6);
                for (int c = 0; c < _candidateCount; c++)
                {
                    if (_matchedCandidates[c]) continue;
                    Candidate candidate = _candidates[c];
                    double dx = candidate.U - track.U, dy = (candidate.V - track.V) * .30;
                    double distance = dx * dx + dy * dy;
                    if (distance >= bestDistance || distance > maximumDistance * maximumDistance ||
                        candidate.Area < track.Area * .30 || candidate.Area > track.Area * 3.3) continue;
                    bestDistance = distance; bestTrack = t; bestCandidate = c;
                }
            }
            if (bestTrack < 0) break;
            Track previous = _tracks[bestTrack];
            Candidate current = _candidates[bestCandidate];
            bool moved = Math.Abs(current.U - previous.AnchorU) > .01 || Math.Abs(current.V - previous.AnchorV) > .04;
            Track updated = new(previous.Id, current.U, current.V, current.Area, frameTime,
                moved ? current.U : previous.AnchorU, moved ? current.V : previous.AnchorV,
                moved ? frameTime : previous.LastMoved);
            _tracks[bestTrack] = updated;
            _componentProtection[current.ComponentStart] = (byte)(frameTime - updated.LastMoved <= TimeSpan.FromMilliseconds(650) ? 1 : 2);
            _matchedTracks[bestTrack] = true;
            _matchedCandidates[bestCandidate] = true;
            _candidateTrackingIds[bestCandidate] = updated.Id;
        }
        for (int c = 0; c < _candidateCount; c++)
        {
            if (_matchedCandidates[c]) continue;
            for (int t = 0; t < _tracks.Length; t++)
            {
                if (_tracks[t].Id != 0) continue;
                Candidate candidate = _candidates[c];
                _tracks[t] = new(++_nextTrackingId, candidate.U, candidate.V, candidate.Area, frameTime,
                    candidate.U, candidate.V, frameTime);
                _candidateTrackingIds[c] = _nextTrackingId;
                _componentProtection[candidate.ComponentStart] = 1;
                break;
            }
        }
        // Internal identity protects moving foreground from background adaptation. A swipe
        // consumes the current compact disturbances immediately, even when their shape splits
        // or neither protection track can be matched. It does not require continuous identity.
        for (int c = 0; c < _candidateCount; c++)
            _observations.Add(new(_candidateTrackingIds[c], _candidates[c].U, _candidates[c].V));
    }

    private struct DetailLoss
    {
        public int Count, Left, Right, Top, Bottom;
        public long X, Y;

        public void Merge(DetailLoss loss)
        {
            if (Count == 0) { this = loss; return; }
            Count += loss.Count; X += loss.X; Y += loss.Y;
            Left = Math.Min(Left, loss.Left); Right = Math.Max(Right, loss.Right);
            Top = Math.Min(Top, loss.Top); Bottom = Math.Max(Bottom, loss.Bottom);
        }
    }

    private readonly record struct Candidate(double U, double V, int Area, int ComponentStart);
    private readonly record struct Track(long Id, double U, double V, int Area, DateTimeOffset LastSeen,
        double AnchorU, double AnchorV, DateTimeOffset LastMoved);
}
