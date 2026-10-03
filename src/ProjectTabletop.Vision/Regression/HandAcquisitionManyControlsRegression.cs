using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

internal static class HandAcquisitionManyControlsRegression
{
    private const int Size = 1000;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly PixelPoint[] Polygon = [new(0, 0), new(Size, 0), new(Size, Size), new(0, Size)];

    public static void Run()
    {
        // Reuse actual Win2D glyph pixels. A grid of distinct control positions
        // verifies that high indices remain independent through template creation,
        // clean registration, corruption confirmation, and hint generation.
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "paint-compact-controls-expected.png"),
            ImreadModes.Unchanged);
        Require(image.Width == Size && image.Height == Size && image.Channels() == 4 && image.IsContinuous(),
            "The real caption fixture changed format.");
        byte[] original = new byte[Size * Size * 4];
        Marshal.Copy(image.Data, original, 0, original.Length);
        foreach (int count in new[] { 58, 64 })
        {
            var scene = MakeScene(original, count);
            var tracker = new HandAcquisitionPresenceTracker();
            var clean = Feed(tracker, scene.Bgra, scene, 0);
            Require(clean.BaselineReady && clean.Hints.Count == 0 && clean.TextPatterns?.Count == count &&
                clean.TextPatterns.All(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted),
                $"The {count}-control rendered board was rejected or acquired its intact captions: {clean.Reason}.");
            byte[] covered = (byte[])scene.Bgra.Clone();
            var trigger = scene.BoardTriggerRegions![count - 1];
            int left = (int)Math.Round(trigger.X * Size) - 3, top = (int)Math.Round(trigger.Y * Size) - 4;
            for (int finger = 0; finger < 4; finger++)
                Fill(covered, left + finger * 12, top, 10, 35, 75, 95, 185);
            var first = Feed(tracker, covered, scene, 100);
            var second = Feed(tracker, covered, scene, 200);
            Require(first.Hints.Count == 0 && second.TextPatterns?.Single(pattern => pattern.ControlRegion == count - 1)
                is { ShapeCorrupted: true, ConfirmationFrames: >= 2 },
                $"The final caption of {count} controls lost fresh corruption confirmation.");
            Require(second.Hints.Count == 1 && second.TextPatterns!.Where(pattern => pattern.ControlRegion != count - 1)
                .All(pattern => pattern.LabelIntact && !pattern.ShapeCorrupted),
                $"Corrupting control {count - 1} lost its hint or contaminated another caption.");
            var hint = second.Hints.Single();
            double centerX = (trigger.X + trigger.Width / 2) * Size;
            double centerY = (trigger.Y + trigger.Height / 2) * Size;
            Require(Math.Abs(hint.Center.X - centerX) < 25 && Math.Abs(hint.Center.Y - centerY) < 25 &&
                hint.ControlCoverage >= .07 && hint.ControlTriggerCoverage >= .07,
                "High-index acquisition pointed at the wrong control or bypassed coverage floors.");
            Require(Feed(tracker, scene.Bgra, scene, 300).Hints.Count == 0,
                "A removed high-index obstruction left an acquisition ghost.");
        }

        var valid = MakeScene(original, 64);
        HandTrackingBounds[] excessive = [.. valid.BoardSearchRegions!, valid.BoardSearchRegions![0]];
        HandTrackingBounds[] excessiveTriggers = [.. valid.BoardTriggerRegions!, valid.BoardTriggerRegions![0]];
        foreach (var invalid in new[]
        {
            valid with { BoardSearchRegions = excessive, BoardTriggerRegions = null },
            valid with { BoardReferenceRegions = excessive },
            valid with { BoardSearchRegions = excessive, BoardTriggerRegions = excessiveTriggers }
        })
        {
            var rejected = Feed(new(), invalid.Bgra, invalid, 0);
            Require(rejected.Reason == "invalid-rendered-scene" && !rejected.BaselineReady && rejected.Hints.Count == 0 &&
                rejected.TextPatterns is null, "An excessive region collection bypassed the bounded scene limit.");
        }
        Console.WriteLine("Dense acquisition regression: 58/64 real rendered captions, final-index corruption/removal, " +
            "unchanged neighboring captions, and excessive 65-region rejection passed.");
    }

    private static HandAcquisitionSceneImage MakeScene(byte[] source, int count)
    {
        byte[] pixels = new byte[Size * Size * 4];
        Fill(pixels, 0, 0, Size, Size, 30, 35, 40);
        var controls = new List<HandTrackingBounds>();
        var triggers = new List<HandTrackingBounds>();
        for (int index = 0; index < count; index++)
        {
            int left = 20 + index % 8 * 120, top = 40 + index / 8 * 90;
            for (int row = 0; row < 51; row++)
                Array.Copy(source, ((892 + row) * Size + 72) * 4, pixels, ((top + row) * Size + left) * 4, 116 * 4);
            controls.Add(new(left / (double)Size, top / (double)Size, .116, .051));
            triggers.Add(new((left + 35) / (double)Size, (top + 10) / (double)Size, .048, .027));
        }
        return new(Size, Size, pixels, [1.0 / 999, 0, 0, 0, 1.0 / 999, 0, 0, 0, 1],
            controls, controls, triggers);
    }

    private static HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels,
        HandAcquisitionSceneImage scene, int milliseconds) =>
        tracker.Update(Size, Size, Size * 4, pixels, Polygon, scene, Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(milliseconds));

    private static void Fill(byte[] pixels, int left, int top, int width, int height, byte blue, byte green, byte red)
    {
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
        {
            int p = (y * Size + x) * 4;
            pixels[p] = blue; pixels[p + 1] = green; pixels[p + 2] = red; pixels[p + 3] = 255;
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
