using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class MenuTextAcquisitionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 9, 50, 0, TimeSpan.Zero);

    [Fact]
    public void RecordedCaptionDamageAcquiresWithoutRequiringTheTileArtworkToChange()
    {
        var fixture = new Fixture();
        var tracker = new HandAcquisitionPresenceTracker();
        Quiet(fixture.Feed(tracker, fixture.Clear, 0));
        var first = fixture.Feed(tracker, fixture.HeldFirst, 100);
        var caption = Assert.Single(first.TextPatterns!, pattern => pattern.ControlRegion == 3);
        Assert.False(caption.LabelIntact);
        Assert.True(caption.ShapeCorrupted);
        Assert.Equal(1, caption.ConfirmationFrames);
        Assert.Empty(first.Hints);
        Assert.Empty(fixture.Feed(tracker, fixture.HeldFirst, 100).Hints);

        var confirmed = fixture.Feed(tracker, fixture.HeldLast, 200);
        var hint = Assert.Single(confirmed.Hints);
        Assert.InRange(hint.ControlCoverage!.Value, .25, .40);
        Assert.Equal(hint.ControlCoverage, hint.ControlTriggerCoverage);
        Assert.Equal(2, confirmed.TextPatterns!.Single(pattern => pattern.ControlRegion == 3).ConfirmationFrames);
        Assert.Single(fixture.Feed(tracker, fixture.HeldFirst, 300).Hints);
        Quiet(fixture.Feed(tracker, fixture.Clear, 400));
    }

    [Theory]
    [InlineData(.72, 8, 8, 8)]
    [InlineData(1.12, 12, 12, 12)]
    [InlineData(.76, 24, 9, 38)]
    public void IntactRecordedCaptionsCancelDespiteExposureOrColourChanges(double gain, int blue, int green, int red)
    {
        var fixture = new Fixture();
        var tracker = new HandAcquisitionPresenceTracker();
        fixture.Feed(tracker, fixture.Clear, 0);
        fixture.Feed(tracker, fixture.HeldFirst, 100);
        byte[] changed = fixture.Clear.ToArray();
        int[] offsets = [blue, green, red];
        for (int index = 0; index < changed.Length; index += 4)
            for (int channel = 0; channel < 3; channel++)
                changed[index + channel] = (byte)Math.Clamp(changed[index + channel] * gain + offsets[channel], 0, 255);
        Quiet(fixture.Feed(tracker, changed, 200));
        var restarted = fixture.Feed(tracker, fixture.HeldLast, 300);
        Assert.Empty(restarted.Hints);
        Assert.Equal(1, restarted.TextPatterns!.Single(pattern => pattern.ControlRegion == 3).ConfirmationFrames);
    }

    private static void Quiet(HandAcquisitionPresenceResult result)
    {
        Assert.Empty(result.Hints);
        Assert.All(result.TextPatterns!, pattern =>
        {
            Assert.True(pattern.LabelIntact);
            Assert.False(pattern.ShapeCorrupted);
            Assert.False(pattern.CaptionHoldEvidence);
            Assert.Equal(0, pattern.ConfirmationFrames);
        });
    }

    private sealed class Fixture
    {
        private readonly int _width, _height, _stride;
        private readonly PixelPoint[] _polygon;
        private readonly HandAcquisitionSceneImage _scene;
        public byte[] Clear { get; }
        public byte[] HeldFirst { get; }
        public byte[] HeldLast { get; }

        public Fixture()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(PathFor(".json")));
            var root = document.RootElement;
            T Read<T>(string key) => root.GetProperty(key).Deserialize<T>()!;
            _width = Read<int>("cameraWidth"); _height = Read<int>("cameraHeight"); _stride = Read<int>("Stride");
            _polygon = Read<PixelPoint[]>("SearchPolygon");
            var triggers = Read<HandTrackingBounds[]>("BoardTriggerRegions");
            var search = Read<HandTrackingBounds[]>("BoardSearchRegions");
            // Gesture tiles acquire from their stationary caption. The full tile
            // remains an optical reference, and the paging hold keeps its own policy.
            for (int index = 0; index < 7; index++) search[index] = triggers[index];
            _scene = new(Read<int>("Width"), Read<int>("Height"), Pixels("expected"),
                Read<double[]>("CameraToBoard"), search, Read<HandTrackingBounds[]>("BoardReferenceRegions"),
                triggers, Read<bool>("AllowsLocalForegroundContext"),
                Read<HandTrackingBounds[]>("BoardControlReferenceRegions"), Read<int[]>("BoardVectorGlyphRegions"));
            Clear = Pixels("clear"); HeldFirst = Pixels("held-first"); HeldLast = Pixels("held-last");
        }

        public HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int milliseconds)
        {
            var time = Epoch.AddMilliseconds(milliseconds);
            return tracker.Update(_width, _height, _stride, pixels, _polygon, _scene, time, time);
        }

        private static byte[] Pixels(string role)
        {
            using var image = Cv2.ImRead(PathFor("-" + role + ".png"), ImreadModes.Unchanged);
            Assert.Equal(MatType.CV_8UC4, image.Type());
            var pixels = new byte[image.Width * image.Height * 4];
            Marshal.Copy(image.Data, pixels, 0, pixels.Length);
            return pixels;
        }
        private static string PathFor(string suffix) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "menu-text-acquisition" + suffix);
    }
}
