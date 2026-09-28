using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionLocalContextRegression
{
    private const int Size = 1000;
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 17, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Polygon = [new(0, 0), new(Size, 0), new(Size, Size), new(0, Size)];

    public static void Run()
    {
        using var board = new Mat(Size, Size, MatType.CV_8UC4, new Scalar(100, 100, 100, 255));
        Cv2.Rectangle(board, new Rect(480, 710, 280, 100), new Scalar(28, 28, 28, 255), -1);
        Cv2.Rectangle(board, new Rect(80, 80, 290, 100), new Scalar(28, 28, 28, 255), -1);
        Cv2.PutText(board, "START", new Point(535, 777), HersheyFonts.HersheySimplex, 1.5,
            new Scalar(235, 235, 235, 255), 3, LineTypes.AntiAlias);
        Cv2.PutText(board, "EXIT", new Point(155, 145), HersheyFonts.HersheySimplex, 1.5,
            new Scalar(235, 235, 235, 255), 3, LineTypes.AntiAlias);
        // Fixed lettering in the local hand context must not enlarge geometry.
        Cv2.PutText(board, "BOARD", new Point(720, 635), HersheyFonts.HersheySimplex, 1,
            new Scalar(220, 220, 220, 255), 2, LineTypes.AntiAlias);
        byte[] empty = Pixels(board);
        var scene = new HandAcquisitionSceneImage(Size, Size, empty, [.001, 0, 0, 0, .001, 0, 0, 0, 1],
            [new(.49, .72, .26, .08), new(.09, .09, .27, .08)],
            [new(.03, .03, .37, .20)],
            [new(.535, .738, .17, .046), new(.153, .108, .13, .046)], AllowsLocalForegroundContext: true);

        byte[] hand = Patch(empty, new(563, 565, 106, 150), 78, 82, 110); // weak connected palm
        hand = Patch(hand, new(563, 700, 106, 90), 40, 55, 100); // strong caption interference
        var tracker = new HandAcquisitionPresenceTracker();
        var first = Feed(tracker, hand, scene, 0);
        Require(first.Hints.Count == 0 && tracker.LocalContextSampledCellCount == 0,
            "Unconfirmed control interference scanned context", first);
        var second = Feed(tracker, hand, scene, 100);
        Require(second.Hints.Count == 1 && second.Hints[0].CandidateBounds is { } candidate &&
            Contains(candidate, 615, 630) && Contains(candidate, 615, 775) && candidate.X > 540 &&
            candidate.X + candidate.Width < 705 && candidate.Width < 180 && candidate.Height > 180 &&
            candidate.Width <= Size * .60 && candidate.Height <= Size * .60 &&
            tracker.LocalContextSampledCellCount is > 0 and < 12000,
            "Qualified context failed to fit the connected weak palm and strong fingers", second);
        int contextCost = tracker.LocalContextSampledCellCount;
        var disabled = scene with { AllowsLocalForegroundContext = false };
        var old = new HandAcquisitionPresenceTracker();
        Feed(old, hand, disabled, 0);
        var anchored = Feed(old, hand, disabled, 100).Hints.Single();
        var fitted = second.Hints.Single();
        Require(anchored == fitted with { CandidateBounds = null } && old.LocalContextSampledCellCount == 0,
            "Context geometry changed the qualified anchor, search bounds, coverage, radius or source time");
        for (int frame = 2; frame < 6; frame++)
            Require(Feed(tracker, hand, scene, frame * 100).Hints.Single().CandidateBounds == fitted.CandidateBounds,
                "Stationary local context was absorbed or changed its fit");
        Require(Feed(tracker, empty, scene, 600).Hints.Count == 0 && tracker.LocalContextSampledCellCount == 0,
            "Removed foreground retained a candidate or scanned idle context");

        byte[] separate = Patch(hand, new(795, 560, 65, 180), 35, 50, 90);
        var separateTracker = new HandAcquisitionPresenceTracker();
        Feed(separateTracker, separate, scene, 0);
        Require(Feed(separateTracker, separate, scene, 100).Hints.Single().CandidateBounds == fitted.CandidateBounds,
            "A disconnected object enlarged the caption-connected candidate");
        byte[] weakOnly = Patch(empty, new(563, 565, 106, 145), 78, 82, 110);
        byte[] offButton = Patch(empty, new(400, 565, 90, 135), 35, 50, 90);
        byte[] panelOnly = Patch(empty, new(495, 722, 30, 60), 35, 50, 90);
        byte[] tiny = Patch(empty, new(585, 742, 8, 24), 35, 50, 90);
        byte[] drift = empty.Select((value, index) => index % 4 == 3 ? value : (byte)Math.Min(255, value + 8)).ToArray();
        foreach (var negative in new[] { empty, weakOnly, offButton, panelOnly, tiny, drift })
        {
            var quiet = new HandAcquisitionPresenceTracker();
            for (int frame = 0; frame < 3; frame++)
                Require(Feed(quiet, negative, scene, frame * 100).Hints.Count == 0 &&
                    quiet.LocalContextSampledCellCount == 0,
                    "Idle, off-button, weak, panel-only, sub-floor or global drift acquired local context");
        }
        Console.WriteLine($"Local acquisition context passed: qualified strong caption seeds only, weak connected palm, " +
            $"fixed-artwork/disconnected/idle rejection, unchanged anchor and both7% floors, disabled policy, " +
            $"stationary/removal; {contextCost} additional bounded native sample cells.");
    }

    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] frame,
        HandAcquisitionSceneImage scene, int milliseconds)
    {
        var time = Epoch.AddMilliseconds(milliseconds);
        return tracker.Update(Size, Size, Size * 4, frame, Polygon, scene, time, time);
    }
    private static bool Contains(HandTrackingBounds bounds, double x, double y) => x >= bounds.X &&
        y >= bounds.Y && x <= bounds.X + bounds.Width && y <= bounds.Y + bounds.Height;
    private static byte[] Patch(byte[] source, Rect rectangle, byte b, byte g, byte r)
    {
        byte[] result = (byte[])source.Clone();
        for (int y = rectangle.Y; y < rectangle.Bottom; y++)
        for (int x = rectangle.X; x < rectangle.Right; x++)
        {
            int offset = (y * Size + x) * 4;
            result[offset] = b; result[offset + 1] = g; result[offset + 2] = r;
        }
        return result;
    }
    private static byte[] Pixels(Mat image)
    {
        byte[] result = new byte[Size * Size * 4];
        Marshal.Copy(image.Data, result, 0, result.Length);
        return result;
    }
    private static void Require(bool condition, string message, object? result = null)
    {
        if (!condition) throw new InvalidOperationException(message +
            (result is null ? "." : ": " + JsonSerializer.Serialize(result)));
    }
}
