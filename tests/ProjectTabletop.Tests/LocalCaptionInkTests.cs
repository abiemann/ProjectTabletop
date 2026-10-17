using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class LocalCaptionInkTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 7, 5, 0, TimeSpan.Zero);

    [Fact]
    public void RecordedFingerSeamBreaksInkDespiteCoherentWholeArrow()
    {
        var fixture = new Fixture("drawer-ink");
        var tracker = new HandAcquisitionPresenceTracker();
        var board = new BoardSession();
        board.ShowWaterGarden(Epoch.AddSeconds(-2));
        for (int ms = 0; ms <= 300; ms += 100)
            Assert.True(fixture.Feed(tracker, fixture.Clear, ms).LabelIntact);
        board.ObserveHeldButtons([], Epoch, Epoch, ["water-drawer-open"]);
        for (int sample = 9; sample <= 19; sample++)
        {
            int ms = 400 + (sample - 9) * 100;
            var pattern = fixture.Feed(tracker, fixture.Pixels("held-" + sample), ms);
            Assert.True(pattern.Correlation >= .80, $"Sample {sample} no longer exercises a coherent whole-glyph match: {pattern.Correlation}");
            Assert.True(pattern.CaptionHoldEvidence, $"Sample {sample} lost its measured stroke break: {JsonSerializer.Serialize(pattern)}");
            Assert.False(pattern.LabelIntact);
            var now = Epoch.AddMilliseconds(ms);
            var activated = board.ObserveHeldButtons(["water-drawer-open"], now, now, []);
            Assert.Equal(sample == 19 ? ["water-drawer-open"] : Array.Empty<string>(), activated);
        }
        var removed = fixture.Feed(tracker, fixture.Clear, 1600);
        Assert.True(removed.LabelIntact);
        Assert.False(removed.CaptionHoldEvidence);
    }

    [Fact]
    public void AProvableStartupInkBreakNeverBecomesACleanBaseline()
    {
        var fixture = new Fixture("drawer-ink");
        byte[] held = fixture.Pixels("held-9");
        var tracker = new HandAcquisitionPresenceTracker();
        for (int ms = 0; ms <= 1200; ms += 100)
        {
            var pattern = fixture.Feed(tracker, held, ms);
            Assert.True(pattern.CaptionHoldEvidence);
            Assert.False(pattern.LabelIntact);
        }
        Assert.True(fixture.Feed(tracker, fixture.Clear, 1400).LabelIntact);
    }

    [Fact]
    public void NativeTextRasterizationDoesNotOptIntoVectorInkPolicy()
    {
        // Native ZOOM- has a coherent subpixel stroke displacement that lowers
        // two tiny-window matches. It is still intact text, not a vector symbol.
        var fixture = new Fixture("native-drawer-raster");
        var tracker = new HandAcquisitionPresenceTracker();
        for (int ms = 0; ms <= 300; ms += 100)
            Assert.All(fixture.FeedAll(tracker, fixture.Clear, ms), pattern =>
            {
                Assert.True(pattern.LabelIntact);
                Assert.False(pattern.CaptionHoldEvidence);
            });
    }

    [Fact]
    public void LegacyMetadataPreservesExistingCaptionPolicy()
    {
        var fixture = new Fixture("drawer-ink", vectorGlyphs: false);
        var tracker = new HandAcquisitionPresenceTracker();
        Assert.True(fixture.Feed(tracker, fixture.Clear, 0).LabelIntact);
        Assert.False(fixture.Feed(tracker, fixture.Pixels("held-10"), 100).CaptionHoldEvidence);
    }

    [Theory]
    [InlineData("drawer-ink")]
    [InlineData("drawer-spread")]
    public void IntactInkRemainsClearThroughColourExposureSoftnessAndSpreading(string name)
    {
        var fixture = new Fixture(name);
        foreach (string change in new[] { "clear", "dim", "bright", "clipped", "colour", "gradient", "soft", "spread" })
        {
            byte[] pixels = fixture.Altered(change);
            foreach (bool cold in new[] { false, true })
            {
                var tracker = new HandAcquisitionPresenceTracker();
                if (!cold) fixture.Feed(tracker, fixture.Clear, 0);
                for (int ms = 100; ms <= 300; ms += 100)
                {
                    var pattern = fixture.Feed(tracker, pixels, ms);
                    Assert.False(pattern.CaptionHoldEvidence, $"{name}/{change}, cold={cold}: {JsonSerializer.Serialize(pattern)}");
                }
            }
        }
    }

    private sealed class Fixture
    {
        private readonly string _name;
        private readonly int _width, _height;
        private readonly HandAcquisitionSceneImage _scene;
        private readonly PixelPoint[] _polygon;
        public byte[] Clear { get; }

        public Fixture(string name, bool vectorGlyphs = true)
        {
            _name = name;
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
            var metadata = json.RootElement;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _width = metadata.GetProperty("cameraWidth").GetInt32();
            _height = metadata.GetProperty("cameraHeight").GetInt32();
            Clear = Pixels("clear");
            _scene = new(1000, 1000, Pixels("expected"),
                metadata.GetProperty("CameraToBoard").Deserialize<double[]>(options)!,
                metadata.GetProperty("BoardSearchRegions").Deserialize<HandTrackingBounds[]>(options),
                metadata.GetProperty("BoardReferenceRegions").Deserialize<HandTrackingBounds[]>(options),
                metadata.GetProperty("BoardTriggerRegions").Deserialize<HandTrackingBounds[]>(options),
                BoardControlReferenceRegions: metadata.GetProperty("BoardControlReferenceRegions").Deserialize<HandTrackingBounds[]>(options),
                BoardVectorGlyphRegions: vectorGlyphs && metadata.TryGetProperty("BoardVectorGlyphRegions", out var vectors)
                    ? vectors.Deserialize<int[]>() : null);
            _polygon = metadata.GetProperty("SearchPolygon").Deserialize<PixelPoint[]>(options)!;
        }

        public HandAcquisitionTextPatternResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int ms)
            => Assert.Single(FeedAll(tracker, pixels, ms));

        public IReadOnlyList<HandAcquisitionTextPatternResult> FeedAll(HandAcquisitionPresenceTracker tracker, byte[] pixels, int ms)
        {
            var now = Epoch.AddMilliseconds(ms);
            return tracker.Update(_width, _height, _width * 4, pixels, _polygon, _scene, now, now).TextPatterns!;
        }

        public byte[] Pixels(string kind)
        {
            using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", _name + "-" + kind + ".png"), ImreadModes.Unchanged);
            Assert.Equal(MatType.CV_8UC4, image.Type());
            var pixels = new byte[image.Width * image.Height * 4];
            Marshal.Copy(image.Data, pixels, 0, pixels.Length);
            return pixels;
        }

        public byte[] Altered(string change)
        {
            var pixels = (byte[])Clear.Clone();
            if (change is "soft" or "spread")
            {
                using var input = Mat.FromPixelData(_height, _width, MatType.CV_8UC4, pixels);
                using var output = new Mat();
                if (change == "soft") Cv2.GaussianBlur(input, output, new Size(0, 0), 1.5);
                else
                {
                    using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
                    Cv2.Erode(input, output, kernel);
                }
                Marshal.Copy(output.Data, pixels, 0, pixels.Length);
                return pixels;
            }
            for (int at = 0; at < pixels.Length; at += 4)
            for (int channel = 0; channel < 3; channel++)
            {
                double value = pixels[at + channel];
                value = change switch
                {
                    "dim" => value * .6 + 18,
                    "bright" => value * 1.2 + 15,
                    "clipped" => value * 1.8 + 20,
                    "colour" => value + (channel == 0 ? 20 : channel == 2 ? -20 : 0),
                    "gradient" => value + (at / 4 % _width - 700) * .15,
                    _ => value
                };
                pixels[at + channel] = (byte)Math.Clamp(value, 0, 255);
            }
            return pixels;
        }
    }
}
