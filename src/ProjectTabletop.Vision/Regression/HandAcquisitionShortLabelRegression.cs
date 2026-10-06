using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionShortLabelRegression
{
    public static void Run()
    {
        // Neutral lossless Win2D controls and a synthetic four-strip obstruction.
        // The short gold caption contributes under 7% glyph area; the actual
        // foreground covers most of its panel. No user camera pixels are stored.
        const int width = 3840, height = 2160;
        byte[] expected = Pixels("expected", 1000, 1000);
        byte[] empty = Pixels("empty", width, height);
        byte[] occupied = Pixels("occupied", width, height);
        HandTrackingBounds[] controls = [new(.837, .024, .126, .014), new(.372, .432, .046, .034),
            new(.582, .432, .046, .034), new(.372, .542, .046, .034), new(.582, .542, .046, .034),
            new(.377, .677, .246, .048), new(.407, .767, .186, .024)];
        HandTrackingBounds[] triggers = [new(.883030029296875, .024, .03443798828125, .014),
            new(.386337890625, .445337890625, .0174609375, .011130859375),
            new(.595380859375, .439322265625, .019375, .0191015625),
            new(.386337890625, .555337890625, .0174609375, .011130859375),
            new(.595380859375, .549322265625, .019375, .0191015625),
            new(.46806396484375, .686595703125, .0634912109375, .02683984375),
            new(.4713798828125, .7675048828125, .057904296875, .020318359375)];
        var scene = new HandAcquisitionSceneImage(1000, 1000, expected,
            [.0002828463922517498, 0, -.04306506098490942, -3.8706232721267385e-20, .0005028380306697774,
                -.04306506098490936, 0, 0, 1], controls,
            [.. controls, new(.18, .045, .64, .12), new(.18, .835, .64, .12)], triggers);
        PixelPoint[] polygon = [new(169.93344197273234, 95.58756110966185),
            new(3670.06647219658, 95.58756110966212), new(3670.06647219658, 2064.412390610577),
            new(169.93344197273234, 2064.4123906105765)];
        var epoch = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) Require(Feed(empty, 0).Hints.Count == 0, "An empty short-caption board acquired a search light.");
            var first = Feed(occupied, 100);
            var second = Feed(occupied, 200);
            var glyph = second.TextPatterns!.Single(pattern => pattern.ControlRegion == 5);
            Require(first.Hints.Count == 0 && first.TextPatterns!.Single(pattern => pattern.ControlRegion == 5).ConfirmationFrames == 1,
                "One short-caption corruption bypassed two-frame confirmation.");
            Require(glyph.ShapeCorrupted && glyph.ControlTriggerCoverage < .07 && glyph.ConfirmationFrames == 2 &&
                second.Hints.Count == 1 && second.Hints[0].ControlCoverage >= .07 && second.Hints[0].ControlTriggerCoverage >= .07 &&
                Math.Abs(second.Hints[0].Center.X - 1920) < 15 && Math.Abs(second.Hints[0].Center.Y - 1481) < 15,
                "A broad obstruction on the short gold caption trained away its own measured foreground.");
            Require(Feed(occupied, 300).Hints.Count == 1 && Feed(empty, 400).Hints.Count == 0,
                "A stationary short-caption obstruction was absorbed or left a ghost after removal.");
            HandAcquisitionPresenceResult Feed(byte[] pixels, int milliseconds)
            {
                var now = epoch.AddMilliseconds(milliseconds);
                return tracker.Update(width, height, width * 4, pixels, polygon, scene, now, now);
            }
        }
        Console.WriteLine("Short-caption acquisition regression passed: corrupted panels excluded from colour training, " +
            "independent reference bands, first-frame arrival, actual 7% foreground floors, two-frame confirmation and removal.");
    }
    private static byte[] Pixels(string kind, int width, int height)
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "short-gold-caption-" + kind + ".png"), ImreadModes.Unchanged);
        Require(!image.Empty() && image.Width == width && image.Height == height && image.Type() == MatType.CV_8UC4,
            "The short-caption fixture lost its native geometry or BGRA pixels.");
        var pixels = new byte[width * height * 4]; Marshal.Copy(image.Data, pixels, 0, pixels.Length); return pixels;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
