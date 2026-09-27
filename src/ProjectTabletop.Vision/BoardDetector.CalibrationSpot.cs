using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>Camera position and apparent radius of one projected dark calibration disk.</summary>
public sealed record CalibrationSpotDetection(PixelPoint Center, double Confidence, double RadiusPixels);

public static partial class BoardDetector
{
    /// <summary>
    /// Locate a dark disk projected on a solid-white field by comparing fresh
    /// camera frames before and after the disk appears. The global median change
    /// is removed to tolerate webcam auto-exposure drift. Returns null when the
    /// change is not a distinct compact spot.
    /// </summary>
    public static CalibrationSpotDetection? DetectDarkCalibrationSpot(int width, int height,
        int stride, byte[] whiteBgra, byte[] spotBgra)
    {
        using Mat white = ReducedGray(width, height, stride, whiteBgra);
        using Mat spot = ReducedGray(width, height, stride, spotBgra);
        int reducedWidth = white.Width, reducedHeight = white.Height;
        int count = checked(reducedWidth * reducedHeight);
        byte[] before = new byte[count];
        byte[] after = new byte[count];
        Marshal.Copy(white.Data, before, 0, count);
        Marshal.Copy(spot.Data, after, 0, count);
        var sampleDeltas = new List<int>(count / 64 + 1);
        for (int y = 0; y < reducedHeight; y += 8)
            for (int x = 0; x < reducedWidth; x += 8)
            {
                int index = y * reducedWidth + x;
                sampleDeltas.Add(before[index] - after[index]);
            }
        sampleDeltas.Sort();
        int medianChange = sampleDeltas[sampleDeltas.Count / 2];
        byte[] corrected = new byte[count];
        int[] histogram = new int[256];
        for (int i = 0; i < count; i++)
        {
            int value = Math.Clamp(before[i] - after[i] - medianChange, 0, 255);
            corrected[i] = (byte)value;
            histogram[value]++;
        }
        int upperTail = Math.Max(1, count / 2000);
        int highValue = 255;
        while (highValue > 0 && upperTail > 0)
            upperTail -= histogram[highValue--];
        highValue++;
        if (highValue < 26) return null;
        using Mat difference = new(white.Height, white.Width, MatType.CV_8UC1);
        Marshal.Copy(corrected, 0, difference.Data, count);
        using Mat smooth = new();
        using Mat mask = new();
        Cv2.GaussianBlur(difference, smooth, new Size(7, 7), 0);
        double threshold = Math.Max(20, Math.Min(95, highValue * 0.42));
        Cv2.Threshold(smooth, mask, threshold, 255, ThresholdTypes.Binary);
        Cv2.FindContours(mask, out Point[][] contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        double frameArea = white.Width * (double)white.Height;
        var candidates = new List<(Point Center, double Radius, double Score)>();
        foreach (Point[] contour in contours)
        {
            double area = Math.Abs(Cv2.ContourArea(contour));
            if (area < frameArea * 0.00012 || area > frameArea * 0.018) continue;
            double perimeter = Cv2.ArcLength(contour, true);
            double circularity = 4 * Math.PI * area / Math.Max(1, perimeter * perimeter);
            if (circularity < 0.45) continue;
            Rect bounds = Cv2.BoundingRect(contour);
            double aspect = bounds.Width / (double)Math.Max(1, bounds.Height);
            if (aspect < 0.38 || aspect > 2.65) continue;
            Moments moments = Cv2.Moments(contour);
            if (moments.M00 <= 0) continue;
            Point center = new((int)Math.Round(moments.M10 / moments.M00),
                (int)Math.Round(moments.M01 / moments.M00));
            if (center.X < white.Width * 0.05 || center.Y < white.Height * 0.05 ||
                center.X > white.Width * 0.95 || center.Y > white.Height * 0.95) continue;
            double contrast = smooth.At<byte>(center.Y, center.X);
            if (contrast < threshold + 8) continue;
            candidates.Add((center, Math.Sqrt(area / Math.PI),
                circularity * Math.Min(contrast / 120, 1.0) * Math.Sqrt(area)));
        }
        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (candidates.Count > 1 && candidates[1].Score >= candidates[0].Score * 0.72)
            return null;
        var selected = candidates[0];
        double scale = width / (double)white.Width;
        return new CalibrationSpotDetection(
            new PixelPoint(selected.Center.X * scale, selected.Center.Y * height / (double)white.Height),
            Math.Clamp(selected.Score / 28, 0, 1), selected.Radius * scale);
    }

    private static Mat ReducedGray(int width, int height, int stride, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
            bgra.Length < (long)(height - 1) * stride + width * 4)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        using Mat frame = new(height, width, MatType.CV_8UC4);
        int rowBytes = checked(width * 4);
        for (int row = 0; row < height; row++)
            Marshal.Copy(bgra, row * stride, IntPtr.Add(frame.Data, row * rowBytes), rowBytes);
        double factor = Math.Min(1.0, WorkingWidth / (double)Math.Max(width, height));
        using Mat reduced = new();
        if (factor < 1.0)
            Cv2.Resize(frame, reduced,
                new Size(Math.Max(1, (int)Math.Round(width * factor)),
                    Math.Max(1, (int)Math.Round(height * factor))),
                interpolation: InterpolationFlags.Area);
        else
            frame.CopyTo(reduced);
        Mat gray = new();
        Cv2.CvtColor(reduced, gray, ColorConversionCodes.BGRA2GRAY);
        return gray;
    }
}
