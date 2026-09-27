using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoPrintedSurfaceAcquisitionRegression
{
    private const int Size = PhotoHandCutout.BoardPixels;
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, 0, 0, 0, 1];
    private static readonly Scalar Grey = new(115, 115, 115, 255);
    private static readonly Scalar Ink = new(35, 35, 35, 255);
    private static readonly Point2f Center = new(479, 620);

    public static void Run()
    {
        // Procedural analogue of a pale printed carton: its face matches the
        // board, three sides make one C-shaped component, and printing forms
        // separate components. Only the faint fourth edge closes the surface.
        foreach (double angle in new[] { 0.0, 25.0, -25.0 })
        {
            using Mat scene = Scene();
            DrawPrintedBox(scene, faintClosure: true);
            using Mat rotated = Rotate(scene, angle);
            var target = Locate(rotated, out var failure);
            Require(target is not null && failure is null && target.HasRecoveredSurface,
                $"A pale printed rectangle at {angle} degrees was not recovered: {failure}");
            Require(target!.Spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
                "A recovered rectangular carton received a circular spotlight.");
            Require(target.ForegroundArea is > 56000 and < 72000,
                "Recovery kept only ink or filled excessive surrounding board.");
            foreach (Point blank in new[] { new Point(400, 520), new Point(400, 640),
                         new Point(550, 520), new Point(560, 685) })
            {
                Point sample = Transform(blank, angle);
                Require(Alpha(target, sample) == 255,
                    "A blank part of the pale carton remained transparent.");
                Require(Evidence(target, sample) == 0,
                    "Recovered blank surface was incorrectly claimed as observed contrast.");
            }
            Require(Alpha(target, Transform(new Point(325, 615), angle)) == 0 &&
                    Alpha(target, Transform(new Point(620, 615), angle)) == 0,
                "Surface recovery included exterior board pixels.");
        }

        // Rectification can turn a narrow light seam in the dark rim into a
        // small enclosed hole. It must neither veto the whole carton nor become
        // an opaque part of the recovered face.
        using Mat rimGap = Scene();
        DrawPrintedBox(rimGap, faintClosure: true);
        Cv2.Rectangle(rimGap, new Rect(368, 595, 5, 37), Grey, -1);
        var gappedTarget = Locate(rimGap, out var gapFailure);
        Require(gappedTarget is not null && gappedTarget.HasRecoveredSurface &&
                Alpha(gappedTarget, new Point(370, 613)) == 0 &&
                Evidence(gappedTarget, new Point(370, 613)) == 0 &&
                Alpha(gappedTarget, new Point(400, 613)) == 255,
            "A tiny rim hole rejected the carton or lost its transparent interior: " + gapFailure);

        using Mat open = Scene();
        DrawPrintedBox(open, faintClosure: false);
        Reject(open, "An open U/C-shaped object with separate inner marks was filled from its hull alone.");

        // A pale carton can have two faint edges, such as its top seam and the
        // side opposite its shadow. Both still need their own camera evidence.
        using Mat twoFaint = Scene();
        DrawPrintedBox(twoFaint, faintClosure: true, faintTop: true);
        var twoFaintTarget = Locate(twoFaint, out var twoFaintFailure);
        Require(twoFaintTarget is not null && twoFaintTarget.HasRecoveredSurface &&
                Alpha(twoFaintTarget, new Point(550, 520)) == 255,
            "Two supported faint carton edges were treated as separate objects: " + twoFaintFailure);
        using Mat missingFaintSide = Scene();
        DrawPrintedBox(missingFaintSide, faintClosure: false, faintTop: true);
        Reject(missingFaintSide, "A missing faint side was inferred from two strong edges and the hull.");

        using Mat outside = Scene();
        DrawPrintedBox(outside, faintClosure: true);
        Cv2.Rectangle(outside, new Rect(140, 500, 85, 115), Ink, -1);
        Reject(outside, "A separate object outside the carton was silently merged or discarded.");

        using Mat twoBoxes = Scene();
        DrawPrintedBox(twoBoxes, faintClosure: true);
        DrawPrintedBox(twoBoxes, faintClosure: true, offsetX: -270, offsetY: -160);
        Reject(twoBoxes, "Two distinct printed cartons were treated as one surface.");

        // A complete high-contrast frame already encloses a genuine background
        // hole. An unrelated object in that hole must not make it a solid face.
        using Mat frame = Scene();
        Cv2.Rectangle(frame, new Rect(360, 480, 235, 276), Ink, 14);
        Cv2.Rectangle(frame, new Rect(445, 560, 60, 95), Ink, -1);
        Reject(frame, "A closed frame and separate inner object had their real hole filled.");

        using Mat empty = Scene();
        Reject(empty, "A blank field acquired an unsupported pale surface.");

        Console.WriteLine("Printed-surface acquisition regression: faint-edge printed cartons, rotation, " +
            "complete opaque face and rectangular light; open contours, separate objects, two cartons, " +
            "two faint edges with missing-edge rejection, tiny transparent rim holes, closed-frame holes and empty-field rejection passed.");
    }

    private static Mat Scene() => new(Size, Size, MatType.CV_8UC4, Grey);

    private static void DrawPrintedBox(Mat scene, bool faintClosure, int offsetX = 0, int offsetY = 0,
        bool faintTop = false)
    {
        // Shift the original synthetic fixture away from the capture boundary so
        // rotation tests exercise closure rather than edge-clipping rejection.
        int dx = offsetX - 200, dy = offsetY - 220;
        Rect R(int x, int y, int width, int height) => new(x + dx, y + dy, width, height);
        Point P(int x, int y) => new(x + dx, y + dy);
        Cv2.Rectangle(scene, R(562, 701, 18, 276), Ink, -1);
        if (faintTop)
            Cv2.Line(scene, P(562, 701), P(796, 701), new Scalar(90, 90, 90, 255), 1);
        else
            Cv2.Rectangle(scene, R(562, 701, 235, 11), Ink, -1);
        Cv2.Rectangle(scene, R(562, 932, 232, 45), Ink, -1);
        if (faintClosure)
            Cv2.Line(scene, P(794, 712), P(794, 932), new Scalar(103, 103, 103, 255), 2);
        Cv2.Rectangle(scene, R(637, 734, 91, 10), Ink, -1);
        Cv2.Rectangle(scene, R(658, 750, 66, 10), Ink, -1);
        Cv2.Ellipse(scene, P(670, 845), new Size(22, 41), -10, 0, 360, Ink, -1);
        Cv2.Ellipse(scene, P(728, 795), new Size(28, 20), -25, 0, 360, Ink, -1);
        Cv2.Line(scene, P(675, 810), P(717, 799), Ink, 15);
        Cv2.Rectangle(scene, R(661, 900, 43, 11), Ink, -1);
    }

    private static Mat Rotate(Mat source, double angle)
    {
        using Mat rotation = Cv2.GetRotationMatrix2D(Center, angle, 1);
        Mat result = new();
        Cv2.WarpAffine(source, result, rotation, new Size(Size, Size), InterpolationFlags.Linear,
            BorderTypes.Constant, Grey);
        return result;
    }

    private static Point Transform(Point point, double angle)
    {
        double radians = angle * Math.PI / 180, x = point.X - Center.X, y = point.Y - Center.Y;
        return new((int)Math.Round(Center.X + x * Math.Cos(radians) + y * Math.Sin(radians)),
            (int)Math.Round(Center.Y - x * Math.Sin(radians) + y * Math.Cos(radians)));
    }

    private static PhotoObjectTarget? Locate(Mat photo, out string? failure)
    {
        byte[] bytes = new byte[Size * Size * 4];
        Marshal.Copy(photo.Data, bytes, 0, bytes.Length);
        return PhotoObjectLocator.Locate(Size, Size, Size * 4, bytes, BoardMap, out failure);
    }

    private static byte Alpha(PhotoObjectTarget target, Point point) =>
        Sample(target, target.Alpha, point);
    private static byte Evidence(PhotoObjectTarget target, Point point) =>
        target.ContrastEvidence is null ? (byte)0 : Sample(target, target.ContrastEvidence, point);
    private static byte Sample(PhotoObjectTarget target, IReadOnlyList<byte> values, Point point)
    {
        int x = point.X - target.Left, y = point.Y - target.Top;
        return x < 0 || y < 0 || x >= target.Width || y >= target.Height ? (byte)0 : values[y * target.Width + x];
    }

    private static void Reject(Mat scene, string message) =>
        Require(Locate(scene, out var failure) is null && failure is not null, message);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
