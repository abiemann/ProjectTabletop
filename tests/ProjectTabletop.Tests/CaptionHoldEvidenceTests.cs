using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class CaptionHoldEvidenceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 5, 54, 0, TimeSpan.Zero);
    private static readonly string[] Ids = ["water-drawer-close", "water-garden-exit", "water-garden-reset", "water-garden-duck-add"];

    [Fact]
    public void RecordedCaptionDamageCompletesHoldWithoutWholePlateCoverage()
    {
        var fixture = new Fixture();
        var tracker = new HandAcquisitionPresenceTracker();
        var board = new BoardSession();
        board.ShowWaterGarden(Epoch.AddSeconds(-2));
        Assert.True(board.ActivateButton("water-drawer-open", Epoch.AddSeconds(-1)));
        board.TickWaterGarden(Epoch);
        var clear = fixture.Feed(tracker, fixture.Clear, 0);
        Assert.All(clear.TextPatterns!, pattern => Assert.True(pattern.LabelIntact));
        board.ObserveHeldButtons([], Epoch, Epoch, Ids);
        for (int ms = 100; ms <= 1100; ms += 100)
        {
            var result = fixture.Feed(tracker, fixture.Held, ms);
            var caption = result.TextPatterns!.Single(pattern => pattern.ControlRegion == 3);
            Assert.True(caption.ShapeCorrupted);
            Assert.False(caption.LabelIntact);
            Assert.True(caption.ControlCoverage < HandAcquisitionPresenceTracker.MinimumControlCoverage);
            Assert.Equal(0, caption.ConfirmationFrames);
            Assert.True(caption.CaptionHoldEvidence);
            var held = result.TextPatterns!.Where(pattern => pattern.CaptionHoldEvidence).Select(pattern => Ids[pattern.ControlRegion]).ToArray();
            Assert.Equal([Ids[3]], held);
            var now = Epoch.AddMilliseconds(ms);
            var activated = board.ObserveHeldButtons(held, now, now, []);
            Assert.Equal(ms == 1100 ? [Ids[3]] : Array.Empty<string>(), activated);
        }
        Assert.Equal(1, board.WaterGardenAddedDuckCount);
        var removed = fixture.Feed(tracker, fixture.Clear, 1200);
        Assert.All(removed.TextPatterns!, pattern => Assert.False(pattern.CaptionHoldEvidence));
        board.ObserveHeldButtons([], Epoch.AddMilliseconds(1200), Epoch.AddMilliseconds(1200), Ids);
        Assert.Empty(board.HoldProgress(Epoch.AddMilliseconds(1200)));
    }

    [Fact]
    public void IntactLetteringRejectsColourBrightnessAndBorderChanges()
    {
        var fixture = new Fixture();
        foreach (string change in new[] { "clear", "brightness", "colour", "border" })
        {
            var tracker = new HandAcquisitionPresenceTracker();
            fixture.Feed(tracker, fixture.Clear, 0);
            var pixels = (byte[])fixture.Clear.Clone();
            for (int y = 0; y < fixture.Height; y++)
            for (int x = 0; x < fixture.Width; x++)
            {
                int at = (y * fixture.Width + x) * 4;
                if (change == "brightness")
                    for (int channel = 0; channel < 3; channel++) pixels[at + channel] = (byte)Math.Clamp(pixels[at + channel] * .8 + 15, 0, 255);
                if (change == "colour")
                {
                    pixels[at] = (byte)Math.Clamp(pixels[at] + 15, 0, 255);
                    pixels[at + 2] = (byte)Math.Clamp(pixels[at + 2] - 15, 0, 255);
                }
                if (change == "border" && y is >= 1030 and <= 1033 && x is >= 615 and <= 1245)
                    for (int channel = 0; channel < 3; channel++) pixels[at + channel] = 12;
            }
            var result = fixture.Feed(tracker, pixels, 100);
            Assert.All(result.TextPatterns!, pattern => Assert.False(pattern.CaptionHoldEvidence));
        }
    }

    private sealed class Fixture
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Clear { get; }
        public byte[] Held { get; }
        private readonly HandAcquisitionSceneImage _scene;
        private readonly PixelPoint[] _polygon;

        public Fixture()
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "water-caption-hold.json")));
            var metadata = json.RootElement;
            Width = metadata.GetProperty("cameraWidth").GetInt32();
            Height = metadata.GetProperty("cameraHeight").GetInt32();
            Clear = Pixels("clear");
            Held = Pixels("held");
            _scene = new(metadata.GetProperty("Width").GetInt32(), metadata.GetProperty("Height").GetInt32(), Pixels("expected"),
                metadata.GetProperty("CameraToBoard").Deserialize<double[]>(options)!,
                metadata.GetProperty("BoardSearchRegions").Deserialize<HandTrackingBounds[]>(options),
                metadata.GetProperty("BoardReferenceRegions").Deserialize<HandTrackingBounds[]>(options),
                metadata.GetProperty("BoardTriggerRegions").Deserialize<HandTrackingBounds[]>(options),
                BoardControlReferenceRegions: metadata.GetProperty("BoardControlReferenceRegions").Deserialize<HandTrackingBounds[]>(options));
            _polygon = metadata.GetProperty("SearchPolygon").Deserialize<PixelPoint[]>(options)!;
        }

        public HandAcquisitionPresenceResult Feed(HandAcquisitionPresenceTracker tracker, byte[] pixels, int ms)
        {
            var now = Epoch.AddMilliseconds(ms);
            return tracker.Update(Width, Height, Width * 4, pixels, _polygon, _scene, now, now);
        }

        private static byte[] Pixels(string kind)
        {
            using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "water-caption-" + kind + ".png"), ImreadModes.Unchanged);
            Assert.False(image.Empty());
            Assert.Equal(MatType.CV_8UC4, image.Type());
            var pixels = new byte[image.Width * image.Height * 4];
            Marshal.Copy(image.Data, pixels, 0, pixels.Length);
            return pixels;
        }
    }
}
