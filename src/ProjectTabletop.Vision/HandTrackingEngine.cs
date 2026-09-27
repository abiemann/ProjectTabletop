// Adapted from OpenCV Zoo's mp_palmdet.py and mp_handpose.py (Apache-2.0):
// https://github.com/opencv/opencv_zoo/tree/main/models/palm_detection_mediapipe
// https://github.com/opencv/opencv_zoo/tree/main/models/handpose_estimation_mediapipe
// See the accompanying model licenses. This C# port uses the original RGB/NHWC
// input layout, anchor order, crop geometry and inverse landmark transform.
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace ProjectTabletop.Vision;

/// <summary>
/// Local CPU palm detection and 21-point hand pose estimation. Input is top-down
/// BGRA8 camera data; returned coordinates are in the original camera image.
/// Detection and disposal are synchronized. No camera, service or Python runtime
/// is needed by this class; both OpenCV Zoo ONNX model files must be local.
/// </summary>
public sealed class HandTrackingEngine : IDisposable
{
    private const int PalmSize = 192;
    private const int HandSize = 224;
    private const int AnchorCount = 2016;
    private const double PalmThreshold = 0.5;
    private const double HandThreshold = 0.8;
    private static readonly PixelPoint[] Anchors = CreateAnchors();
    private readonly object _gate = new();
    private readonly Net _palmNet;
    private readonly Net _handNet;
    private IReadOnlyList<HandDetection> _previousHands = [];
    private int _previousWidth, _previousHeight, _framesSincePalmSearch;
    private bool _captureDiagnostics;
    private HandTrackingDiagnostics? _lastDiagnostics;
    private bool _disposed;

    public bool CaptureDiagnostics
    {
        get { lock (_gate) return _captureDiagnostics; }
        set { lock (_gate) { _captureDiagnostics = value; if (!value) _lastDiagnostics = null; } }
    }

    public HandTrackingDiagnostics? LastDiagnostics
    {
        get { lock (_gate) return _lastDiagnostics; }
    }

    public HandTrackingEngine(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        _palmNet = LoadNetwork(Path.Combine(modelDirectory, "palm_detection_mediapipe_2023feb.onnx"));
        try
        {
            _handNet = LoadNetwork(Path.Combine(modelDirectory, "handpose_estimation_mediapipe_2023feb.onnx"));
        }
        catch
        {
            _palmNet.Dispose();
            throw;
        }
    }

    public IReadOnlyList<HandDetection> Detect(int width, int height, int stride, byte[] bgra)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lastDiagnostics = null;
            ArgumentNullException.ThrowIfNull(bgra);
            if (width is <= 0 or > 16384 || height is <= 0 or > 16384 ||
                stride < (long)width * 4 || bgra.Length < (long)(height - 1) * stride + width * 4L)
                throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");

