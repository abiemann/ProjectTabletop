using OpenCvSharp;

namespace ProjectTabletop.Vision;

public sealed record PatternRailObservation(float? Position, bool Ambiguous, bool Reliable, string Reason,
    int DamageCount, DateTimeOffset SourceTime, int IntactCount = 0, double Correlation = 0,
    int RegistrationX = 0, int RegistrationY = 0, double Blur = 0,
    double OpticalRadius = PatternRailGeometry.DotRadius, bool GeometryRefined = false);

public sealed record PatternRailDiagnostics(long Revision, DateTimeOffset? SourceTime, bool Registered,
    bool GeometryRefined, IReadOnlyList<PixelPoint> OriginalCameraCorners, IReadOnlyList<PixelPoint> EffectiveCameraCorners,
    int RegisteredVariant, int RegistrationX, int RegistrationY, double OpticalRadius, double Blur,
    int MeasuredVariant, int MeasuredX, int MeasuredY, double MeasuredOpticalRadius, double MeasuredBlur,
    IReadOnlyList<double> Correlations, IReadOnlyList<double> Contrasts);

/// <summary>
/// Measures physical corruption of a known white-colon rail. Intact marks with changed brightness
/// or colour remain clear. No camera background is learned and no hand recognition is required.
/// One worker owns this instance; callers also reject late results at publication.
/// </summary>
public sealed class PatternRailDetector
{
    private const int Width = PatternRailGeometry.ReferenceWidth, Height = PatternRailGeometry.ReferenceHeight;
    private const int Marks = PatternRailGeometry.Columns * 2;
    private const double IntactCorrelation = .82, DamagedCorrelation = .60, MinimumContrast = 2.5;
    private static readonly double[] Blurs = [0, .8, 1.6, 2.4];
    private static readonly (double Radius, double Blur)[] Optics =
        Enumerable.Range(PatternRailGeometry.DotRadius, 4)
            .SelectMany(radius => Blurs.Select(blur => ((double)radius, blur))).ToArray();
    private static readonly float[][] Expected = MakeExpected();
    private static readonly int[][] Support = MakeSupport();
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMilliseconds(250);
    private readonly float[] _gray = new float[Width * Height];
    private readonly float[] _boxMean = new float[Width * Height];
    private readonly float[] _highPass = new float[Width * Height];
    private readonly double[] _correlations = new double[Marks];
    private readonly double[] _contrasts = new double[Marks];
    private readonly bool[] _damagedColumns = new bool[PatternRailGeometry.Columns];
    private int[] _offsets = [];
    private float[] _fractionX = [], _fractionY = [];
    private int[] _candidateOffsets = [];
    private float[] _candidateFractionX = [], _candidateFractionY = [];
    private PatternRailRegistration? _registrationHelper;
    private PixelPoint[] _corners = [], _effectiveCorners = [];
    private long _revision = long.MinValue;
    private int _cameraWidth, _cameraHeight, _stride;
    private bool _registered, _hasFrame, _geometryRefined;
    private int _dx, _dy, _variant, _confirmation;
    private int _measuredVariant, _measuredX, _measuredY;
    private double _pendingPosition;
    private DateTimeOffset _lastFrame;

    // Call on the detector's owning worker. Only this explicit diagnostic request clones measurements.
    // A rejected recovery candidate may own the last measurements without replacing the registered optics.
    public PatternRailDiagnostics GetDiagnostics() => new(_revision, _hasFrame ? _lastFrame : null,
        _registered, _geometryRefined, Array.AsReadOnly((PixelPoint[])_corners.Clone()),
        Array.AsReadOnly((PixelPoint[])_effectiveCorners.Clone()), _variant, _dx, _dy,
        Optics[_variant].Radius, Optics[_variant].Blur, _measuredVariant, _measuredX, _measuredY,
        Optics[_measuredVariant].Radius, Optics[_measuredVariant].Blur,
        Array.AsReadOnly((double[])_correlations.Clone()), Array.AsReadOnly((double[])_contrasts.Clone()));

    public PatternRailObservation Observe(byte[] bgra, int width, int height, int stride,
        PatternRailGeometry geometry, DateTimeOffset frameTime)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(geometry);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (_revision != geometry.Revision || width != _cameraWidth || height != _cameraHeight || stride != _stride ||
            geometry.CameraCorners is null || !_corners.SequenceEqual(geometry.CameraCorners))
            if (!Configure(width, height, stride, geometry)) return Empty("invalid-rail-geometry");
        if (_hasFrame && frameTime <= _lastFrame) return Empty("old-camera-frame");
        if (_hasFrame && frameTime - _lastFrame > MaximumGap) _confirmation = 0;
        _lastFrame = frameTime;
        _hasFrame = true;
        Sample(bgra);
        HighPass(_gray, _boxMean, _highPass);

