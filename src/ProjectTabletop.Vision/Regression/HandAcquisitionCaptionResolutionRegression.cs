using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionCaptionResolutionRegression
{
    private const int Width = 3840, Height = 2160;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly double[] Map = [.0002828463922517498, 0, -.04306506098490942,
        -3.8706232721267385e-20, .0005028380306697774, -.04306506098490936, 0, 0, 1];
    private static readonly PixelPoint[] Polygon = [new(169.93344197273234, 95.58756110966185),
        new(3670.06647219658, 95.58756110966212), new(3670.06647219658, 2064.412390610577),
        new(169.93344197273234, 2064.4123906105765)];
    private static readonly HandTrackingBounds[] Controls = [new(.372, .403, .046, .032),
        new(.582, .403, .046, .032), new(.372, .5, .046, .032), new(.582, .5, .046, .032)];
    private static readonly HandTrackingBounds[] Triggers = [new(.386337890625, .415337890625, .0174609375, .011130859375),
        new(.595380859375, .409322265625, .019375, .0191015625),
        new(.386337890625, .512337890625, .0174609375, .011130859375),
        new(.595380859375, .506322265625, .019375, .0191015625)];

    public static void Run()
    {
        OffCameraGridResolution();
        // Lossless crops of actual Win2D +/- controls and their native 4K render.
        // The second minus falls between all valid coarse trigger samples.
        // Store only synthetic control pixels, never a photograph or full board.
        byte[] expected = Canvas(1000, 1000), empty = Canvas(Width, Height);
        Paste("compact-caption-expected.png", expected, 1000, 360, 391, 280, 153);
        Paste("compact-caption-camera.png", empty, Width, 1425, 863, 990, 305);
        var scene = new HandAcquisitionSceneImage(1000, 1000, expected, Map, Controls, Controls, Triggers);
        for (int control = 0; control < Controls.Length; control++)
        {
            var tracker = new HandAcquisitionPresenceTracker();
            byte[] fingers = Fingers(empty, control);
            var first = Feed(tracker, fingers, scene, 0);
            Require(first.Hints.Count == 0 && first.TextPatterns![control] is { ShapeCorrupted: true, ConfirmationFrames: 1 },
                $"Native compact caption {control} did not start with exactly one fresh confirmation: " + System.Text.Json.JsonSerializer.Serialize(first));
            CheckSampling(tracker, scene);
            var second = Feed(tracker, fingers, scene, 100);
            Require(second.Hints.Count == 1 && second.Hints[0].ControlCoverage >= .07 &&
                second.Hints[0].ControlTriggerCoverage >= .07 && second.TextPatterns![control].ConfirmationFrames == 2,
                "Resolved native samples did not meet both actual 7% floors after two fresh frames.");
            Require(second.TextPatterns!.Where(pattern => pattern.ControlRegion != control)
                .All(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted && pattern.ConfirmationFrames == 0),
                "A compact-caption obstruction disturbed an intact neighbouring control.");
            Require(Feed(tracker, fingers, scene, 100).Hints.Count == 0 &&
                Feed(tracker, fingers, scene, 200).Hints.Count == 1 && Feed(tracker, empty, scene, 300).Hints.Count == 0,
                "Refined sampling changed repeated-frame rejection, stationary persistence or removal.");
        }

        foreach (bool colour in new[] { false, true })
        {
            byte[] tint = (byte[])empty.Clone();
            var top = CameraPoint(.372, .5); var bottom = CameraPoint(.418, .532);
            for (int y = (int)Math.Ceiling(top.Y); y < bottom.Y; y++)
            for (int x = (int)Math.Ceiling(top.X); x < bottom.X; x++)
            {
                int offset = (y * Width + x) * 4;
                tint[offset] = (byte)(tint[offset] * .75 + (colour ? 15 : 4));
                tint[offset + 1] = (byte)(tint[offset + 1] * .75 + 4);
                tint[offset + 2] = (byte)(tint[offset + 2] * .75 + (colour ? 25 : 4));
            }
            var tracker = new HandAcquisitionPresenceTracker();
            Feed(tracker, Fingers(empty, 2), scene, 0);
            var clear = Feed(tracker, tint, scene, 100);
            Require(clear.Hints.Count == 0 && clear.TextPatterns![2] is
                { LabelIntact: true, ShapeCorrupted: false, ConfirmationFrames: 0 },
                "Intact recoloured or darker lettering failed to cancel compact-caption confirmation.");
            Require(Feed(tracker, Fingers(empty, 2), scene, 200).TextPatterns![2].ConfirmationFrames == 1,
                "Cleared lettering left a previous partial confirmation armed.");
        }

        var transition = new HandAcquisitionPresenceTracker();
        byte[] occupied = Fingers(empty, 2);
        Feed(transition, occupied, scene, 0);
        double refinedScale = Field<double>(transition, "_scale");
        var standard = scene with { BoardTriggerRegions = Controls };
        Feed(transition, empty, standard, 100);
        Require(Math.Abs(Field<double>(transition, "_scale") - refinedScale * 2) < .001,
            "Already resolved controls no longer use the standard bounded density.");
        var restarted = Feed(transition, occupied, scene, 200);
        Require(restarted.Hints.Count == 0 && restarted.TextPatterns![2].ConfirmationFrames == 1,
            "Fine/coarse reference transitions replayed a prior confirmation.");
        var copied = scene with { Bgra = (byte[])expected.Clone() };
        Require(Feed(transition, occupied, copied, 300).TextPatterns![2].ConfirmationFrames == 1,
            "A changed rendered reference retained old caption evidence.");
        Require(Feed(transition, occupied, copied, 400).TextPatterns![2].ConfirmationFrames == 2,
            "An unchanged fine reference did not retain a real continuous obstruction.");
        PixelPoint[] resizedPolygon = Polygon.Select(point => new PixelPoint(point.X + 1, point.Y)).ToArray();
        Require(Feed(transition, occupied, copied, 500, resizedPolygon).TextPatterns![2].ConfirmationFrames == 1,
            "Changed projection geometry retained a stale caption confirmation.");
        Console.WriteLine("Native compact-caption resolution passed: actual 4K +/- controls, bounded native footprints, " +
            "two fresh frames, both 7% floors, intact tint/dimming cancellation, stationary persistence and reference/geometry resets.");
    }

    private static void OffCameraGridResolution()
    {
        // A raw off-camera origin predicts a sample at126.5625. The real
        // clipped grid instead has coarse rows124.21875/128.90625; neither
        // complete footprint fits this6px caption. Its fine grid does fit.
        var bounds = new HandTrackingBounds(.18, .10, .10, .08);
        var trigger = new HandTrackingBounds(.20, .1235625, .04, .006);
        var scene = new HandAcquisitionSceneImage(1000, 1000, Canvas(1000, 1000),
            [.001, 0, 0, 0, .001, 0, 0, 0, 1], [bounds], [bounds, new(.4, .4, .2, .2)], [trigger]);
        PixelPoint[] polygon = [new(-100, -100), new(900, -100), new(900, 900), new(-100, 900)];
        var tracker = new HandAcquisitionPresenceTracker();
        tracker.Update(1000, 1000, 4000, scene.Bgra, polygon, scene, Epoch, Epoch);
        Require(Math.Abs(Field<double>(tracker, "_scale") - 900.0 / 384) < .001,
            "An off-camera polygon chose caption resolution against an unclipped grid.");
        var assigned = Field<int[]>(tracker, "_controlTriggerRegions");
        var locations = Field<PixelPoint[]>(tracker, "_locations");
        double radius = Field<double>(tracker, "_scale") * .25 + 1;
        Require(assigned.Any(region => region == 0), "The refined clipped grid still misses its caption.");
        for (int index = 0; index < assigned.Length; index++)
            if (assigned[index] == 0)
                Require(locations[index].Y - radius >= trigger.Y * 1000 &&
                    locations[index].Y + radius <= (trigger.Y + trigger.Height) * 1000,
                    "Off-camera refinement counted a footprint beyond the caption.");
    }

    private static void CheckSampling(HandAcquisitionPresenceTracker tracker, HandAcquisitionSceneImage scene)
    {
        double scale = Field<double>(tracker, "_scale"), radius = scale * .25 + 1;
        var locations = Field<PixelPoint[]>(tracker, "_locations");
        var regions = Field<int[]>(tracker, "_controlTriggerRegions");
        Require(Field<int>(tracker, "_columns") <= 384 && Field<int>(tracker, "_rows") <= 384 &&
            tracker.SampledCellCount < 2000, "Caption refinement exceeded its bounded grid or sampled the whole camera.");
        foreach (int region in Enumerable.Range(0, Triggers.Length))
        {
            int count = 0;
            for (int index = 0; index < regions.Length; index++)
            {
                if (regions[index] != region) continue;
                count++;
                foreach (int dx in new[] { -1, 1 })
                foreach (int dy in new[] { -1, 1 })
                {
                    double u = Map[0] * (locations[index].X + dx * radius) + Map[2];
                    double v = Map[4] * (locations[index].Y + dy * radius) + Map[5];
                    var bounds = scene.BoardTriggerRegions![region];
                    Require(u >= bounds.X && u <= bounds.X + bounds.Width && v >= bounds.Y && v <= bounds.Y + bounds.Height,
                        "A trigger borrowed partial sample pixels from outside its exact caption rectangle.");
                }
            }
            Require(count > 0, "A native compact caption still has no complete foreground samples.");
        }
    }

    private static byte[] Fingers(byte[] empty, int index)
    {
        byte[] pixels = (byte[])empty.Clone();
        var control = Controls[index]; var trigger = Triggers[index];
        double center = trigger.X + trigger.Width / 2;
        var top = CameraPoint(center - .019, control.Y + .001);
        var bottom = CameraPoint(center + .019, control.Y + control.Height - .001);
        int span = (int)(bottom.X - top.X);
        for (int finger = 0; finger < 4; finger++)
        for (int y = (int)top.Y; y < (int)bottom.Y; y++)
        for (int x = (int)top.X + finger * span / 4; x < (int)top.X + (finger + 1) * span / 4 - 2; x++)
        {
            int offset = (y * Width + x) * 4;
            pixels[offset] = (byte)(15 + finger * 3); pixels[offset + 1] = (byte)(20 + finger * 5);
            pixels[offset + 2] = (byte)(225 - finger * 7);
        }
        return pixels;
    }

    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels,
        HandAcquisitionSceneImage scene, int milliseconds, PixelPoint[]? polygon = null)
    {
        var time = Epoch.AddMilliseconds(milliseconds);
        return tracker.Update(Width, Height, Width * 4, pixels, polygon ?? Polygon, scene, time, time);
    }
    private static PixelPoint CameraPoint(double u, double v) => new((u - Map[2]) / Map[0], (v - Map[5]) / Map[4]);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static byte[] Canvas(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        { pixels[offset] = 18; pixels[offset + 1] = 30; pixels[offset + 2] = 20; pixels[offset + 3] = 255; }
        return pixels;
    }
    private static void Paste(string file, byte[] pixels, int strideWidth, int left, int top, int width, int height)
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file), ImreadModes.Unchanged);
        Require(image.Width == width && image.Height == height && image.Type() == MatType.CV_8UC4,
            "The generated compact-caption crop lost its native pixel geometry.");
        byte[] row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        { Marshal.Copy(image.Ptr(y), row, 0, row.Length); Array.Copy(row, 0, pixels, ((top + y) * strideWidth + left) * 4, row.Length); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
