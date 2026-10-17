using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class HandAcquisitionHintPriorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveLowerLightRetainsHighestOfTwoQualifiedAlternatives(bool reverseCamera)
    {
        const int size = 1000;
        var epoch = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "paint-compact-controls-expected.png"),
            ImreadModes.Unchanged);
        Assert.Equal(MatType.CV_8UC4, image.Type());
        Assert.Equal(size, image.Width);
        Assert.Equal(size, image.Height);
        byte[] artwork = new byte[size * size * 4];
        Marshal.Copy(image.Data, artwork, 0, artwork.Length);
        byte[] expected = new byte[artwork.Length];
        Fill(expected, 0, 0, size, size, 30, 35, 40);
        for (int stripe = 0; stripe < 8; stripe++)
            Fill(expected, 100 + stripe * 100, 80, 100, 100,
                (byte)(20 + stripe * 9), (byte)(30 + stripe * 11), (byte)(50 + stripe * 13));
        // Deliberately list the middle caption before the highest. Its larger
        // obstruction must not consume the only alternative slot beside renewal.
        int[] tops = [442, 192, 692];
        foreach (int top in tops)
            for (int row = 0; row < 51; row++)
                Array.Copy(artwork, ((892 + row) * size + 72) * 4, expected, ((top + row) * size + 432) * 4, 116 * 4);
        var captions = tops.Select(top => new HandTrackingBounds(.467, (top + 10) / 1000.0, .048, .027)).ToArray();
        var scene = new HandAcquisitionSceneImage(size, size, expected,
            reverseCamera ? [-1.0 / 999, 0, 1, 0, -1.0 / 999, 1, 0, 0, 1]
                : [1.0 / 999, 0, 0, 0, 1.0 / 999, 0, 0, 0, 1],
            captions, [new(.10, .08, .80, .10)], captions,
            BoardControlReferenceRegions: tops.Select(top => new HandTrackingBounds(.432, top / 1000.0, .116, .051)).ToArray());
        PixelPoint[] polygon = [new(0, 0), new(size, 0), new(size, size), new(0, size)];
        var center = new PixelPoint(reverseCamera ? 999 - 490 : 490, reverseCamera ? 999 - 717 : 717);
        var light = new HandAcquisitionHint(new(center.X - 140, center.Y - 140, 280, 280), center, 70, epoch,
            .1, ControlCoverage: .2, ControlTriggerCoverage: .3);
        var tracker = new HandAcquisitionPresenceTracker();
        Assert.Empty(Feed(expected, 0, lit: false).Hints);
        byte[] covered = (byte[])expected.Clone();
        Fill(covered, 420, 647, 140, 140, 220, 225, 220);
        Fill(covered, 470, 700, 40, 35, 65, 95, 150);
        for (int finger = 0; finger < 4; finger++)
            Fill(covered, 464 + finger * 12, 448, 11, 35, 75, 95, 185);
        for (int finger = 0; finger < 3; finger++)
            Fill(covered, 464 + finger * 12, 198, 10, 35, 75, 95, 185);
        var pending = Feed(covered, 300);
        Assert.True(pending.IlluminatedPresence);
        Assert.Single(pending.Hints);
        var confirmed = Feed(covered, 400);
        Assert.True(confirmed.IlluminatedPresence);
        Assert.All(confirmed.TextPatterns!.Where(pattern => pattern.ControlRegion < 2), pattern =>
        {
            Assert.True(pattern.ShapeCorrupted);
            Assert.Equal(2, pattern.ConfirmationFrames);
            Assert.True(pattern.ControlCoverage >= .07 && pattern.ControlTriggerCoverage >= .07);
        });
        Assert.Equal(2, confirmed.Hints.Count);
        Assert.Equal(center, confirmed.Hints[0].Center);
        double targetY = reverseCamera ? 999 - confirmed.Hints[1].Center.Y : confirmed.Hints[1].Center.Y;
        Assert.InRange(targetY, 192, 243);
        // Removing the two higher obstructions leaves only measured renewal.
        foreach (int top in tops.Take(2))
            for (int row = 0; row < 51; row++)
                Array.Copy(expected, ((top + row) * size + 432) * 4, covered, ((top + row) * size + 432) * 4, 116 * 4);
        Assert.Equal(center, Assert.Single(Feed(covered, 500).Hints).Center);

        HandAcquisitionPresenceResult Feed(byte[] board, int milliseconds, bool lit = true)
        {
            byte[] camera = board;
            if (reverseCamera)
            {
                camera = new byte[board.Length];
                for (int pixel = 0; pixel < size * size; pixel++)
                    Array.Copy(board, pixel * 4, camera, (size * size - 1 - pixel) * 4, 4);
            }
            var now = epoch.AddMilliseconds(milliseconds);
            return tracker.Update(size, size, size * 4, camera, polygon, scene, now, now,
                lit ? light : null, lit ? epoch : null);
        }

        static void Fill(byte[] pixels, int left, int top, int width, int height, byte blue, byte green, byte red)
        {
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            {
                int offset = (y * size + x) * 4;
                pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = 255;
            }
        }
    }
}
