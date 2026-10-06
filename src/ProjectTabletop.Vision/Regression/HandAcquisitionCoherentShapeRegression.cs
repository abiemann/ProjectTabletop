using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionCoherentShapeRegression
{
    private const int Width = 1920, Height = 1080;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Polygon = [new(405, 100), new(1425, 100), new(1425, 890), new(405, 890)];
    private static readonly HandTrackingBounds Search = new(.8028, .8785, .0544, .048);
    private static readonly HandTrackingBounds Caption = new(.8108, .8865, .0384, .032);
    private static readonly HandTrackingBounds Plate = new(.752, .862, .156, .081);
    private static readonly HandTrackingBounds Other = new(.44, .3, .26, .08);
    private static readonly double[] Map = [1.0 / 1020, 0, -405.0 / 1020, 0, 1.0 / 790, -100.0 / 790, 0, 0, 1];

    public static void Run()
    {
        using var board = new Mat(1000, 1000, MatType.CV_8UC4, new Scalar(20, 15, 11, 255));
        Cv2.PutText(board, "SETTINGS", new(448, 353), HersheyFonts.HersheySimplex, 1.2,
            new Scalar(231, 223, 217, 255), 2, LineTypes.AntiAlias);
        using var mask = new Mat(1000, 1000, MatType.CV_8UC1, Scalar.All(0));
        Point[] chevron = [new(815, 895), new(830, 915), new(845, 895),
            new(843, 891), new(830, 907), new(817, 891)];
        Cv2.FillPoly(mask, [chevron], Scalar.All(255), LineTypes.AntiAlias);
        board.SetTo(new Scalar(231, 223, 217, 255), mask);
        byte[] generated = Bytes(board);
        var scene = new HandAcquisitionSceneImage(1000, 1000, generated, Map, [Search, Other],
            [Plate, Other], [Caption, Other]);

        // Render only generated controls. Symmetric ink spreading and the
        // native camera's softer vertical response preserve the arrow's whole
        // silhouette, but the earlier binary classifier scores it as broken.
        // These two profiles reproduce its .73/.77 global match and coherent
        // independent sectors without using a person's camera photograph.
        foreach (int thickness in new[] { 3, 4 })
        {
            using var observed = board.Clone();
            using var widened = mask.Clone();
            if (thickness > 0)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(thickness * 2 + 1, thickness * 2 + 1));
                Cv2.Dilate(mask, widened, kernel);
                observed.SetTo(new Scalar(231, 223, 217, 255), widened);
            }
            byte[] empty = Project(observed, 1.5);
            var tracker = new HandAcquisitionPresenceTracker();
            HandAcquisitionTextPatternResult? pattern = null;
            for (int frame = 0; frame < 20; frame++)
            {
                var result = Feed(tracker, empty, scene, frame * 100);
                pattern = result.TextPatterns!.Single(pattern => pattern.ControlRegion == 0);
                Require(pattern.Correlation is >= .70 and < .78 &&
                    pattern.SectorCorrelations is { Count: 6 } sectors && sectors.All(value => value >= .65) &&
                    pattern.LocalDamageCoverage == 0,
                    "The coherent arrow fixture no longer exercises the observed weak global/strong local match", result);
                Require(!pattern.LabelIntact && !pattern.ShapeCorrupted && pattern.ConfirmationFrames == 0 &&
                    !pattern.CaptionReflectanceChanged && result.Hints.Count == 0,
                    "Uniformly coherent optics manufactured broken lettering, learned a clean caption, or triggered a light", result);
                Require(result.TextPatterns!.Single(pattern => pattern.ControlRegion == 1) is
                    { LabelIntact: true, ShapeCorrupted: false, ConfirmationFrames: 0 },
                    "The uncertain arrow disturbed the independent Settings caption", result);
            }

            // Preserve the actual empty optical image while introducing fresh
            // opaque loss at the middle or across the entire chevron. Neither
            // a weak optical reference nor the coherence veto may hide fingers.
            foreach (string damage in new[] { "middle", "apex", "one-arm", "distributed-strokes", "complete" })
            foreach (bool cold in new[] { false, true })
            {
                var detection = cold ? new HandAcquisitionPresenceTracker() : tracker;
                byte[] occupied = Obstruct(empty, damage);
                var first = Feed(detection, occupied, scene, 2100);
                var second = Feed(detection, occupied, scene, 2200);
                var damaged = second.TextPatterns!.Single(pattern => pattern.ControlRegion == 0);
                Require(first.Hints.Count == 0 && first.TextPatterns!.Single(pattern => pattern.ControlRegion == 0)
                        is { ShapeCorrupted: true, ConfirmationFrames: 1 } &&
                    second.Hints.Count == 1 && damaged is { LabelIntact: false, ShapeCorrupted: true, ConfirmationFrames: 2 } &&
                    second.Hints[0].ControlCoverage >= .07 && second.Hints[0].ControlTriggerCoverage >= .07,
                    $"A real {damage} loss did not qualify two fresh frames and both 7% areas (cold={cold})",
                    new { first, second });
                Require(Feed(detection, occupied, scene, 2200).Hints.Count == 0 &&
                    Feed(detection, occupied, scene, 2300).Hints.Count == 1,
                    "A duplicate frame confirmed damage, or stationary damage lost acquisition");
                var removed = Feed(detection, empty, scene, 2400);
                Require(removed.Hints.Count == 0 && removed.TextPatterns!.Single(pattern => pattern.ControlRegion == 0)
                    is { LabelIntact: false, ShapeCorrupted: false, ConfirmationFrames: 0 },
                    "Hand removal turned the same uncertain intact arrow into a new search", removed);
                // The next independent case uses increasing source times and
                // fresh optical registration rather than a duplicate timestamp.
                tracker = new HandAcquisitionPresenceTracker();
                Feed(tracker, empty, scene, 0);
            }
            Console.WriteLine($"Coherent arrow optics passed: spreading {thickness}px, global {pattern!.Correlation:F5}, " +
                $"minimum sector {pattern.SectorCorrelations!.Min():F5}, twenty quiet frames without learning, " +
                "middle/apex/one-arm/distributed/complete cold and warm damage, fresh paired 7% areas, " +
                "duplicate rejection, stationary persistence and removal.");
        }
    }

    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels,
        HandAcquisitionSceneImage scene, int milliseconds) => tracker.Update(Width, Height, Width * 4, pixels,
            Polygon, scene, Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(milliseconds));

    private static byte[] Project(Mat board, double verticalBlur)
    {
        using var camera = new Mat(Height, Width, MatType.CV_8UC4, new Scalar(18, 18, 18, 255));
        using var resized = new Mat();
        Cv2.Resize(board, resized, new Size(1020, 790), interpolation: InterpolationFlags.Linear);
        using (var view = new Mat(camera, new Rect(405, 100, 1020, 790))) resized.CopyTo(view);
        Cv2.GaussianBlur(camera, camera, new Size(0, 0), .75, verticalBlur);
        return Bytes(camera);
    }

    private static byte[] Obstruct(byte[] empty, string damage)
    {
        var result = (byte[])empty.Clone();
        HandTrackingBounds[] patches = damage switch
        {
            "complete" => [new(.800, .874, .060, .061)],
            "apex" => [new(.824, .900, .012, .025)],
            "one-arm" => [new(.810, .883, .017, .041)],
            "distributed-strokes" => [new(.815, .884, .003, .040), new(.824, .884, .003, .040),
                new(.833, .884, .003, .040), new(.842, .884, .003, .040)],
            _ => [new(.822, .884, .020, .040)]
        };
        foreach (var bounds in patches)
        for (int y = (int)Math.Ceiling(100 + bounds.Y * 790); y < 100 + (bounds.Y + bounds.Height) * 790; y++)
        for (int x = (int)Math.Ceiling(405 + bounds.X * 1020); x < 405 + (bounds.X + bounds.Width) * 1020; x++)
        {
            int offset = (y * Width + x) * 4;
            result[offset] = 85; result[offset + 1] = 125; result[offset + 2] = 185;
        }
        return result;
    }

    private static byte[] Bytes(Mat image)
    {
        byte[] pixels = new byte[image.Rows * image.Cols * 4];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return pixels;
    }

    private static void Require(bool condition, string message, object? details = null)
    {
        if (!condition) throw new InvalidOperationException(message +
            (details is null ? "." : ": " + JsonSerializer.Serialize(details)));
    }
}
