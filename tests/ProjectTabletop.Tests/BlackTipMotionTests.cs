using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BlackTipMotionTests
{
    private const int Width = 512, Height = 384;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 17, 22, 0, TimeSpan.Zero);
    private static readonly BlackTipProfile Profile = new(1, .1333333333333333, .2647058823529412,
        .000523485725308642 * 1920 * 1080 / (Width * Height), 3.4314989988349875, 2, .4466474001132885);

    [Fact]
    public void RecordedMotionStillRequiresMeasuredOpposingEdgesAndPhysicalEvidence()
    {
        var pixels = Read("early");
        var result = BlackTipDetector.Detect(Width, Height, Width * 4, pixels, Profile);
        var marker = Assert.Single(result.Candidates);
        Assert.Equal("measured-edges", result.Source);
        Assert.Empty(result.AmbiguousMarkerCenters);
        Assert.InRange(marker.Center.X, 253, 273);
        Assert.InRange(marker.Center.Y, 123, 143);
        var projected = new MarkerTipDetectionOptions(ProjectionFrames:
            [new(Width, Height, pixels, [1.0 / Width, 0, 0, 0, 1.0 / Height, 0, 0, 0, 1], Epoch)], FrameTime: Epoch);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, pixels, Profile, projected).Candidates);
    }

    [Fact]
    public void RealGapDecisionIsIndependentOfStripOrderAndEndpointDirection()
    {
        var pixels = Read("gap-axis");
        // Opposing edges measured offline from this actual frame. The complete
        // detector still rejects its heavier blur; isolate the order-dependent
        // gap decision without asserting that every moving frame is acquired.
        var first = new MarkerTipObservation(Point(738.7177878912457, 614.3838747873056), 18.463745234996065, 1071, .82)
        {
            Bar = new(Point(737.349510082054, 582.9136060271428), Point(740.0860657004374, 645.8541435474683),
                Point(747.209765175734, 614.0146569657776), Point(730.2258106067574, 614.7530926088334))
        };
        var second = new MarkerTipObservation(Point(772.7611455151867, 610.5564829832064), 18.463745234996065, 1071, .83)
        {
            Bar = new(Point(769.1504815688997, 579.2641020276609), Point(776.3718094614736, 641.8488639387518),
                Point(781.2051213285879, 609.5821768389701), Point(764.3171697017856, 611.5307891274425))
        };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var imageType = typeof(BlackTipDetector).GetNestedType("BlackImage", BindingFlags.NonPublic)!;
        using var image = (IDisposable)Activator.CreateInstance(imageType, flags, null,
            [Width, Height, Width * 4, pixels, null], null)!;
        var hasGap = imageType.GetMethod("HasSeparatingGap", flags)!;
        foreach (var a in new[] { first, Reversed(first) })
        foreach (var b in new[] { second, Reversed(second) })
        {
            Assert.True((bool)hasGap.Invoke(image, [a, b, Profile])!);
            Assert.True((bool)hasGap.Invoke(image, [b, a, Profile])!);
        }

        static MarkerTipObservation Reversed(MarkerTipObservation strip) =>
            strip with { Bar = strip.Bar! with { End1 = strip.Bar.End2, End2 = strip.Bar.End1 } };
        static PixelPoint Point(double x, double y) => new(x - 540, y - 440);
    }

    private static byte[] Read(string name)
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "football-motion-" + name + ".png"), ImreadModes.Unchanged);
        Assert.Equal(MatType.CV_8UC4, image.Type());
        Assert.Equal(Width, image.Width);
        Assert.Equal(Height, image.Height);
        var pixels = new byte[Width * Height * 4];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return pixels;
    }
}
