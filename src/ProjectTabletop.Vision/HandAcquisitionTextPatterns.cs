namespace ProjectTabletop.Vision;

// Labels have a known shape even when the projector/phone changes their color.
// Compare that shape at native-camera resolution before trusting a broad color fit.
internal sealed class HandAcquisitionTextPatterns
{
    internal sealed record Observation(int Region, bool Clean, bool StrongCorruption, double Correlation,
        IReadOnlyList<PixelPoint> ChangedBoardPixels, double OpticalBlur, int OffsetX, int OffsetY,
        double ScaleX = 1, double ScaleY = 1, IReadOnlyList<double>? SectorCorrelations = null,
        double LocalDamageCoverage = 0, double ExposureGain = 1, double ExposureBackground = 0);
    private sealed record Template(int Region, int Left, int Top, int Width, int Height,
        double[][] HighPass, double[][] Blurred, double Background, int[] Evidence)
    {
        public double CleanCorrelation { get; set; }
        public int? RegisteredVariant { get; set; }
        public int RegisteredDx { get; set; }
        public int RegisteredDy { get; set; }
        public double RegisteredScaleX { get; set; } = 1;
        public double RegisteredScaleY { get; set; } = 1;
        public int? TentativeVariant { get; set; }
        public int TentativeDx { get; set; }
        public int TentativeDy { get; set; }
        public double TentativeScaleX { get; set; } = 1;
        public double TentativeScaleY { get; set; } = 1;
        public int ObservationCount { get; set; }
        public double[]? PreviousHighPass { get; set; }
        public double ExposureGain { get; set; } = 1;
        public double ExposureBackground { get; set; }
        public double[]? ExposureHighPass { get; set; }
        public int ExposureVariant { get; set; }
    }
    private readonly HandAcquisitionSceneImage _scene;
    private readonly double[] _inverse;
    private readonly List<Template> _templates = [];
    private const int LogicalSize = 1000;
    private static readonly double[] Blurs = [0, .5, 1, 1.5, 2, 2.5, 3, 4];

