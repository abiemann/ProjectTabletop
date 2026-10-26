using System.IO.Compression;
using System.Text.Json;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class MenuArrowBorderRegressionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 26, 0, 32, 48, TimeSpan.Zero);

    [Fact]
    public void RecordedTopBorderDoesNotVetoStationaryDownArrowHold()
    {
        var fixture = new Fixture();
        var tracker = new HandAcquisitionPresenceTracker();
        foreach (int milliseconds in new[] { 0, 100, 1200, 2400 })
        {
            var result = fixture.Feed(tracker, fixture.Held, milliseconds);
            AssertOnlyArrowDamaged(result);
            Assert.False(result.OutsideControlDisturbance,
                "The untouched top border must not block the held arrow: " + result.OutsideControlForegroundFraction);
        }
    }

    [Theory]
    [InlineData(.79, .75, .07, .06)] // Above the arrow, outside its approach corridor.
    [InlineData(.64, .91, .06, .07)] // To the left of the approach corridor.
    [InlineData(.94, .91, .055, .07)] // To the right of the approach corridor.
    [InlineData(.485, .26, .03, .50)] // A narrow reach through the unlabelled central gap.
    [InlineData(.82, .50, .06, .06)] // On the Water Garden artwork, away from its caption.
    public void IndependentObstructionStillVetoesRecordedArrowAndReleasesWhenRemoved(
        double x, double y, double width, double height)
    {
        var fixture = new Fixture();
        byte[] obstructed = fixture.Obstruct(new(x, y, width, height));
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) fixture.Feed(tracker, fixture.Held, 0);
            foreach (int milliseconds in new[] { 100, 1400 })
            {
                var result = fixture.Feed(tracker, obstructed, milliseconds);
                Assert.True(result.TextPatterns!.Single(pattern => pattern.ControlRegion == 7).CaptionHoldEvidence);
                Assert.True(result.OutsideControlDisturbance,
                    $"Independent obstruction at ({x}, {y}) must veto a {(warm ? "warm" : "cold")} arrow hold.");
            }
            var removed = fixture.Feed(tracker, fixture.Held, 1500);
            AssertOnlyArrowDamaged(removed);
            Assert.False(removed.OutsideControlDisturbance,
                "Removing the independent obstruction must leave only the valid arrow hold.");
        }
    }

    [Fact]
    public void RecordedRareThumbnailColourDoesNotVetoStationaryUpArrowHold()
    {
        var fixture = new Fixture("menu-up-palette");
        var tracker = new HandAcquisitionPresenceTracker();
        foreach (int milliseconds in new[] { 0, 100, 1200, 2400 })
        {
            var result = fixture.Feed(tracker, fixture.Held, milliseconds);
            AssertOnlyArrowDamaged(result, 6);
            Assert.False(result.OutsideControlDisturbance,
                "The intact orange thumbnail must not block the held UP arrow: " + result.OutsideControlForegroundFraction);
        }
    }

    [Theory]
    [InlineData(.81, .25, .06, .07, 45, 55, 85, "")]
    [InlineData(.81, .25, .06, .07, 220, 225, 230, "")]
    [InlineData(.81, .25, .06, .07, 45, 55, 85, "shaded")]
    [InlineData(.81, .25, .06, .07, 45, 55, 85, "gradient")]
    [InlineData(.81, .25, .06, .07, 0, 0, 0, "chroma")]
    [InlineData(.81, .25, .06, .07, 0, 0, 0, "tint")]
    [InlineData(.78, .27, .10, .12, 75, 95, 185, "")]
    public void IndependentThumbnailObstructionStillVetoesRecordedUpArrow(
        double x, double y, double width, double height, byte blue, byte green, byte red, string texture)
    {
        var fixture = new Fixture("menu-up-palette");
        byte[] obstructed = fixture.Obstruct(new(x, y, width, height), blue, green, red, texture);
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) fixture.Feed(tracker, fixture.Held, 0);
            foreach (int milliseconds in new[] { 100, 1400 })
            {
                var result = fixture.Feed(tracker, obstructed, milliseconds);
                Assert.True(result.TextPatterns!.Single(pattern => pattern.ControlRegion == 6).CaptionHoldEvidence);
                Assert.True(result.OutsideControlDisturbance,
                    $"An independent thumbnail obstruction must veto a {(warm ? "warm" : "cold")} UP hold.");
            }
            var removed = fixture.Feed(tracker, fixture.Held, 1500);
            AssertOnlyArrowDamaged(removed, 6);
            Assert.False(removed.OutsideControlDisturbance,
                "Removing the thumbnail obstruction must release the arrow immediately.");
        }
    }

    private static void AssertOnlyArrowDamaged(HandAcquisitionPresenceResult result, int arrowIndex = 7)
    {
        var arrow = Assert.Single(result.TextPatterns!, pattern => pattern.ControlRegion == arrowIndex);
        Assert.True(arrow.ShapeCorrupted);
        Assert.False(arrow.LabelIntact);
        Assert.True(arrow.CaptionHoldEvidence);
        Assert.All(result.TextPatterns!.Where(pattern => pattern.ControlRegion != arrowIndex), pattern =>
        {
            Assert.True(pattern.LabelIntact);
            Assert.False(pattern.CaptionHoldEvidence);
        });
    }

    private sealed class Fixture
    {
        private readonly string _name;
        private readonly int _width, _height, _stride;
        private readonly HandAcquisitionSceneImage _scene;
        private readonly PixelPoint[] _polygon;
        public byte[] Held { get; }

        public Fixture(string name = "menu-down-border")
        {
            _name = name;
            using var json = JsonDocument.Parse(File.ReadAllText(PathFor(".json")));
            var root = json.RootElement;
            T Read<T>(string name) => root.GetProperty(name).Deserialize<T>()!;
            _width = Read<int>("cameraWidth"); _height = Read<int>("cameraHeight"); _stride = Read<int>("Stride");
            _polygon = Read<PixelPoint[]>("SearchPolygon");
            _scene = new(Read<int>("Width"), Read<int>("Height"), Pixels("expected"),
                Read<double[]>("CameraToBoard"), Read<HandTrackingBounds[]>("BoardSearchRegions"),
                Read<HandTrackingBounds[]>("BoardReferenceRegions"), Read<HandTrackingBounds[]>("BoardTriggerRegions"),
                Read<bool>("AllowsLocalForegroundContext"), Read<HandTrackingBounds[]>("BoardControlReferenceRegions"),
                Read<int[]>("BoardVectorGlyphRegions"), Read<HandTrackingBounds>("BoardDisturbanceExclusionRegion"));
            Held = Pixels("camera");
        }

        public HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var now = Epoch.AddMilliseconds(milliseconds);
            return tracker.Update(_width, _height, _stride, pixels, _polygon, _scene, now, now);
        }

        public byte[] Obstruct(HandTrackingBounds bounds, byte blue = 75, byte green = 95, byte red = 185, string texture = "")
        {
            byte[] pixels = (byte[])Held.Clone();
            var m = _scene.CameraToBoard;
            for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
            {
                double d = m[6] * x + m[7] * y + m[8];
                double u = (m[0] * x + m[1] * y + m[2]) / d, v = (m[3] * x + m[4] * y + m[5]) / d;
                if (u < bounds.X || u > bounds.X + bounds.Width || v < bounds.Y || v > bounds.Y + bounds.Height) continue;
                int index = y * _stride + x * 4;
                // Model both lost projected texture and a changed surface still receiving it.
                // A local colour fit must not learn either obstruction as the clear board.
                double fraction = (u - bounds.X) / bounds.Width - .5;
                double variation = texture == "gradient" ? fraction * 70 : texture == "shaded" ?
                    10 * Math.Sin(x * .071 + y * .053) + 12 * fraction : 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    double value = channel == 0 ? blue : channel == 1 ? green : red;
                    if (texture == "chroma") value = pixels[index + channel] + (channel == 0 ? 90 : channel == 1 ? -17.5 : 0);
                    else if (texture == "tint") value = pixels[index + channel] + (channel == 0 ? 70 : channel == 1 ? 30 : -20);
                    else value += variation;
                    pixels[index + channel] = (byte)Math.Clamp(value, 0, 255);
                }
            }
            return pixels;
        }

        private byte[] Pixels(string role)
        {
            using var input = File.OpenRead(PathFor("-" + role + ".bgra.gz"));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private string PathFor(string suffix) => Path.Combine(AppContext.BaseDirectory, "Fixtures", _name + suffix);
    }
}
