using System.Runtime.InteropServices;
using OpenCvSharp;
using static ProjectTabletop.Vision.PhotoObjectExtractor;

namespace ProjectTabletop.Vision;

/// <summary>
/// Contrast-based acquisition on grey and conservative verification of a locked
/// silhouette under white light. Call Locate only after hands and projected
/// spotlights have left the acquisition area and the camera exposure has settled.
/// No semantic object recognition or empty-board reference is required.
/// </summary>
public static class PhotoObjectLocator
{
    private const int BoardSize = PhotoHandCutout.BoardPixels;
    private static readonly Rect Capture = new(PhotoObjectTarget.CaptureLeft, PhotoObjectTarget.CaptureTop,
        PhotoObjectTarget.CaptureRight - PhotoObjectTarget.CaptureLeft,
        PhotoObjectTarget.CaptureBottom - PhotoObjectTarget.CaptureTop);

    public static PhotoObjectTarget? Locate(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<double> cameraToBoard, out string? failure)
    {
        if (!TryRectify(width, height, stride, bgra, cameraToBoard, out var photo, out _, out failure)) return null;
        if (!TryBackground(photo, new byte[BoardSize * BoardSize], out var background,
            out double threshold, neutralGrey: true))
        {
            failure = "Leave plain grey space around one object and keep hands off the capture area.";
            return null;
        }
        float[] illumination = BuildIlluminationCorrection(photo, background, threshold);
        byte[] pixels = new byte[BoardSize * BoardSize];
        for (int y = Capture.Top; y < Capture.Bottom; y++)
            for (int x = Capture.Left; x < Capture.Right; x++)
                if (Difference(photo, y * BoardSize + x, background, x, y, illumination) > threshold)
                    pixels[y * BoardSize + x] = 255;
        using Mat mask = Mat.FromPixelData(BoardSize, BoardSize, MatType.CV_8UC1, pixels);
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        using Mat labels = new();
        int count = Cv2.ConnectedComponents(mask, labels, PixelConnectivity.Connectivity8, MatType.CV_32SC1);
        int[] ids = new int[pixels.Length], area = new int[count];
        bool[] touchesEdge = new bool[count];
        Marshal.Copy(labels.Data, ids, 0, ids.Length);
        for (int y = Capture.Top; y < Capture.Bottom; y++)
            for (int x = Capture.Left; x < Capture.Right; x++)
            {
                int id = ids[y * BoardSize + x];
                if (id == 0) continue;
                area[id]++;
                if (x <= Capture.Left + 2 || x >= Capture.Right - 3 || y <= Capture.Top + 2 || y >= Capture.Bottom - 3)
                    touchesEdge[id] = true;
            }
        int[] candidates = Enumerable.Range(1, count - 1).Where(id => area[id] >= 400).ToArray();
        if (candidates.Length != 1)
        {
            failure = candidates.Length == 0
                ? "Place one contrasting object on the grey area, then move your hands away."
                : "Leave just one object in the grey capture area.";
            return null;
        }
        int selected = candidates[0];
        if (touchesEdge[selected] || area[selected] > Capture.Width * Capture.Height * .45)
        {
            failure = "Move the whole object inside the grey area and leave space around its edges.";
            return null;
        }
        for (int index = 0; index < pixels.Length; index++) pixels[index] = ids[index] == selected ? (byte)255 : (byte)0;
        Marshal.Copy(pixels, 0, mask.Data, pixels.Length);
        using Mat feather = new();
        Cv2.GaussianBlur(mask, feather, new Size(3, 3), .65);
        byte[] fullAlpha = Bytes(feather, 1);
        int left = BoardSize, top = BoardSize, right = 0, bottom = 0;
        for (int y = Capture.Top; y < Capture.Bottom; y++)
            for (int x = Capture.Left; x < Capture.Right; x++)
            {
                int index = y * BoardSize + x;
                if (fullAlpha[index] < 3) { fullAlpha[index] = 0; continue; }
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        left -= 2; top -= 2; right += 2; bottom += 2;
        int targetWidth = right - left + 1, targetHeight = bottom - top + 1;
        byte[] alpha = new byte[targetWidth * targetHeight];
        for (int y = 0; y < targetHeight; y++)
            Array.Copy(fullAlpha, (top + y) * BoardSize + left, alpha, y * targetWidth, targetWidth);
        failure = null;
        return new(left, top, targetWidth, targetHeight, alpha, area[selected]);
    }

    /// <summary>
    /// Check a stationary target against its immediate illuminated surroundings.
    /// Occlusion and insufficient usable margin are distinct from confirmed loss.
    /// An object with no visible contrast under white light cannot be verified.
    /// </summary>
    public static PhotoObjectTargetState ObserveTarget(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<double> cameraToBoard, PhotoObjectTarget target, out string? failure,
        IReadOnlyList<HandDetection>? hands = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!TryRectify(width, height, stride, bgra, cameraToBoard, out var photo, out var h, out failure))
            return PhotoObjectTargetState.Unavailable;
        return ObserveRectified(photo, h, target, hands, out failure);
    }

