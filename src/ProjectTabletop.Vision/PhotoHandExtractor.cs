using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>
/// Extracts a photographed hand from a white board using the measured landmarks
/// and local image colors. The optional reference must be the same camera's empty
/// white board at identical dimensions/stride. No photograph is saved to disk.
/// </summary>
public static class PhotoHandExtractor
{
    private const int BoardSize = PhotoHandCutout.BoardPixels;
    private static readonly int[][] Fingers = [[1, 2, 3, 4], [5, 6, 7, 8],
        [9, 10, 11, 12], [13, 14, 15, 16], [17, 18, 19, 20]];
    private static readonly int[] Palm = [0, 1, 2, 5, 9, 13, 17];

    /// <param name="cameraToBoard">Row-major 3x3 map from raw camera pixels to
    /// normalized board coordinates (0..1 on each axis).</param>
    /// <returns>Null when pose, contrast, board containment, or segmentation is
    /// unreliable. Invalid buffer arguments throw ArgumentException.</returns>
    public static PhotoHandCutout? Extract(int width, int height, int stride,
        byte[] bgra, HandDetection hand, IReadOnlyList<double> cameraToBoard,
        byte[]? whiteReferenceBgra = null)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(cameraToBoard);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 ||
            stride < (long)width * 4 || bgra.Length < (long)(height - 1) * stride + width * 4L ||
            whiteReferenceBgra is not null && whiteReferenceBgra.Length < (long)(height - 1) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (cameraToBoard.Count != 9 || cameraToBoard.Any(value => !double.IsFinite(value)) ||
            hand.Landmarks.Count != 21 || !double.IsFinite(hand.Confidence) ||
            hand.Confidence < 0.8 || hand.Confidence > 1 ||
            hand.Landmarks.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                p.X < 3 || p.Y < 3 || p.X >= width - 3 || p.Y >= height - 3)) return null;

        double[] h = cameraToBoard.ToArray();
        double determinant = h[0] * (h[4] * h[8] - h[5] * h[7]) -
            h[1] * (h[3] * h[8] - h[5] * h[6]) + h[2] * (h[3] * h[7] - h[4] * h[6]);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-14) return null;
        var points = new PixelPoint[21];
        for (int i = 0; i < points.Length; i++)
        {
            PixelPoint p = hand.Landmarks[i];
            double z = h[6] * p.X + h[7] * p.Y + h[8];
            if (!double.IsFinite(z) || Math.Abs(z) < 1e-10) return null;
            points[i] = new PixelPoint(BoardSize * (h[0] * p.X + h[1] * p.Y + h[2]) / z,
                BoardSize * (h[3] * p.X + h[4] * p.Y + h[5]) / z);
            if (!double.IsFinite(points[i].X) || !double.IsFinite(points[i].Y) ||
                points[i].X < 10 || points[i].Y < 10 || points[i].X >= BoardSize - 10 ||
                points[i].Y >= BoardSize - 10) return null;
        }

        double scale = Math.Max(Distance(points[5], points[17]), Distance(points[0], points[9]));
        double middleLength = Distance(points[9], points[12]);
        double middlePath = Distance(points[9], points[10]) + Distance(points[10], points[11]) +
            Distance(points[11], points[12]);
        // A straight, extended middle finger gives an unambiguous inward axis.
        if (scale < 28 || scale > 420 || middleLength < scale * 0.45 ||
            middleLength < middlePath * 0.85 ||
            Distance(points[0], points[12]) < Distance(points[0], points[9]) + scale * 0.35)
            return null;

        int margin = (int)Math.Ceiling(scale * 0.22) + 4;
        int left = (int)Math.Floor(points.Min(p => p.X)) - margin;
        int top = (int)Math.Floor(points.Min(p => p.Y)) - margin;
        int right = (int)Math.Ceiling(points.Max(p => p.X)) + margin;
        int bottom = (int)Math.Ceiling(points.Max(p => p.Y)) + margin;
        if (left < 1 || top < 1 || right >= BoardSize - 1 || bottom >= BoardSize - 1 ||
            right - left > 850 || bottom - top > 850) return null;
        var roi = new Rect(left, top, right - left + 1, bottom - top + 1);
        PixelPoint[] local = points.Select(p => new PixelPoint(p.X - left, p.Y - top)).ToArray();
        PixelPoint anchor = new(Palm.Average(i => local[i].X), Palm.Average(i => local[i].Y));
        using Mat camera = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra, stride);
        using Mat transform = new(3, 3, MatType.CV_64FC1);
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                transform.Set(row, column, h[row * 3 + column] * (row < 2 ? BoardSize : 1));
        using Mat rectified = new();
        Cv2.WarpPerspective(camera, rectified, transform, new Size(BoardSize, BoardSize),
            InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255, 255, 255, 255));
        using Mat crop = new(rectified, roi);
        using Mat color = new();
        Cv2.CvtColor(crop, color, ColorConversionCodes.BGRA2BGR);
        using Mat envelope = new(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
        using Mat seeds = new(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
        BuildAnatomyMasks(envelope, seeds, local, anchor, scale);

        byte[] photo = Bytes(crop, 4), allowed = Bytes(envelope, 1), core = Bytes(seeds, 1);
        byte[]? reference = null;
        if (whiteReferenceBgra is not null)
        {
            using Mat emptyCamera = Mat.FromPixelData(height, width, MatType.CV_8UC4,
                whiteReferenceBgra, stride);
            using Mat emptyBoard = new();
            Cv2.WarpPerspective(emptyCamera, emptyBoard, transform, new Size(BoardSize, BoardSize),
                InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255, 255, 255, 255));
            using Mat emptyCrop = new(emptyBoard, roi);
            reference = Bytes(emptyCrop, 4);
        }
        ColorSample white = MedianColor(photo, allowed, value => value == 0);
        ColorSample skin = MedianColor(photo, core, value => value != 0);
        if (white.Brightness < 80 || ColorDistance(white, skin) < 14) return null;
        double skinChroma = ChromaDistance(white, skin);
        using Mat labels = new(roi.Height, roi.Width, MatType.CV_8UC1);
        byte[] labelsBytes = new byte[allowed.Length];
        int foregroundSeeds = 0;
        for (int i = 0; i < allowed.Length; i++)
        {
            if (allowed[i] == 0) continue; // Definite background, including wrist cutoff.
            ColorSample pixel = Sample(photo, i);
            ColorSample background = reference is null ? white : Sample(reference, i);
            double chroma = ChromaDistance(background, pixel);
            // A neutral darkening of the white board is usually a cast shadow.
            // Only use that veto when the photographed hand has distinct chroma.
            bool shadow = skinChroma > 0.025 && chroma < Math.Max(0.012, skinChroma * 0.24) &&
                pixel.Brightness > background.Brightness * 0.23;
            bool unchanged = ColorDistance(background, pixel) < 12;
            if (shadow || unchanged) continue;
            if (core[i] != 0)
            {
                labelsBytes[i] = (byte)GrabCutClasses.FGD;
                foregroundSeeds++;
            }
            else labelsBytes[i] = (byte)(ColorDistance(pixel, skin) < ColorDistance(pixel, background)
                ? GrabCutClasses.PR_FGD : GrabCutClasses.PR_BGD);
        }
        if (foregroundSeeds < scale * scale * 0.025) return null;
        Marshal.Copy(labelsBytes, 0, labels.Data, labelsBytes.Length);
        using Mat bgModel = new(), fgModel = new();
        Cv2.GrabCut(color, labels, default, bgModel, fgModel, 2, GrabCutModes.InitWithMask);
        labelsBytes = Bytes(labels, 1);
        byte[] maskBytes = labelsBytes.Select(value => value is (byte)GrabCutClasses.FGD or
            (byte)GrabCutClasses.PR_FGD ? (byte)255 : (byte)0).ToArray();
        using Mat mask = Mat.FromPixelData(roi.Height, roi.Width, MatType.CV_8UC1, maskBytes);
        // Keep the component with the most trusted hand pixels; disconnected
        // objects and neutral shadows never become part of the photograph.
        using Mat components = new();
        int count = Cv2.ConnectedComponents(mask, components, PixelConnectivity.Connectivity8, MatType.CV_32SC1);
        int[] componentIds = new int[maskBytes.Length], votes = new int[count];
        Marshal.Copy(components.Data, componentIds, 0, componentIds.Length);
        for (int i = 0; i < maskBytes.Length; i++)
            if (core[i] != 0 && maskBytes[i] != 0) votes[componentIds[i]]++;
        int selected = Enumerable.Range(1, count - 1).OrderByDescending(i => votes[i]).FirstOrDefault();
        if (selected == 0) return null;
        int area = 0;
        for (int i = 0; i < maskBytes.Length; i++)
        {
            maskBytes[i] = componentIds[i] == selected ? (byte)255 : (byte)0;
            if (maskBytes[i] != 0) area++;
        }
        if (area < scale * scale * 0.35 || area > scale * scale * 4.5) return null;
        // Reject missing fingers instead of manufacturing a silhouette from the
        // landmarks. A tiny contour feather supplies actual alpha, including gaps.
        foreach (int index in new[] { 0, 5, 9, 13, 17, 12 })
            if (!HasMaskNear(maskBytes, roi.Width, roi.Height, local[index], Math.Max(2, scale * 0.06)))
                return null;
        Marshal.Copy(maskBytes, 0, mask.Data, maskBytes.Length);
        using Mat feather = new();
        Cv2.GaussianBlur(mask, feather, new Size(3, 3), 0.65);
        byte[] alpha = Bytes(feather, 1);
        int minX = roi.Width, minY = roi.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < roi.Height; y++)
            for (int x = 0; x < roi.Width; x++)
            {
                int i = y * roi.Width + x;
                if (alpha[i] < 3) alpha[i] = 0;
                if (alpha[i] == 0) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        // A guaranteed transparent guard border supports rotated bilinear draws.
        int outWidth = maxX - minX + 5, outHeight = maxY - minY + 5;
        byte[] pixels = new byte[outWidth * outHeight * 4];
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                int source = y * roi.Width + x;
                if (alpha[source] == 0) continue;
                int target = ((y - minY + 2) * outWidth + x - minX + 2) * 4;
                pixels[target] = photo[source * 4];
                pixels[target + 1] = photo[source * 4 + 1];
                pixels[target + 2] = photo[source * 4 + 2];
                pixels[target + 3] = alpha[source];
            }
        return new PhotoHandCutout(outWidth, outHeight, pixels,
            new PixelPoint(anchor.X - minX + 2, anchor.Y - minY + 2),
            new PixelPoint((local[12].X - local[9].X) / middleLength,
                (local[12].Y - local[9].Y) / middleLength))
            { BoardOrigin = new(left + minX - 2, top + minY - 2) };
    }

    private static void BuildAnatomyMasks(Mat envelope, Mat seeds, PixelPoint[] points,
        PixelPoint center, double scale)
    {
        Point[] palm = Cv2.ConvexHull(Palm.Select(i => ToPoint(points[i])).ToArray());
        Cv2.FillConvexPoly(envelope, palm, Scalar.White);
        int expansion = Math.Max(3, (int)Math.Round(scale * 0.11));
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse,
            new Size(expansion * 2 + 1, expansion * 2 + 1));
        Cv2.Dilate(envelope, envelope, kernel);
        Point[] insidePalm = Cv2.ConvexHull(Palm.Select(i => ToPoint(new PixelPoint(
            center.X + (points[i].X - center.X) * 0.70,
            center.Y + (points[i].Y - center.Y) * 0.70))).ToArray());
        Cv2.FillConvexPoly(seeds, insidePalm, Scalar.White);
        foreach (int[] finger in Fingers)
        {
            int radius = Math.Max(3, (int)Math.Round(scale * (finger[0] == 1 ? 0.15 : 0.12)));
            int seedRadius = Math.Max(1, (int)Math.Round(scale * 0.027));
            for (int segment = 0; segment < finger.Length - 1; segment++)
            {
                Point a = ToPoint(points[finger[segment]]), b = ToPoint(points[finger[segment + 1]]);
                Cv2.Line(envelope, a, b, Scalar.White, radius * 2);
                Cv2.Circle(envelope, b, radius, Scalar.White, -1);
                Cv2.Line(seeds, a, b, Scalar.White, seedRadius * 2);
                Cv2.Circle(seeds, b, seedRadius, Scalar.White, -1);
            }
        }
        // Cut behind the wrist, perpendicular to the wrist-to-palm direction.
        // Without this plane a bare forearm could merge into the same component.
        PixelPoint forward = new(center.X - points[0].X, center.Y - points[0].Y);
        double length = Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y);
        byte[] aMask = Bytes(envelope, 1), sMask = Bytes(seeds, 1);
        int width = envelope.Width, height = envelope.Height;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (((x - points[0].X) * forward.X + (y - points[0].Y) * forward.Y) / length < -scale * 0.055)
                    aMask[y * width + x] = sMask[y * width + x] = 0;
        Marshal.Copy(aMask, 0, envelope.Data, aMask.Length);
        Marshal.Copy(sMask, 0, seeds.Data, sMask.Length);
    }

    private readonly record struct ColorSample(double B, double G, double R)
    {
        public double Brightness => (B + G + R) / 3;
    }

    private static ColorSample Sample(byte[] pixels, int index) =>
        new(pixels[index * 4], pixels[index * 4 + 1], pixels[index * 4 + 2]);

    private static ColorSample MedianColor(byte[] pixels, byte[] mask, Func<byte, bool> includes)
    {
        var channels = new[] { new List<byte>(), new List<byte>(), new List<byte>() };
        for (int i = 0; i < mask.Length; i += 3)
            if (includes(mask[i]))
                for (int channel = 0; channel < 3; channel++) channels[channel].Add(pixels[i * 4 + channel]);
        foreach (List<byte> channel in channels) channel.Sort();
        return channels[0].Count == 0 ? default : new ColorSample(channels[0][channels[0].Count / 2],
            channels[1][channels[1].Count / 2], channels[2][channels[2].Count / 2]);
    }

    private static double ColorDistance(ColorSample a, ColorSample b) =>
        Math.Sqrt(Math.Pow(a.B - b.B, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.R - b.R, 2));

    private static double ChromaDistance(ColorSample a, ColorSample b)
    {
        double sumA = Math.Max(1, a.B + a.G + a.R), sumB = Math.Max(1, b.B + b.G + b.R);
        return Math.Sqrt(Math.Pow(a.B / sumA - b.B / sumB, 2) +
            Math.Pow(a.G / sumA - b.G / sumB, 2) + Math.Pow(a.R / sumA - b.R / sumB, 2));
    }

    private static bool HasMaskNear(byte[] mask, int width, int height, PixelPoint point, double radius)
    {
        for (int y = Math.Max(0, (int)(point.Y - radius)); y <= Math.Min(height - 1, point.Y + radius); y++)
            for (int x = Math.Max(0, (int)(point.X - radius)); x <= Math.Min(width - 1, point.X + radius); x++)
                if (mask[y * width + x] != 0 && Math.Pow(x - point.X, 2) + Math.Pow(y - point.Y, 2) <= radius * radius)
                    return true;
        return false;
    }

    private static Point ToPoint(PixelPoint p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));
    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static byte[] Bytes(Mat image, int channels)
    {
        int width = image.Width, height = image.Height;
        var bytes = new byte[checked(width * height * channels)];
        for (int y = 0; y < height; y++)
            Marshal.Copy(image.Ptr(y), bytes, y * width * channels, width * channels);
        return bytes;
    }
}