        if (!_registered)
        {
            FindRegistration(out int variant, out int dx, out int dy);
            Measure(variant, dx, dy);
            if (!HasWitnesses() && !TryRefineRegistration(bgra, ref variant, ref dx, ref dy))
            {
                _confirmation = 0;
                return CandidateEmpty("rail-shape-uncertain", variant, dx, dy);
            }
            _variant = variant; _dx = dx; _dy = dy; _registered = true;
        }
        else
        {
            Measure(_variant, _dx, _dy);
            if (!HasWitnesses())
            {
                // Recover one common optical profile on the frozen map. During contact,
                // every mark outside one damaged span must still match; obscured cells
                // never train the geometry or move independently to neighbouring colons.
                FindRegistration(out int variant, out int dx, out int dy);
                Measure(variant, dx, dy);
                if (CountIntact() < Marks - 2)
                {
                    if (CanRecoverCoveredRegistration(dx, dy))
                        _confirmation = 0; // The new profile needs two fresh contact observations.
                    else if (!TryRefineRegistration(bgra, ref variant, ref dx, ref dy))
                    {
                        _confirmation = 0;
                        return CandidateEmpty("rail-shape-uncertain", variant, dx, dy);
                    }
                }
                _variant = variant; _dx = dx; _dy = dy;
            }
        }

        int intact = CountIntact(), damaged = 0;
        double confidence = 0;
        Array.Clear(_damagedColumns);
        for (int mark = 0; mark < Marks; mark++)
        {
            if (IsIntact(mark)) confidence += _correlations[mark];
            if (_correlations[mark] >= DamagedCorrelation && _contrasts[mark] >= MinimumContrast) continue;
            if (MarkUncertainty(mark, _dx, _dy) is { } reason)
            {
                _confirmation = 0;
                return Empty(reason);
            }
            _damagedColumns[mark / 2] = true;
            damaged++;
        }
        confidence /= Math.Max(1, intact);
        if (damaged == 0)
        {
            _confirmation = 0;
            return Result(null, false, true, "intact-rail");
        }

        int spans = 0, previous = -2;
        double weightedX = 0, weightSum = 0;
        for (int column = 0; column < _damagedColumns.Length; column++)
        {
            if (!_damagedColumns[column]) continue;
            if (column != previous + 1) spans++;
            previous = column;
            double weight = 0;
            for (int mark = column * 2; mark < column * 2 + 2; mark++)
                if (_correlations[mark] < DamagedCorrelation || _contrasts[mark] < MinimumContrast)
                    weight += Math.Clamp(1 - _correlations[mark], .1, 1);
            weightedX += (column + .5) / PatternRailGeometry.Columns * weight;
            weightSum += weight;
        }
        if (spans > 1)
        {
            _confirmation = 0;
            return Result(null, true, false, "multiple-occlusions");
        }
        if (damaged < 2)
        {
            _confirmation = 0;
            return Result(null, false, false, "insufficient-shape-damage");
        }
        double position = weightedX / weightSum;
        _confirmation = _confirmation > 0 && Math.Abs(position - _pendingPosition) <= .12
            ? Math.Min(2, _confirmation + 1) : 1;
        _pendingPosition = position;
        return Result(_confirmation >= 2 ? (float)position : null, false, true,
            _confirmation >= 2 ? "pattern-occluded" : "confirming-occlusion");

