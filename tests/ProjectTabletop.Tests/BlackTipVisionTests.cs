using System.Text.Json;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BlackTipVisionTests
{
    private const int Width = 320, Height = 240;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlackCrossbarLearnsShapeAndSurvivesRotationLightingAndResolutionChanges()
    {
        var image = Frame();
        Bar(image, 160, 100, 30, 8);
        // The uncoloured cardboard handle touches the top bar, but must not join its component.
        Fill(image, 157, 105, 6, 45, 125, 175, 200);
        var profile = Learn(image, 160, 100);
        Assert.True(profile.IsValid);
        Assert.InRange(profile.AspectRatio, 3.4, 4.2);
        Assert.Equal(profile, JsonSerializer.Deserialize<BlackTipProfile>(JsonSerializer.Serialize(profile)));
        foreach (double angle in new[] { 0, 20, 45, 70, 90, 140 })
        foreach (byte brightness in new byte[] { 9, 20, 40 })
        {
            var rotated = Frame();
            Bar(rotated, 163, 123, 30, 8, angle, brightness);
            var found = Assert.Single(Detect(rotated, profile).Candidates);
            Assert.InRange(found.Center.X, 162, 164);
            Assert.InRange(found.Center.Y, 122, 124);
        }
        var larger = Frame(640, 480);
        Bar(larger, 320, 240, 60, 16, 35, width: 640, height: 480);
        Assert.Single(BlackTipDetector.Detect(640, 480, 640 * 4, larger, profile).Candidates);
    }

    [Fact]
    public void EmptyHubRoundHolesRingsAndIncorrectSizeCannotBecomeBats()
    {
        var training = Frame();
        Bar(training, 160, 120, 30, 8);
        var profile = Learn(training, 160, 120);
        var emptyHub = Frame();
        Disk(emptyHub, 160, 120, 25, 175);
        Assert.Empty(Detect(emptyHub, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(emptyHub, 160, 120));
        var hole = emptyHub.ToArray();
        Disk(hole, 160, 120, 9, 18);
        Assert.Empty(Detect(hole, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(hole, 160, 120));
        var ring = emptyHub.ToArray();
        Bar(ring, 160, 120, 40, 16);
        Bar(ring, 160, 120, 32, 8, brightness: 175);
        Assert.Empty(Detect(ring, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(ring, 160, 120));
        var wrongSizes = Frame();
        Bar(wrongSizes, 60, 70, 12, 3);
        Bar(wrongSizes, 240, 160, 68, 18);
        Assert.Empty(Detect(wrongSizes, profile).Candidates);
        var lowContrast = Frame();
        Fill(lowContrast, 120, 90, 80, 60, 24, 24, 24);
        Bar(lowContrast, 160, 120, 30, 8);
        Assert.Empty(Detect(lowContrast, profile).Candidates);
    }

    [Fact]
    public void ProjectedBlackArtworkIsVetoedWhilePhysicalCrossbarRemainsVisible()
    {
        var training = Frame();
        Bar(training, 160, 120, 30, 8);
        var profile = Learn(training, 160, 120);
        var projected = Frame();
        Bar(projected, 70, 110, 30, 8, 20);
        Bar(projected, 240, 70, 28, 8, 90);
        Disk(projected, 160, 70, 16, 20); // Projected football panel or tyre.
        Disk(projected, 220, 170, 24, 175); // Pale centre of the rendered kicker.
        var options = Options(projected);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, projected, profile, options).Candidates);
        Assert.Throws<InvalidOperationException>(() => BlackTipDetector.Learn(Width, Height, Width * 4,
            projected, new(70, 110), new(options.ProjectionFrames, options.FrameTime)));
        foreach ((int x, int y) in new[] { (160, 125), (220, 170) })
        {
            var camera = projected.ToArray();
            Bar(camera, x, y, 30, 8, 35);
            var physical = Assert.Single(BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, options).Candidates);
            Assert.InRange(physical.Center.X, x - 1, x + 1);
            Assert.InRange(physical.Center.Y, y - 1, y + 1);
            Assert.True(BlackTipDetector.Learn(Width, Height, Width * 4, camera, new(x, y),
                new(options.ProjectionFrames, options.FrameTime)).IsValid);
        }
        var movedReference = projected.ToArray();
        Bar(movedReference, 160, 125, 30, 8);
        var newest = Options(movedReference);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, movedReference, profile, newest).Candidates);
    }

    [Fact]
    public void MissingOrStaleProjectedReferenceDisarmsInsteadOfAcceptingArtwork()
    {
        var training = Frame();
        Bar(training, 160, 120, 30, 8);
        var profile = Learn(training, 160, 120);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, training, profile,
            new(ProjectionFrames: [], FrameTime: Epoch)).Candidates);
        var expired = Options(Frame()) with { FrameTime = Epoch.AddSeconds(2) };
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, training, profile, expired).Candidates);
        Assert.Throws<InvalidOperationException>(() => BlackTipDetector.Learn(Width, Height, Width * 4,
            training, new(160, 120), new([], Epoch)));
        Assert.Throws<ArgumentException>(() => BlackTipDetector.Detect(Width, Height, Width * 4,
            training, profile, new(ProjectionFrames: Options(Frame()).ProjectionFrames)));
    }

    [Fact]
    public void IdenticalBatsStayInTheirAssignedHalvesAndOcclusionCannotSwapPlayers()
    {
        var training = Frame();
        Bar(training, 75, 120, 30, 8);
        var profile = Learn(training, 75, 120);
        var left = new ColorTipTracker();
        var right = new ColorTipTracker();
        ColorTipDetectionResult Half(ColorTipDetectionResult all, bool first) =>
            new(all.Candidates.Where(item => first ? item.Center.X < Width / 2 : item.Center.X >= Width / 2).ToArray(), all.Reason);
        for (int frame = 0; frame < 12; frame++)
        {
            var image = Frame();
            int lx = 75 + frame * 4, rx = 245 - frame * 4;
            Bar(image, lx, 100, 30, 8, frame * 7);
            Bar(image, rx, 145, 30, 8, 90 - frame * 5);
            var all = Detect(image, profile);
            Assert.Equal(2, all.Candidates.Count);
            var now = Epoch.AddMilliseconds(frame * 40);
            var first = left.Update(Half(all, true), now, now);
            var second = right.Update(Half(all, false), now, now);
            Assert.Equal(frame > 0, first.Confirmed);
            Assert.Equal(frame > 0, second.Confirmed);
            Assert.InRange(first.Observation!.Center.X, lx - 1, lx + 1);
            Assert.InRange(second.Observation!.Center.X, rx - 1, rx + 1);
        }
        var occluded = Frame();
        Bar(occluded, 200, 145, 30, 8, 35);
        var lostAt = Epoch.AddMilliseconds(480);
        var onlyRight = Detect(occluded, profile);
        Assert.False(left.Update(Half(onlyRight, true), lostAt, lostAt).Confirmed);
        Assert.True(right.Update(Half(onlyRight, false), lostAt, lostAt).Confirmed);
        Bar(occluded, 117, 100, 30, 8, 70);
        var back = Detect(occluded, profile);
        var backAt = lostAt.AddMilliseconds(40);
        Assert.False(left.Update(Half(back, true), backAt, backAt).Confirmed);
        backAt = backAt.AddMilliseconds(40);
        Assert.True(left.Update(Half(back, true), backAt, backAt).Confirmed);
        Assert.False(left.Update(Half(back, true), backAt, backAt.AddMilliseconds(400)).Confirmed);
    }

    [Fact]
    public void TwoSimilarCrossbarsInOneHalfAreAmbiguousAndNeverChosenGlobally()
    {
        var image = Frame();
        Bar(image, 90, 120, 30, 8);
        var profile = Learn(image, 90, 120);
        Bar(image, 125, 120, 30, 8);
        Bar(image, 245, 120, 30, 8);
        var candidates = Detect(image, profile).Candidates;
        Assert.Equal(3, candidates.Count);
        var left = new ColorTipDetectionResult(candidates.Where(item => item.Center.X < Width / 2).ToArray(), "left-half");
        var right = new ColorTipDetectionResult(candidates.Where(item => item.Center.X >= Width / 2).ToArray(), "right-half");
        Assert.False(new ColorTipTracker().Update(left, Epoch, Epoch).Confirmed);
        var rightTracker = new ColorTipTracker();
        rightTracker.Update(right, Epoch, Epoch);
        Assert.True(rightTracker.Update(right, Epoch.AddMilliseconds(40), Epoch.AddMilliseconds(40)).Confirmed);
    }

    [Fact]
    public void OptionalEmptyBoardBaselineRejectsAnAlreadyPresentBlackBar()
    {
        var empty = Frame();
        Bar(empty, 160, 120, 30, 8);
        Assert.Throws<InvalidOperationException>(() => BlackTipDetector.Learn(Width, Height, Width * 4,
            empty, new(160, 120), new(EmptyBgra: empty)));
        var withBat = empty.ToArray();
        Bar(withBat, 230, 160, 30, 8, 45);
        Assert.True(BlackTipDetector.Learn(Width, Height, Width * 4, withBat, new(230, 160), new(EmptyBgra: empty)).IsValid);
    }

    private static BlackTipProfile Learn(byte[] image, int x, int y) =>
        BlackTipDetector.Learn(Width, Height, Width * 4, image, new(x, y));
    private static ColorTipDetectionResult Detect(byte[] image, BlackTipProfile profile) =>
        BlackTipDetector.Detect(Width, Height, Width * 4, image, profile);
    private static ColorTipDetectionOptions Options(byte[] projected) => new(ProjectionFrames:
        [new(Width, Height, projected, [1.0 / Width, 0, 0, 0, 1.0 / Height, 0, 0, 0, 1], Epoch)], FrameTime: Epoch);

    private static byte[] Frame(int width = Width, int height = Height)
    {
        var result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            int grain = (x * 7 + y * 13) % 13;
            result[i] = (byte)(55 + grain); result[i + 1] = (byte)(112 + grain);
            result[i + 2] = (byte)(68 + grain); result[i + 3] = 255;
        }
        return result;
    }

    private static void Bar(byte[] image, int cx, int cy, int length, int thickness, double degrees = 0,
        byte brightness = 20, int width = Width, int height = Height)
    {
        double radians = degrees * Math.PI / 180, c = Math.Cos(radians), s = Math.Sin(radians);
        int extent = length + thickness;
        for (int y = Math.Max(0, cy - extent); y <= Math.Min(height - 1, cy + extent); y++)
        for (int x = Math.Max(0, cx - extent); x <= Math.Min(width - 1, cx + extent); x++)
        {
            double u = (x - cx) * c + (y - cy) * s, v = -(x - cx) * s + (y - cy) * c;
            if (Math.Abs(u) > length / 2.0 || Math.Abs(v) > thickness / 2.0) continue;
            int i = (y * width + x) * 4;
            image[i] = image[i + 1] = image[i + 2] = brightness;
        }
    }

    private static void Disk(byte[] image, int cx, int cy, int radius, byte brightness)
    {
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(Height - 1, cy + radius); y++)
        for (int x = Math.Max(0, cx - radius); x <= Math.Min(Width - 1, cx + radius); x++)
        {
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius) continue;
            int i = (y * Width + x) * 4;
            image[i] = image[i + 1] = image[i + 2] = brightness;
        }
    }

    private static void Fill(byte[] image, int left, int top, int width, int height, byte blue, byte green, byte red)
    {
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
        {
            int i = (y * Width + x) * 4;
            image[i] = blue; image[i + 1] = green; image[i + 2] = red;
        }
    }
}
