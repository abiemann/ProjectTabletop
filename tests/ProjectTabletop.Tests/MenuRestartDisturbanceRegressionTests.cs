using System.IO.Compression;
using System.Text.Json;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class MenuRestartDisturbanceRegressionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 1, 22, 0, 39, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(.75, 0, 1)]
    [InlineData(-.75, 0, 1)]
    [InlineData(0, .75, 1)]
    [InlineData(0, -.75, 1)]
    [InlineData(0, 0, .88)]
    [InlineData(0, 0, 1.08)]
    public void HeldArrowSurvivesRestartArtworkResidualsAndSmallOpticalVariations(
        double shiftX, double shiftY, double gain)
    {
        var fixture = new Fixture(shiftX, shiftY, gain);
        var tracker = new HandAcquisitionPresenceTracker();
        foreach (int milliseconds in new[] { 0, 100, 1200, 2400 })
        {
            var result = fixture.Feed(tracker, fixture.Held, milliseconds);
            Assert.True(result.TextPatterns!.Single(pattern => pattern.ControlRegion == 7).CaptionHoldEvidence);
            Assert.False(result.OutsideControlDisturbance,
                $"Untouched artwork blocked a valid arrow at shift ({shiftX}, {shiftY}), gain {gain}: " +
                result.OutsideControlForegroundFraction);
        }
    }

    [Theory]
    [InlineData(.80, .45, .08, .07)] // The same thumbnail whose intact detail formerly vetoed.
    [InlineData(.485, .26, .03, .50)] // A narrow reaching arm through the central gap.
    [InlineData(.64, .91, .06, .07)] // An independent finger beside the arrow approach.
    public void RealObstructionStillBlocksAndClearsImmediately(double x, double y, double width, double height)
    {
        var fixture = new Fixture();
        byte[] obstruction = fixture.Obstruct(new(x, y, width, height));
        foreach (bool warm in new[] { false, true })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            if (warm) Assert.False(fixture.Feed(tracker, fixture.Held, 0).OutsideControlDisturbance);
            foreach (int milliseconds in new[] { 100, 1400 })
                Assert.True(fixture.Feed(tracker, obstruction, milliseconds).OutsideControlDisturbance);
            Assert.False(fixture.Feed(tracker, fixture.Held, 1500).OutsideControlDisturbance);
        }
    }

    private sealed class Fixture
    {
        private readonly int _width, _height, _stride;
        private readonly HandAcquisitionSceneImage _scene;
        private readonly PixelPoint[] _polygon;
        public byte[] Held { get; }

        public Fixture(double shiftX = 0, double shiftY = 0, double gain = 1)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(PathFor("menu-restart-artwork.json")));
            var root = json.RootElement;
            T Read<T>(string name) => root.GetProperty(name).Deserialize<T>()!;
            _width = Read<int>("cameraWidth"); _height = Read<int>("cameraHeight"); _stride = Read<int>("Stride");
            _polygon = Read<PixelPoint[]>("SearchPolygon").Select(point => new PixelPoint(point.X + shiftX, point.Y + shiftY)).ToArray();
            double[] mapping = Read<double[]>("CameraToBoard");
            mapping[2] -= mapping[0] * shiftX + mapping[1] * shiftY;
            mapping[5] -= mapping[3] * shiftX + mapping[4] * shiftY;
            mapping[8] -= mapping[6] * shiftX + mapping[7] * shiftY;
            _scene = new(Read<int>("Width"), Read<int>("Height"), Pixels(Read<string>("ExpectedRasterFixture")),
                mapping, Read<HandTrackingBounds[]>("BoardSearchRegions"), Read<HandTrackingBounds[]>("BoardReferenceRegions"),
                Read<HandTrackingBounds[]>("BoardTriggerRegions"), Read<bool>("AllowsLocalForegroundContext"),
                Read<HandTrackingBounds[]>("BoardControlReferenceRegions"), Read<int[]>("BoardVectorGlyphRegions"),
                Read<HandTrackingBounds>("BoardDisturbanceExclusionRegion"));
            Held = Pixels("menu-restart-artwork-camera.bgra.gz");
            if (gain != 1)
                for (int pixel = 0; pixel < Held.Length; pixel += 4)
                for (int channel = 0; channel < 3; channel++)
                    Held[pixel + channel] = (byte)Math.Clamp(Held[pixel + channel] * gain, 0, 255);
        }

        public HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var now = Epoch.AddMilliseconds(milliseconds);
            return tracker.Update(_width, _height, _stride, pixels, _polygon, _scene, now, now);
        }

        public byte[] Obstruct(HandTrackingBounds bounds)
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
                pixels[index] = 75; pixels[index + 1] = 95; pixels[index + 2] = 185;
            }
            return pixels;
        }

        private static byte[] Pixels(string name)
        {
            using var input = File.OpenRead(PathFor(name));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private static string PathFor(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    }
}
