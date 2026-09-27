using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>
/// Photographs one contrasting foreground object on the white capture field.
/// This is color/background segmentation, not a trained object recognizer. The
/// shutter hand and its connected forearm are excluded before choosing an object.
/// </summary>
public static class PhotoObjectExtractor
{
    private const int BoardSize = PhotoHandCutout.BoardPixels;
    private static readonly Rect Capture = new(30, 230, 940, 740);
    private const int MinimumArea = 400;

    public static PhotoHandCutout? Extract(int width, int height, int stride, byte[] bgra,
        HandDetection shutter, IReadOnlyList<double> cameraToBoard, out string? failure,
        IReadOnlyList<HandDetection>? otherHands = null)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(shutter);
        ArgumentNullException.ThrowIfNull(cameraToBoard);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        failure = "The hand or board mapping is unavailable. Keep the pinching hand visible and try again.";
        if (!TryMatrix(cameraToBoard, out var h) || !TryMapHand(shutter, h, out var points)) return null;
        double palmScale = Math.Max(Distance(points[0], points[9]), Distance(points[5], points[17]));
        if (palmScale is < 20 or > 450) return null;

        using Mat camera = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra, stride);
        using Mat transform = new(3, 3, MatType.CV_64FC1);
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                transform.Set(row, column, h[row * 3 + column] * (row < 2 ? BoardSize : 1));
        using Mat board = new();
        Cv2.WarpPerspective(camera, board, transform, new Size(BoardSize, BoardSize),
            InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255, 255, 255, 255));
        using Mat cameraCoverage = new(height, width, MatType.CV_8UC1, Scalar.White);
        using Mat coverage = new();
        Cv2.WarpPerspective(cameraCoverage, coverage, transform, new Size(BoardSize, BoardSize),
            InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.Black);
        using (Mat captureCoverage = new(coverage, Capture))
            if (Cv2.CountNonZero(captureCoverage) < Capture.Width * Capture.Height * .995)
            {
                failure = "The camera cannot see the entire capture area. Rescan the board.";
                return null;
            }

        using Mat exclusion = new(BoardSize, BoardSize, MatType.CV_8UC1, Scalar.Black);
        if (!DrawShutterExclusion(exclusion, points, palmScale)) return null;
        byte[] photo = Bytes(board, 4), excluded = Bytes(exclusion, 1);
        if (!TryBackground(photo, excluded, out var background, out double threshold))
        {
            failure = "Leave more of the white capture area visible around the object.";
            return null;
        }

        byte[] maskBytes = new byte[BoardSize * BoardSize];
        for (int y = Capture.Top; y < Capture.Bottom; y++)
            for (int x = Capture.Left; x < Capture.Right; x++)
            {
                int index = y * BoardSize + x;
                double difference = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    double delta = photo[index * 4 + channel] - BackgroundAt(background[channel], x, y);
                    difference += delta * delta;
                }
                if (difference > 3 * threshold * threshold) maskBytes[index] = 255;
            }
        using Mat mask = Mat.FromPixelData(BoardSize, BoardSize, MatType.CV_8UC1, maskBytes);
        // Close single-pixel camera seams, retaining real openings and holes.
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        using Mat components = new();
        int count = Cv2.ConnectedComponents(mask, components, PixelConnectivity.Connectivity8, MatType.CV_32SC1);
        int[] ids = new int[BoardSize * BoardSize];
        Marshal.Copy(components.Data, ids, 0, ids.Length);
        int[] areas = new int[count], outsideHand = new int[count];
        bool[] touchesHand = new bool[count], touchesEdge = new bool[count];
        for (int y = Capture.Top; y < Capture.Bottom; y++)
            for (int x = Capture.Left; x < Capture.Right; x++)
            {
                int index = y * BoardSize + x, id = ids[index];
                if (id == 0) continue;
                areas[id]++;
                if (excluded[index] != 0) touchesHand[id] = true;
                else outsideHand[id]++;
                if (x <= Capture.Left + 2 || x >= Capture.Right - 3 ||
                    y <= Capture.Top + 2 || y >= Capture.Bottom - 3) touchesEdge[id] = true;
            }
        int[] candidates = Enumerable.Range(1, count - 1)
            .Where(id => areas[id] >= MinimumArea && !touchesHand[id]).ToArray();
        if (candidates.Length == 0)
        {
            bool joined = Enumerable.Range(1, count - 1).Any(id => touchesHand[id] &&
                outsideHand[id] > Math.Max(MinimumArea, areas[id] * .12));
            failure = joined ? "Keep the object separate from the pinching hand and forearm, then pinch again."
                : "No separate object found. Put one contrasting object in the white area and pinch beside it.";
            return null;
        }
        if (candidates.Length != 1)
        {
            failure = "More than one object is visible. Leave one object in the white area and try again.";
            return null;
        }
        int selected = candidates[0];

        // A recognized non-pinching hand keeps the established wrist cutoff and
        // middle-finger direction, even if its forearm reaches the board edge.
        foreach (HandDetection hand in otherHands ?? [])
        {
            if (!TryMapHand(hand, h, out var other)) continue;
            double otherScale = Math.Max(Distance(other[0], other[9]), Distance(other[5], other[17]));
            if (otherScale <= 0 || Distance(other[4], other[8]) / otherScale < .45) continue;
            int matches = new[] { 0, 5, 9, 13, 17, 12 }.Count(index => ComponentNear(ids, selected, other[index], 8));
            if (matches < 4) continue;
            var handCutout = PhotoHandExtractor.Extract(width, height, stride, bgra, hand, cameraToBoard);
            if (handCutout is not null) { failure = null; return handCutout; }
        }
        if (touchesEdge[selected])
        {
            failure = "Move the whole object inside the white capture area, away from its edges.";
            return null;
        }
        if (areas[selected] > Capture.Width * Capture.Height * .45)
        {
            failure = "The object covers too much of the white area. Move it inward or use a smaller object.";
            return null;
        }
        for (int index = 0; index < maskBytes.Length; index++) maskBytes[index] = ids[index] == selected ? (byte)255 : (byte)0;
        Marshal.Copy(maskBytes, 0, mask.Data, maskBytes.Length);
        using Mat feather = new();
        Cv2.GaussianBlur(mask, feather, new Size(3, 3), .65);
        byte[] alpha = Bytes(feather, 1);
        int minX = BoardSize, minY = BoardSize, maxX = 0, maxY = 0;
        for (int y = Capture.Top; y < Capture.Bottom; y++)
            for (int x = Capture.Left; x < Capture.Right; x++)
            {
                int index = y * BoardSize + x;
                if (alpha[index] < 3) alpha[index] = 0;
                if (alpha[index] == 0) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        int outputWidth = maxX - minX + 5, outputHeight = maxY - minY + 5;
        byte[] result = new byte[outputWidth * outputHeight * 4];
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                int source = y * BoardSize + x;
                if (alpha[source] == 0) continue;
                int target = ((y - minY + 2) * outputWidth + x - minX + 2) * 4;
                Array.Copy(photo, source * 4, result, target, 3);
                result[target + 3] = alpha[source];
            }
        failure = null;
        return new(outputWidth, outputHeight, result,
            new((outputWidth - 1) / 2.0, (outputHeight - 1) / 2.0), new(0, -1));
    }

    private static bool TryMatrix(IReadOnlyList<double> input, out double[] h)
    {
        h = [];
        if (input.Count != 9 || input.Any(value => !double.IsFinite(value))) return false;
        double scale = input.Max(value => Math.Abs(value));
        if (scale == 0) return false;
        h = input.Select(value => value / scale).ToArray();
        double determinant = h[0] * (h[4] * h[8] - h[5] * h[7]) -
            h[1] * (h[3] * h[8] - h[5] * h[6]) + h[2] * (h[3] * h[7] - h[4] * h[6]);
        return Math.Abs(determinant) > 1e-14;
    }

    private static bool TryMapHand(HandDetection hand, double[] h, out PixelPoint[] mapped)
    {
        mapped = [];
        if (hand?.Landmarks is not { Count: 21 } points || !double.IsFinite(hand.Confidence) ||
            hand.Confidence is < .5 or > 1 || !double.IsFinite(hand.RightHandProbability) ||
            hand.RightHandProbability is < 0 or > 1) return false;
        var result = new PixelPoint[21];
        int sign = 0;
        for (int index = 0; index < points.Count; index++)
        {
            PixelPoint point = points[index];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
            double divisor = h[6] * point.X + h[7] * point.Y + h[8];
            if (!double.IsFinite(divisor) || Math.Abs(divisor) < 1e-10 ||
                sign != 0 && Math.Sign(divisor) != sign) return false;
            sign = Math.Sign(divisor);
            result[index] = new(BoardSize * (h[0] * point.X + h[1] * point.Y + h[2]) / divisor,
                BoardSize * (h[3] * point.X + h[4] * point.Y + h[5]) / divisor);
            if (!double.IsFinite(result[index].X) || !double.IsFinite(result[index].Y) ||
                result[index].X is < -500 or > 1500 || result[index].Y is < -500 or > 1500) return false;
        }
        mapped = result;
        return true;
    }

    private static bool DrawShutterExclusion(Mat mask, PixelPoint[] points, double scale)
    {
        int[] palm = [0, 1, 2, 5, 9, 13, 17];
        Cv2.FillConvexPoly(mask, Cv2.ConvexHull(palm.Select(index => ToPoint(points[index])).ToArray()), Scalar.White);
        int radius = Math.Max(8, (int)Math.Ceiling(scale * .23));
        foreach (int start in new[] { 1, 5, 9, 13, 17 })
            for (int index = start; index < start + 3; index++)
            {
                Cv2.Line(mask, ToPoint(points[index]), ToPoint(points[index + 1]), Scalar.White, radius * 2);
                Cv2.Circle(mask, ToPoint(points[index + 1]), radius, Scalar.White, -1);
            }
        using Mat expand = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(radius + 1, radius + 1));
        Cv2.Dilate(mask, mask, expand);
        PixelPoint center = new(new[] { 5, 9, 13, 17 }.Average(index => points[index].X),
            new[] { 5, 9, 13, 17 }.Average(index => points[index].Y));
        double length = Distance(points[0], center);
        if (length < scale * .15) return false;
        PixelPoint end = new(points[0].X + (points[0].X - center.X) / length * BoardSize * 3,
            points[0].Y + (points[0].Y - center.Y) / length * BoardSize * 3);
        Cv2.Line(mask, ToPoint(points[0]), ToPoint(end), Scalar.White, (int)Math.Ceiling(scale * 1.05));
        return true;
    }

    private sealed record BackgroundSample(double X, double Y, double[] Color);

    private static bool TryBackground(byte[] photo, byte[] excluded, out double[][] model, out double threshold)
    {
        var samples = new List<BackgroundSample>();
        const int columns = 10, rows = 8;
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                int left = Capture.Left + column * Capture.Width / columns;
                int right = Capture.Left + (column + 1) * Capture.Width / columns;
                int top = Capture.Top + row * Capture.Height / rows;
                int bottom = Capture.Top + (row + 1) * Capture.Height / rows;
                var pixels = new List<int>();
                for (int y = top + 2; y < bottom - 2; y += 4)
                    for (int x = left + 2; x < right - 2; x += 4)
                        if (excluded[y * BoardSize + x] == 0) pixels.Add(y * BoardSize + x);
                if (pixels.Count < 40) continue;
                // Bright, near-white observations dominate a white board; using
                // the weakest color channel also suppresses bright colored objects.
                pixels.Sort((a, b) => MinimumChannel(photo, a).CompareTo(MinimumChannel(photo, b)));
                int[] bright = pixels.Skip(pixels.Count * 3 / 4).ToArray();
                double[] color = Enumerable.Range(0, 3).Select(channel =>
                    (double)bright.Select(index => photo[index * 4 + channel]).Order().ElementAt(bright.Length / 2)).ToArray();
                samples.Add(new((left + right) / (2.0 * BoardSize), (top + bottom) / (2.0 * BoardSize), color));
            }
        model = [];
        threshold = 18;
        if (samples.Count < 24) return false;
        var weights = Enumerable.Repeat(1.0, samples.Count).ToArray();
        model = new double[3][];
        double[] residuals = new double[samples.Count];
        for (int iteration = 0; iteration < 8; iteration++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                if (!FitPlane(samples, weights, channel, out var plane)) return false;
                model[channel] = plane;
            }
            for (int index = 0; index < samples.Count; index++)
            {
                var sample = samples[index];
                double squared = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    double delta = sample.Color[channel] - (model[channel][0] +
                        model[channel][1] * sample.X + model[channel][2] * sample.Y);
                    squared += delta * delta;
                }
                residuals[index] = Math.Sqrt(squared / 3);
                weights[index] = residuals[index] <= 8 ? 1 : 8 / residuals[index];
            }
        }
        double typicalError = residuals.Order().ElementAt(residuals.Length / 2);
        threshold = Math.Clamp(typicalError * 3 + 6, 16, 30);
        return typicalError < 14 && model.All(plane => BackgroundAt(plane, 500, 600) > 90);
    }

    private static bool FitPlane(IReadOnlyList<BackgroundSample> samples, double[] weights, int channel, out double[] plane)
    {
        var equations = new double[3, 4];
        for (int index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            double[] values = [1, sample.X, sample.Y];
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                    equations[row, column] += weights[index] * values[row] * values[column];
                equations[row, 3] += weights[index] * values[row] * sample.Color[channel];
            }
        }
        plane = new double[3];
        for (int pivot = 0; pivot < 3; pivot++)
        {
            int largest = pivot;
            for (int row = pivot + 1; row < 3; row++)
                if (Math.Abs(equations[row, pivot]) > Math.Abs(equations[largest, pivot])) largest = row;
            if (Math.Abs(equations[largest, pivot]) < 1e-10) return false;
            for (int column = pivot; column < 4; column++)
                (equations[pivot, column], equations[largest, column]) = (equations[largest, column], equations[pivot, column]);
            double divisor = equations[pivot, pivot];
            for (int column = pivot; column < 4; column++) equations[pivot, column] /= divisor;
            for (int row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                double factor = equations[row, pivot];
                for (int column = pivot; column < 4; column++) equations[row, column] -= factor * equations[pivot, column];
            }
        }
        for (int index = 0; index < 3; index++) plane[index] = equations[index, 3];
        return true;
    }

    private static bool ComponentNear(int[] ids, int selected, PixelPoint point, int radius)
    {
        for (int y = Math.Max(0, (int)point.Y - radius); y <= Math.Min(BoardSize - 1, point.Y + radius); y++)
            for (int x = Math.Max(0, (int)point.X - radius); x <= Math.Min(BoardSize - 1, point.X + radius); x++)
                if (ids[y * BoardSize + x] == selected) return true;
        return false;
    }

    private static double BackgroundAt(double[] plane, int x, int y) =>
        Math.Clamp(plane[0] + plane[1] * x / BoardSize + plane[2] * y / BoardSize, 0, 255);
    private static byte MinimumChannel(byte[] pixels, int index) =>
        Math.Min(pixels[index * 4], Math.Min(pixels[index * 4 + 1], pixels[index * 4 + 2]));
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static Point ToPoint(PixelPoint p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));
    private static byte[] Bytes(Mat image, int channels)
    {
        int width = image.Width, height = image.Height;
        var result = new byte[width * height * channels];
        for (int row = 0; row < height; row++) Marshal.Copy(image.Ptr(row), result, row * width * channels, width * channels);
        return result;
    }
}
