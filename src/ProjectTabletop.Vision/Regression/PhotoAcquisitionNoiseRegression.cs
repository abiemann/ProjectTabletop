using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoAcquisitionNoiseRegression
{
    private const int Size = PhotoHandCutout.BoardPixels;
    // Keep fixture camera pixels unchanged while placing the carton above the controls.
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, -.25, 0, 0, 1];
    private static readonly Scalar Grey = new(115, 115, 115, 255);

    public static void Run()
    {
        foreach (int seed in new[] { 13, 47, 109 })
        {
            using Mat scene = Scene(seed);
            var target = Locate(scene, out var failure);
            Require(target is not null && failure is null && target.HasRecoveredSurface &&
                    target.Spotlight.Shape == PhotoObjectSpotlightShape.RoundedRectangle,
                "Fine camera noise beside a pale carton became a second object: " + failure);
            Require(Alpha(target!, 550, 520) == 255 && Alpha(target!, 480, 440) == 0,
                "Denoising lost the blank face or included the exterior noisy board.");

            // Real small objects must survive the same retry, including a pale
            // low-contrast subject only slightly above the segmentation threshold.
            foreach (byte value in new byte[] { 35, 137, 220 })
            {
                using Mat two = scene.Clone();
                Cv2.Rectangle(two, new Rect(230, 520, 28, 30), new Scalar(value, value, value, 255), -1);
                Require(Locate(two, out failure) is null && failure is not null,
                    "Noise rejection silently discarded a small real second object.");
            }
        }
        Console.WriteLine("Acquisition-noise regression: deterministic fine noise near a pale carton, " +
            "complete face and rectangular light; exterior transparency and small dark/pale/bright second-object rejection passed.");
    }

    private static Mat Scene(int seed)
    {
        Mat scene = new(Size, Size, MatType.CV_8UC4, Grey);
        var ink = new Scalar(35, 35, 35, 255);
        Cv2.Rectangle(scene, new Rect(362, 481, 18, 276), ink, -1);
        Cv2.Rectangle(scene, new Rect(362, 712, 235, 45), ink, -1);
        Cv2.Line(scene, new Point(380, 481), new Point(596, 481), new Scalar(103, 103, 103, 255), 3);
        Cv2.Line(scene, new Point(596, 481), new Point(596, 712), new Scalar(103, 103, 103, 255), 3);
        Cv2.Rectangle(scene, new Rect(437, 514, 91, 10), ink, -1);
        Cv2.Rectangle(scene, new Rect(440, 570, 70, 95), ink, -1);
        var random = new Random(seed);
        for (int y = 420; y < 450; y++)
            for (int x = 430; x < 510; x++)
            {
                byte value = (byte)(115 + random.Next(-24, 25));
                scene.Set(y, x, new Vec4b(value, value, value, 255));
            }
        return scene;
    }

    private static PhotoObjectTarget? Locate(Mat scene, out string? failure)
    {
        byte[] bytes = new byte[Size * Size * 4];
        Marshal.Copy(scene.Data, bytes, 0, bytes.Length);
        return PhotoObjectLocator.Locate(Size, Size, Size * 4, bytes, BoardMap, out failure);
    }
    private static byte Alpha(PhotoObjectTarget target, int x, int y) =>
        x < target.Left || y - 250 < target.Top || x >= target.Left + target.Width || y - 250 >= target.Top + target.Height
            ? (byte)0 : target.Alpha[(y - 250 - target.Top) * target.Width + x - target.Left];
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
