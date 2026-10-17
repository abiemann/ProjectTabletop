using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BlackTipCoreAnchorTests
{
    private const int Width = 1920, Height = 1080;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 8, 36, 22, TimeSpan.Zero);
    private static readonly BlackTipProfile Profile = new(1, .09803921568627451, .25,
        .0005173369984567901, 3.3837977865010407, 2, .45120179135363003);

    [Theory]
    [InlineData("held-1", 883, 760)]
    [InlineData("held-4", 883, 760)]
    [InlineData("held-ordinary", 883, 760)]
    [InlineData("held-shadowed-gap", 1275, 825)]
    public void FreshDarkCoreStripsAnchorMeasuredEdgesWithoutAPreviousPose(string name, double x, double y)
    {
        var pixels = Read(name);
        var detection = BlackTipDetector.Detect(Width, Height, Width * 4, pixels, Profile);
        var marker = Assert.Single(detection.Candidates);
        Assert.Contains(detection.Source, new[] { "measured-edges", "dark-core" });
        Assert.Empty(detection.AmbiguousMarkerCenters);
        Assert.InRange(marker.Center.X, x - 22, x + 22);
        Assert.InRange(marker.Center.Y, y - 15, y + 15);
        Assert.NotNull(marker.Bar);

        // A newly usable core is only an anchor; every measured strip still has
        // to be physically distinct from the current projected scene.
        double[] map = [1.0 / Width, 0, 0, 0, 1.0 / Height, 0, 0, 0, 1];
        var artwork = new MarkerTipDetectionOptions(ProjectionFrames:
            [new(Width, Height, pixels, map, Epoch)], FrameTime: Epoch);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, pixels, Profile, artwork).Candidates);
    }

    [Fact]
    public void DarkPebblesWithoutTheMarkerDoNotSupplyARecoveredPair()
    {
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, Read("clear"), Profile).Candidates);
    }

    [Theory]
    [InlineData(180, 25, 61, 19, 28)]
    [InlineData(150, 12, 41, 13, 18)]
    [InlineData(220, 35, 51, 15, 22)]
    [InlineData(220, 35, 81, 25, 38)]
    public void GuidedProfileToleratesSpreadOutGapShadeWithoutAcceptingFlecksOrArtwork(
        byte background, byte ink, int length, int thickness, int separation)
    {
        const int width = 320, height = 240;
        var neutral = new byte[width * height * 4];
        Paint(neutral, 0, 0, width, height, background);
        var training = neutral.ToArray();
        int left = 160 - (length - 1) / 2;
        int top = 120 - separation / 2 - (thickness - 1) / 2;
        int gapTop = top + thickness, gapHeight = separation - thickness;
        Paint(training, left, top, length, thickness, ink);
        Paint(training, left, top + separation, length, thickness, ink);
        PixelPoint[] target = [new(90, 50), new(230, 50), new(230, 190), new(90, 190)];
        double[] map = [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1];
        var session = new MarkerSetupSession();
        for (int frame = 0; frame <= 20; frame++)
        {
            var time = Epoch.AddMilliseconds(frame * 50);
            var sample = BlackTipDetector.LearnPairInTarget(width, height, width * 4, training,
                target, new([new(width, height, neutral, map, time)], time));
            Assert.NotNull(sample.Profile);
            session.ObserveLearning(sample, time, time);
        }
        Assert.Equal(MarkerSetupStage.Validating, session.Stage);
        var profile = Assert.IsType<BlackTipProfile>(session.CandidateProfile);
        Assert.Equal(2, profile.BarCount);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, neutral, profile).Candidates);
        Assert.Null(BlackTipDetector.LearnPairInTarget(width, height, width * 4, training,
            target, new([new(width, height, training, map, Epoch)], Epoch)).Profile);
        int step = (length - 1) / 10, patchWidth = step - 1;
        foreach (int[] clearOffsets in new[] { new[] { -2, -1, 3 }, new[] { -2, -1, 0 }, new[] { -1 }, Array.Empty<int>() })
        {
            var image = training.ToArray();
            // These projected dark patches connect both ordinary threshold masks.
            // The physical bars remain darker and retain their learned geometry.
            Paint(image, left, gapTop, length, gapHeight, 40);
            foreach (int offset in clearOffsets)
                Paint(image, 160 + offset * step - patchWidth / 2, gapTop, patchWidth, gapHeight, background);
            var result = BlackTipDetector.Detect(width, height, width * 4, image, profile);
            if (clearOffsets.Length == 3 && clearOffsets[^1] == 3)
            {
                Assert.Single(result.Candidates);
                var projected = new MarkerTipDetectionOptions(ProjectionFrames:
                    [new(width, height, image, map, Epoch)], FrameTime: Epoch);
                Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, image, profile, projected).Candidates);
            }
            else
                Assert.Empty(result.Candidates);
        }

        static void Paint(byte[] image, int left, int top, int w, int h, byte value)
        {
            for (int y = top; y < top + h; y++)
            for (int x = left; x < left + w; x++)
            {
                int at = (y * width + x) * 4;
                image[at] = image[at + 1] = image[at + 2] = value;
                image[at + 3] = 255;
            }
        }
    }

    private static byte[] Read(string name)
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "black-bars-core-anchor-" + name + ".png"), ImreadModes.Unchanged);
        Assert.Equal(MatType.CV_8UC4, image.Type());
        Assert.Equal(Width, image.Width);
        Assert.Equal(Height, image.Height);
        var pixels = new byte[Width * Height * 4];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return pixels;
    }
}
