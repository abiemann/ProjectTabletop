using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class CaptionWashoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalWashoutIsUnreadableWithoutMeasuredCaptionDamage(bool warm)
    {
        var fixture = new Fixture();
        var tracker = new HandAcquisitionPresenceTracker();
        if (warm) fixture.Feed(tracker, fixture.Clear, 0);
        var white = (byte[])fixture.Clear.Clone();
        for (int i = 0; i < white.Length; i++) white[i] = 255;
        var result = fixture.Feed(tracker, white, 100);
        Assert.Empty(result.Hints);
        Assert.All(result.TextPatterns!, pattern =>
        {
            Assert.True(pattern.CaptionClipped);
            Assert.False(pattern.LabelIntact);
            Assert.Equal(0, pattern.ControlTriggerCoverage);
            Assert.False(pattern.ShapeCorrupted);
            Assert.False(pattern.CaptionHoldEvidence);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VanishedCaptionRetainsDamageEvidenceBesideReadableLetters(bool warm)
    {
        var fixture = new Fixture();
        var tracker = new HandAcquisitionPresenceTracker();
        if (warm) fixture.Feed(tracker, fixture.Clear, 0);
        var erased = (byte[])fixture.Clear.Clone();
        var region = fixture.Triggers[0];
        for (int y = (int)(region.Y * 1000) - 4; y < (region.Y + region.Height) * 1000 + 4; y++)
        for (int x = (int)(region.X * 1000) - 4; x < (region.X + region.Width) * 1000 + 4; x++)
            for (int channel = 0; channel < 4; channel++) erased[(y * 1000 + x) * 4 + channel] = 255;
        var result = fixture.Feed(tracker, erased, 100);
        var vanished = result.TextPatterns!.Single(pattern => pattern.ControlRegion == 0);
        Assert.Contains(result.TextPatterns!, pattern => pattern.ControlRegion != 0 && pattern.LabelIntact);
        Assert.True(vanished.ControlTriggerCoverage > 0);
        Assert.True(vanished.ShapeCorrupted);
        Assert.True(vanished.CaptionHoldEvidence);
    }

    private sealed class Fixture
    {
        public byte[] Clear { get; }
        public HandTrackingBounds[] Triggers { get; }
        private readonly HandAcquisitionSceneImage _scene;
        private static readonly PixelPoint[] Polygon = [new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)];

        public Fixture()
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "native-drawer-raster.json")));
            // Use real native EXIT/ZOOM glyph pixels at their own resolution,
            // isolating optical washout from camera mapping and arrow policy.
            HandTrackingBounds[] Read(string name) => json.RootElement.GetProperty(name)
                .Deserialize<HandTrackingBounds[]>()!.Skip(1).ToArray();
            using var image = Cv2.ImRead(Path.Combine(directory, "native-drawer-raster-expected.png"), ImreadModes.Unchanged);
            Assert.Equal(MatType.CV_8UC4, image.Type());
            Assert.Equal(1000, image.Width);
            Assert.Equal(1000, image.Height);
            Clear = new byte[1000 * 1000 * 4];
            Marshal.Copy(image.Data, Clear, 0, Clear.Length);
            Triggers = Read("BoardTriggerRegions");
            _scene = new(1000, 1000, Clear, [.001, 0, 0, 0, .001, 0, 0, 0, 1],
                Read("BoardSearchRegions"), Read("BoardReferenceRegions"), Triggers,
                BoardControlReferenceRegions: Read("BoardControlReferenceRegions"));
        }

        public HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int ms)
        {
            var now = DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
            return tracker.Update(1000, 1000, 4000, pixels, Polygon, _scene, now, now);
        }
    }
}
