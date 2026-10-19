using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BlackTipDarkSceneTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 19, 2, 51, 40, TimeSpan.Zero);

    [Theory]
    [InlineData("held", 326, 383)]
    [InlineData("moving", 193, 190)]
    public void RecordedDarkTurfKeepsPreviouslyLearnedPairWithoutARecentPose(string name, double x, double y)
    {
        const int width = 640, height = 512;
        var profile = new BlackTipProfile(1, .2196078431372549, .3291139240506329,
            .0005107060185185185 * 1920 * 1080 / (width * height),
            3.477617017477188, 2, .4472024292961637);
        var held = Read(name, width, height);
        var clear = Read("clear", width, height);
        // The moving view crosses a goal line. Its real projected reference is
        // needed to exclude an attached pitch contour before comparing the pair.
        var result = BlackTipDetector.Detect(width, height, width * 4, held, profile,
            name == "moving" ? ReadReference("moving").Options : null);
        var marker = Assert.Single(result.Candidates);
        Assert.InRange(marker.Center.X, x - 8, x + 8);
        Assert.InRange(marker.Center.Y, y - 8, y + 8);
        Assert.Empty(result.AmbiguousMarkerCenters);
        Assert.NotNull(marker.Bar);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, clear, profile).Candidates);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, held, profile,
            Artwork(width, height, held)).Candidates);

    }

    [Theory]
    [InlineData("held", 1)]
    [InlineData("clear", 0)]
    public void ExactFullPitchUsesRecordedReferencesAndSearchAreaWithoutARecentPose(string kind, int expectedMarkers)
    {
        const int width = 1920, height = 1080;
        var profile = new BlackTipProfile(1, .2196078431372549, .3291139240506329,
            .0005107060185185185, 3.477617017477188, 2, .4472024292961637);
        var pixels = Read(kind + "-full", width, height);
        var reference = ReadReference(kind + "-full");
        Assert.NotNull(reference.SearchArea);
        // Keep the opposite half and its goal graphics in the real search area.
        // An unrelated accepted contour there must not prevent recovery here.
        var result = Assert.Single(BlackTipDetector.DetectEach(width, height, width * 4, pixels,
            [profile], reference.Options, searchArea: reference.SearchArea));
        Assert.Equal(expectedMarkers, result.Candidates.Count);
        Assert.Empty(result.AmbiguousMarkerCenters);
        if (expectedMarkers == 1)
        {
            var marker = Assert.Single(result.Candidates);
            Assert.InRange(marker.Center.X, 918, 934);
            Assert.InRange(marker.Center.Y, 895, 911);
        }
    }

    [Theory]
    [InlineData(56, 40, 5)]
    [InlineData(70, 50, 12)]
    [InlineData(85, 35, 8)]
    [InlineData(56, 30, 5)]
    [InlineData(56, 45, 24)]
    [InlineData(56, 48, 28)]
    [InlineData(56, 65, 25)]
    public void BrightTrainingRemainsUsableOnDimTurfWithoutAcceptingFalsePairs(
        byte trainingInk, byte surface, byte ink)
    {
        const int width = 320, height = 240;
        var training = Make(200);
        Paint(training, 130, 97, 61, 19, trainingInk);
        Paint(training, 130, 125, 61, 19, trainingInk);
        var profile = BlackTipDetector.Learn(width, height, width * 4, training, new(160, 106));
        Assert.Equal(2, profile.BarCount);

        var clear = Make(surface);
        Assert.Empty(Detect(clear).Candidates);
        var held = clear.ToArray();
        Paint(held, 130, 97, 61, 19, ink);
        Paint(held, 130, 125, 61, 19, ink);
        var marker = Assert.Single(Detect(held).Candidates);
        Assert.InRange(marker.Center.X, 159, 161);
        Assert.InRange(marker.Center.Y, 119, 121);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, held, profile,
            Artwork(width, height, held)).Candidates);

        var single = clear.ToArray();
        Paint(single, 130, 97, 61, 19, ink);
        Assert.Empty(Detect(single).Candidates);
        var third = held.ToArray();
        Paint(third, 130, 69, 61, 19, ink);
        var ambiguous = Detect(third);
        Assert.Empty(ambiguous.Candidates);
        Assert.NotEmpty(ambiguous.AmbiguousMarkerCenters);

        // A brighter line within one dark object is not the visible board gap
        // between two bars. Recovery must retain the gap's surround checks.
        var block = Make(180);
        Paint(block, 130, 97, 61, 47, ink);
        Paint(block, 130, 116, 61, 9, surface);
        Assert.Empty(Detect(block).Candidates);

        MarkerTipDetectionResult Detect(byte[] pixels) =>
            BlackTipDetector.Detect(width, height, width * 4, pixels, profile);
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

    private static MarkerTipDetectionOptions Artwork(int width, int height, byte[] pixels) =>
        new(ProjectionFrames: [new(width, height, pixels,
            [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1], Epoch)], FrameTime: Epoch);

    private static byte[] Read(string kind, int width, int height)
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "football-dark-turf-" + kind + ".png"), ImreadModes.Unchanged);
        Assert.Equal(MatType.CV_8UC4, image.Type());
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        var bytes = new byte[width * height * 4];
        Marshal.Copy(image.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static (MarkerTipDetectionOptions Options, PixelPoint[]? SearchArea) ReadReference(string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,
            "football-dark-turf-" + name + "-reference.json")));
        var root = document.RootElement;
        var frames = root.GetProperty("frames").EnumerateArray().Select(frame =>
        {
            using var image = Cv2.ImRead(Path.Combine(directory, frame.GetProperty("file").GetString()!),
                ImreadModes.Unchanged);
            Assert.Equal(MatType.CV_8UC4, image.Type());
            var pixels = new byte[image.Width * image.Height * 4];
            Marshal.Copy(image.Data, pixels, 0, pixels.Length);
            return new MarkerProjectionFrame(image.Width, image.Height, pixels,
                frame.GetProperty("cameraToBoard").EnumerateArray().Select(value => value.GetDouble()).ToArray(),
                frame.GetProperty("renderedAt").GetDateTimeOffset());
        }).ToArray();
        PixelPoint[]? searchArea = root.TryGetProperty("searchArea", out var area)
            ? area.Deserialize<PixelPoint[]>() : null;
        return (new(ProjectionFrames: frames, FrameTime: root.GetProperty("frameTime").GetDateTimeOffset()), searchArea);
    }
}
