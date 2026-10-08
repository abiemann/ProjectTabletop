using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class FootballInputVisionTests
{
    private const int Width = 320, Height = 240;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoColouredPlayersStayIndependentWhenTheirPathsCross()
    {
        var first = Frame((80, 100, true), (240, 140, false));
        var redProfile = ColorTipDetector.Learn(Width, Height, Width * 4, first, new(80, 100));
        var blueProfile = ColorTipDetector.Learn(Width, Height, Width * 4, first, new(240, 140));
        var red = new ColorTipTracker();
        var blue = new ColorTipTracker();
        for (int index = 0; index < 12; index++)
        {
            int redX = 80 + index * 14, blueX = 240 - index * 14;
            var pixels = Frame((redX, 100, true), (blueX, 140, false));
            var time = Epoch.AddMilliseconds(index * 40);
            var redTrack = red.Update(Detect(pixels, redProfile), time, time);
            var blueTrack = blue.Update(Detect(pixels, blueProfile), time, time);
            Assert.Equal(index > 0, redTrack.Confirmed);
            Assert.Equal(index > 0, blueTrack.Confirmed);
            Assert.InRange(redTrack.Observation!.Center.X, redX - 1, redX + 1);
            Assert.InRange(blueTrack.Observation!.Center.X, blueX - 1, blueX + 1);
        }
        var hiddenTime = Epoch.AddMilliseconds(480);
        var onlyBlue = Frame((72, 140, false));
        Assert.False(red.Update(Detect(onlyBlue, redProfile), hiddenTime, hiddenTime).Confirmed);
        Assert.True(blue.Update(Detect(onlyBlue, blueProfile), hiddenTime, hiddenTime).Confirmed);
        var bothBack = Frame((234, 100, true), (72, 140, false));
        var reacquireTime = hiddenTime.AddMilliseconds(40);
        Assert.False(red.Update(Detect(bothBack, redProfile), reacquireTime, reacquireTime).Confirmed);
        reacquireTime = reacquireTime.AddMilliseconds(40);
        Assert.True(red.Update(Detect(bothBack, redProfile), reacquireTime, reacquireTime).Confirmed);
    }

    [Fact]
    public void AmbiguousOrStalePlayerCannotBecomeAPhantomKicker()
    {
        var initial = Frame((80, 100, true), (240, 140, false));
        var profile = ColorTipDetector.Learn(Width, Height, Width * 4, initial, new(80, 100));
        var tracker = new ColorTipTracker();
        tracker.Update(Detect(initial, profile), Epoch, Epoch);
        Assert.True(tracker.Update(Detect(initial, profile), Epoch.AddMilliseconds(40), Epoch.AddMilliseconds(40)).Confirmed);
        var ambiguous = Frame((68, 100, true), (92, 100, true), (240, 140, false));
        var ambiguousTrack = tracker.Update(Detect(ambiguous, profile), Epoch.AddMilliseconds(80), Epoch.AddMilliseconds(80));
        Assert.False(ambiguousTrack.Confirmed);
        Assert.Null(ambiguousTrack.Observation);
        var stale = tracker.Update(Detect(initial, profile), Epoch.AddMilliseconds(120), Epoch.AddMilliseconds(500));
        Assert.False(stale.Confirmed);
        Assert.Null(stale.Observation);
        var next = Epoch.AddMilliseconds(540);
        Assert.False(tracker.Update(Detect(initial, profile), next, next).Confirmed);
        next = next.AddMilliseconds(40);
        Assert.True(tracker.Update(Detect(initial, profile), next, next).Confirmed);
    }

    [Fact]
    public void NeutralKickerCentreSeparatesPhysicalTipFromMatchingProjectedTeamColour()
    {
        foreach (bool red in new[] { true, false })
        {
            var learning = Frame((160, 120, red));
            var profile = ColorTipDetector.Learn(Width, Height, Width * 4, learning, new(160, 120));
            var projected = Frame();
            // Model a saturated avatar surrounding a neutral central badge.
            // The neutral margin matters: colour segmentation precedes the
            // rendered-image veto, so a touching same-colour body would merge
            // with the small marker instead of remaining a separate contour.
            PaintDisk(projected, 160, 120, 31, red ? (byte)30 : (byte)210, 30, red ? (byte)210 : (byte)30);
            PaintDisk(projected, 160, 120, 14, 172, 172, 172);
            var reference = new EyeTipProjectionFrame(Width, Height, projected,
                [1.0 / Width, 0, 0, 0, 1.0 / Height, 0, 0, 0, 1], Epoch);
            var options = new ColorTipDetectionOptions(ProjectionFrames: [reference], FrameTime: Epoch);
            Assert.Empty(ColorTipDetector.Detect(Width, Height, Width * 4, projected, profile, options).Candidates);
            foreach (int offset in new[] { -3, 0, 3 })
            {
                var camera = projected.ToArray();
                PaintDisk(camera, 160 + offset, 120, 5, red ? (byte)20 : (byte)140, 20, red ? (byte)140 : (byte)20);
                var measured = ColorTipDetector.Detect(Width, Height, Width * 4, camera, profile, options);
                var tip = Assert.Single(measured.Candidates);
                Assert.InRange(tip.Center.X, 159 + offset, 161 + offset);
                Assert.InRange(tip.Center.Y, 119, 121);
            }
        }
    }

    [Fact]
    public void LearningInsideNestedColourUsesTheTipAndNeverTheSurroundingRingOrItsHole()
    {
        var original = Frame((160, 120, true));
        var originalProfile = ColorTipDetector.Learn(Width, Height, Width * 4, original, new(160, 120));
        var ring = Frame();
        PaintDisk(ring, 160, 120, 18, 30, 30, 210);
        PaintDisk(ring, 160, 120, 12, 172, 172, 172);
        Assert.Empty(Detect(ring, originalProfile).Candidates);
        Assert.Throws<InvalidOperationException>(() =>
            ColorTipDetector.Learn(Width, Height, Width * 4, ring, new(160, 120)));

        PaintDisk(ring, 160, 120, 5, 30, 30, 210);
        var nestedProfile = ColorTipDetector.Learn(Width, Height, Width * 4, ring, new(160, 120));
        Assert.InRange(nestedProfile.NormalizedArea, originalProfile.NormalizedArea * .98, originalProfile.NormalizedArea * 1.02);
        Assert.Single(Detect(ring, nestedProfile).Candidates);
    }

    private static ColorTipDetectionResult Detect(byte[] pixels, ColorTipProfile profile) =>
        ColorTipDetector.Detect(Width, Height, Width * 4, pixels, profile);

    private static byte[] Frame(params (int X, int Y, bool Red)[] tips)
    {
        var pixels = new byte[Width * Height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = pixels[index + 1] = pixels[index + 2] = 70;
            pixels[index + 3] = 255;
        }
        foreach (var tip in tips)
            PaintDisk(pixels, tip.X, tip.Y, 5, tip.Red ? (byte)30 : (byte)210, 30, tip.Red ? (byte)210 : (byte)30);
        return pixels;
    }

    private static void PaintDisk(byte[] pixels, int centerX, int centerY, int radius, byte blue, byte green, byte red)
    {
        for (int y = centerY - radius; y <= centerY + radius; y++)
            for (int x = centerX - radius; x <= centerX + radius; x++)
            {
                if ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) > radius * radius) continue;
                int offset = (y * Width + x) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
            }
    }
}
