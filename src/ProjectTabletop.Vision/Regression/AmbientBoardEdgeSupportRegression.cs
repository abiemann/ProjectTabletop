using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class AmbientBoardEdgeSupportRegression
{
    public static void Run()
    {
        const int width = 1920, height = 1080;
        Point[] board = [new(556, 94), new(1620, 100), new(1628, 934), new(554, 944)];
        foreach (bool spur in new[] { false, true })
        {
            using Mat scene = new(height, width, MatType.CV_8UC4, new Scalar(95, 95, 95, 255));
            Cv2.FillConvexPoly(scene, board, new Scalar(225, 225, 225, 255));
            if (spur)
                Cv2.Line(scene, new Point(1623, 46), new Point(1623, 105), new Scalar(145, 145, 145, 255), 5);
            byte[] pixels = new byte[width * height * 4];
            Marshal.Copy(scene.Data, pixels, 0, pixels.Length);
            var detected = BoardDetector.DetectAmbientBoard(width, height, width * 4, pixels);
            if (detected is null || Enumerable.Range(0, 4).Any(i =>
                Math.Sqrt(Math.Pow(detected.Corners[i].X - board[i].X, 2) +
                    Math.Pow(detected.Corners[i].Y - board[i].Y, 2)) > 10))
                throw new InvalidOperationException("An attached floor-edge spur displaced an ambient board corner.");
        }
        Console.WriteLine("Ambient board edge regression: clean board and attached corner spur retain all four physical edges.");
    }
}
