using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoCopyRegression
{
    private const int Size = 500;
    private static readonly PixelPoint[] Points =
    [
        new(250, 305), new(215, 284), new(190, 263), new(166, 240), new(151, 225),
        new(215, 230), new(210, 185), new(208, 155), new(208, 133),
        new(250, 220), new(250, 168), new(249, 132), new(248, 103),
        new(277, 227), new(283, 184), new(285, 153), new(284, 130),
        new(295, 241), new(310, 210), new(318, 186), new(322, 164)
    ];
    private static readonly double[] IdentityBoard = [1.0 / Size, 0, 0, 0, 1.0 / Size, 0, 0, 0, 1];
    private static readonly HandDetection Hand = new(Points, 0.99, 0.7);

    public static void Run()
    {
        foreach (Scalar skin in new[] { new Scalar(125, 167, 209, 255), new Scalar(38, 62, 91, 255) })
        {
            using Mat scene = DrawHand(skin, true);
            byte[] source = Bytes(scene);
            PhotoHandCutout result = Require(PhotoHandExtractor.Extract(Size, Size, Size * 4,
                source, Hand, IdentityBoard), "Synthetic photographed hand was rejected.");
            CheckCutout(result, source);

            // Noncontiguous camera rows must produce exactly the same sprite.
            int stride = Size * 4 + 29;
            byte[] padded = new byte[stride * Size];
            for (int row = 0; row < Size; row++) Array.Copy(source, row * Size * 4, padded, row * stride, Size * 4);
            PhotoHandCutout paddedResult = Require(PhotoHandExtractor.Extract(Size, Size, stride,
                padded, Hand, IdentityBoard), "Padded-stride hand failed.");
            CheckCutout(paddedResult, source);
            // GrabCut's color clustering is stochastic; compare aligned coverage
            // rather than requiring the same random optimization boundary.
            int different = 0, compared = 0;
            for (int y = 95; y <= 313; y += 2)
                for (int x = 140; x <= 335; x += 2)
                {
                    compared++;
                    if (Math.Abs(AlphaAt(result, x, y) - AlphaAt(paddedResult, x, y)) > 80) different++;
                }
            if (different > compared * 0.02)
                throw new Exception("Photo Copy coverage changed substantially with camera row padding.");

            using Mat empty = new(Size, Size, MatType.CV_8UC4, new Scalar(242, 242, 242, 255));
            PhotoHandCutout withReference = Require(PhotoHandExtractor.Extract(Size, Size, Size * 4,
                source, Hand, IdentityBoard, Bytes(empty)), "Empty-white reference rejected a hand.");
            CheckCutout(withReference, source);
        }
        CheckPerspective();
        CheckRejections();
        Console.WriteLine("Photo Copy regression: real-color pixels, alpha exterior/finger gaps, " +
            "shadow/forearm removal, light/dark sample colors, padded stride, perspective direction, " +
            "empty reference and invalid/clipped/folded inputs passed.");
    }

    private static void CheckCutout(PhotoHandCutout result, byte[] source)
    {
        if (result.Width < 100 || result.Height < 200 || result.BgraPixels.Length != result.Width * result.Height * 4)
            throw new Exception("Photo Copy returned implausible dimensions.");
        for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
            {
                int i = (y * result.Width + x) * 4;
                if ((x < 2 || y < 2 || x >= result.Width - 2 || y >= result.Height - 2) && result.BgraPixels[i + 3] != 0)
                    throw new Exception("Photo Copy exterior border is not genuinely transparent.");
                if (result.BgraPixels[i + 3] == 0 && (result.BgraPixels[i] != 0 ||
                    result.BgraPixels[i + 1] != 0 || result.BgraPixels[i + 2] != 0))
                    throw new Exception("Transparent Photo Copy pixels retain background color.");
            }
        if (AlphaAt(result, 228, 165) != 0 || AlphaAt(result, 268, 165) != 0 ||
            AlphaAt(result, 250, 330) != 0)
            throw new Exception("Photo Copy retained a finger gap or forearm.");
        if (AlphaAt(result, 250, 250) < 250 || AlphaAt(result, 248, 103) < 245 ||
            AlphaAt(result, 208, 145) < 245)
            throw new Exception("Photo Copy removed the palm or a fingertip.");
        // This lies on the neutral shadow beside the middle finger.
        if (AlphaAt(result, 261, 140) > 10)
            throw new Exception("Photo Copy retained a cast shadow.");
        PixelPoint palm = Palm();
        int px = (int)Math.Round(result.PalmAnchor.X + (250 - palm.X) * 2);
        int py = (int)Math.Round(result.PalmAnchor.Y + (250 - palm.Y) * 2);
        int outputIndex = (py * result.Width + px) * 4, inputIndex = (250 * Size + 250) * 4;
        for (int channel = 0; channel < 3; channel++)
            if (Math.Abs(result.BgraPixels[outputIndex + channel] - source[inputIndex + channel]) > 3)
                throw new Exception("Photo Copy did not preserve the photographed color.");
        if (result.MiddleFingerDirection.Y > -0.99 || Math.Abs(result.MiddleFingerDirection.X) > 0.03)
            throw new Exception("Photo Copy middle-finger direction is incorrect.");
        if (!Enumerable.Range(0, result.Width * result.Height).Any(i => result.BgraPixels[i * 4 + 3] is > 0 and < 255))
            throw new Exception("Photo Copy silhouette has no antialiased alpha edge.");
    }

    private static void CheckPerspective()
    {
        using Mat scene = DrawHand(new Scalar(125, 167, 209, 255), false);
        Point2f[] corners = [new(0, 0), new(Size, 0), new(Size, Size), new(0, Size)];
        Point2f[] cameraCorners = [new(69, 23), new(469, 69), new(443, 471), new(34, 428)];
        using Mat boardToCamera = Cv2.GetPerspectiveTransform(corners, cameraCorners);
        using Mat cameraToBoard = Cv2.GetPerspectiveTransform(cameraCorners,
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        using Mat camera = new();
        Cv2.WarpPerspective(scene, camera, boardToCamera, new Size(Size, Size),
            InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(242, 242, 242, 255));
        double[] forward = Matrix(boardToCamera), inverse = Matrix(cameraToBoard);
        var perspectiveHand = new HandDetection(Points.Select(p => Transform(p, forward)).ToArray(), 0.99, 0.7);
        PhotoHandCutout result = Require(PhotoHandExtractor.Extract(Size, Size, Size * 4,
            Bytes(camera), perspectiveHand, inverse), "Perspective camera hand was rejected.");
        if (result.MiddleFingerDirection.Y > -0.99 || Math.Abs(result.MiddleFingerDirection.X) > 0.03 ||
            AlphaAt(result, 250, 250) < 245 || AlphaAt(result, 228, 165) != 0)
            throw new Exception("Photo Copy failed to rectify the camera perspective before extraction.");
    }

    private static void CheckRejections()
    {
        using Mat scene = DrawHand(new Scalar(125, 167, 209, 255), true);
        byte[] pixels = Bytes(scene);
        using Mat white = new(Size, Size, MatType.CV_8UC4, new Scalar(242, 242, 242, 255));
        if (PhotoHandExtractor.Extract(Size, Size, Size * 4, Bytes(white), Hand, IdentityBoard) is not null)
            throw new Exception("Photo Copy fabricated a hand in a blank white frame.");
        var folded = Points.ToArray(); folded[12] = new PixelPoint(251, 247);
        var nonfinite = Points.ToArray(); nonfinite[10] = new PixelPoint(double.NaN, 123);
        var offboard = Points.Select(p => new PixelPoint(p.X - 140, p.Y)).ToArray();
        foreach (HandDetection bad in new[] { new HandDetection(folded, 0.99, 0.7),
            new HandDetection(nonfinite, 0.99, 0.7), new HandDetection(offboard, 0.99, 0.7),
            new HandDetection(Points.Take(20).ToArray(), 0.99, 0.7), Hand with { Confidence = double.NaN } })
            if (PhotoHandExtractor.Extract(Size, Size, Size * 4, pixels, bad, IdentityBoard) is not null)
                throw new Exception("Photo Copy accepted invalid, folded, or clipped landmarks.");
        foreach (double[] map in new[] { new double[9], new double[8],
            new[] { double.NaN, 0, 0, 0, 1, 0, 0, 0, 1 }, new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 } })
            if (PhotoHandExtractor.Extract(Size, Size, Size * 4, pixels, Hand, map) is not null)
                throw new Exception("Photo Copy accepted an invalid calibration map.");
        ExpectArgument(() => PhotoHandExtractor.Extract(Size, Size, Size * 4 - 1, pixels, Hand, IdentityBoard));
        ExpectArgument(() => PhotoHandExtractor.Extract(Size, Size, Size * 4, pixels[..^1], Hand, IdentityBoard));
        ExpectArgument(() => PhotoHandExtractor.Extract(Size, Size, Size * 4, pixels, Hand, IdentityBoard, new byte[1]));
    }

    private static Mat DrawHand(Scalar skin, bool shadow)
    {
        Mat image = new(Size, Size, MatType.CV_8UC4, new Scalar(242, 242, 242, 255));
        DrawShape(image, new Scalar(160, 160, 160, 255), shadow ? 10 : 0, shadow ? 7 : 0);
        DrawShape(image, skin, 0, 0);
        // Fine camera-like photographic texture must survive the extraction.
        byte[] pixels = Bytes(image);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                if (pixels[i] == (byte)skin.Val0 && pixels[i + 2] == (byte)skin.Val2)
                    for (int c = 0; c < 3; c++)
                        pixels[i + c] = (byte)Math.Clamp(pixels[i + c] + ((x / 8 + y / 11) % 5) - 2, 0, 255);
            }
        Marshal.Copy(pixels, 0, image.Data, pixels.Length);
        return image;
    }

    private static void DrawShape(Mat image, Scalar color, int dx, int dy)
    {
        Point P(PixelPoint p) => new((int)p.X + dx, (int)p.Y + dy);
        Point[] palm = [new(224 + dx, 312 + dy), new(208 + dx, 280 + dy), new(186 + dx, 263 + dy),
            new(206 + dx, 226 + dy), new(250 + dx, 213 + dy), new(286 + dx, 222 + dy),
            new(303 + dx, 242 + dy), new(281 + dx, 312 + dy)];
        Cv2.FillConvexPoly(image, palm, color);
        Cv2.Rectangle(image, new Rect(233 + dx, 300 + dy, 39, 75), color, -1);
        foreach (int start in new[] { 1, 5, 9, 13, 17 })
            for (int i = start; i < start + 3; i++)
            {
                Cv2.Line(image, P(Points[i]), P(Points[i + 1]), color, 16, LineTypes.AntiAlias);
                Cv2.Circle(image, P(Points[i + 1]), 8, color, -1, LineTypes.AntiAlias);
            }
    }

    private static int AlphaAt(PhotoHandCutout cutout, double x, double y)
    {
        PixelPoint palm = Palm();
        int px = (int)Math.Round(cutout.PalmAnchor.X + (x - palm.X) * 2);
        int py = (int)Math.Round(cutout.PalmAnchor.Y + (y - palm.Y) * 2);
        return px < 0 || py < 0 || px >= cutout.Width || py >= cutout.Height ? 0 :
            cutout.BgraPixels[(py * cutout.Width + px) * 4 + 3];
    }

    private static PixelPoint Palm() => new(new[] { 0, 1, 2, 5, 9, 13, 17 }.Average(i => Points[i].X),
        new[] { 0, 1, 2, 5, 9, 13, 17 }.Average(i => Points[i].Y));
    private static PhotoHandCutout Require(PhotoHandCutout? cutout, string error) => cutout ?? throw new Exception(error);
    private static void ExpectArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Photo Copy accepted an invalid pixel buffer.");
    }
    private static byte[] Bytes(Mat image)
    {
        byte[] bytes = new byte[Size * Size * 4];
        Marshal.Copy(image.Data, bytes, 0, bytes.Length);
        return bytes;
    }
    private static double[] Matrix(Mat mat) => Enumerable.Range(0, 9).Select(i => mat.At<double>(i / 3, i % 3)).ToArray();
    private static PixelPoint Transform(PixelPoint p, double[] h)
    {
        double z = h[6] * p.X + h[7] * p.Y + h[8];
        return new((h[0] * p.X + h[1] * p.Y + h[2]) / z, (h[3] * p.X + h[4] * p.Y + h[5]) / z);
    }
}
