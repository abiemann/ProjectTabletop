using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

// A hand reaching past a nearer control from the viewer's edge (the rendered
// board's bottom) must target the farthest disturbed control and span both
// measured caption cores, so assistance lights the palm as well as the fingers.
internal static class HandAcquisitionReachingRegression
{
    private const int Size = 1000;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 29, 3, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Polygon = [new(0, 0), new(Size, 0), new(Size, Size), new(0, Size)];

    public static void Run()
    {
        // Two stacked controls: the far one sits above the near one on the board.
        var (stacked, stackedScene) = Board(
            [(new Rect(360, 250, 280, 100), "FAR", new Point(440, 317)), (new Rect(360, 550, 280, 100), "NEAR", new Point(425, 617))],
            [new(.37, .26, .26, .08), new(.37, .56, .26, .08)],
            [new(.435, .278, .115, .046), new(.42, .578, .15, .046)]);
        // A forearm enters from the viewer's edge, crosses NEAR and ends on FAR.
        byte[] reaching = Patch(stacked, new(430, 262, 150, 738), 40, 55, 100);
        var tracker = new HandAcquisitionPresenceTracker();
        Feed(tracker, reaching, stackedScene, 0);
        var result = Feed(tracker, reaching, stackedScene, 100);
        Require(result.Hints.Count == 2 && result.Hints[0].Center.Y < 400 && result.Hints[1].Center.Y > 500,
            "The far disturbed control did not become the first acquisition target", result);
        var target = result.Hints[0];
        Require(target.ValidatedCandidateBounds is { } span && Contains(span, target.Center) &&
            Contains(span, result.Hints[1].Center) && result.Hints[1].CandidateBounds is null,
            "Reaching assistance did not span the fingertip and palm controls", result);
        Require(target.IlluminationCenter.Y > target.Center.Y && target.IlluminationRadiusPixels > target.RadiusPixels &&
            target.ControlCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage &&
            target.ControlTriggerCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage,
            "Reaching assistance moved the measured target or its coverage", result);

        // The palm's control may confirm a frame after the fingertips' control;
        // its fresh evidence still places the span, but cannot start a light.
        byte[] fingertipsFirst = Patch(stacked, new(430, 262, 150, 200), 40, 55, 100);
        var lagging = new HandAcquisitionPresenceTracker();
        Feed(lagging, fingertipsFirst, stackedScene, 0);
        var lagged = Feed(lagging, reaching, stackedScene, 100);
        Require(lagged.Hints.Count == 1 && lagged.Hints[0].Center.Y < 400 &&
            lagged.Hints[0].ValidatedCandidateBounds is { } lagSpan && lagSpan.Y + lagSpan.Height > 590,
            "A palm control awaiting its second frame did not extend the reaching span", lagged);

        // Separate hands on side-by-side controls are not one reaching arm.
        var (sideBySide, sideScene) = Board(
            [(new Rect(100, 550, 280, 100), "LEFT", new Point(160, 617)), (new Rect(620, 550, 280, 100), "RIGHT", new Point(665, 617))],
            [new(.11, .56, .26, .08), new(.63, .56, .26, .08)],
            [new(.155, .578, .15, .046), new(.66, .578, .175, .046)]);
        byte[] twoHands = Patch(Patch(sideBySide, new(170, 560, 140, 440), 40, 55, 100), new(690, 560, 140, 440), 40, 55, 100);
        var separate = new HandAcquisitionPresenceTracker();
        Feed(separate, twoHands, sideScene, 0);
        var apart = Feed(separate, twoHands, sideScene, 100);
        Require(apart.Hints.Count == 2 && apart.Hints.All(hint => hint.CandidateBounds is null),
            "Side-by-side controls were merged into one reaching span", apart);

        // A single disturbed control keeps its own geometry.
        byte[] nearOnly = Patch(stacked, new(430, 562, 150, 438), 40, 55, 100);
        var single = new HandAcquisitionPresenceTracker();
        Feed(single, nearOnly, stackedScene, 0);
        var alone = Feed(single, nearOnly, stackedScene, 100);
        Require(alone.Hints.Count == 1 && alone.Hints[0].CandidateBounds is null,
            "A single disturbed control gained reaching geometry", alone);
        Console.WriteLine("Reaching hand acquisition passed: farthest control from the viewer's edge targeted first, " +
            "fingertip and palm cores spanned, measured coverage unchanged, side-by-side hands and single controls unmerged.");
    }

    private static (byte[] Empty, HandAcquisitionSceneImage Scene) Board(
        (Rect Panel, string Label, Point Origin)[] controls, HandTrackingBounds[] search, HandTrackingBounds[] triggers)
    {
        using var board = new Mat(Size, Size, MatType.CV_8UC4, new Scalar(100, 100, 100, 255));
        // A fixed reference panel away from the controls anchors the camera fit.
        Cv2.Rectangle(board, new Rect(30, 30, 300, 150), new Scalar(28, 28, 28, 255), -1);
        Cv2.PutText(board, "MENU", new Point(90, 125), HersheyFonts.HersheySimplex, 1.5,
            new Scalar(235, 235, 235, 255), 3, LineTypes.AntiAlias);
        foreach (var (panel, label, origin) in controls)
        {
            Cv2.Rectangle(board, panel, new Scalar(28, 28, 28, 255), -1);
            Cv2.PutText(board, label, origin, HersheyFonts.HersheySimplex, 1.5,
                new Scalar(235, 235, 235, 255), 3, LineTypes.AntiAlias);
        }
        byte[] empty = new byte[Size * Size * 4];
        Marshal.Copy(board.Data, empty, 0, empty.Length);
        var scene = new HandAcquisitionSceneImage(Size, Size, empty, [.001, 0, 0, 0, .001, 0, 0, 0, 1],
            search, [new(.02, .02, .34, .19)], triggers);
        return (empty, scene);
    }

    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] frame,
        HandAcquisitionSceneImage scene, int milliseconds)
    {
        var time = Epoch.AddMilliseconds(milliseconds);
        return tracker.Update(Size, Size, Size * 4, frame, Polygon, scene, time, time);
    }

    private static bool Contains(HandTrackingBounds bounds, PixelPoint point) => point.X >= bounds.X &&
        point.Y >= bounds.Y && point.X <= bounds.X + bounds.Width && point.Y <= bounds.Y + bounds.Height;

    private static byte[] Patch(byte[] source, Rect rectangle, byte b, byte g, byte r)
    {
        byte[] result = (byte[])source.Clone();
        for (int y = Math.Max(0, rectangle.Y); y < Math.Min(Size, rectangle.Bottom); y++)
        for (int x = Math.Max(0, rectangle.X); x < Math.Min(Size, rectangle.Right); x++)
        {
            int offset = (y * Size + x) * 4;
            result[offset] = b; result[offset + 1] = g; result[offset + 2] = r;
        }
        return result;
    }

    private static void Require(bool condition, string message, object? result = null)
    {
        if (!condition) throw new InvalidOperationException(message +
            (result is null ? "." : ": " + JsonSerializer.Serialize(result)));
    }
}
