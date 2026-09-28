using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoObjectRegression
{
    private const int Size = 500;
    // Translate the unchanged camera fixtures into the upper capture field.
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, -.2, 0, 0, 1];
    private static readonly PixelPoint[] OpenPoints =
    [
        new(250, 305), new(215, 284), new(190, 263), new(166, 240), new(151, 225),
        new(215, 230), new(210, 185), new(208, 155), new(208, 133),
        new(250, 220), new(250, 168), new(249, 132), new(248, 103),
        new(277, 227), new(283, 184), new(285, 153), new(284, 130),
        new(295, 241), new(310, 210), new(318, 186), new(322, 164)
    ];
    private static readonly HandDetection Shutter = CreateShutter();
    private static readonly PixelPoint ObjectCenter = new(159.5, 259.5);

    public static void Run()
    {
        foreach (Scalar color in new[] { new Scalar(20, 20, 20, 255), new Scalar(120, 120, 120, 255),
            new Scalar(205, 205, 205, 255), new Scalar(45, 125, 205, 255) })
        {
            using Mat scene = Scene();
            DrawShutter(scene);
            Cv2.Rectangle(scene, new Rect(90, 185, 140, 150), color, -1);
            var cutout = Extract(scene);
            CheckAlpha(cutout);
            Require(AlphaAt(cutout, 120, 220) == 255 && AlphaAt(cutout, 210, 310) == 255,
                "A dark, neutral or colored object lost its interior.");
            int center = ((int)cutout.PalmAnchor.Y * cutout.Width + (int)cutout.PalmAnchor.X) * 4;
            Require(Math.Abs(cutout.BgraPixels[center] - color.Val0) <= 3 &&
                Math.Abs(cutout.BgraPixels[center + 1] - color.Val1) <= 3 &&
                Math.Abs(cutout.BgraPixels[center + 2] - color.Val2) <= 3,
                "The generic cutout replaced photographed colors.");
            Require(cutout.MiddleFingerDirection == new PixelPoint(0, -1), "A generic object's top did not point inward.");
            Require(cutout.Width is > 270 and < 295 && cutout.Height is > 290 and < 315,
                "The shutter hand or forearm became part of the object photograph.");
        }
        CheckHolesAndPadding();
        CheckGradientAndForearm();
        CheckPerspective();
        CheckOtherHand();
        CheckOtherHand(greyBackground: true);
        CheckRejections();
        Console.WriteLine("Photo object regression: one-hand shutter, dark/neutral/color pixels, genuine alpha/holes, " +
            "tinted gradient background, disconnected forearm exclusion, perspective, padding, " +
            "other-hand middle-finger orientation on white/grey and empty/overlap/ambiguous/edge/invalid rejection passed.");
    }

    private static void CheckHolesAndPadding()
    {
        using Mat scene = Scene();
        DrawShutter(scene);
        Cv2.Rectangle(scene, new Rect(90, 185, 140, 150), new Scalar(35, 110, 175, 255), -1);
        Cv2.Circle(scene, new Point(160, 260), 24, new Scalar(242, 242, 242, 255), -1);
        // Connected texture of different colors remains opaque; the white hole does not.
        Cv2.Rectangle(scene, new Rect(100, 200, 40, 25), new Scalar(35, 35, 35, 255), -1);
        var cutout = Extract(scene);
        CheckAlpha(cutout);
        Require(AlphaAt(cutout, 160, 260) == 0 && AlphaAt(cutout, 120, 210) == 255 &&
            AlphaAt(cutout, 200, 300) == 255, "An opening was filled or object texture became transparent.");
        byte[] source = Bytes(scene);
        int stride = Size * 4 + 37;
        byte[] padded = new byte[stride * Size];
        for (int row = 0; row < Size; row++) Array.Copy(source, row * Size * 4, padded, row * stride, Size * 4);
        var paddedCutout = PhotoObjectExtractor.Extract(Size, Size, stride, padded, Shutter, BoardMap, out var failure);
        Require(paddedCutout is not null, "Padded rows were rejected: " + failure);
        Require(cutout.Width == paddedCutout!.Width && cutout.Height == paddedCutout.Height &&
            cutout.BgraPixels.SequenceEqual(paddedCutout.BgraPixels), "Row padding changed the object photograph.");
    }

    private static void CheckGradientAndForearm()
    {
        using Mat scene = Scene(gradient: true);
        DrawShutter(scene);
        // Split a dark cuff from the hand: the forearm ray must suppress this
        // disconnected component too, rather than selecting it as another object.
        Cv2.Rectangle(scene, new Rect(410, 405, 35, 95), new Scalar(30, 30, 30, 255), -1);
        for (int y = 392; y < 405; y++)
            for (int x = 402; x < 453; x++) scene.Set(y, x, Background(x, y, true));
        Cv2.Rectangle(scene, new Rect(90, 185, 140, 150), new Scalar(70, 70, 70, 255), -1);
        var cutout = Extract(scene);
        CheckAlpha(cutout);
        Require(cutout.Width < 295 && cutout.Height < 315 && AlphaAt(cutout, 160, 260) == 255,
            "A tinted gradient or disconnected forearm contaminated the selected object.");

        using Mat empty = Scene(gradient: true);
        DrawShutter(empty);
        ExpectReject(empty, Shutter, BoardMap, "A tinted gradient with only a shutter hand produced an object.");
    }

    private static void CheckPerspective()
    {
        using Mat scene = Scene(gradient: true);
        DrawShutter(scene);
        Cv2.Rectangle(scene, new Rect(90, 185, 140, 150), new Scalar(50, 130, 200, 255), -1);
        Point2f[] corners = [new(0, 0), new(Size, 0), new(Size, Size), new(0, Size)];
        Point2f[] observed = [new(50, 20), new(470, 50), new(440, 480), new(30, 450)];
        using Mat forward = Cv2.GetPerspectiveTransform(corners, observed);
        using Mat inverse = Cv2.GetPerspectiveTransform(observed, [new(0, -.2f), new(1, -.2f), new(1, .8f), new(0, .8f)]);
        using Mat camera = new();
        Cv2.WarpPerspective(scene, camera, forward, new Size(Size, Size), InterpolationFlags.Linear,
            BorderTypes.Constant, new Scalar(242, 242, 242, 255));
        var hand = Shutter with { Landmarks = Shutter.Landmarks.Select(point => Map(point, Matrix(forward))).ToArray() };
        var result = PhotoObjectExtractor.Extract(Size, Size, Size * 4, Bytes(camera), hand, Matrix(inverse), out var failure);
        Require(result is not null, "Perspective object extraction failed: " + failure);
        CheckAlpha(result!);
        Require(result!.Width is > 270 and < 300 && result.Height is > 290 and < 320 && AlphaAt(result, 120, 220) == 255,
            "Camera perspective changed the object's rectified coverage.");
    }

    private static void CheckOtherHand(bool greyBackground = false)
    {
        using Mat scene = Scene();
        if (greyBackground)
            Cv2.Rectangle(scene, new Rect(0, 115, Size, Size - 115), new Scalar(110, 110, 110, 255), -1);
        DrawShutter(scene);
        var target = new HandDetection(OpenPoints.Select(point => new PixelPoint(point.X - 80, point.Y + 70)).ToArray(), .99, .7);
        DrawHand(scene, target, new Scalar(125, 167, 209, 255), 16, 45);
        string? failure = null;
        var result = greyBackground
            ? PhotoHandExtractor.Extract(Size, Size, Size * 4, Bytes(scene), target, BoardMap)
            : PhotoObjectExtractor.Extract(Size, Size, Size * 4, Bytes(scene), Shutter, BoardMap, out failure, [target]);
        Require(result is not null, "The existing other-hand photograph was not preserved: " + failure);
        CheckAlpha(result!);
        Require(result!.MiddleFingerDirection.Y < -.99 && result.MiddleFingerDirection.X < -.005 &&
            result.MiddleFingerDirection.X > -.04,
            "A recognized target hand lost its measured middle-finger direction.");
        // The established extractor cuts the connected arm at the wrist rather
        // than copying the forearm extending to the edge of the capture field.
        Require(result.Height < 470, "A recognized hand photograph retained its forearm.");
    }

    private static void CheckRejections()
    {
        using Mat empty = Scene();
        DrawShutter(empty);
        ExpectReject(empty, Shutter, BoardMap, "The shutter hand alone was copied.");
        using Mat bare = Scene();
        ExpectReject(bare, Shutter, BoardMap, "An empty white board produced an object.");
        using Mat bottom = Scene(); DrawShutter(bottom);
        Cv2.Rectangle(bottom, new Rect(90, 475, 100, 20), new Scalar(60, 60, 60, 255), -1);
        ExpectReject(bottom, Shutter, BoardMap, "An object in the bottom control strip was copied.");
        using Mat upper = Scene(); DrawShutter(upper);
        Cv2.Rectangle(upper, new Rect(90, 145, 100, 65), new Scalar(60, 60, 60, 255), -1);
        var upperCopy = Extract(upper);
        Require(upperCopy.BoardOrigin is { Y: > 80 and < 100 },
            "The upper capture area below the title could not be photographed.");
        using Mat ambiguous = empty.Clone();
        Cv2.Rectangle(ambiguous, new Rect(90, 185, 100, 100), new Scalar(60, 60, 60, 255), -1);
        Cv2.Rectangle(ambiguous, new Rect(240, 200, 70, 90), new Scalar(30, 140, 210, 255), -1);
        string reason = ExpectReject(ambiguous, Shutter, BoardMap, "Two substantial objects were silently reduced to one.");
        Require(reason.Contains("one object"), "Ambiguity did not provide an actionable explanation.");
        using Mat touching = empty.Clone();
        Cv2.Rectangle(touching, new Rect(300, 290, 130, 50), new Scalar(70, 70, 70, 255), -1);
        reason = ExpectReject(touching, Shutter, BoardMap, "An object touching the shutter hand was copied as a fragment.");
        Require(reason.Contains("separate"), "Shutter overlap did not explain how to retry.");
        using Mat edge = empty.Clone();
        Cv2.Rectangle(edge, new Rect(5, 190, 100, 130), new Scalar(70, 70, 70, 255), -1);
        reason = ExpectReject(edge, Shutter, BoardMap, "An object clipped by the capture boundary was copied.");
        Require(reason.Contains("edges"), "Clipping did not explain how to retry.");
        foreach (double[] map in new[] { new double[9], new double[8],
            new[] { double.NaN, 0, 0, 0, 1, 0, 0, 0, 1 } })
            ExpectReject(empty, Shutter, map, "Invalid calibration was accepted.");
        var invalidPoints = Shutter.Landmarks.ToArray(); invalidPoints[12] = new(double.NaN, 20);
        foreach (var hand in new[] { Shutter with { Landmarks = [] }, Shutter with { Landmarks = invalidPoints },
            Shutter with { Confidence = double.NaN } })
            ExpectReject(empty, hand, BoardMap, "Invalid shutter landmarks were accepted.");
        byte[] source = Bytes(empty);
        ExpectArgument(() => PhotoObjectExtractor.Extract(Size, Size, Size * 4 - 1, source, Shutter, BoardMap, out _));
        ExpectArgument(() => PhotoObjectExtractor.Extract(Size, Size, Size * 4, source[..^1], Shutter, BoardMap, out _));
    }

    private static PhotoHandCutout Extract(Mat scene)
    {
        var result = PhotoObjectExtractor.Extract(Size, Size, Size * 4, Bytes(scene), Shutter, BoardMap, out var failure);
        Require(result is not null, "A single object with a separate shutter hand was rejected: " + failure);
        return result!;
    }

    private static string ExpectReject(Mat scene, HandDetection hand, double[] map, string message)
    {
        var result = PhotoObjectExtractor.Extract(Size, Size, Size * 4, Bytes(scene), hand, map, out var failure);
        Require(result is null && !string.IsNullOrWhiteSpace(failure), message);
        return failure!;
    }

    private static void CheckAlpha(PhotoHandCutout cutout)
    {
        Require(cutout.BgraPixels.Length == cutout.Width * cutout.Height * 4, "The cutout buffer is malformed.");
        bool feather = false;
        for (int y = 0; y < cutout.Height; y++)
            for (int x = 0; x < cutout.Width; x++)
            {
                int index = (y * cutout.Width + x) * 4;
                byte alpha = cutout.BgraPixels[index + 3];
                if (x < 2 || y < 2 || x >= cutout.Width - 2 || y >= cutout.Height - 2)
                    Require(alpha == 0, "An intended exterior pixel was not genuinely transparent.");
                if (alpha == 0) Require(cutout.BgraPixels[index] == 0 && cutout.BgraPixels[index + 1] == 0 &&
                    cutout.BgraPixels[index + 2] == 0, "Transparent pixels retained background RGB.");
                if (alpha is > 0 and < 255) feather = true;
            }
        Require(feather, "The photographed object has no antialiased alpha edge.");
    }

    private static int AlphaAt(PhotoHandCutout cutout, double x, double y)
    {
        int px = (int)Math.Round(cutout.PalmAnchor.X + (x - ObjectCenter.X) * 2);
        int py = (int)Math.Round(cutout.PalmAnchor.Y + (y - ObjectCenter.Y) * 2);
        return px < 0 || py < 0 || px >= cutout.Width || py >= cutout.Height ? 0 :
            cutout.BgraPixels[(py * cutout.Width + px) * 4 + 3];
    }

    private static Mat Scene(bool gradient = false)
    {
        Mat result = new(Size, Size, MatType.CV_8UC4);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++) result.Set(y, x, Background(x, y, gradient));
        // Controls must never influence the background estimate or segmentation.
        Cv2.Rectangle(result, new Rect(0, 470, Size, 30), new Scalar(30, 65, 40, 255), -1);
        return result;
    }

    private static Vec4b Background(int x, int y, bool gradient)
    {
        if (!gradient) return new(242, 242, 242, 255);
        double change = 28.0 * x / Size + 14.0 * y / Size;
        return new((byte)(202 + change), (byte)(187 + change), (byte)(211 + change), 255);
    }

    private static HandDetection CreateShutter()
    {
        var points = OpenPoints.Select(point => new PixelPoint(315 + point.X * .45, 200 + point.Y * .45)).ToArray();
        points[4] = points[8] = new(410, 270);
        return new(points, .99, .5);
    }

    private static void DrawShutter(Mat scene) => DrawHand(scene, Shutter, new Scalar(105, 145, 190, 255), 8, 34);
    private static void DrawHand(Mat scene, HandDetection hand, Scalar color, int thickness, int armWidth)
    {
        Point P(PixelPoint point) => new((int)Math.Round(point.X), (int)Math.Round(point.Y));
        Cv2.FillConvexPoly(scene, Cv2.ConvexHull(new[] { 0, 1, 2, 5, 9, 13, 17 }.Select(index => P(hand.Landmarks[index])).ToArray()), color);
        foreach (int start in new[] { 1, 5, 9, 13, 17 })
            for (int index = start; index < start + 3; index++)
            {
                Cv2.Line(scene, P(hand.Landmarks[index]), P(hand.Landmarks[index + 1]), color, thickness, LineTypes.AntiAlias);
                Cv2.Circle(scene, P(hand.Landmarks[index + 1]), thickness / 2, color, -1, LineTypes.AntiAlias);
            }
        Cv2.Line(scene, P(hand.Landmarks[0]), new Point((int)hand.Landmarks[0].X, Size + 10), color, armWidth);
    }

    private static byte[] Bytes(Mat image)
    {
        byte[] result = new byte[Size * Size * 4];
        Marshal.Copy(image.Data, result, 0, result.Length);
        return result;
    }
    private static double[] Matrix(Mat matrix) => Enumerable.Range(0, 9).Select(index => matrix.At<double>(index / 3, index % 3)).ToArray();
    private static PixelPoint Map(PixelPoint point, double[] h)
    {
        double divisor = h[6] * point.X + h[7] * point.Y + h[8];
        return new((h[0] * point.X + h[1] * point.Y + h[2]) / divisor,
            (h[3] * point.X + h[4] * point.Y + h[5]) / divisor);
    }
    private static void ExpectArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid camera buffer arguments were accepted.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
