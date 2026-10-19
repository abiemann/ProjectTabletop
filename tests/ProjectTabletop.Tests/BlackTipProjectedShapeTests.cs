using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BlackTipProjectedShapeTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(.7, 15)]
    public void MatchingOneSidedTurfEdgeKeepsTheArtworkVeto(double gain, double background)
    {
        const int width = 160, height = 160;
        var expected = new byte[width * height * 4];
        var camera = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            byte value = x < 80 ? (byte)35 : (byte)120;
            int pixel = y * width + x;
            expected[pixel * 4] = expected[pixel * 4 + 1] = expected[pixel * 4 + 2] = value;
            expected[pixel * 4 + 3] = 255;
            camera[pixel] = (byte)(value * gain + background);
        }
        // A fitted turf contour can resemble a strip while lacking contrast
        // on one side. Inspect the matcher independently of contour selection:
        // the complete captured-frame regression covers that integration.
        var strip = new MarkerTipObservation(new(73, 80), Math.Sqrt(1920 / Math.PI), 1920, 1)
        { Bar = new(new(73, 48), new(73, 112), new(58, 80), new(88, 80)) };
        MarkerProjectionFrame[] frames = [new(width, height, expected,
            [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1], DateTimeOffset.UnixEpoch)];
        Type type = typeof(BlackTipDetector).Assembly.GetType("ProjectTabletop.Vision.MarkerProjectionMatcher")!;
        object matcher = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public,
            null, [frames, DateTimeOffset.UnixEpoch], null)!;
        bool physical = (bool)type.GetMethod("IsPhysicalBlackBarCandidate")!.Invoke(matcher,
            [strip, camera, width, height, 1d, 1d, 0d, 0d])!;
        Assert.False(physical);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(.8, .75, 12)]
    [InlineData(1.5, 1.2, 8)]
    public void WideProjectedStripRemainsVetoedBesidePhysicalNeighbourAfterOpticalChanges(
        double blur, double gain, double background)
    {
        const int width = 320, height = 240;
        var both = Frame(true);
        var profile = BlackTipDetector.Learn(width, height, width * 4, both, new(160, 104));
        Assert.Equal(2, profile.BarCount);
        var camera = Camera(both);
        Assert.Single(BlackTipDetector.Detect(width, height, width * 4, camera, profile).Candidates);
        foreach (bool secondProjected in new[] { false, true })
        {
            var reference = new MarkerProjectionFrame(width, height, Frame(secondProjected),
                [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1], DateTimeOffset.UnixEpoch);
            var result = BlackTipDetector.Detect(width, height, width * 4, camera, profile,
                new(ProjectionFrames: [reference], FrameTime: DateTimeOffset.UnixEpoch));
            Assert.Empty(result.Candidates);
        }

        byte[] Camera(byte[] image)
        {
            using var source = Mat.FromPixelData(height, width, MatType.CV_8UC4, image);
            using var softened = new Mat();
            if (blur > 0) Cv2.GaussianBlur(source, softened, new Size(0, 0), blur);
            else source.CopyTo(softened);
            var pixels = new byte[image.Length];
            Marshal.Copy(softened.Data, pixels, 0, pixels.Length);
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                for (int channel = 0; channel < 3; channel++)
                    pixels[pixel + channel] = (byte)Math.Clamp(pixels[pixel + channel] * gain + background, 0, 255);
            return pixels;
        }

        static byte[] Frame(bool second)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool ink = x is >= 128 and <= 192 && (y is >= 95 and <= 113 || second && y is >= 127 and <= 145);
                byte value = ink ? (byte)20 : (byte)(125 + (x * 7 + y * 13) % 13);
                int pixel = (y * width + x) * 4;
                pixels[pixel] = pixels[pixel + 1] = pixels[pixel + 2] = value;
                pixels[pixel + 3] = 255;
            }
            return pixels;
        }
    }
}
