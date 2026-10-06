using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using OpenCvSharp.ML;

namespace ProjectTabletop.Vision;

/// <summary>
/// Local, supervised tabletop vision baseline. A caller labels each piece's outline and
/// front point in captured camera frames. Training learns shape and bright-pattern
/// descriptors; inference finds candidate contours and identifies/fits multiple pieces.
/// Field reliability depends on the optical contrast in the actual projection setup.
/// </summary>
public sealed partial class VisionEngine : IDisposable
{
    private const string ProfileFileName = "vision-profile.json";
    private const string SvmFileName = "vision-svm.yml";
    private readonly object _gate = new();
    private readonly List<StoredCapture> _captures = [];
    private readonly List<TrainedCapture> _trained = [];
    private SVM? _svm;
    private Mat? _backgroundGray;
    private byte[]? _backgroundPng;
    private double[]? _featureMean;
    private double[]? _featureDeviation;
    private string[] _classIds = [];
    private bool _disposed;

    // Tracks actual classifier fits, including recovery of a missing or stale
    // cache. Profile-load regressions use this to catch redundant startup work.
    internal int ClassifierTrainingCount { get; private set; }

    public VisionEngine(VisionSettings? settings = null) => Settings = settings ?? new VisionSettings();

    public VisionSettings Settings { get; }
    public IReadOnlyList<string> PieceIds
    {
        get { lock (_gate) return _captures.Select(c => c.PieceId)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }
    public IReadOnlyList<CaptureInfo> Captures
    {
        get { lock (_gate) return _captures
            .Select(c => new CaptureInfo(c.CaptureId, c.PieceId, c.Width, c.Height)).ToArray(); }
    }
    public bool IsTrained
    {
        get { lock (_gate) return _trained.Count > 0; }
    }
    public bool HasEmptyBoardReference
    {
        get { lock (_gate) return _backgroundGray is not null; }
    }

    /// <summary>
    /// Add one labeled piece from a camera frame. Call once for each visible piece when
    /// a frame contains several pieces. The outline and front point use camera pixels.
    /// </summary>
    public CaptureInfo AddLabeledCapture(string pieceId, int width, int height, int stride,
        byte[] bgra, IReadOnlyList<PixelPoint> outline, PixelPoint front)
    {
        lock (_gate) return AddLabeledCaptureCore(pieceId, width, height, stride, bgra, outline, front);
    }

    private CaptureInfo AddLabeledCaptureCore(string pieceId, int width, int height, int stride,
        byte[] bgra, IReadOnlyList<PixelPoint> outline, PixelPoint front)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(pieceId))
            throw new ArgumentException("A piece needs a stable nonempty ID.", nameof(pieceId));
        if (outline is null || outline.Count < 3)
            throw new ArgumentException("Mark at least three outline points.", nameof(outline));
        using Mat image = FrameToMat(width, height, stride, bgra);
        using Mat gray = ToGray(image);
        VisionFeatures features = VisionFeatures.Extract(gray, outline, Settings.PatternBrightnessThreshold);
        double frontDistance = Math.Sqrt(Math.Pow(front.X - features.Center.X, 2) +
            Math.Pow(front.Y - features.Center.Y, 2));
        if (frontDistance < 2)
            throw new ArgumentException("The front anchor must be away from the piece center.", nameof(front));
        if (!Cv2.ImEncode(".png", image, out byte[] png))
            throw new InvalidOperationException("Could not encode the labeled camera frame.");

        // The app's media assignments are case-insensitive. Preserve the first spelling
        // so a later "boat" sample joins an existing "Boat" class.
        string cleanedId = pieceId.Trim();
        string canonicalId = _captures.Select(capture => capture.PieceId)
            .FirstOrDefault(existing => string.Equals(existing, cleanedId,
                StringComparison.OrdinalIgnoreCase)) ?? cleanedId;

