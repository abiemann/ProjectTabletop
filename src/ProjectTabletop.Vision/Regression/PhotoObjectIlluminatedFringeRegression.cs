using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class PhotoObjectIlluminatedFringeRegression
{
    private const int Size = PhotoHandCutout.BoardPixels;
    private static readonly double[] BoardMap = [1.0 / Size, 0, 0, 0, 1.0 / Size, 0, 0, 0, 1];
    private static readonly Rect Subject = new(375, 295, 180, 150);
    private static readonly Scalar Background = new(220, 165, 185, 255);
    private static readonly Scalar Ink = new(80, 45, 95, 255);

    public static void Run()
    {
        foreach (bool hole in new[] { false, true })
        {
            using Mat grey = new(Size, Size, MatType.CV_8UC4, new Scalar(75, 50, 55, 255));
            DrawSubject(grey, new Scalar(20, 15, 25, 255), hole);
            var target = PhotoObjectLocator.Locate(Size, Size, Size * 4, Bytes(grey), BoardMap, out var failure);
            Require(target is not null, "The fringe fixture did not acquire its original object: " + failure);
            byte[] alpha = target!.Alpha.ToArray();

            // Real camera replay shows dark and bright ringing within three
            // board pixels of a stationary edge after white light turns on.
            // The old all-margin spill gate rejects this otherwise intact body.
            using Mat lit = Illuminated(hole, fringe: true);
            Require(Observe(lit, target) == PhotoObjectTargetState.Present,
                "White-light edge ringing discarded a stationary object's intact body and edge.");
            var capture = PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, Bytes(lit), null,
                BoardMap, target, out failure);
            Require(capture is not null && target.Alpha.SequenceEqual(alpha),
                "Tolerating camera edge ringing altered the acquired silhouette: " + failure);
            for (int index = 0; index < alpha.Length; index++)
                Require(capture!.BgraPixels[index * 4 + 3] == alpha[index],
                    "The edge tolerance filled a real hole or expanded the photographed alpha.");

            foreach (int dx in new[] { -12, -8, -6, 6, 8, 12 })
            {
                using Mat moved = Illuminated(hole, fringe: true, offsetX: dx);
                Reject(moved, target, "A translated object remained eligible for capture through its old outline.");
            }
            using Mat beside = Illuminated(hole, fringe: true);
            Cv2.Rectangle(beside, new Rect(Subject.Right + 3, Subject.Top - 3, 6, Subject.Height + 6), Ink, -1);
            Cv2.Rectangle(beside, new Rect(Subject.Left - 9, Subject.Top - 3, 6, Subject.Height + 6), Ink, -1);
            Reject(beside, target, "Foreign contrast beyond the narrow optical fringe was ignored.");
            using Mat removed = new(Size, Size, MatType.CV_8UC4, Background);
            Reject(removed, target, "The empty projected light retained a removed object.");
            using Mat washedOut = new(Size, Size, MatType.CV_8UC4, Background);
            DrawFringe(washedOut, hole);
            DrawSubject(washedOut, Background, hole, background: Background);
            Reject(washedOut, target, "An edge halo alone proved a washed-out object was present.");
            if (hole)
            {
                using Mat filledHole = Illuminated(hole: false, fringe: true);
                Reject(filledHole, target, "A real opening filled with foreign contrast was ignored.");
            }
        }
        Console.WriteLine("Illuminated-edge regression: stationary dark/bright camera fringe and unchanged alpha/holes; " +
            "translated objects, nearby contrast, removal and washed-out bodies rejected.");
    }

    private static Mat Illuminated(bool hole, bool fringe, int offsetX = 0)
    {
        Mat scene = new(Size, Size, MatType.CV_8UC4, Background);
        if (fringe) DrawFringe(scene, hole, offsetX);
        DrawSubject(scene, Ink, hole, offsetX, Background);
        return scene;
    }

    private static void DrawFringe(Mat scene, bool hole, int offsetX = 0)
    {
        var bounds = new Rect(Subject.X + offsetX, Subject.Y, Subject.Width, Subject.Height);
        Cv2.Rectangle(scene, new Rect(bounds.X - 3, bounds.Y - 3, bounds.Width + 6, bounds.Height + 6),
            new Scalar(245, 190, 210, 255), -1);
        Cv2.Rectangle(scene, new Rect(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, bounds.Height + 2),
            new Scalar(175, 120, 140, 255), -1);
        if (hole)
        {
            // The opening itself remains photographed background.
            Cv2.Rectangle(scene, new Rect(bounds.X + 65, bounds.Y + 55, 50, 40), Background, -1);
        }
    }

    private static void DrawSubject(Mat scene, Scalar color, bool hole, int offsetX = 0, Scalar? background = null)
    {
        Cv2.Rectangle(scene, new Rect(Subject.X + offsetX, Subject.Y, Subject.Width, Subject.Height), color, -1);
        if (hole) Cv2.Rectangle(scene, new Rect(Subject.X + offsetX + 65, Subject.Y + 55, 50, 40),
            background ?? new Scalar(75, 50, 55, 255), -1);
    }

    private static PhotoObjectTargetState Observe(Mat scene, PhotoObjectTarget target) =>
        PhotoObjectLocator.ObserveTarget(Size, Size, Size * 4, Bytes(scene), BoardMap, target, out _);
    private static void Reject(Mat scene, PhotoObjectTarget target, string message)
    {
        Require(Observe(scene, target) == PhotoObjectTargetState.MissingOrMoved, message);
        Require(PhotoObjectExtractor.ExtractTarget(Size, Size, Size * 4, Bytes(scene), null,
                BoardMap, target, out _) is null, "A failed presence check still produced a photograph.");
    }
    private static byte[] Bytes(Mat scene)
    {
        byte[] result = new byte[Size * Size * 4];
        Marshal.Copy(scene.Data, result, 0, result.Length);
        return result;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
