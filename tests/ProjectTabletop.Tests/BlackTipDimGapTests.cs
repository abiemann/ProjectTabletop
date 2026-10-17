using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BlackTipDimGapTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 16, 51, 39, TimeSpan.Zero);

    [Fact]
    public void RecordedDimFootballSurfaceSeparatesPhysicalInkButNotItsProjectedCopy()
    {
        const int width = 640, height = 360;
        var profile = new BlackTipProfile(1, .1333333333333333, .2647058823529412,
            .000523485725308642 * 1920 * 1080 / (width * height), 3.4314989988349875, 2, .4466474001132885);
        var held = Read("held");
        var clear = Read("clear");
        var result = BlackTipDetector.Detect(width, height, width * 4, held, profile);
        var marker = Assert.Single(result.Candidates);
        Assert.Equal("dark-core", result.Source);
        Assert.InRange(marker.Center.X, 212, 221);
        Assert.InRange(marker.Center.Y, 167, 177);
        Assert.Empty(result.AmbiguousMarkerCenters);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, clear, profile).Candidates);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, held, profile,
            Artwork(width, height, held)).Candidates);

        static byte[] Read(string kind)
        {
            using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                "football-dim-gap-" + kind + ".png"), ImreadModes.Unchanged);
            Assert.Equal(MatType.CV_8UC4, image.Type());
            Assert.Equal(width, image.Width);
            Assert.Equal(height, image.Height);
            var bytes = new byte[width * height * 4];
            Marshal.Copy(image.Data, bytes, 0, bytes.Length);
            return bytes;
        }
    }

    [Theory]
    [InlineData(60, 0)]
    [InlineData(70, 15)]
    [InlineData(50, 5)]
    public void DimSurfaceNeedsDistributedContrastWithBothInkStripsAndTheirOuterSurround(byte surface, byte ink)
    {
        const int width = 320, height = 240;
        var profile = new BlackTipProfile(1, 34.0 / 255, .25, 1080.0 / (width * height), 60.0 / 18, 2, 28.0 / 60);
        var image = Make(surface);
        Paint(image, 130, 97, 61, 19, ink);
        Paint(image, 130, 125, 61, 19, ink);
        Assert.Single(BlackTipDetector.Detect(width, height, width * 4, image, profile).Candidates);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, image, profile,
            Artwork(width, height, image)).Candidates);
        foreach (int angle in new[] { 30, 90 })
        {
            using var source = Mat.FromPixelData(height, width, MatType.CV_8UC4, image);
            using var rotation = Cv2.GetRotationMatrix2D(new Point2f(160, 120), angle, 1);
            using var target = new Mat();
            Cv2.WarpAffine(source, target, rotation, new Size(width, height), InterpolationFlags.Nearest,
                BorderTypes.Constant, new Scalar(surface, surface, surface, 255));
            var rotated = new byte[image.Length];
            Marshal.Copy(target.Data, rotated, 0, rotated.Length);
            Assert.Single(BlackTipDetector.Detect(width, height, width * 4, rotated, profile).Candidates);
            Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, rotated, profile,
                Artwork(width, height, rotated)).Candidates);
        }

        // The same spacing inside one larger black rectangle is not a gap merely
        // because its central stripe is a little brighter than the two ink cores.
        var split = Make(180);
        Paint(split, 130, 97, 61, 47, ink);
        Paint(split, 130, 116, 61, 9, 40);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, split, profile).Candidates);
        Paint(split, 157, 116, 5, 9, 180);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, split, profile).Candidates);

        var third = image.ToArray();
        Paint(third, 130, 69, 61, 19, ink);
        var ambiguous = BlackTipDetector.Detect(width, height, width * 4, third, profile);
        Assert.Empty(ambiguous.Candidates);
        Assert.NotEmpty(ambiguous.AmbiguousMarkerCenters);

        static byte[] Make(byte value)
        {
            var pixels = new byte[width * height * 4];
            Paint(pixels, 0, 0, width, height, value);
            return pixels;
        }
        static void Paint(byte[] pixels, int left, int top, int w, int h, byte value)
        {
            for (int y = top; y < top + h; y++)
            for (int x = left; x < left + w; x++)
            {
                int at = (y * width + x) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }
    }

    [Fact]
    public void GapContrastMustBeMeasuredAgainstTheBrighterInkStripToo()
    {
        const int width = 320, height = 240;
        var pixels = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            int x = pixel % width, y = pixel / width;
            byte value = x is >= 130 and <= 190 && y is >= 97 and <= 115 ? (byte)0 :
                x is >= 130 and <= 190 && y is >= 125 and <= 143 ? (byte)45 : (byte)60;
            int at = pixel * 4;
            pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
            pixels[at + 3] = 255;
        }
        var profile = new BlackTipProfile(1, 34.0 / 255, .25, 1080.0 / (width * height), 60.0 / 18, 2, 28.0 / 60);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, pixels, profile).Candidates);
    }

    private static MarkerTipDetectionOptions Artwork(int width, int height, byte[] pixels) =>
        new(ProjectionFrames: [new(width, height, pixels,
            [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1], Epoch)], FrameTime: Epoch);
}