    internal HandAcquisitionTextPatterns(HandAcquisitionSceneImage scene)
    {
        _scene = scene;
        _inverse = Inverse(scene.CameraToBoard);
        if (scene.BoardTriggerRegions is null || _inverse.Length == 0) return;
        for (int region = 0; region < scene.BoardTriggerRegions.Count; region++)
        {
            var bounds = scene.BoardTriggerRegions[region];
            int left = (int)(bounds.X * LogicalSize) - 10, top = (int)(bounds.Y * LogicalSize) - 10;
            int width = (int)((bounds.X + bounds.Width) * LogicalSize) + 10 - left;
            int height = (int)((bounds.Y + bounds.Height) * LogicalSize) + 10 - top;
            if (width is < 24 or > 380 || height is < 24 or > 110) continue;
            double[] expected = new double[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                expected[y * width + x] = SceneLuminance(left + x, top + y);
            double[] highPass = HighPass(expected, width, height);
            bool[] halo = new bool[expected.Length];
            int inkCount = 0, interior = 0;
            double background = expected.Order().ElementAt(expected.Length / 2);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double u = (left + x) / (double)LogicalSize, v = (top + y) / (double)LogicalSize;
                if (!Within(bounds, u, v)) continue;
                interior++;
                int index = y * width + x;
                if (Math.Abs(expected[index] - background) <= 24 || Math.Abs(highPass[index]) <= 6) continue;
                inkCount++;
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    if (x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                        halo[(y + dy) * width + x + dx] = true;
            }
            // A flat trigger patch is not a glyph. Preserve the generic region gate
            // for callers that supply geometric triggers rather than generated text.
            if (inkCount < 20 || inkCount > interior * .75) continue;
            int[] evidence = Enumerable.Range(0, halo.Length).Where(index => halo[index] &&
                Within(bounds, (left + index % width) / (double)LogicalSize,
                    (top + index / width) / (double)LogicalSize)).ToArray();
            if (evidence.Length < 40) continue;
            var blurred = Blurs.Select(blur => blur == 0 ? expected : Blur(expected, width, height, blur)).ToArray();
            var variants = blurred.Select(image => HighPass(image, width, height)).ToArray();
            _templates.Add(new(region, left, top, width, height, variants, blurred,
                expected.Order().ElementAt(expected.Length / 10), evidence));
        }
    }

    internal IReadOnlyList<Observation> Observe(int width, int height, int stride, byte[] camera,
        HandAcquisitionHint? light)
    {
        var observations = new List<Observation>();
        var missingContrast = new Dictionary<int, IReadOnlyList<PixelPoint>>();
        foreach (var template in _templates)
        {
            // The acquisition disk intentionally replaces the label; its white-core
            // measurement owns renewal and must never be compared with the unlit text.
            PixelPoint center = CameraPoint(template.Left + template.Width / 2.0,
                template.Top + template.Height / 2.0);
            if (light is not null && Distance(center, light.Center) < light.RadiusPixels * 1.4)
            {
                observations.Add(new(template.Region, false, false, 0, [], 0, 0, 0));
                continue;
            }
            double[] current = new double[template.Width * template.Height];
            bool valid = true;
            for (int y = 0; y < template.Height; y++)
            for (int x = 0; x < template.Width; x++)
            {
                PixelPoint point = CameraPoint(template.Left + x, template.Top + y);
                if (point.X < 0 || point.Y < 0 || point.X >= width - 1 || point.Y >= height - 1 ||
                    !double.IsFinite(point.X) || !double.IsFinite(point.Y)) { valid = false; continue; }
                int ix = (int)point.X, iy = (int)point.Y;
                double fx = point.X - ix, fy = point.Y - iy;
                current[y * template.Width + x] = PixelLuminance(camera, iy * stride + ix * 4) * (1 - fx) * (1 - fy) +
                    PixelLuminance(camera, iy * stride + (ix + 1) * 4) * fx * (1 - fy) +
                    PixelLuminance(camera, (iy + 1) * stride + ix * 4) * (1 - fx) * fy +
                    PixelLuminance(camera, (iy + 1) * stride + (ix + 1) * 4) * fx * fy;
            }
            if (!valid)
            {
                observations.Add(new(template.Region, false, false, 0, [], 0, 0, 0));
                continue;
            }
            double[] cameraHighPass = HighPass(current, template.Width, template.Height);
            double best = -1, bestMeanExpected = 0, bestMeanCurrent = 0, bestGain = 0, bestStd = 0;
            int bestVariant = 0, bestDx = 0, bestDy = 0;
            double bestScaleX = 1, bestScaleY = 1;
            double[] bestExpected = template.HighPass[0];
            double bestExposureGain = 1, bestExposureBackground = template.Background;
            // Tentative alignment bounds the cost of a still-obstructed startup
            // label. It never establishes clean appearance; periodically retry
            // the full bounded registration until the generated shape matches.
            int? cachedVariant = template.RegisteredVariant ?? template.TentativeVariant;
            bool appearanceChanged = template.PreviousHighPass is { } previous &&
                template.Evidence.Average(index => Math.Abs(cameraHighPass[index] - previous[index])) > 3;
            bool fullSearch = cachedVariant is null || template.RegisteredVariant is null &&
                (++template.ObservationCount % 8 == 0 || appearanceChanged);
            template.PreviousHighPass = cameraHighPass;
            int cachedDx = template.RegisteredVariant is null ? template.TentativeDx : template.RegisteredDx;
            int cachedDy = template.RegisteredVariant is null ? template.TentativeDy : template.RegisteredDy;
            double cachedScaleX = template.RegisteredVariant is null ? template.TentativeScaleX : template.RegisteredScaleX;
            double cachedScaleY = template.RegisteredVariant is null ? template.TentativeScaleY : template.RegisteredScaleY;
            int neighborhood = template.RegisteredVariant is null ? 0 : 2;
            int blurNeighborhood = template.RegisteredVariant is null ? 0 : 1;
            int firstVariant = fullSearch ? 0 : Math.Max(0, cachedVariant!.Value - blurNeighborhood);
            int lastVariant = fullSearch ? Blurs.Length - 1 : Math.Min(Blurs.Length - 1, cachedVariant!.Value + blurNeighborhood);
            int firstDx = fullSearch ? -8 : Math.Max(-8, cachedDx - neighborhood);
            int lastDx = fullSearch ? 8 : Math.Min(8, cachedDx + neighborhood);
            int firstDy = fullSearch ? -8 : Math.Max(-8, cachedDy - neighborhood);
            int lastDy = fullSearch ? 8 : Math.Min(8, cachedDy + neighborhood);
            for (int variant = firstVariant; variant <= lastVariant; variant++)
            for (int dy = firstDy; dy <= lastDy; dy++)
            for (int dx = firstDx; dx <= lastDx; dx++)
                Evaluate(variant, dx, dy, fullSearch ? 1 : cachedScaleX, fullSearch ? 1 : cachedScaleY);
            if (template.ExposureHighPass is { } cachedExposure)
            {
                for (int dy = firstDy; dy <= lastDy; dy++)
                for (int dx = firstDx; dx <= lastDx; dx++)
                    Evaluate(template.ExposureVariant, dx, dy, fullSearch ? 1 : cachedScaleX,
                        fullSearch ? 1 : cachedScaleY, cachedExposure,
                        template.ExposureGain, template.ExposureBackground);
            }
            // Auto-exposure can clip bright glyphs after the optical blur, merging
            // strokes that a linear high-pass gain cannot explain. Forward-model
            // only that bounded camera effect; covered/missing letters still have
            // to match their generated shape, with the same registration.
            bool clipped = template.Evidence.Count(index => current[index] >= 235) >= template.Evidence.Length * .10;
            // Exposure can drift gradually while each adjacent frame differs
            // by less than the registration-change threshold. A stale exposure
            // model must still be refreshed when intact clipped letters stop
            // matching, without resetting their verified optical geometry.
            if (best < Math.Max(.82, template.CleanCorrelation * .90) && bestStd >= 3 && clipped)
            {
                int originalDx = bestDx, originalDy = bestDy;
                bool registered = template.RegisteredVariant is not null;
                if (registered) { originalDx = template.RegisteredDx; originalDy = template.RegisteredDy; }
                int exposureFirstVariant = registered ? Math.Max(1, template.RegisteredVariant!.Value - 1) : 1;
                int exposureLastVariant = registered ? Math.Min(6, template.RegisteredVariant!.Value + 1) : 6;
                double exposureScaleX = registered ? template.RegisteredScaleX : 1;
                double exposureScaleY = registered ? template.RegisteredScaleY : 1;
                foreach (double exposureGain in new double[] { 2, 3, 4, 6, 8, 12 })
                foreach (double exposureBackground in new double[] { 80, 120, 160, 200 })
                for (int variant = exposureFirstVariant; variant <= exposureLastVariant; variant++)
                {
                    double[] exposed = HighPass(template.Blurred[variant].Select(value =>
                        Math.Clamp(exposureBackground + exposureGain * (value - template.Background), 0, 255)).ToArray(),
                        template.Width, template.Height);
                    for (int dy = registered ? originalDy : Math.Max(-8, originalDy - 3);
                        dy <= (registered ? originalDy : Math.Min(8, originalDy + 3)); dy++)
                    for (int dx = registered ? originalDx : Math.Max(-8, originalDx - 3);
                        dx <= (registered ? originalDx : Math.Min(8, originalDx + 3)); dx++)
                        Evaluate(variant, dx, dy, exposureScaleX, exposureScaleY, exposed, exposureGain, exposureBackground);
                }
            }
            if (fullSearch && template.RegisteredVariant is null && best < .82 && bestStd >= 3)
            {
                int originalDx = bestDx, originalDy = bestDy, originalVariant = bestVariant;
                // Local optical proportions differ slightly near a board edge.
                // Adapt only an unverified startup pattern, within strict bounds;
                // verified optics stay fixed when a hand distorts the letters.
                for (int xScale = -2; xScale <= 2; xScale++)
                for (int yScale = -2; yScale <= 2; yScale++)
                for (int variant = Math.Max(0, originalVariant - 1); variant <= Math.Min(Blurs.Length - 1, originalVariant + 1); variant++)
                for (int dy = Math.Max(-8, originalDy - 2); dy <= Math.Min(8, originalDy + 2); dy++)
                for (int dx = Math.Max(-8, originalDx - 2); dx <= Math.Min(8, originalDx + 2); dx++)
                    Evaluate(variant, dx, dy, 1 + xScale * .04, 1 + yScale * .05);
            }
            void Evaluate(int variant, int dx, int dy, double scaleX, double scaleY,
                double[]? exposed = null, double exposureGain = 1, double exposureBackground = 0)
            {
                double[] expectedPattern = exposed ?? template.HighPass[variant];
                double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
                foreach (int index in template.Evidence)
                {
                    double a = expectedPattern[index];
                    double b = scaleX == 1 && scaleY == 1 ? cameraHighPass[index + dy * template.Width + dx]
                        : AffineSample(cameraHighPass, template.Width, template.Height, index, dx, dy, scaleX, scaleY);
                    sumA += a; sumB += b; sumAA += a * a; sumBB += b * b; sumAB += a * b;
                }
                double n = template.Evidence.Length, meanA = sumA / n, meanB = sumB / n;
                double aa = sumAA - sumA * meanA, bb = sumBB - sumB * meanB, ab = sumAB - sumA * meanB;
                double correlation = aa > 1e-8 && bb > 1e-8 ? ab / Math.Sqrt(aa * bb) : 0;
                if (correlation <= best) return;
                best = correlation; bestVariant = variant; bestDx = dx; bestDy = dy;
                bestScaleX = scaleX; bestScaleY = scaleY;
                bestExpected = expectedPattern;
                bestExposureGain = exposureGain; bestExposureBackground = exposureBackground;
                bestMeanExpected = meanA; bestMeanCurrent = meanB;
                bestGain = aa > 1e-8 ? ab / aa : 0; bestStd = Math.Sqrt(Math.Max(0, bb / n));
            }
            if (fullSearch)
            {
                template.TentativeVariant = bestVariant;
                template.TentativeDx = bestDx; template.TentativeDy = bestDy;
                template.TentativeScaleX = bestScaleX; template.TentativeScaleY = bestScaleY;
            }
            if (bestExposureGain > 1)
            {
                template.ExposureGain = bestExposureGain;
                template.ExposureBackground = bestExposureBackground;
                template.ExposureHighPass = bestExpected;
                template.ExposureVariant = bestVariant;
            }
            bool verified = template.CleanCorrelation >= .82;
            bool clean = best >= (verified ? Math.Max(.80, template.CleanCorrelation * .90) : .82);
            bool stronglyCorrupted = best < (verified ? Math.Max(.78, template.CleanCorrelation * .86) : .78);
            // Only a close match to the generated glyphs can establish a clean
            // appearance. A covered label is never learned merely because a color
            // fit happened to explain its current pixels.
            var changed = new List<PixelPoint>();
            var changedIndices = new HashSet<int>();
            if (bestStd >= 3)
            {
                double threshold = Math.Max(2, bestStd * .45);
                foreach (int index in template.Evidence)
                {
                    double expected = bestGain * (bestExpected[index] - bestMeanExpected) + bestMeanCurrent;
                    double actual = bestScaleX == 1 && bestScaleY == 1 ? cameraHighPass[index + bestDy * template.Width + bestDx]
                        : AffineSample(cameraHighPass, template.Width, template.Height, index, bestDx, bestDy, bestScaleX, bestScaleY);
                    if (Math.Abs(actual - expected) <= threshold) continue;
                    changedIndices.Add(index);
                    changed.Add(new((template.Left + index % template.Width) / (double)LogicalSize,
                        (template.Top + index / template.Width) / (double)LogicalSize));
                }
            }
            else
            {
                // A regression gain of zero must not explain away vanished
                // letters. Count their known contrast support only when another
                // fresh readable label proves the camera has usable exposure.
                double[] contrast = template.HighPass[2];
                double mean = template.Evidence.Average(index => contrast[index]);
                double std = Math.Sqrt(template.Evidence.Average(index => Math.Pow(contrast[index] - mean, 2)));
                missingContrast[template.Region] = template.Evidence.Where(index =>
                    Math.Abs(contrast[index] - mean) > Math.Max(2, std * .45)).Select(index =>
                    new PixelPoint((template.Left + index % template.Width) / (double)LogicalSize,
                        (template.Top + index / template.Width) / (double)LogicalSize)).ToArray();
            }
            // A long caption can remain globally recognizable while two central
            // groups of letters disappear under four fingers. Detect severe
            // adjacent local losses at the same registered optics, not by letting
            // each sector independently move until it finds another letter.
            var sectors = new double[6];
            var severe = new bool[6];
            var sectorEvidence = new int[6][];
            int glyphLeft = template.Evidence.Min(index => index % template.Width);
            int glyphRight = template.Evidence.Max(index => index % template.Width) + 1;
            for (int sector = 0; sector < sectors.Length; sector++)
            {
                int start = glyphLeft + (glyphRight - glyphLeft) * sector / sectors.Length;
                int end = glyphLeft + (glyphRight - glyphLeft) * (sector + 1) / sectors.Length;
                int[] indices = template.Evidence.Where(index => index % template.Width >= start && index % template.Width < end).ToArray();
                sectorEvidence[sector] = indices;
                if (indices.Length < 24) { sectors[sector] = 1; continue; }
                double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
                foreach (int index in indices)
                {
                    double a = bestExpected[index];
                    double b = bestScaleX == 1 && bestScaleY == 1 ? cameraHighPass[index + bestDy * template.Width + bestDx]
                        : AffineSample(cameraHighPass, template.Width, template.Height, index, bestDx, bestDy, bestScaleX, bestScaleY);
                    sumA += a; sumB += b; sumAA += a * a; sumBB += b * b; sumAB += a * b;
                }
                double aa = sumAA - sumA * sumA / indices.Length;
                double bb = sumBB - sumB * sumB / indices.Length;
                double ab = sumAB - sumA * sumB / indices.Length;
                if (aa / indices.Length < 9) { sectors[sector] = 1; continue; }
                sectors[sector] = bb > 1e-8 ? ab / Math.Sqrt(aa * bb) : 0;
                severe[sector] = sectors[sector] < .40;
            }
            var locallyChanged = new HashSet<int>();
            for (int sector = 0; sector < sectors.Length - 1; sector++)
                if (severe[sector] && severe[sector + 1])
                    foreach (int index in sectorEvidence[sector].Concat(sectorEvidence[sector + 1]))
                        if (changedIndices.Contains(index)) locallyChanged.Add(index);
            double localCoverage = locallyChanged.Count / 1_000_000.0 /
                (_scene.BoardTriggerRegions![template.Region].Width * _scene.BoardTriggerRegions[template.Region].Height);
            if (localCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage)
            {
                clean = false;
                stronglyCorrupted = true;
            }
            if (best >= .82 && clean)
            {
                template.CleanCorrelation = Math.Max(template.CleanCorrelation, best);
                // Keep registration tied to intact generated letters. Covered
                // sectors cannot establish a verified expected appearance.
                template.RegisteredVariant = bestVariant;
                template.RegisteredDx = bestDx; template.RegisteredDy = bestDy;
                template.RegisteredScaleX = bestScaleX; template.RegisteredScaleY = bestScaleY;
            }
            observations.Add(new(template.Region, clean, stronglyCorrupted, best, changed,
                Blurs[bestVariant], bestDx, bestDy, bestScaleX, bestScaleY, sectors, localCoverage,
                bestExposureGain, bestExposureBackground));
        }
        for (int index = 0; index < observations.Count; index++)
            if (missingContrast.TryGetValue(observations[index].Region, out var missing) &&
                observations.Any(other => other.Region != observations[index].Region && other.Clean && other.Correlation >= .82))
                observations[index] = observations[index] with { ChangedBoardPixels = missing };
        return observations;
    }

    private static double AffineSample(double[] pixels, int width, int height, int index,
        int dx, int dy, double scaleX, double scaleY)
    {
        double x = (index % width - width / 2.0) * scaleX + width / 2.0 + dx;
        double y = (index / width - height / 2.0) * scaleY + height / 2.0 + dy;
        x = Math.Clamp(x, 0, width - 1.001); y = Math.Clamp(y, 0, height - 1.001);
        int ix = (int)x, iy = (int)y;
        double fx = x - ix, fy = y - iy;
        return pixels[iy * width + ix] * (1 - fx) * (1 - fy) + pixels[iy * width + ix + 1] * fx * (1 - fy) +
            pixels[(iy + 1) * width + ix] * (1 - fx) * fy + pixels[(iy + 1) * width + ix + 1] * fx * fy;
    }

    private double SceneLuminance(int x, int y)
    {
        x = Math.Clamp(x * _scene.Width / LogicalSize, 0, _scene.Width - 1);
        y = Math.Clamp(y * _scene.Height / LogicalSize, 0, _scene.Height - 1);
        return PixelLuminance(_scene.Bgra, (y * _scene.Width + x) * 4);
    }
    internal PixelPoint CameraPoint(double boardX, double boardY)
    {
        double u = boardX / LogicalSize, v = boardY / LogicalSize;
        double divisor = _inverse[6] * u + _inverse[7] * v + _inverse[8];
        return new((_inverse[0] * u + _inverse[1] * v + _inverse[2]) / divisor,
            (_inverse[3] * u + _inverse[4] * v + _inverse[5]) / divisor);
    }
    private static double[] HighPass(double[] image, int width, int height)
    {
        double[] low = Blur(image, width, height, 3);
        return Enumerable.Range(0, image.Length).Select(index => image[index] - low[index]).ToArray();
    }
    private static double[] Blur(double[] image, int width, int height, double sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        double[] kernel = Enumerable.Range(-radius, radius * 2 + 1).Select(x => Math.Exp(-x * x / (2 * sigma * sigma))).ToArray();
        double total = kernel.Sum();
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;
        double[] horizontal = new double[image.Length], result = new double[image.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            for (int k = -radius; k <= radius; k++)
                horizontal[y * width + x] += image[y * width + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            for (int k = -radius; k <= radius; k++)
                result[y * width + x] += horizontal[Math.Clamp(y + k, 0, height - 1) * width + x] * kernel[k + radius];
        return result;
    }
    private static double[] Inverse(IReadOnlyList<double> m)
    {
        double[] c = [m[4]*m[8]-m[5]*m[7], m[2]*m[7]-m[1]*m[8], m[1]*m[5]-m[2]*m[4],
            m[5]*m[6]-m[3]*m[8], m[0]*m[8]-m[2]*m[6], m[2]*m[3]-m[0]*m[5],
            m[3]*m[7]-m[4]*m[6], m[1]*m[6]-m[0]*m[7], m[0]*m[4]-m[1]*m[3]];
        double determinant = m[0] * c[0] + m[1] * c[3] + m[2] * c[6];
        return Math.Abs(determinant) < 1e-12 ? [] : c.Select(value => value / determinant).ToArray();
    }
    private static bool Within(HandTrackingBounds b, double u, double v) =>
        u >= b.X && u <= b.X + b.Width && v >= b.Y && v <= b.Y + b.Height;
    private static double PixelLuminance(byte[] bgra, int offset) =>
        bgra[offset] * .114 + bgra[offset + 1] * .587 + bgra[offset + 2] * .299;
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