        PatternRailObservation Result(float? position, bool ambiguous, bool reliable, string reason) =>
            new(position, ambiguous, reliable, reason, damaged, frameTime, intact, confidence,
                _dx, _dy, Optics[_variant].Blur, Optics[_variant].Radius, _geometryRefined);
        PatternRailObservation CandidateEmpty(string reason, int variant, int dx, int dy) =>
            new(null, false, false, reason, 0, frameTime, CountIntact(), _correlations.Average(),
                dx, dy, Optics[variant].Blur, Optics[variant].Radius, _geometryRefined);
        PatternRailObservation Empty(string reason) => new(null, false, false, reason, 0, frameTime,
            RegistrationX: _dx, RegistrationY: _dy, Blur: Optics[_variant].Blur,
            OpticalRadius: Optics[_variant].Radius, GeometryRefined: _geometryRefined);
    }

    public void Reset()
    {
        _revision = long.MinValue; _cameraWidth = _cameraHeight = _stride = 0;
        _corners = []; _effectiveCorners = []; _offsets = []; _fractionX = []; _fractionY = [];
        _registered = _hasFrame = _geometryRefined = false; _dx = _dy = _variant = _confirmation = 0;
        _measuredVariant = _measuredX = _measuredY = 0;
        Array.Clear(_correlations); Array.Clear(_contrasts);
        _pendingPosition = 0; _lastFrame = default;
    }

    private bool Configure(int width, int height, int stride, PatternRailGeometry geometry)
    {
        Reset();
        if (geometry.CameraCorners is not { Count: 4 } ||
            geometry.CameraCorners.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < 0 || point.Y < 0 || point.X >= width - 1 || point.Y >= height - 1)) return false;
        var corners = geometry.CameraCorners;
        if (!SetSamplingOffsets(width, height, stride, corners)) return false;
        _corners = corners.ToArray(); _effectiveCorners = _corners; _revision = geometry.Revision;
        _cameraWidth = width; _cameraHeight = height; _stride = stride;
        return true;
    }

    private bool SetSamplingOffsets(int width, int height, int stride, IReadOnlyList<PixelPoint> corners)
    {
        double winding = 0;
        for (int i = 0; i < 4; i++)
        {
            var first = corners[i]; var second = corners[(i + 1) % 4]; var third = corners[(i + 2) % 4];
            double cross = (second.X - first.X) * (third.Y - second.Y) -
                (second.Y - first.Y) * (third.X - second.X);
            if (Math.Abs(cross) < 1e-6 || winding != 0 && Math.Sign(winding) != Math.Sign(cross)) return false;
            winding = cross;
        }
        var p0 = corners[0]; var p1 = corners[1]; var p2 = corners[2]; var p3 = corners[3];
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, sx = p0.X - p1.X + p2.X - p3.X;
        double dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, sy = p0.Y - p1.Y + p2.Y - p3.Y;
        double determinant = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(determinant) < 1e-9) return false;
        double g = (sx * dy2 - dx2 * sy) / determinant, h = (dx1 * sy - sx * dy1) / determinant;
        double a = p1.X - p0.X + g * p1.X, b = p3.X - p0.X + h * p3.X;
        double d = p1.Y - p0.Y + g * p1.Y, e = p3.Y - p0.Y + h * p3.Y;
        PixelPoint Map(double x, double y)
        {
            double u = x / Width, v = y / Height, divisor = g * u + h * v + 1;
            return new((a * u + b * v + p0.X) / divisor, (d * u + e * v + p0.Y) / divisor);
        }
        // Tiny projected marks cannot supply a trustworthy shape at native camera resolution.
        foreach (double x in new[] { 10d, Width / 2d, Width - 10d })
        {
            const int radius = PatternRailGeometry.DotRadius;
            var left = Map(x - radius, 32); var right = Map(x + radius, 32);
            var top = Map(x, 32 - radius); var bottom = Map(x, 32 + radius);
            if (Distance(left, right) < 3 || Distance(top, bottom) < 3) return false;
        }
        if (_offsets.Length != Width * Height)
        {
            _offsets = new int[Width * Height]; _fractionX = new float[_offsets.Length]; _fractionY = new float[_offsets.Length];
        }
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var point = Map(x + .5, y + .5);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 ||
                point.X >= width - 1 || point.Y >= height - 1) return false;
            int ix = (int)point.X, iy = (int)point.Y, index = y * Width + x;
            _offsets[index] = iy * stride + ix * 4;
            _fractionX[index] = (float)(point.X - ix); _fractionY[index] = (float)(point.Y - iy);
        }
        return true;
    }

    private bool TryRefineRegistration(byte[] bgra, ref int variant, ref int dx, ref int dy)
    {
        // This geometry is a fixed interaction reference. A correction is accepted only on a
        // completely intact rail, then stays frozen until the caller changes the geometry epoch.
        if (_geometryRefined || !(_registrationHelper ??= new()).TryRefine(bgra, _cameraWidth, _cameraHeight,
                _stride, _corners, out var corrected)) return false;
        SwapCoordinates();
        bool accepted = false;
        int candidateVariant = 0, candidateDx = 0, candidateDy = 0;
        if (SetSamplingOffsets(_cameraWidth, _cameraHeight, _stride, corrected))
        {
            Sample(bgra); HighPass(_gray, _boxMean, _highPass);
            FindRegistration(out candidateVariant, out candidateDx, out candidateDy);
            Measure(candidateVariant, candidateDx, candidateDy);
            // Validate the proposed mapping itself. The normal ±3-pixel translation search may
            // not extend the four-native-pixel geometric bound or conceal a periodic shift.
            accepted = candidateDx == 0 && candidateDy == 0 && CountIntact() == Marks;
        }
        if (accepted)
        {
            _geometryRefined = true;
            _effectiveCorners = corrected;
            variant = candidateVariant; dx = candidateDx; dy = candidateDy;
            return true;
        }
        SwapCoordinates();
        Sample(bgra); HighPass(_gray, _boxMean, _highPass);
        Measure(variant, dx, dy);
        return false;

        void SwapCoordinates()
        {
            (_offsets, _candidateOffsets) = (_candidateOffsets, _offsets);
            (_fractionX, _candidateFractionX) = (_candidateFractionX, _fractionX);
            (_fractionY, _candidateFractionY) = (_candidateFractionY, _fractionY);
        }
    }

    private void Sample(byte[] camera)
    {
        for (int i = 0; i < _gray.Length; i++)
        {
            int p = _offsets[i]; float x = _fractionX[i], y = _fractionY[i];
            _gray[i] = Luminance(camera, p) * (1 - x) * (1 - y) + Luminance(camera, p + 4) * x * (1 - y) +
                Luminance(camera, p + _stride) * (1 - x) * y + Luminance(camera, p + _stride + 4) * x * y;
        }
    }

    private void FindRegistration(out int bestVariant, out int bestDx, out int bestDy)
    {
        bestVariant = bestDx = bestDy = 0;
        double best = double.NegativeInfinity;
        Span<double> scores = stackalloc double[24];
        for (int variant = 0; variant < Expected.Length; variant++)
        for (int dy = -3; dy <= 3; dy++)
        for (int dx = -3; dx <= 3; dx++)
        {
            int count = 0, intact = 0;
            for (int column = 1; column < PatternRailGeometry.Columns; column += 4)
            for (int dot = 0; dot < 2; dot++)
            {
                double correlation = Correlate(column * 2 + dot, variant, dx, dy, out double contrast);
                scores[count++] = contrast >= MinimumContrast ? correlation : -1;
                if (contrast >= MinimumContrast && correlation >= IntactCorrelation) intact++;
            }
            scores.Sort();
            double score = scores[count / 2] + intact * .01 - (Math.Abs(dx) + Math.Abs(dy)) * .00001;
            if (score <= best) continue;
            best = score; bestVariant = variant; bestDx = dx; bestDy = dy;
        }
    }

    private void Measure(int variant, int dx, int dy)
    {
        _measuredVariant = variant; _measuredX = dx; _measuredY = dy;
        for (int mark = 0; mark < Marks; mark++)
            _correlations[mark] = Correlate(mark, variant, dx, dy, out _contrasts[mark]);
    }

    private double Correlate(int mark, int variant, int dx, int dy, out double contrast)
    {
        double sumA = 0, sumB = 0, aa = 0, bb = 0, ab = 0;
        int count = 0;
        foreach (int index in Support[mark])
        {
            int x = index % Width + dx, y = index / Width + dy;
            if (x < 0 || x >= Width || y < 0 || y >= Height) continue;
            double a = Expected[variant][index], b = _highPass[y * Width + x];
            sumA += a; sumB += b; aa += a * a; bb += b * b; ab += a * b; count++;
        }
        aa -= sumA * sumA / count; bb -= sumB * sumB / count; ab -= sumA * sumB / count;
        contrast = Math.Sqrt(Math.Max(0, bb / count));
        return aa > 1e-9 && bb > 1e-9 ? Math.Clamp(ab / Math.Sqrt(aa * bb), -1, 1) : 0;
    }

    private bool IsIntact(int mark) => _correlations[mark] >= IntactCorrelation && _contrasts[mark] >= MinimumContrast;

    private bool CanRecoverCoveredRegistration(int dx, int dy)
    {
        if (!HasWitnesses()) return false;
        int damaged = 0;
        bool inSpan = false, spanEnded = false;
        for (int column = 0; column < PatternRailGeometry.Columns; column++)
        {
            bool columnDamaged = false;
            for (int mark = column * 2; mark < column * 2 + 2; mark++)
            {
                if (MarkUncertainty(mark, dx, dy) is not null) return false;
                if (_correlations[mark] >= DamagedCorrelation) continue;
                columnDamaged = true;
                damaged++;
            }
            if (columnDamaged)
            {
                if (spanEnded) return false;
                inSpan = true;
            }
            else
            {
                // Intermediate correlations outside the obstruction do not establish
                // a coherent profile change, even if enough other witnesses survived.
                if (!IsIntact(column * 2) || !IsIntact(column * 2 + 1)) return false;
                if (inSpan) spanEnded = true;
            }
        }
        return damaged >= 2;
    }

    private string? MarkUncertainty(int mark, int dx, int dy)
    {
        // Dim correlated dots and saturated erased dots are unavailable evidence,
        // not physical obstruction. Apply the same checks to recovery candidates.
        if (_correlations[mark] >= DamagedCorrelation)
            return _contrasts[mark] < MinimumContrast ? "rail-contrast-uncertain" : null;
        return IsSaturated(mark, dx, dy) ? "rail-exposure-clipped" : null;
    }

    private bool IsSaturated(int mark, int dx, int dy)
    {
        int clipped = 0, count = 0;
        foreach (int index in Support[mark])
        {
            int x = index % Width + dx, y = index / Width + dy;
            if (x < 0 || x >= Width || y < 0 || y >= Height) continue;
            count++;
            if (_gray[y * Width + x] >= 250) clipped++;
        }
        return clipped >= count * .75;
    }
    private int CountIntact()
    {
        int count = 0;
        for (int mark = 0; mark < Marks; mark++) if (IsIntact(mark)) count++;
        return count;
    }
    private bool HasWitnesses()
    {
        int first = 0, last = 0, total = 0;
        for (int mark = 0; mark < Marks; mark++)
            if (IsIntact(mark)) { total++; if (mark < 32) first++; if (mark >= 64) last++; }
        return total >= 64 && first >= 12 && last >= 12;
    }

    private static int[][] MakeSupport()
    {
        var support = new int[Marks][];
        for (int mark = 0; mark < Marks; mark++)
        {
            int centerX = PatternRailGeometry.ColumnCenter(mark / 2);
            int centerY = mark % 2 == 0 ? PatternRailGeometry.DotY1ForColumn(mark / 2) :
                PatternRailGeometry.DotY2ForColumn(mark / 2);
            var indices = new List<int>();
            for (int y = centerY - 8; y <= centerY + 8; y += 2)
            for (int x = centerX - 8; x <= centerX + 8; x += 2) indices.Add(y * Width + x);
            support[mark] = indices.ToArray();
        }
        return support;
    }

    private static float[][] MakeExpected()
    {
        return Optics.Select(optics =>
        {
            // A common projector/camera bloom can expand every bright dot. This optical
            // profile is selected from intact witnesses, including during coherent recovery.
            var image = new float[Width * Height];
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                double dx = x % PatternRailGeometry.CellWidth + .5 - PatternRailGeometry.CellWidth / 2d;
                int column = x / PatternRailGeometry.CellWidth;
                double dy = Math.Min(Math.Abs(y + .5 - PatternRailGeometry.DotY1ForColumn(column)),
                    Math.Abs(y + .5 - PatternRailGeometry.DotY2ForColumn(column)));
                image[y * Width + x] = (float)(255 * Math.Clamp(optics.Radius + .5 - Math.Sqrt(dx * dx + dy * dy), 0, 1));
            }
            float[] blurred = optics.Blur == 0 ? image : Gaussian(image, optics.Blur);
            var result = new float[image.Length];
            HighPass(blurred, new float[image.Length], result);
            return result;
        }).ToArray();
    }

    // The image minus its 11 x 11 box mean, replicating edge pixels. The OpenCV
    // filters are the shared implementation; the caller supplies the mean buffer.
    private static void HighPass(float[] image, float[] mean, float[] result)
    {
        const int diameter = 11;
        using var source = Mat.FromPixelData(Height, Width, MatType.CV_32FC1, image);
        using var average = Mat.FromPixelData(Height, Width, MatType.CV_32FC1, mean);
        using var output = Mat.FromPixelData(Height, Width, MatType.CV_32FC1, result);
        Cv2.BoxFilter(source, average, MatType.CV_32F, new Size(diameter, diameter), new Point(-1, -1), true,
            BorderTypes.Replicate);
        Cv2.Subtract(source, average, output);
    }

    private static float[] Gaussian(float[] source, double sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        var result = new float[source.Length];
        using var input = Mat.FromPixelData(Height, Width, MatType.CV_32FC1, source);
        using var output = Mat.FromPixelData(Height, Width, MatType.CV_32FC1, result);
        Cv2.GaussianBlur(input, output, new Size(radius * 2 + 1, radius * 2 + 1), sigma, sigma, BorderTypes.Replicate);
        return result;
    }
    private static float Luminance(byte[] bgra, int offset) => bgra[offset] * .114f + bgra[offset + 1] * .587f + bgra[offset + 2] * .299f;
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