            using Mat frame = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra, stride);
            using var rgb = new Mat();
            Cv2.CvtColor(frame, rgb, ColorConversionCodes.BGRA2RGB);
            var hands = new List<HandDetection>(2);
            var trackedPreviousBoundsIou = new Dictionary<HandDetection, double>(ReferenceEqualityComparer.Instance);
            var diagnostics = _captureDiagnostics ? new DetectionTrace(_previousHands.Count) : null;
            if (width == _previousWidth && height == _previousHeight)
            {
                // Once acquired, a hand's own palm landmarks provide a much
                // larger, steadier crop than locating its tiny palm afresh in
                // every 192-pixel full-frame scan. Still infer ALL landmarks
                // from this image: these are crop hints, never stale results.
                int previousIndex = 0;
                foreach (HandDetection previous in _previousHands)
                {
                    Palm palm = PalmFromHand(previous);
                    var attempt = diagnostics?.Begin("tracked-roi", palm, previousIndex++);
                    HandDetection? hand = DetectHand(rgb, palm, attempt);
                    if (hand is not null)
                    {
                        double overlap = IntersectionOverUnion(HandBounds(previous), HandBounds(hand));
                        if (attempt is not null) attempt.PreviousBoundsIou = double.IsFinite(overlap) ? overlap : null;
                        if (overlap >= 0.2)
                        {
                            hands.Add(hand);
                            trackedPreviousBoundsIou.Add(hand, overlap);
                        }
                        else if (attempt is not null) attempt.Result = "tracked-iou-rejected";
                    }
                }
            }

            // Periodically search for an arriving second hand. A lost track
            // triggers acquisition immediately, including on this very frame.
            bool search = hands.Count == 0 || hands.Count < _previousHands.Count ||
                ++_framesSincePalmSearch >= 6;
            if (diagnostics is not null)
            {
                diagnostics.FullSearch = search;
                diagnostics.FullSearchReason = !search ? "tracking-only" : hands.Count == 0
                    ? "no-tracked-hand" : hands.Count < _previousHands.Count ? "lost-tracked-hand" : "periodic";
            }
            if (search)
            {
                _framesSincePalmSearch = 0;
                FindHands(rgb, hands, diagnostics);
            }
            diagnostics?.RecordPreNms(hands);

            // Several views can fit the same hand. A reliable current-frame ROI
            // wins a near-tie over a search fit; clearly better fits still recover
            // tracking. Every selected pose was inferred from this camera frame.
            var selected = HandCandidateSelector.Select(hands, trackedPreviousBoundsIou,
                diagnostics is null ? null : diagnostics.RecordNms);
            _lastDiagnostics = diagnostics?.Finish(selected);
            _previousHands = selected;
            _previousWidth = width;
            _previousHeight = height;
            return selected;
        }
    }

    /// <summary>Forget crop hints after a camera change, interruption, or scene reset.</summary>
    public void ResetTracking()
    {
        lock (_gate)
        {
            _previousHands = [];
            _previousWidth = _previousHeight = _framesSincePalmSearch = 0;
            _lastDiagnostics = null;
        }
    }

    private static Palm PalmFromHand(HandDetection hand)
    {
        PixelPoint[] points = new[] { 0, 5, 9, 13, 17, 1, 2 }
            .Select(index => hand.Landmarks[index]).ToArray();
        double left = points.Min(point => point.X), top = points.Min(point => point.Y);
        return new Palm(new Rect2d(left, top, points.Max(point => point.X) - left,
            points.Max(point => point.Y) - top), points, hand.Confidence);
    }

    private void FindHands(Mat rgb, List<HandDetection> hands, DetectionTrace? diagnostics)
    {
        var fullProposals = DetectPalms(rgb);
        diagnostics?.Searches.Add(new("full-frame", new(0, 0, rgb.Width, rgb.Height), fullProposals.Count));
        foreach (Palm palm in fullProposals)
        {
            HandDetection? hand = DetectHand(rgb, palm, diagnostics?.Begin("full-frame", palm,
                searchView: new(0, 0, rgb.Width, rgb.Height)));
            if (hand is not null) hands.Add(hand);
        }

        // Letterboxing a wide webcam image into 192 pixels makes a tabletop
        // hand very small. Recheck overlapping square views when acquisition
        // fails or the observed hand is small relative to the whole image.
        // Refine on the original image so a tile boundary cannot cut off fingers.
        int side = Math.Min(rgb.Width, rgb.Height), longest = Math.Max(rgb.Width, rgb.Height);
        if (longest > side * 1.25 && (hands.Count == 0 ||
            hands.Any(hand => HandExtent(hand) < longest * 0.35)))
        {
            foreach (int offset in new[] { 0, (longest - side) / 2, longest - side }.Distinct())
            {
                int x = rgb.Width > rgb.Height ? offset : 0, y = rgb.Height > rgb.Width ? offset : 0;
                using var tile = new Mat(rgb, new Rect(x, y, side, side));
                var tileProposals = DetectPalms(tile);
                diagnostics?.Searches.Add(new("tile", new(x, y, side, side), tileProposals.Count));
                foreach (Palm local in tileProposals)
                {
                    var palm = new Palm(new Rect2d(local.Bounds.X + x, local.Bounds.Y + y,
                        local.Bounds.Width, local.Bounds.Height),
                        local.Landmarks.Select(point => new PixelPoint(point.X + x, point.Y + y)).ToArray(),
                        local.Score);
                    HandDetection? hand = DetectHand(rgb, palm, diagnostics?.Begin("tile", palm,
                        searchView: new(x, y, side, side)));
                    if (hand is not null) hands.Add(hand);
                }
            }
        }
    }

    private static Rect2d HandBounds(HandDetection hand)
    {
        double left = hand.Landmarks.Min(point => point.X), top = hand.Landmarks.Min(point => point.Y);
        return new Rect2d(left, top, hand.Landmarks.Max(point => point.X) - left,
            hand.Landmarks.Max(point => point.Y) - top);
    }

    private static double HandExtent(HandDetection hand)
    {
        Rect2d bounds = HandBounds(hand);
        return Math.Max(bounds.Width, bounds.Height);
    }

    private IReadOnlyList<Palm> DetectPalms(Mat rgb)
    {
        double ratio = PalmSize / (double)Math.Max(rgb.Width, rgb.Height);
        int resizedWidth = Math.Max(1, (int)(rgb.Width * ratio));
        int resizedHeight = Math.Max(1, (int)(rgb.Height * ratio));
        int left = (PalmSize - resizedWidth) / 2;
        int top = (PalmSize - resizedHeight) / 2;
        using var resized = new Mat();
        using var padded = new Mat();
        Cv2.Resize(rgb, resized, new Size(resizedWidth, resizedHeight));
        Cv2.CopyMakeBorder(resized, padded, top, PalmSize - resizedHeight - top,
            left, PalmSize - resizedWidth - left, BorderTypes.Constant, Scalar.Black);
        using Mat blob = ToNhwcBlob(padded);
        float[][] output = Forward(_palmNet, blob, [AnchorCount * 18, AnchorCount]);
        float[] boxes = output[0], scores = output[1];
        double scale = Math.Max(rgb.Width, rgb.Height);
        // The reference implementation quantizes its letterbox bias in camera pixels.
        int biasX = (int)(left / ratio), biasY = (int)(top / ratio);
        var candidates = new List<Palm>();
        for (int index = 0; index < AnchorCount; index++)
        {
            if (!float.IsFinite(scores[index])) continue;
            double score = 1 / (1 + Math.Exp(-scores[index]));
            if (score < PalmThreshold) continue;
            int offset = index * 18;
            if (!boxes.AsSpan(offset, 18).ToArray().All(float.IsFinite)) continue;
            double centerX = (boxes[offset] / PalmSize + Anchors[index].X) * scale - biasX;
            double centerY = (boxes[offset + 1] / PalmSize + Anchors[index].Y) * scale - biasY;
            double boxWidth = boxes[offset + 2] / PalmSize * scale;
            double boxHeight = boxes[offset + 3] / PalmSize * scale;
            if (boxWidth < 2 || boxHeight < 2 || boxWidth > scale * 2 || boxHeight > scale * 2)
                continue;
            var bounds = new Rect2d(centerX - boxWidth / 2, centerY - boxHeight / 2, boxWidth, boxHeight);
            if (bounds.Right <= 0 || bounds.Bottom <= 0 || bounds.Left >= rgb.Width || bounds.Top >= rgb.Height)
                continue;
            var landmarks = new PixelPoint[7];
            for (int point = 0; point < landmarks.Length; point++)
                landmarks[point] = new PixelPoint(
                    (boxes[offset + 4 + point * 2] / PalmSize + Anchors[index].X) * scale - biasX,
                    (boxes[offset + 5 + point * 2] / PalmSize + Anchors[index].Y) * scale - biasY);
            if (landmarks.Any(point => Math.Abs(point.X) > scale * 2 || Math.Abs(point.Y) > scale * 2))
                continue;
            candidates.Add(new Palm(bounds, landmarks, score));
        }

        // Use actual rectangle IoU: OpenCV's NMSBoxes takes x/y/width/height,
        // while the model decoder represents boxes by their opposite corners.
        var selected = new List<Palm>(2);
        foreach (Palm candidate in candidates.OrderByDescending(palm => palm.Score))
        {
            if (selected.Any(palm => IntersectionOverUnion(palm.Bounds, candidate.Bounds) > 0.3)) continue;
            selected.Add(candidate);
            if (selected.Count == 2) break;
        }
        return selected;
    }

    private HandDetection? DetectHand(Mat rgb, Palm palm, CandidateTrace? diagnostics = null)
    {
        if (diagnostics is not null) diagnostics.Result = "invalid-rotation-crop";
        var first = CropAndPad(rgb, palm.Bounds, forRotation: true);
        if (first is null) return null;
        using Mat padded = first.Value.Image;
        PixelPoint bias = first.Value.Bias;
        Rect firstBounds = first.Value.Bounds;
        PixelPoint[] palmPoints = palm.Landmarks.Select(point =>
            new PixelPoint(point.X - bias.X, point.Y - bias.Y)).ToArray();
        double dx = palmPoints[2].X - palmPoints[0].X;
        double dy = palmPoints[2].Y - palmPoints[0].Y;
        if (diagnostics is not null) diagnostics.Result = "degenerate-palm-direction";
        if (dx * dx + dy * dy < 1) return null;
        double radians = Math.PI / 2 - Math.Atan2(-dy, dx);
        radians -= 2 * Math.PI * Math.Floor((radians + Math.PI) / (2 * Math.PI));
        var center = new Point2f((float)(firstBounds.X + firstBounds.Width / 2.0 - bias.X),
            (float)(firstBounds.Y + firstBounds.Height / 2.0 - bias.Y));
        using Mat rotation = Cv2.GetRotationMatrix2D(center, radians * 180 / Math.PI, 1);
        using var rotated = new Mat();
        Cv2.WarpAffine(padded, rotated, rotation, padded.Size());
        PixelPoint[] rotatedPoints = palmPoints.Select(point => Transform(rotation, point)).ToArray();
        double minX = rotatedPoints.Min(point => point.X), minY = rotatedPoints.Min(point => point.Y);
        var rotatedBounds = new Rect2d(minX, minY, rotatedPoints.Max(point => point.X) - minX,
            rotatedPoints.Max(point => point.Y) - minY);
        if (diagnostics is not null) diagnostics.Result = "invalid-hand-crop";
        var second = CropAndPad(rotated, rotatedBounds, forRotation: false);
        if (second is null) return null;
        using Mat crop = second.Value.Image;
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(HandSize, HandSize), interpolation: InterpolationFlags.Area);
        using Mat blob = ToNhwcBlob(resized);
        float[][] output = Forward(_handNet, blob, [63, 1, 1, 63]);
        double confidence = output[1][0], handedness = output[2][0];
        if (diagnostics is not null)
        {
            diagnostics.HandConfidence = double.IsFinite(confidence) ? confidence : null;
            diagnostics.Result = !double.IsFinite(confidence) || confidence < HandThreshold || confidence > 1
                ? "hand-score-rejected" : "invalid-model-output";
        }
        if (!double.IsFinite(confidence) || confidence < HandThreshold || confidence > 1 ||
            !double.IsFinite(handedness) || handedness is < 0 or > 1 || !output[0].All(float.IsFinite))
            return null;

        Rect bounds = second.Value.Bounds;
        double scale = Math.Max(bounds.Width, bounds.Height) / (double)HandSize;
        using var inverseRotation = new Mat();
        Cv2.InvertAffineTransform(rotation, inverseRotation);
        var landmarks = new PixelPoint[21];
        if (diagnostics is not null) diagnostics.Result = "landmarks-outside-frame";
        for (int index = 0; index < landmarks.Length; index++)
        {
            // The reference unpads around the clipped crop's center, then applies
            // inverse rotation and the first crop bias to recover camera pixels.
            var point = new PixelPoint((output[0][index * 3] - HandSize / 2.0) * scale +
                    bounds.X + bounds.Width / 2.0,
                (output[0][index * 3 + 1] - HandSize / 2.0) * scale + bounds.Y + bounds.Height / 2.0);
            PixelPoint unrotated = Transform(inverseRotation, point);
            landmarks[index] = new PixelPoint(unrotated.X + bias.X, unrotated.Y + bias.Y);
            // Partially visible hands may place a wrist outside the image. Reject
            // implausible results without snapping landmarks onto the frame edge.
            if (!double.IsFinite(landmarks[index].X) || !double.IsFinite(landmarks[index].Y) ||
                landmarks[index].X < -rgb.Width * 0.25 || landmarks[index].X > rgb.Width * 1.25 ||
                landmarks[index].Y < -rgb.Height * 0.25 || landmarks[index].Y > rgb.Height * 1.25)
                return null;
        }
        PixelPoint tip = landmarks[8];
        if (diagnostics is not null) diagnostics.Result = "index-tip-outside-frame";
        if (tip.X < 0 || tip.Y < 0 || tip.X >= rgb.Width || tip.Y >= rgb.Height) return null;
        var result = new HandDetection(Array.AsReadOnly(landmarks), confidence, handedness);
        if (diagnostics is not null)
        {
            diagnostics.Hand = result;
            diagnostics.Result = "hand-accepted";
        }
        return result;
    }

    private static (Mat Image, Rect Bounds, PixelPoint Bias)? CropAndPad(Mat image, Rect2d palm,
        bool forRotation)
    {
        double centerX = palm.X + palm.Width / 2;
        double centerY = palm.Y + palm.Height / 2 - (forRotation ? 0 : 0.4 * palm.Height);
        double factor = forRotation ? 4 : 3;
        int left = Math.Clamp((int)(centerX - palm.Width * factor / 2), 0, image.Width);
        int top = Math.Clamp((int)(centerY - palm.Height * factor / 2), 0, image.Height);
        int right = Math.Clamp((int)(centerX + palm.Width * factor / 2), 0, image.Width);
        int bottom = Math.Clamp((int)(centerY + palm.Height * factor / 2), 0, image.Height);
        if (right - left < 2 || bottom - top < 2) return null;
        var bounds = new Rect(left, top, right - left, bottom - top);
        int side = forRotation ? (int)Math.Sqrt((double)bounds.Width * bounds.Width +
            (double)bounds.Height * bounds.Height) : Math.Max(bounds.Width, bounds.Height);
        int padLeft = (side - bounds.Width) / 2, padTop = (side - bounds.Height) / 2;
        using var crop = new Mat(image, bounds);
        var padded = new Mat();
        try
        {
            Cv2.CopyMakeBorder(crop, padded, padTop, side - bounds.Height - padTop,
                padLeft, side - bounds.Width - padLeft, BorderTypes.Constant | BorderTypes.Isolated, Scalar.Black);
            return (padded, bounds, new PixelPoint(left - padLeft, top - padTop));
        }
        catch
        {
            padded.Dispose();
            throw;
        }
    }

    private static Mat ToNhwcBlob(Mat rgb)
    {
        using var normalized = new Mat();
        rgb.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
        // Reshape preserves interleaved RGB. BlobFromImage defaults to NCHW,
        // which is the wrong memory layout for these particular ONNX models.
        return normalized.Reshape(1, [1, rgb.Height, rgb.Width, 3]);
    }

    private static float[][] Forward(Net net, Mat blob, int[] expectedCounts)
    {
        string[] names = net.GetUnconnectedOutLayersNames().Select(name => name ??
            throw new InvalidDataException("The hand model has an unnamed output layer.")).ToArray();
        if (names.Length != expectedCounts.Length)
            throw new InvalidDataException("The hand model has unexpected output layers.");
        Mat[] outputs = names.Select(_ => new Mat()).ToArray();
        try
        {
            net.SetInput(blob);
            net.Forward(outputs, names);
            var values = new float[outputs.Length][];
            for (int index = 0; index < outputs.Length; index++)
            {
                Mat output = outputs[index];
                if (output.Type() != MatType.CV_32FC1 || output.Total() != expectedCounts[index] ||
                    !output.IsContinuous())
                    throw new InvalidDataException("The hand model has an unexpected output tensor.");
                values[index] = new float[expectedCounts[index]];
                Marshal.Copy(output.Data, values[index], 0, values[index].Length);
            }
            return values;
        }
        finally
        {
            foreach (Mat output in outputs) output.Dispose();
        }
    }

    private static Net LoadNetwork(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The local hand tracking model is missing.", path);
        Net net = Net.ReadNetFromONNX(path) ??
            throw new InvalidDataException("The local hand tracking model could not be loaded.");
        try
        {
            net.SetPreferableBackend(Backend.OPENCV);
            net.SetPreferableTarget(Target.CPU);
            return net;
        }
        catch
        {
            net.Dispose();
            throw;
        }
    }

    private static PixelPoint[] CreateAnchors()
    {
        var anchors = new List<PixelPoint>(AnchorCount);
        // Stride 8 contributes 24*24*2 anchors; merged stride-16 layers add
        // 12*12*6. Every anchor has unit size in this fixed-anchor model.
        foreach (var (grid, repeats) in new[] { (24, 2), (12, 6) })
            for (int y = 0; y < grid; y++)
                for (int x = 0; x < grid; x++)
                    for (int repeat = 0; repeat < repeats; repeat++)
                        anchors.Add(new PixelPoint((x + 0.5) / grid, (y + 0.5) / grid));
        return anchors.ToArray();
    }

    private static PixelPoint Transform(Mat matrix, PixelPoint point) => new(
        matrix.At<double>(0, 0) * point.X + matrix.At<double>(0, 1) * point.Y + matrix.At<double>(0, 2),
        matrix.At<double>(1, 0) * point.X + matrix.At<double>(1, 1) * point.Y + matrix.At<double>(1, 2));

    private static double IntersectionOverUnion(Rect2d first, Rect2d second)
    {
        double width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        double height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        double intersection = width * height;
        return intersection / (first.Width * first.Height + second.Width * second.Height - intersection);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _palmNet.Dispose();
            _handNet.Dispose();
            _disposed = true;
        }
    }

    // Mutable trace builders live only within one Detect call. Public snapshots
    // copy their collections and expose no state used by subsequent inference.
    private sealed class DetectionTrace(int previousHandCount)
    {
        private readonly List<CandidateTrace> _candidates = [];
        public readonly List<HandTrackingSearchDiagnostics> Searches = [];
        public bool FullSearch;
        public string FullSearchReason = "tracking-only";

        public CandidateTrace Begin(string source, Palm palm, int? previousHandIndex = null,
            HandTrackingBounds? searchView = null)
        {
            var candidate = new CandidateTrace(_candidates.Count, source, palm, previousHandIndex, searchView);
            _candidates.Add(candidate);
            return candidate;
        }

        public void RecordPreNms(IReadOnlyList<HandDetection> hands)
        {
            for (int index = 0; index < hands.Count; index++)
            {
                var candidate = ForHand(hands[index]);
                candidate.PreNmsIndex = index;
                candidate.NmsResult = "selection-limit";
            }
        }

        public void RecordNms(HandDetection hand, string result, HandDetection? suppressor = null)
        {
            var candidate = ForHand(hand);
            candidate.NmsResult = result;
            candidate.SuppressedByCandidateIndex = suppressor is null ? null : ForHand(suppressor).Index;
        }

        public HandTrackingDiagnostics Finish(IReadOnlyList<HandDetection> selected) => new(
            FullSearch, FullSearchReason, previousHandCount,
            _candidates.Count(candidate => candidate.Source == "tracked-roi"),
            Array.AsReadOnly(Searches.ToArray()),
            Array.AsReadOnly(_candidates.Select(candidate => candidate.Snapshot()).ToArray()),
            Array.AsReadOnly(_candidates.Where(candidate => candidate.PreNmsIndex.HasValue)
                .OrderBy(candidate => candidate.PreNmsIndex).Select(candidate => candidate.Index).ToArray()),
            Array.AsReadOnly(selected.Select(hand => ForHand(hand).Index).ToArray()));

        private CandidateTrace ForHand(HandDetection hand) =>
            _candidates.First(candidate => ReferenceEquals(candidate.Hand, hand));
    }

    private sealed class CandidateTrace(int index, string source, Palm palm, int? previousHandIndex,
        HandTrackingBounds? searchView)
    {
        public int Index { get; } = index;
        public string Source { get; } = source;
        public double? HandConfidence;
        public string Result = "not-run";
        public double? PreviousBoundsIou;
        public HandDetection? Hand;
        public int? PreNmsIndex;
        public string NmsResult = "not-eligible";
        public int? SuppressedByCandidateIndex;

        public HandTrackingCandidateDiagnostics Snapshot() => new(Index, Source, searchView, previousHandIndex,
            new(palm.Bounds.X, palm.Bounds.Y, palm.Bounds.Width, palm.Bounds.Height), palm.Score,
            HandConfidence, Result, PreviousBoundsIou, Hand, PreNmsIndex, NmsResult, SuppressedByCandidateIndex);
    }

    private sealed record Palm(Rect2d Bounds, PixelPoint[] Landmarks, double Score);
}
