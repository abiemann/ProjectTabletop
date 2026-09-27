using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ProjectTabletop.Vision;

/// <summary>The immutable mapping that accompanies an original camera-pixel cutout.</summary>
public sealed class PhotoCopyCameraGeometry
{
    private readonly ReadOnlyCollection<double> _cameraToBoard;
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    /// <summary>Row-major homography from raw camera pixels to normalized (0..1) board coordinates.</summary>
    public IReadOnlyList<double> CameraToBoard => _cameraToBoard;

    public PhotoCopyCameraGeometry(int frameWidth, int frameHeight, IReadOnlyList<double> cameraToBoard)
    {
        ArgumentNullException.ThrowIfNull(cameraToBoard);
        if (frameWidth is <= 0 or > 16384 || frameHeight is <= 0 or > 16384 ||
            !PhotoObjectExtractor.TryMatrix(cameraToBoard, out _))
            throw new ArgumentException("Invalid camera dimensions or board mapping.");
        FrameWidth = frameWidth; FrameHeight = frameHeight;
        _cameraToBoard = Array.AsReadOnly(cameraToBoard.ToArray());
    }
}

/// <summary>
/// Transfers a segmented board-space alpha to camera coordinates, then copies
/// original camera RGB byte-for-byte. Only the mask is resampled; the photograph
/// is neither resized nor rectified. The renderer rotates this native image in
/// camera coordinates before projecting the completed camera layer onto the board.
/// </summary>
public static class PhotoCopyCameraImage
{
    public static PhotoHandCutout? Capture(int width, int height, int stride, byte[] bgra,
        IReadOnlyList<double> cameraToBoard, PhotoHandCutout boardCutout)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(cameraToBoard);
        ArgumentNullException.ThrowIfNull(boardCutout);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || stride < width * 4L ||
            bgra.Length < (height - 1L) * stride + width * 4L)
            throw new ArgumentException("Invalid BGRA dimensions, stride, or buffer length.");
        if (!PhotoObjectExtractor.TryMatrix(cameraToBoard, out var h))
            throw new ArgumentException("Invalid camera-to-board mapping.", nameof(cameraToBoard));
        if (boardCutout.BoardOrigin is not { } origin || !Finite(origin) ||
            boardCutout.CameraGeometry is not null || boardCutout.Width is <= 0 or > 1000 ||
            boardCutout.Height is <= 0 or > 1000 ||
            boardCutout.BgraPixels.LongLength != (long)boardCutout.Width * boardCutout.Height * 4 ||
            !Finite(boardCutout.PalmAnchor) || !Finite(boardCutout.MiddleFingerDirection) ||
            boardCutout.PalmAnchor.X < 0 || boardCutout.PalmAnchor.Y < 0 ||
            boardCutout.PalmAnchor.X >= boardCutout.Width || boardCutout.PalmAnchor.Y >= boardCutout.Height)
            return null;

        using Mat normalized = new(3, 3, MatType.CV_64FC1);
        using Mat cameraToCutout = new(3, 3, MatType.CV_64FC1);
        for (int column = 0; column < 3; column++)
        {
            for (int row = 0; row < 3; row++) normalized.Set(row, column, h[row * 3 + column]);
            cameraToCutout.Set(0, column, PhotoHandCutout.BoardPixels * h[column] - origin.X * h[6 + column]);
            cameraToCutout.Set(1, column, PhotoHandCutout.BoardPixels * h[3 + column] - origin.Y * h[6 + column]);
            cameraToCutout.Set(2, column, h[6 + column]);
        }
        using Mat inverse = normalized.Inv();
        double[] boardToCamera = Enumerable.Range(0, 9).Select(i => inverse.At<double>(i / 3, i % 3)).ToArray();
        var boardAnchor = new PixelPoint(origin.X + boardCutout.PalmAnchor.X,
            origin.Y + boardCutout.PalmAnchor.Y);
        PixelPoint? anchor = Project(boardAnchor, boardToCamera);
        PixelPoint? forward = Project(new(boardAnchor.X + boardCutout.MiddleFingerDirection.X,
            boardAnchor.Y + boardCutout.MiddleFingerDirection.Y), boardToCamera);
        if (anchor is not { } cameraAnchor || forward is not { } cameraForward) return null;
        double dx = cameraForward.X - cameraAnchor.X, dy = cameraForward.Y - cameraAnchor.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length < 1e-8) return null;

        byte[] alpha = Enumerable.Range(0, boardCutout.Width * boardCutout.Height)
            .Select(i => boardCutout.BgraPixels[i * 4 + 3]).ToArray();
        using Mat sourceAlpha = Mat.FromPixelData(boardCutout.Height, boardCutout.Width, MatType.CV_8UC1, alpha);
        using Mat cameraAlpha = new();
        Cv2.WarpPerspective(sourceAlpha, cameraAlpha, cameraToCutout, new Size(width, height),
            InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, BorderTypes.Constant, Scalar.Black);
        byte[] mask = new byte[width * height];
        Marshal.Copy(cameraAlpha.Data, mask, 0, mask.Length);
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (mask[y * width + x] < 3) { mask[y * width + x] = 0; continue; }
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        // Do not silently crop a photographed subject at the frame boundary.
        if (maxX < minX || minX < 2 || minY < 2 || maxX >= width - 2 || maxY >= height - 2) return null;
        int left = minX - 2, top = minY - 2, outputWidth = maxX - minX + 5, outputHeight = maxY - minY + 5;
        var outputAnchor = new PixelPoint(cameraAnchor.X - left, cameraAnchor.Y - top);
        if (outputAnchor.X < 0 || outputAnchor.Y < 0 || outputAnchor.X >= outputWidth || outputAnchor.Y >= outputHeight)
            return null;
        byte[] result = new byte[outputWidth * outputHeight * 4];
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                byte opacity = mask[y * width + x];
                if (opacity == 0) continue;
                int destination = ((y - top) * outputWidth + x - left) * 4;
                Array.Copy(bgra, y * stride + x * 4, result, destination, 3);
                result[destination + 3] = opacity;
            }
        return new(outputWidth, outputHeight, result, outputAnchor, new(dx / length, dy / length))
        {
            CameraGeometry = new(width, height, cameraToBoard)
        };
    }

    private static bool Finite(PixelPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static PixelPoint? Project(PixelPoint point, double[] inverse)
    {
        double x = point.X / PhotoHandCutout.BoardPixels, y = point.Y / PhotoHandCutout.BoardPixels;
        double denominator = inverse[6] * x + inverse[7] * y + inverse[8];
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12) return null;
        var result = new PixelPoint((inverse[0] * x + inverse[1] * y + inverse[2]) / denominator,
            (inverse[3] * x + inverse[4] * y + inverse[5]) / denominator);
        return Finite(result) ? result : null;
    }
}
