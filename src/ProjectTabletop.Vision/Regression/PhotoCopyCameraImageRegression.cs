using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoCopyCameraImageRegression
{
    private const int Width = 480, Height = 320;

    public static void Run()
    {
        foreach (bool perspective in new[] { false, true })
            foreach (double angle in new[] { 0.0, 43, 87 })
                CheckOriginalCameraPixels(perspective, angle);
        CheckInvalidInput();
        Console.WriteLine("Photo Copy camera-image regression: native dimensions and byte-identical RGB under " +
            "non-square/perspective mapping, diagonal objects, holes and transparent guard, anchor/direction, " +
            "padded stride, immutable calibration metadata and invalid-input rejection passed.");
    }

    private static void CheckOriginalCameraPixels(bool perspective, double angle)
    {
        using Mat mask = new(Height, Width, MatType.CV_8UC1, Scalar.Black);
        var rectangle = new RotatedRect(new(225, 180), new(100, 56), (float)angle);
        Cv2.FillConvexPoly(mask, rectangle.Points().Select(p => new Point((int)Math.Round(p.X),
            (int)Math.Round(p.Y))).ToArray(), Scalar.White);
        Cv2.Circle(mask, new Point(225, 180), 8, Scalar.Black, -1);
        var cameraAnchor = new PixelPoint(225, 180);
        double[] map = Mapping(perspective);
        PhotoHandCutout segmented = BoardCutout(mask, map, cameraAnchor);
        byte[] source = Photograph(Width * 4);
        var native = PhotoCopyCameraImage.Capture(Width, Height, Width * 4, source, map, segmented)
            ?? throw new Exception("Native camera cutout was rejected.");
        Require(native.BoardOrigin is null && native.CameraGeometry is { FrameWidth: Width, FrameHeight: Height },
            "Native camera cutout retained a misleading board origin or lost its frame dimensions.");
        Require(native.CameraGeometry!.CameraToBoard.SequenceEqual(map), "Native cutout changed its calibration mapping.");
        Require(native.CameraGeometry.CameraToBoard is not double[], "Native cutout exposes mutable calibration storage.");
        map[0] *= 2;
        Require(native.CameraGeometry.CameraToBoard[0] != map[0], "Native cutout retains its caller's calibration array.");
        map = Mapping(perspective);
        int left = (int)Math.Round(cameraAnchor.X - native.PalmAnchor.X);
        int top = (int)Math.Round(cameraAnchor.Y - native.PalmAnchor.Y);
        Require(Math.Abs(cameraAnchor.X - left - native.PalmAnchor.X) < 1e-5 &&
            Math.Abs(cameraAnchor.Y - top - native.PalmAnchor.Y) < 1e-5,
            "Native camera anchor shifted during mask conversion.");
        Require(Math.Abs(native.MiddleFingerDirection.X) < 1e-6 && native.MiddleFingerDirection.Y < -.999999,
            "The native camera cutout failed to map the original inward direction.");
        Rect originalBounds = Cv2.BoundingRect(mask);
        Require(Math.Abs(native.Width - (originalBounds.Width + 4)) <= 4 &&
            Math.Abs(native.Height - (originalBounds.Height + 4)) <= 4,
            "The copied image kept square-board stretching instead of its camera dimensions.");
        int opaque = 0;
        for (int y = 0; y < native.Height; y++)
            for (int x = 0; x < native.Width; x++)
            {
                int index = (y * native.Width + x) * 4;
                byte alpha = native.BgraPixels[index + 3];
                if (x < 2 || y < 2 || x >= native.Width - 2 || y >= native.Height - 2)
                    Require(alpha == 0, "Native cutout has no transparent two-pixel guard.");
                if (alpha == 0)
                {
                    Require(native.BgraPixels.AsSpan(index, 3).SequenceEqual(new byte[3]),
                        "Transparent native pixels contain a camera background color.");
                    continue;
                }
                opaque++;
                int cameraIndex = ((top + y) * Width + left + x) * 4;
                Require(native.BgraPixels.AsSpan(index, 3).SequenceEqual(source.AsSpan(cameraIndex, 3)),
                    "Camera RGB was resized, filtered or replaced by rectified segmentation pixels.");
            }
        Require(opaque > 4500, "Native camera cutout lost most of the subject.");
        int hole = ((180 - top) * native.Width + 225 - left) * 4 + 3;
        Require(native.BgraPixels[hole] == 0, "Native mask filled a real photographed hole.");
        int stride = Width * 4 + 27;
        var padded = PhotoCopyCameraImage.Capture(Width, Height, stride, Photograph(stride), map, segmented)
            ?? throw new Exception("Padded native camera crop was rejected.");
        Require(padded.Width == native.Width && padded.Height == native.Height &&
            padded.BgraPixels.SequenceEqual(native.BgraPixels), "Camera row padding changed a native image.");
    }

    private static void CheckInvalidInput()
    {
        using Mat mask = new(Height, Width, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(mask, new Rect(170, 140, 100, 50), Scalar.White, -1);
        double[] map = Mapping(false);
        var cutout = BoardCutout(mask, map, new(220, 165));
        byte[] pixels = Photograph(Width * 4);
        Require(PhotoCopyCameraImage.Capture(Width, Height, Width * 4, pixels, map,
            cutout with { BoardOrigin = null }) is null, "Missing source-board origin was guessed.");
        Require(PhotoCopyCameraImage.Capture(Width, Height, Width * 4, pixels, map,
            cutout with { MiddleFingerDirection = new(0, 0) }) is null, "A zero inward direction was accepted.");
        Require(PhotoCopyCameraImage.Capture(Width, Height, Width * 4, pixels, map,
            cutout with { BoardOrigin = new(double.NaN, 0) }) is null, "Nonfinite board origin was accepted.");
        var native = PhotoCopyCameraImage.Capture(Width, Height, Width * 4, pixels, map, cutout)!;
        Require(PhotoCopyCameraImage.Capture(Width, Height, Width * 4, pixels, map,
            native with { BoardOrigin = new(0, 0) }) is null, "An already-native camera image was converted twice.");
        ExpectArgument(() => PhotoCopyCameraImage.Capture(Width, Height, Width * 4 - 1, pixels, map, cutout));
        ExpectArgument(() => PhotoCopyCameraImage.Capture(Width, Height, Width * 4, pixels[..^1], map, cutout));
        ExpectArgument(() => PhotoCopyCameraImage.Capture(Width, Height, Width * 4, pixels, new double[9], cutout));
        using Mat clipped = new(Height, Width, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(clipped, new Rect(0, 140, 70, 45), Scalar.White, -1);
        var clippedCutout = BoardCutout(clipped, map, new(35, 160));
        Require(PhotoCopyCameraImage.Capture(Width, Height, Width * 4, pixels, map, clippedCutout) is null,
            "A native subject clipped by the camera edge was silently cropped.");
    }

    private static PhotoHandCutout BoardCutout(Mat cameraMask, double[] map, PixelPoint cameraAnchor)
    {
        using Mat matrix = new(3, 3, MatType.CV_64FC1);
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                matrix.Set(row, column, map[row * 3 + column] * (row < 2 ? 1000 : 1));
        using Mat board = new();
        Cv2.WarpPerspective(cameraMask, board, matrix, new Size(1000, 1000),
            InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
        Rect bounds = Cv2.BoundingRect(board);
        int left = bounds.Left - 2, top = bounds.Top - 2, width = bounds.Width + 4, height = bounds.Height + 4;
        var pixels = new byte[width * height * 4];
        for (int y = 2; y < height - 2; y++)
            for (int x = 2; x < width - 2; x++)
            {
                int index = (y * width + x) * 4;
                // These deliberate fake RGB values must never reach the final camera crop.
                pixels[index] = 7; pixels[index + 1] = 19; pixels[index + 2] = 31;
                pixels[index + 3] = board.At<byte>(top + y, left + x);
            }
        PixelPoint anchor = BoardPoint(cameraAnchor, map), forward = BoardPoint(new(cameraAnchor.X, cameraAnchor.Y - 10), map);
        double dx = forward.X - anchor.X, dy = forward.Y - anchor.Y, length = Math.Sqrt(dx * dx + dy * dy);
        return new(width, height, pixels, new(anchor.X - left, anchor.Y - top), new(dx / length, dy / length))
            { BoardOrigin = new(left, top) };
    }

    private static PixelPoint BoardPoint(PixelPoint p, double[] h)
    {
        double z = h[6] * p.X + h[7] * p.Y + h[8];
        return new(1000 * (h[0] * p.X + h[1] * p.Y + h[2]) / z,
            1000 * (h[3] * p.X + h[4] * p.Y + h[5]) / z);
    }

    private static double[] Mapping(bool perspective)
    {
        if (!perspective) return [1.0 / Width, 0, 0, 0, 1.0 / Height, 0, 0, 0, 1];
        using Mat h = Cv2.GetPerspectiveTransform([new(0, 0), new(Width, 0), new(Width, Height), new(0, Height)],
            [new(.1f, .08f), new(.9f, .03f), new(.98f, .92f), new(.05f, .96f)]);
        return Enumerable.Range(0, 9).Select(i => h.At<double>(i / 3, i % 3)).ToArray();
    }

    private static byte[] Photograph(int stride)
    {
        byte[] pixels = new byte[stride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = (byte)((x * 17 + y * 3) % 256);
                pixels[i + 1] = (byte)((x * 7 + y * 13) % 256);
                pixels[i + 2] = (byte)((x * 11 + y * 19) % 256);
                pixels[i + 3] = 255;
            }
        return pixels;
    }

    private static void ExpectArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid camera image input did not throw ArgumentException.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
