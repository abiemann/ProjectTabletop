using System.IO.Compression;
using System.Text.Json;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class RedDotGalleryCameraTests
{
    [Theory]
    [InlineData("gallery-clear.bgra.gz")]
    [InlineData("gallery-clear-small-highlight.bgra.gz")]
    [InlineData("gallery-clear-pure-warm-speck.bgra.gz")]
    [InlineData("gallery-laser-on-skull.bgra.gz")]
    [InlineData("gallery-split-motion-streak.bgra.gz")]
    public void RecordedGallerySeparatesLaserFromWarmWoodAndGold(string file) =>
        VerifyCapture(file, "gallery-red-dot.json");

    [Theory]
    [InlineData("gallery-score-fringe-0021.bgra.gz", "gallery-score-fringe.json")]
    [InlineData("gallery-score-fringe-0034.bgra.gz", "gallery-score-fringe.json")]
    [InlineData("duck-faint-magenta-laser.bgra.gz", "duck-faint-magenta-laser.json")]
    [InlineData("gallery-conveyor-trim-0000.bgra.gz", "gallery-pink-highlight.json")]
    [InlineData("gallery-mirror-glint-0028.bgra.gz", "gallery-pink-highlight.json")]
    public void RecordedScoreFragmentsAreRejectedWhileFaintLaserRemainsVisible(string file, string metadata) =>
        VerifyCapture(file, metadata);

    [Theory]
    [InlineData("gallery-thin-trim-0805.bgra.gz")]
    [InlineData("gallery-thin-trim-0807.bgra.gz")]
    [InlineData("gallery-thin-trim-0898.bgra.gz")]
    public void RecordedThinBrassGlintDoesNotCompeteWithTheLaser(string file) =>
        VerifyCapture(file, "gallery-thin-trim.json");

    private static void VerifyCapture(string file, string metadata)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, metadata)));
        var root = json.RootElement;
        var fixture = root.GetProperty("fixtures").EnumerateArray()
            .Single(item => item.GetProperty("file").GetString() == file);
        using var input = File.OpenRead(Path.Combine(directory, file));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
        int stride = root.GetProperty("stride").GetInt32();
        byte[] pixels = new byte[stride * height];
        gzip.ReadExactly(pixels);
        Assert.Equal(-1, gzip.ReadByte());
        double[] map = root.GetProperty("map").Deserialize<double[]>()!;
        var result = new RedDotDetector().Detect(width, height, stride, pixels, map);
        int expectedCount = fixture.GetProperty("expectedCandidateCount").GetInt32();

        Assert.Equal(expectedCount, result.CandidateCount);
        Assert.Equal(expectedCount == 1, result.Detected);
        Assert.Equal(expectedCount > 1, result.Ambiguous);
        if (expectedCount == 1)
        {
            var expected = fixture.GetProperty("expectedCameraPosition");
            Assert.Equal(expected.GetProperty("x").GetDouble(), result.CameraPosition!.Value.X, 5);
            Assert.Equal(expected.GetProperty("y").GetDouble(), result.CameraPosition.Value.Y, 5);
        }
        else
        {
            Assert.Null(result.CameraPosition);
            Assert.Null(result.BoardX);
            Assert.Null(result.BoardY);
        }
    }
}