        var capture = new StoredCapture
        {
            CaptureId = Guid.NewGuid().ToString("N"),
            PieceId = canonicalId,
            Width = width,
            Height = height,
            PngBase64 = Convert.ToBase64String(png),
            Outline = outline.ToArray(),
            Front = front
        };
        _captures.Add(capture);
        InvalidateTraining();
        return new CaptureInfo(capture.CaptureId, capture.PieceId, width, height);
    }

    /// <summary>Capture an empty-board frame for BackgroundDifference mode.</summary>
    public void SetEmptyBoardReference(int width, int height, int stride, byte[] bgra)
    {
        lock (_gate) SetEmptyBoardReferenceCore(width, height, stride, bgra);
    }

    private void SetEmptyBoardReferenceCore(int width, int height, int stride, byte[] bgra)
    {
        ThrowIfDisposed();
        using Mat image = FrameToMat(width, height, stride, bgra);
        if (!Cv2.ImEncode(".png", image, out byte[] png))
            throw new InvalidOperationException("Could not encode the background reference.");
        _backgroundPng = png;
        _backgroundGray?.Dispose();
        _backgroundGray = ToGray(image);
    }

    public bool RemoveCapture(string captureId)
    {
        lock (_gate) return RemoveCaptureCore(captureId);
    }

    private bool RemoveCaptureCore(string captureId)
    {
        ThrowIfDisposed();
        int removed = _captures.RemoveAll(c => c.CaptureId == captureId);
        if (removed > 0) InvalidateTraining();
        return removed > 0;
    }

    /// <summary>
    /// Train an OpenCV multiclass SVM on rotation-invariant descriptors and retain
    /// labeled exemplars for explicit unknown rejection and pose fitting.
    /// </summary>
    public TrainingReport Train()
    {
        lock (_gate) return TrainCore();
    }

    private TrainingReport TrainCore()
    {
        RebuildTrainedCaptures();
        TrainClassifier();
        return CreateTrainingReport();
    }

    private void RebuildTrainedCaptures()
    {
        ThrowIfDisposed();
        if (_captures.Count == 0)
            throw new InvalidOperationException("Add at least one labeled capture before training.");
        InvalidateTraining();

        foreach (StoredCapture capture in _captures)
        {
            using Mat image = Cv2.ImDecode(Convert.FromBase64String(capture.PngBase64), ImreadModes.Color);
            using Mat gray = ToGray(image);
            VisionFeatures features = VisionFeatures.Extract(gray, capture.Outline,
                Settings.PatternBrightnessThreshold);
            double frontAngle = AngleDegrees(features.Center, capture.Front);
            _trained.Add(new TrainedCapture(capture, features, frontAngle));
        }

        _classIds = PieceIds.ToArray();
        int dimensions = _trained[0].Features.Vector.Length;
        _featureMean = new double[dimensions];
        _featureDeviation = new double[dimensions];
        for (int j = 0; j < dimensions; j++)
        {
            _featureMean[j] = _trained.Average(t => t.Features.Vector[j]);
            double variance = _trained.Average(t => Math.Pow(t.Features.Vector[j] - _featureMean[j], 2));
            _featureDeviation[j] = Math.Max(Math.Sqrt(variance), 0.05);
        }
    }

    private void TrainClassifier()
    {
        if (_classIds.Length >= 2)
        {
            int dimensions = _trained[0].Features.Vector.Length;
            using var samples = new Mat(_trained.Count, dimensions, MatType.CV_32FC1);
            using var labels = new Mat(_trained.Count, 1, MatType.CV_32SC1);
            for (int row = 0; row < _trained.Count; row++)
            {
                float[] normalized = Normalize(_trained[row].Features.Vector);
                for (int col = 0; col < dimensions; col++)
                    samples.Set(row, col, normalized[col]);
                labels.Set(row, 0, Array.IndexOf(_classIds, _trained[row].Capture.PieceId));
            }
            var classifier = SVM.Create();
            try
            {
                classifier.Type = SVM.Types.CSvc;
                classifier.KernelType = SVM.KernelTypes.Rbf;
                classifier.C = 2.0;
                classifier.Gamma = 1.0 / dimensions;
                ClassifierTrainingCount++;
                if (!classifier.Train(samples, SampleTypes.RowSample, labels))
                    throw new InvalidOperationException("OpenCV could not train the piece classifier.");
                _svm = classifier;
            }
            catch { classifier.Dispose(); throw; }
        }
    }

    private TrainingReport CreateTrainingReport()
    {
        double? leaveOneOut = null;
        if (_classIds.Length >= 2 && _classIds.All(id => _trained.Count(t => t.Capture.PieceId == id) >= 2))
        {
            int correct = 0;
            for (int i = 0; i < _trained.Count; i++)
            {
                (string? nearest, _, _) = NearestClass(_trained[i].Features.Vector, excludeIndex: i);
                if (nearest == _trained[i].Capture.PieceId) correct++;
            }
            leaveOneOut = correct / (double)_trained.Count;
        }
        return new TrainingReport(_classIds.Length, _trained.Count, leaveOneOut,
            _classIds.ToDictionary(id => id, id => _trained.Count(t => t.Capture.PieceId == id)));
    }

    /// <summary>Detect all enrolled, confidently recognized pieces in one camera frame.</summary>
    public IReadOnlyList<PieceDetection> Detect(int width, int height, int stride, byte[] bgra)
    {
        lock (_gate) return DetectCore(width, height, stride, bgra);
    }

    private IReadOnlyList<PieceDetection> DetectCore(int width, int height, int stride, byte[] bgra)
    {
        ThrowIfDisposed();
        if (!IsTrained) return [];
        using Mat image = FrameToMat(width, height, stride, bgra);
        using Mat gray = ToGray(image);
        using Mat mask = Segment(gray);
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var recognized = new Dictionary<string, PieceDetection>(StringComparer.Ordinal);
        foreach (Point[] contour in contours)
        {
            double area = Math.Abs(Cv2.ContourArea(contour));
            if (area < Settings.MinimumAreaPixels || area > width * (double)height * Settings.MaximumAreaFraction)
                continue;
            Point[] simplified = Cv2.ApproxPolyDP(contour, Math.Max(1.0, Cv2.ArcLength(contour, true) * 0.004), true);
            if (simplified.Length < 3) continue;
            PixelPoint[] observedOutline = simplified.Select(p => new PixelPoint(p.X, p.Y)).ToArray();
            VisionFeatures observed;
            try { observed = VisionFeatures.Extract(gray, observedOutline, Settings.PatternBrightnessThreshold); }
            catch (ArgumentException) { continue; }

            (string? nearestId, double nearestDistance, double secondDistance) = NearestClass(observed.Vector);
            if (nearestId is null || nearestDistance > Settings.UnknownDistanceThreshold ||
                secondDistance - nearestDistance < Settings.MinimumClassMargin)
                continue;

            string classId = nearestId;
            // The SVM resolves close prototype ties; a large disagreement is treated as
            // unknown rather than projecting a possibly wrong object's media.
            if (_svm is not null)
            {
                int svmLabel = PredictSvm(observed.Vector);
                if (svmLabel < 0 || svmLabel >= _classIds.Length) continue;
                string svmId = _classIds[svmLabel];
                if (svmId != nearestId)
                {
                    if (secondDistance - nearestDistance > 1.0) continue;
                    classId = svmId;
                }
            }

            TrainedCapture? bestTemplate = null;
            double bestPoseError = double.PositiveInfinity, bestAngle = 0;
            foreach (TrainedCapture template in _trained.Where(t => t.Capture.PieceId == classId))
            {
                double error = VisionFeatures.BestPoseError(observed, template.Features,
                    template.FrontAngleDegrees, Settings.PatternPoseWeight, out double angle);
                if (error < bestPoseError)
                {
                    bestPoseError = error;
                    bestAngle = angle;
                    bestTemplate = template;
                }
            }
            if (bestTemplate is null || bestPoseError > Settings.MaximumPoseError) continue;
            PixelPoint[] fittedOutline = VisionFeatures.FitOutline(bestTemplate.Capture.Outline,
                bestTemplate.Features, bestTemplate.FrontAngleDegrees, observed, bestAngle);
            double confidence = Math.Clamp(1 - nearestDistance / Math.Max(0.01, Settings.UnknownDistanceThreshold), 0, 1)
                * Math.Clamp(1 - bestPoseError / Math.Max(0.01, Settings.MaximumPoseError), 0, 1);
            var detection = new PieceDetection(classId, observed.Center, bestAngle,
                fittedOutline, observedOutline, confidence, area);
            if (!recognized.TryGetValue(classId, out PieceDetection? prior) || prior.Confidence < confidence)
                recognized[classId] = detection;
        }
        return recognized.Values.OrderBy(d => d.PieceId, StringComparer.Ordinal).ToArray();
    }

    public void Save(string directory)
    {
        lock (_gate) SaveCore(directory);
    }

    private void SaveCore(string directory)
    {
        ThrowIfDisposed();
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "captures"));
        foreach (StoredCapture capture in _captures)
        {
            capture.PngFileName = "captures/" + capture.CaptureId + ".png";
            File.WriteAllBytes(Path.Combine(directory, "captures", capture.CaptureId + ".png"),
                Convert.FromBase64String(capture.PngBase64));
        }
        var state = new StoredState
        {
            FormatVersion = 1,
            Settings = Settings,
            Captures = _captures,
            BackgroundPngBase64 = _backgroundPng is null ? null : Convert.ToBase64String(_backgroundPng),
            TrainedClassIds = IsTrained ? _classIds : null,
            FeatureMean = IsTrained ? _featureMean : null,
            FeatureDeviation = IsTrained ? _featureDeviation : null
        };
        File.WriteAllText(Path.Combine(directory, ProfileFileName), JsonSerializer.Serialize(state,
            new JsonSerializerOptions { WriteIndented = true }));
        string svmPath = Path.Combine(directory, SvmFileName);
        if (_svm is not null) _svm.Save(svmPath);
        else if (File.Exists(svmPath)) File.Delete(svmPath);
    }

    public static VisionEngine Load(string directory)
    {
        string json = File.ReadAllText(Path.Combine(directory, ProfileFileName));
        StoredState state = JsonSerializer.Deserialize<StoredState>(json)
            ?? throw new InvalidDataException("Vision profile is empty.");
        if (state.FormatVersion != 1)
            throw new InvalidDataException($"Unsupported vision profile version {state.FormatVersion}.");
        if (state.Captures.GroupBy(capture => capture.PieceId, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(capture => capture.PieceId).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            throw new InvalidDataException("This profile contains piece IDs that differ only by letter case. Rename those IDs before loading it.");
        var engine = new VisionEngine(state.Settings);
        try
        {
            foreach (StoredCapture capture in state.Captures)
            {
                if (capture.PngFileName != "captures/" + capture.CaptureId + ".png" ||
                    !Guid.TryParseExact(capture.CaptureId, "N", out _))
                    throw new InvalidDataException("Invalid capture filename in vision profile.");
                capture.PngBase64 = Convert.ToBase64String(File.ReadAllBytes(
                    Path.Combine(directory, "captures", capture.CaptureId + ".png")));
            }
            engine._captures.AddRange(state.Captures);
            if (state.BackgroundPngBase64 is not null)
            {
                engine._backgroundPng = Convert.FromBase64String(state.BackgroundPngBase64);
                using Mat image = Cv2.ImDecode(engine._backgroundPng, ImreadModes.Color);
                engine._backgroundGray = ToGray(image);
            }
            if (state.TrainedClassIds is not null)
            {
                // Pose fitting still needs the saved exemplars. Reconstruct those and
                // their normalization without fitting a classifier or evaluating a
                // training report that the caller never requests.
                engine.RebuildTrainedCaptures();
                if (engine._classIds.Length >= 2 && !engine.TryLoadClassifier(directory, state))
                    engine.TrainClassifier();
            }
            return engine;
        }
        catch { engine.Dispose(); throw; }
    }

    private bool TryLoadClassifier(string directory, StoredState state)
    {
        // An SVM's numeric labels depend on class order, and its inputs depend
        // on the normalization used during fitting. Never combine a saved
        // classifier with different reconstructed descriptors or class labels.
        if (!state.TrainedClassIds!.SequenceEqual(_classIds, StringComparer.Ordinal) ||
            !MatchingNormalization(state.FeatureMean, _featureMean!) ||
            !MatchingNormalization(state.FeatureDeviation, _featureDeviation!)) return false;
        string path = Path.Combine(directory, SvmFileName);
        if (!File.Exists(path)) return false;
        var classifier = SVM.Load(path);
        try
        {
            if (!classifier.IsTrained() || classifier.Type != SVM.Types.CSvc ||
                classifier.KernelType != SVM.KernelTypes.Rbf ||
                classifier.GetVarCount() != _featureMean!.Length)
            {
                classifier.Dispose();
                return false;
            }
            _svm = classifier;
            return true;
        }
        catch { classifier.Dispose(); throw; }
    }

    private static bool MatchingNormalization(double[]? saved, double[] current)
    {
        if (saved is null || saved.Length != current.Length) return false;
        for (int index = 0; index < current.Length; index++)
            if (!double.IsFinite(saved[index]) ||
                Math.Abs(saved[index] - current[index]) > 1e-12 * Math.Max(1, Math.Abs(current[index])))
                return false;
        return true;
    }

    public void Dispose()
    {
        lock (_gate) DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _svm?.Dispose();
        _backgroundGray?.Dispose();
        _disposed = true;
    }

    private Mat Segment(Mat gray)
    {
        var mask = new Mat();
        switch (Settings.SegmentationMode)
        {
            case SegmentationMode.Brightness:
                Cv2.Threshold(gray, mask, Settings.BrightnessThreshold, 255, ThresholdTypes.Binary);
                break;
            case SegmentationMode.OtsuBright:
                Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                break;
            case SegmentationMode.BackgroundDifference:
                if (_backgroundGray is null)
                    throw new InvalidOperationException("Capture an empty-board reference before using background difference.");
                if (_backgroundGray.Size() != gray.Size())
                    throw new InvalidOperationException("The camera resolution differs from the empty-board reference.");
                using (var difference = new Mat())
                {
                    Cv2.Absdiff(gray, _backgroundGray, difference);
                    Cv2.Threshold(difference, mask, Settings.DifferenceThreshold, 255, ThresholdTypes.Binary);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Settings.SegmentationMode));
        }
        int kernelSize = Math.Clamp(Settings.MorphologyKernelSize, 1, 21);
        if (kernelSize > 1)
        {
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse,
                new Size(kernelSize | 1, kernelSize | 1));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
        }
        if (Settings.RegionOfInterest is PixelRect roi)
        {
            var bounded = new Rect(Math.Clamp(roi.X, 0, gray.Width), Math.Clamp(roi.Y, 0, gray.Height),
                Math.Clamp(roi.Width, 0, gray.Width - Math.Clamp(roi.X, 0, gray.Width)),
                Math.Clamp(roi.Height, 0, gray.Height - Math.Clamp(roi.Y, 0, gray.Height)));
            using var regionMask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.Black);
            Cv2.Rectangle(regionMask, bounded, Scalar.White, -1);
            Cv2.BitwiseAnd(mask, regionMask, mask);
        }
        return mask;
    }

    private (string? Id, double Distance, double SecondDistance) NearestClass(double[] vector, int excludeIndex = -1)
    {
        var distances = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < _trained.Count; i++)
        {
            if (i == excludeIndex) continue;
            TrainedCapture item = _trained[i];
            double distance = Distance(vector, item.Features.Vector);
            if (!distances.TryGetValue(item.Capture.PieceId, out double prior) || distance < prior)
                distances[item.Capture.PieceId] = distance;
        }
        var ranked = distances.OrderBy(pair => pair.Value).ToArray();
        return ranked.Length == 0 ? (null, double.PositiveInfinity, double.PositiveInfinity)
            : (ranked[0].Key, ranked[0].Value, ranked.Length > 1 ? ranked[1].Value : double.PositiveInfinity);
    }

    private double Distance(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double difference = (a[i] - b[i]) / _featureDeviation![i];
            sum += difference * difference;
        }
        return Math.Sqrt(sum);
    }

    private float[] Normalize(double[] vector)
    {
        var result = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++)
            result[i] = (float)((vector[i] - _featureMean![i]) / _featureDeviation![i]);
        return result;
    }

    private int PredictSvm(double[] vector)
    {
        float[] normalized = Normalize(vector);
        using var sample = new Mat(1, normalized.Length, MatType.CV_32FC1);
        for (int i = 0; i < normalized.Length; i++) sample.Set(0, i, normalized[i]);
        return (int)Math.Round(_svm!.Predict(sample));
    }

    private void InvalidateTraining()
    {
        _svm?.Dispose();
        _svm = null;
        _trained.Clear();
        _classIds = [];
        _featureMean = null;
        _featureDeviation = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VisionEngine));
    }

    private static double AngleDegrees(PixelPoint center, PixelPoint front) =>
        (Math.Atan2(front.Y - center.Y, front.X - center.X) * 180.0 / Math.PI + 360.0) % 360.0;

    private static Mat FrameToMat(int width, int height, int stride, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
            bgra.Length < (long)(height - 1) * stride + width * 4)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        var image = new Mat(height, width, MatType.CV_8UC4);
        int rowBytes = checked(width * 4);
        for (int row = 0; row < height; row++)
            Marshal.Copy(bgra, row * stride, IntPtr.Add(image.Data, row * rowBytes), rowBytes);
        return image;
    }

    private static Mat ToGray(Mat image)
    {
        var gray = new Mat();
        Cv2.CvtColor(image, gray, image.Channels() == 4
            ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private sealed record TrainedCapture(StoredCapture Capture, VisionFeatures Features, double FrontAngleDegrees);

    private sealed class StoredCapture
    {
        public string CaptureId { get; set; } = "";
        public string PieceId { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public string PngFileName { get; set; } = "";
        [JsonIgnore]
        public string PngBase64 { get; set; } = "";
        public PixelPoint[] Outline { get; set; } = [];
        public PixelPoint Front { get; set; }
    }

    private sealed class StoredState
    {
        public int FormatVersion { get; set; }
        public VisionSettings Settings { get; set; } = new();
        public List<StoredCapture> Captures { get; set; } = [];
        public string? BackgroundPngBase64 { get; set; }
        public string[]? TrainedClassIds { get; set; }
        public double[]? FeatureMean { get; set; }
        public double[]? FeatureDeviation { get; set; }
    }
}