    internal static PhotoObjectTargetState ObserveRectified(byte[] photo, double[] h, PhotoObjectTarget target,
        IReadOnlyList<HandDetection>? hands, out string? failure)
    {
        using Mat mask = TargetMask(target);
        using Mat exclusion = new(BoardSize, BoardSize, MatType.CV_8UC1, Scalar.Black);
        foreach (HandDetection hand in hands ?? [])
        {
            if (!TryMapHand(hand, h, out var points))
            {
                failure = "Keep the selecting hand visible and separate from the object.";
                return PhotoObjectTargetState.Unavailable;
            }
            double scale = Math.Max(Distance(points[0], points[9]), Distance(points[5], points[17]));
            if (scale is < 20 or > 450 || !DrawShutterExclusion(exclusion, points, scale))
            {
                failure = "The hand position is unclear. Keep it beside the object and try again.";
                return PhotoObjectTargetState.Unavailable;
            }
        }
        byte[] excluded = Bytes(exclusion, 1), silhouette = Bytes(mask, 1);
        if (silhouette.Where((value, index) => value != 0 && excluded[index] != 0).Any())
        {
            failure = "Keep the selecting hand and forearm away from the lit object.";
            return PhotoObjectTargetState.Occluded;
        }

        using Mat outside = new(), innerMargin = new(), eroded = new();
        using Mat outerKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(41, 41));
        using Mat marginKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(17, 17));
        using Mat erodeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(11, 11));
        Cv2.Dilate(mask, outside, outerKernel);
        Cv2.Dilate(mask, innerMargin, marginKernel);
        Cv2.Erode(mask, eroded, erodeKernel);
        byte[] outer = Bytes(outside, 1), margin = Bytes(innerMargin, 1), inside = Bytes(eroded, 1);
        var samples = new List<BackgroundSample>();
        int x0 = Math.Max(Capture.Left, target.Left - 23), x1 = Math.Min(Capture.Right, target.Left + target.Width + 23);
        int y0 = Math.Max(Capture.Top, target.Top - 23), y1 = Math.Min(Capture.Bottom, target.Top + target.Height + 23);
        for (int y = y0; y < y1; y += 4)
            for (int x = x0; x < x1; x += 4)
            {
                int index = y * BoardSize + x;
                if (outer[index] == 0 || margin[index] != 0 || excluded[index] != 0) continue;
                samples.Add(new(x / (double)BoardSize, y / (double)BoardSize,
                    [photo[index * 4], photo[index * 4 + 1], photo[index * 4 + 2]]));
            }
        if (!TryLocalBackground(samples, out var background, out double threshold))
        {
            failure = "The object's illuminated surroundings are unclear. Keep space around it.";
            return PhotoObjectTargetState.Unavailable;
        }
        int body = 0, bodySeen = 0, edge = 0, edgeSeen = 0, spill = 0, spillSeen = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int index = y * BoardSize + x;
                if (excluded[index] != 0) continue;
                bool foreground = Difference(photo, index, background, x, y) > threshold;
                if (silhouette[index] != 0)
                {
                    body++; if (foreground) bodySeen++;
                    if (inside[index] == 0) { edge++; if (foreground) edgeSeen++; }
                }
                else if (margin[index] != 0)
                {
                    spill++; if (foreground) spillSeen++;
                }
            }
        // Both sides of the stored boundary must still agree. An occupancy-only
        // check would accept a shifted object while copying background on one side.
        if (body == 0 || edge == 0 || bodySeen < body * .72 || edgeSeen < edge * .65 ||
            spill > 0 && spillSeen > spill * .24)
        {
            failure = "The object moved or cannot be distinguished under the light. Leave it still for a new lock.";
            return PhotoObjectTargetState.MissingOrMoved;
        }
        failure = null;
        return PhotoObjectTargetState.Present;
    }

    internal static bool TryRectify(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<double> cameraToBoard, out byte[] photo, out double[] h, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(cameraToBoard);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        photo = []; failure = "The board mapping is unavailable. Rescan the board.";
        if (!TryMatrix(cameraToBoard, out h)) return false;
        using Mat camera = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra, stride);
        using Mat transform = new(3, 3, MatType.CV_64FC1);
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                transform.Set(row, column, h[row * 3 + column] * (row < 2 ? BoardSize : 1));
        using Mat board = new();
        Cv2.WarpPerspective(camera, board, transform, new Size(BoardSize, BoardSize),
            InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255, 255, 255, 255));
        using Mat coverage = new(), cameraCoverage = new(height, width, MatType.CV_8UC1, Scalar.White);
        Cv2.WarpPerspective(cameraCoverage, coverage, transform, new Size(BoardSize, BoardSize),
            InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.Black);
        using Mat visible = new(coverage, Capture);
        if (Cv2.CountNonZero(visible) < Capture.Width * Capture.Height * .995)
        {
            failure = "The camera cannot see the whole capture area. Rescan the board.";
            return false;
        }
        photo = Bytes(board, 4); failure = null; return true;
    }

    private static Mat TargetMask(PhotoObjectTarget target)
    {
        byte[] mask = new byte[BoardSize * BoardSize];
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
                if (target.Alpha[y * target.Width + x] >= 200)
                    mask[(target.Top + y) * BoardSize + target.Left + x] = 255;
        using Mat data = Mat.FromPixelData(BoardSize, BoardSize, MatType.CV_8UC1, mask);
        return data.Clone();
    }

    private static bool TryLocalBackground(IReadOnlyList<BackgroundSample> samples, out double[][] model, out double threshold)
    {
        model = new double[3][]; threshold = 16;
        if (samples.Count < 80) return false;
        // The whole sampling margin is inside the known solid white spotlight.
        // Prefer its brighter observations so a displaced dark/colored object
        // crossing one side of the old margin cannot become the fitted background.
        samples = samples.OrderBy(sample => sample.Color.Min()).Skip(samples.Count / 2).ToArray();
        double[] weights = Enumerable.Repeat(1.0, samples.Count).ToArray(), residuals = new double[samples.Count];
        for (int iteration = 0; iteration < 6; iteration++)
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
                    double delta = sample.Color[channel] - (model[channel][0] + model[channel][1] * sample.X + model[channel][2] * sample.Y);
                    squared += delta * delta;
                }
                residuals[index] = Math.Sqrt(squared / 3);
                weights[index] = residuals[index] <= 6 ? 1 : 6 / residuals[index];
            }
        }
        double typical = residuals.Order().ElementAt(residuals.Length / 2);
        threshold = Math.Clamp(typical * 3 + 8, 12, 28);
        return typical < 12;
    }

    internal static float[] BuildIlluminationCorrection(byte[] photo, double[][] background, double threshold)
    {
        // Fit only the already background-like observations. A gentle projector
        // hotspot changes the local board color, while a contrasting subject is
        // excluded from this fit. Normalized smoothing interpolates across those
        // excluded regions rather than blurring object colors into the background.
        float[] residual = new float[BoardSize * BoardSize * 4];
        for (int y = Capture.Top; y < Capture.Bottom; y++)
            for (int x = Capture.Left; x < Capture.Right; x++)
            {
                int index = y * BoardSize + x;
                if (Difference(photo, index, background, x, y) > threshold) continue;
                for (int channel = 0; channel < 3; channel++)
                    residual[index * 4 + channel] = (float)(photo[index * 4 + channel] - BackgroundAt(background[channel], x, y));
                residual[index * 4 + 3] = 1;
            }
        using Mat data = Mat.FromPixelData(BoardSize, BoardSize, MatType.CV_32FC4, residual);
        using Mat reduced = new(), smoothed = new(), enlarged = new();
        Cv2.Resize(data, reduced, new Size(250, 250), 0, 0, InterpolationFlags.Area);
        Cv2.GaussianBlur(reduced, smoothed, new Size(0, 0), 8);
        Cv2.Resize(smoothed, enlarged, new Size(BoardSize, BoardSize), 0, 0, InterpolationFlags.Linear);
        Marshal.Copy(enlarged.Data, residual, 0, residual.Length);
        return residual;
    }

    private static double Difference(byte[] photo, int index, double[][] background, int x, int y,
        float[]? illumination = null)
    {
        double squared = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double correction = illumination is not null && illumination[index * 4 + 3] > .005
                ? illumination[index * 4 + channel] / illumination[index * 4 + 3] : 0;
            double delta = photo[index * 4 + channel] - BackgroundAt(background[channel], x, y) - correction;
            squared += delta * delta;
        }
        return Math.Sqrt(squared / 3);
    }
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
