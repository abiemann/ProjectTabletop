using ProjectTabletop.Calibration;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class RedDotDetectorTests
{
    [Theory]
    [InlineData(240, 240, 240)]
    [InlineData(92, 86, 100)]
    [InlineData(180, 205, 220)]
    [InlineData(220, 185, 205)]
    public void SmallRedSpotIsFoundOnWhiteGreyAndTintedBoards(byte b, byte g, byte r)
    {
        var frame = new Frame(b, g, r);
        frame.Disc(230, 170, 3, 25, 35, 245);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(1, result.CandidateCount);
        Assert.InRange(result.Confidence, .7, 1);
        Assert.Equal(230, result.CameraPosition!.Value.X, 5);
        Assert.Equal(170, result.CameraPosition!.Value.Y, 5);
        Assert.Equal(230 / 639.0, result.BoardX!.Value, 5);
        Assert.Equal(170 / 479.0, result.BoardY!.Value, 5);
    }

    [Fact]
    public void WhiteClippedCoreKeepsTheDotCentroid()
    {
        var frame = new Frame();
        frame.Disc(350, 220, 5, 20, 20, 255);
        frame.Disc(350, 220, 3, 255, 255, 255);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(350, result.CameraPosition!.Value.X, 5);
        Assert.Equal(220, result.CameraPosition!.Value.Y, 5);
    }

    [Fact]
    public void CameraTintedMagentaDotIsFoundOnLavenderBoardAndDisappearsImmediately()
    {
        // Measured RGB from the live Facecam: background red/green excess about12,
        // faint spot fringe18 and peak27; blue exceeds red throughout the dot.
        var detector = new RedDotDetector();
        var frame = new Frame(208, 154, 166);
        frame.Disc(320, 240, 3, 216, 160, 178);
        frame.Disc(320, 240, 2, 214, 156, 183);
        var found = frame.Detect(detector);
        Assert.True(found.Detected);
        Assert.Equal(1, found.CandidateCount);
        Assert.Equal(320, found.CameraPosition!.Value.X, 5);
        Assert.Equal(240, found.CameraPosition!.Value.Y, 5);
        frame.Disc(320, 240, 3, 208, 154, 166);
        Assert.False(frame.Detect(detector).Detected);
    }

    [Fact]
    public void CameraTintedDotIsFoundOnBlueFeathersWithClippedCore()
    {
        // Measured live duck pixels: blue feathers(122,121,195), dot(210,180,241),
        // clipped core(255,244,255). Arguments to the frame helpers are B,G,R.
        var frame = new Frame(195, 121, 122);
        frame.Disc(350, 220, 3, 241, 180, 210);
        frame.Disc(350, 220, 1, 255, 244, 255);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(350, result.CameraPosition!.Value.X, 5);
        Assert.Equal(220, result.CameraPosition!.Value.Y, 5);
    }

    [Fact]
    public void BlueArtAndMeasuredChromaticFringesAreNotMagentaDots()
    {
        var frame = new Frame(203, 149, 158);
        frame.Disc(160, 180, 30, 195, 121, 122); // Blue feather patch.
        frame.Disc(160, 180, 2, 190, 100, 116); // Weak projected edge, peak16.
        frame.Disc(330, 180, 14, 220, 150, 166);
        frame.Disc(330, 180, 3, 220, 150, 174); // Stronger edge, local peak only8.
        frame.Disc(480, 180, 3, 210, 150, 170); // Isolated peak20, below laser signal.
        Assert.False(frame.Detect(new()).Detected);
    }

    [Fact]
    public void MagentaDotsRemainAmbiguousAndLargeMagentaArtworkIsRejected()
    {
        var frame = new Frame(208, 154, 166);
        frame.Disc(320, 180, 35, 214, 156, 183);
        Assert.False(frame.Detect(new()).Detected);
        frame.Disc(160, 320, 3, 241, 180, 210);
        frame.Disc(480, 320, 3, 214, 156, 183);
        var result = frame.Detect(new());
        Assert.False(result.Detected);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void StrongRedCoreAndMagentaFringeAreOneDot()
    {
        var frame = new Frame();
        frame.Disc(320, 240, 5, 241, 180, 210);
        frame.Disc(320, 240, 2, 20, 20, 255);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(320, result.CameraPosition!.Value.X, 5);
        Assert.Equal(240, result.CameraPosition!.Value.Y, 5);
    }

    [Fact]
    public void ClippedWhiteCoreJoinsSeparatedMagentaFringesWithoutKeepingOldAim()
    {
        var detector = new RedDotDetector();
        var frame = new Frame(195, 121, 122);
        for (int y = 237; y <= 243; y++)
        {
            for (int x = 318; x <= 322; x++) frame.Pixel(x, y, 255, 244, 255);
            // Strong fringe islands are joined through the measured dim camera response
            // and the clipped centre, not by averaging two nearby candidate positions.
            frame.Pixel(315, y, 112, 65, 118);
            frame.Pixel(316, y, 85, 57, 91);
            frame.Pixel(317, y, 85, 57, 91);
            frame.Pixel(323, y, 85, 57, 91);
            frame.Pixel(324, y, 85, 57, 91);
            frame.Pixel(325, y, 112, 65, 118);
        }
        var found = frame.Detect(detector);
        Assert.True(found.Detected);
        Assert.Equal(1, found.CandidateCount);
        Assert.Equal(320, found.CameraPosition!.Value.X, 5);
        Assert.Equal(240, found.CameraPosition!.Value.Y, 5);
        Assert.False(new Frame(195, 121, 122).Detect(detector).Detected);
    }

    [Fact]
    public void NearbyClippedDotsSeparatedByOrdinaryBackgroundStayAmbiguous()
    {
        var frame = new Frame(195, 121, 122);
        foreach (int x in new[] { 310, 330 })
        {
            frame.Disc(x, 240, 4, 241, 180, 210);
            frame.Disc(x, 240, 2, 255, 244, 255);
        }
        var found = frame.Detect(new());
        Assert.False(found.Detected);
        Assert.Equal(2, found.CandidateCount);
    }

    [Fact]
    public void ClippedGlareWithOnlyDimFringeCannotSeedADot()
    {
        var frame = new Frame(195, 121, 122);
        frame.Disc(320, 240, 5, 85, 57, 91);
        frame.Disc(320, 240, 2, 255, 244, 255);
        Assert.False(frame.Detect(new()).Detected);
    }

    [Fact]
    public void SaturatedWhiteBoardDoesNotSwallowStrongRedDot()
    {
        var frame = new Frame(255, 255, 255);
        frame.Disc(320, 240, 3, 20, 20, 255);
        var found = frame.Detect(new());
        Assert.True(found.Detected);
        Assert.Equal(1, found.CandidateCount);
        Assert.Equal(320, found.CameraPosition!.Value.X, 5);
    }

    [Fact]
    public void MultiplePlausibleDotsHaveNoAimEvenWhenOneIsBrighter()
    {
        var frame = new Frame();
        frame.Disc(160, 180, 3, 20, 20, 190);
        frame.Disc(460, 280, 4, 0, 0, 255);
        var result = frame.Detect(new());
        Assert.False(result.Detected);
        Assert.True(result.Ambiguous);
        Assert.Equal(2, result.CandidateCount);
        Assert.Null(result.CameraPosition);
        Assert.Null(result.BoardX);
        Assert.Null(result.BoardY);
    }

    [Fact]
    public void LargeRedArtworkAndNarrowRedLetteringDoNotCreateAim()
    {
        var frame = new Frame();
        frame.Disc(230, 180, 35, 0, 0, 255);
        for (int x = 350; x < 380; x++)
            for (int y = 280; y < 283; y++) frame.Pixel(x, y, 0, 0, 255);
        Assert.False(frame.Detect(new()).Detected);
        frame.Disc(460, 180, 3, 20, 30, 245);
        Assert.True(frame.Detect(new()).Detected);
    }

    [Fact]
    public void WhiteGlareOrangeBeakAndIsolatedSensorNoiseDoNotCount()
    {
        var frame = new Frame(110, 115, 120);
        frame.Disc(130, 180, 4, 255, 255, 255);
        frame.Disc(300, 180, 4, 20, 180, 245);
        frame.Pixel(430, 180, 0, 0, 255);
        frame.Pixel(460, 180, 0, 0, 255);
        Assert.False(frame.Detect(new()).Detected);
    }

    [Fact]
    public void CompactBrownPhotographicDetailIsNotARedDot()
    {
        var frame = new Frame(90, 110, 130);
        // Five pixels from the photographed duck's foot passed the original
        // brightness/compactness checks despite their orange-brown hue.
        frame.Pixel(320, 238, 74, 104, 172);
        frame.Pixel(320, 239, 59, 88, 156);
        frame.Pixel(320, 240, 62, 90, 154);
        frame.Pixel(321, 240, 75, 103, 168);
        frame.Pixel(321, 241, 63, 93, 157);
        var result = frame.Detect(new());
        Assert.False(result.Detected);
        Assert.Equal(0, result.CandidateCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedOrMagentaDotOnBrownPhotographicArtworkStillTracks(bool magenta)
    {
        var frame = new Frame(74, 104, 172);
        frame.Disc(320, 240, 3, magenta ? (byte)230 : (byte)30,
            magenta ? (byte)150 : (byte)40, magenta ? (byte)245 : (byte)255);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(320, result.CameraPosition!.Value.X, 5);
        Assert.Equal(240, result.CameraPosition!.Value.Y, 5);
    }

    [Theory]
    [InlineData(104, 103, 166)]
    [InlineData(159, 133, 167)]
    public void CameraTintedPhotographicColoursNeedAnActualDotSignal(byte b, byte g, byte r)
    {
        var frame = new Frame(132, 100, 110);
        frame.Disc(320, 240, 3, b, g, r);
        var result = frame.Detect(new());
        Assert.False(result.Detected);
        Assert.Equal(0, result.CandidateCount);
    }

    [Fact]
    public void IsolatedBrightPixelsDoNotPromoteABroadMagentaFeatherEdge()
    {
        var frame = new Frame(132, 100, 110);
        frame.Disc(320, 240, 7, 141, 120, 143);
        frame.Pixel(320, 240, 210, 188, 212);
        frame.Pixel(321, 240, 210, 188, 212);
        Assert.Equal(0, frame.Detect(new()).CandidateCount);
    }

    [Theory]
    [InlineData(160)]
    [InlineData(190)]
    public void ModeratelyBrightSaturatedRedDotDoesNotRequireAClippedCentre(byte red)
    {
        var frame = new Frame(110, 115, 120);
        frame.Disc(320, 240, 3, 20, 25, red);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(1, result.CandidateCount);
    }

    [Fact]
    public void ClippedCorePositionIsNotDraggedIntoConnectedPhotographicTexture()
    {
        var frame = new Frame(132, 100, 110);
        frame.Disc(310, 240, 30, 214, 156, 183);
        frame.Disc(329, 243, 2, 255, 244, 255);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(329, result.CameraPosition!.Value.X, 5);
        Assert.Equal(243, result.CameraPosition!.Value.Y, 5);
    }

    [Fact]
    public void ClippedCoreAcceptsLocalDesaturatedRedFringe()
    {
        var frame = new Frame(132, 100, 110);
        // The camera's red fringe on the photographed neck is neither strong red
        // nor magenta. It is meaningful locally around a multi-pixel bright core.
        frame.Disc(320, 240, 5, 149, 140, 197);
        frame.Disc(320, 240, 2, 255, 244, 255);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(320, result.CameraPosition!.Value.X, 5);
        Assert.Equal(240, result.CameraPosition!.Value.Y, 5);
    }

    [Fact]
    public void MovingClippedStreakUsesItsExposureCentroidAndReleasesImmediately()
    {
        var frame = new Frame(208, 154, 166);
        for (int y = 237; y <= 244; y++)
        for (int x = 300; x <= 335; x++) frame.Pixel(x, y, 214, 156, 183);
        for (int y = 240; y <= 241; y++)
        for (int x = 303; x <= 332; x++) frame.Pixel(x, y, 255, 244, 255);
        var detector = new RedDotDetector();
        var result = frame.Detect(detector);
        Assert.True(result.Detected);
        Assert.Equal(317.5, result.CameraPosition!.Value.X, 5);
        Assert.Equal(240.5, result.CameraPosition!.Value.Y, 5);
        Assert.False(new Frame(208, 154, 166).Detect(detector).Detected);
    }

    [Fact]
    public void ClippedDotDoesNotHideAnotherIndependentNonclippedDot()
    {
        var frame = new Frame();
        frame.Disc(260, 240, 4, 241, 180, 210);
        frame.Disc(260, 240, 2, 255, 244, 255);
        frame.Disc(380, 240, 3, 20, 30, 245);
        var result = frame.Detect(new());
        Assert.False(result.Detected);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void SparseTransientMagentaPhotographicPatchIsNotADot()
    {
        var frame = new Frame(132, 100, 110);
        for (int y = 238; y <= 241; y++)
        for (int x = 318; x <= 322; x++) frame.Pixel(x, y, 200, 150, 170);
        frame.Pixel(320, 239, 209, 153, 177);
        frame.Pixel(321, 239, 208, 152, 179);
        frame.Pixel(320, 240, 209, 153, 177);
        frame.Pixel(321, 240, 209, 153, 180);
        Assert.False(frame.Detect(new()).Detected);
    }

    [Fact]
    public void BroadClippedPatchWithRedSurroundingIsNotALaserCore()
    {
        var frame = new Frame(110, 115, 120);
        frame.Disc(320, 240, 35, 241, 180, 210);
        frame.Disc(320, 240, 30, 255, 244, 255);
        Assert.False(frame.Detect(new()).Detected);
    }

    [Fact]
    public void WhitePhotographicHighlightNeedsRedFringeNotBrownFeathers()
    {
        var frame = new Frame(110, 115, 120);
        // Measured native photographic feather colours around a five-pixel white
        // highlight: red/green contrast alone mistakes this orange-brown ring for a laser.
        frame.Disc(320, 240, 5, 149, 169, 198);
        frame.Pixel(320, 235, 111, 114, 138); // One incidental pinkish pixel is insufficient.
        frame.Disc(320, 240, 1, 255, 244, 255);
        var result = frame.Detect(new());
        Assert.False(result.Detected);
        Assert.Equal(0, result.CandidateCount);
    }

    [Fact]
    public void CalibratedPolygonExcludesCameraSurroundingsAndMapsPerspective()
    {
        Point2[] camera = [new(160, 70), new(550, 105), new(580, 400), new(70, 420)];
        var map = Homography.FromFourPoints(camera, [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        var frame = new Frame();
        // Inside the quad's bounding box but outside its sloping left edge.
        frame.Disc(90, 100, 3, 0, 0, 255);
        Assert.False(frame.Detect(new(), map.ToMatrix()).Detected);
        frame.Disc(320, 230, 3, 0, 0, 255);
        var result = frame.Detect(new(), map.ToMatrix());
        var expected = map.Transform(new(320, 230));
        Assert.True(result.Detected);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(expected.X, result.BoardX!.Value, 5);
        Assert.Equal(expected.Y, result.BoardY!.Value, 5);
    }

    [Fact]
    public void BoardBoundaryClippedSpotIsRejected()
    {
        var frame = new Frame();
        frame.Disc(160, 180, 5, 0, 0, 255);
        double[] map = [1 / 400.0, 0, -.4, 0, 1 / 300.0, -.2, 0, 0, 1];
        Assert.False(frame.Detect(new(), map).Detected);
    }

    [Fact]
    public void MissingCurrentDotDoesNotRetainPreviousAim()
    {
        var detector = new RedDotDetector();
        var frame = new Frame();
        frame.Disc(320, 240, 3, 20, 20, 255);
        Assert.True(frame.Detect(detector).Detected);
        frame.Disc(320, 240, 3, 235, 235, 235);
        var missing = frame.Detect(detector);
        Assert.False(missing.Detected);
        Assert.Equal(0, missing.CandidateCount);
    }

    [Fact]
    public void PaddedStrideIsHonouredAndPaddingCannotBecomeADot()
    {
        var frame = new Frame(padding: 28);
        for (int y = 0; y < Frame.Height; y++)
            for (int x = Frame.Width * 4; x < frame.Stride; x += 4)
            {
                frame.Pixels[y * frame.Stride + x] = 0;
                frame.Pixels[y * frame.Stride + x + 1] = 0;
                frame.Pixels[y * frame.Stride + x + 2] = 255;
            }
        Assert.False(frame.Detect(new()).Detected);
        frame.Disc(420, 210, 3, 0, 0, 255);
        var result = frame.Detect(new());
        Assert.True(result.Detected);
        Assert.Equal(420, result.CameraPosition!.Value.X, 5);
    }

    [Fact]
    public void RecalibrationReplacesTheSearchPolygonWithoutKeepingOldPixels()
    {
        var detector = new RedDotDetector();
        var frame = new Frame();
        frame.Disc(80, 180, 3, 0, 0, 255);
        Assert.True(frame.Detect(detector).Detected);
        double[] map = [1 / 400.0, 0, -.4, 0, 1 / 300.0, -.2, 0, 0, 1];
        Assert.False(frame.Detect(detector, map).Detected);
    }

    [Fact]
    public void InvalidBufferAndSingularCalibrationAreRejected()
    {
        var frame = new Frame();
        var detector = new RedDotDetector();
        Assert.Throws<ArgumentException>(() => detector.Detect(640, 480, 1, frame.Pixels, Frame.Map));
        Assert.Throws<ArgumentException>(() => frame.Detect(detector, new double[9]));
        Assert.Throws<ArgumentException>(() => frame.Detect(detector, [1, 0, 0, 0, 1, 0, 0, 0, double.NaN]));
    }

    private sealed class Frame
    {
        public const int Width = 640, Height = 480;
        public static readonly double[] Map = [1 / 639.0, 0, 0, 0, 1 / 479.0, 0, 0, 0, 1];
        public int Stride { get; }
        public byte[] Pixels { get; }

        public Frame(byte b = 235, byte g = 235, byte r = 235, int padding = 0)
        {
            Stride = Width * 4 + padding;
            Pixels = new byte[Stride * Height];
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++) Pixel(x, y, b, g, r);
        }

        public void Pixel(int x, int y, byte b, byte g, byte r)
        {
            int index = y * Stride + x * 4;
            Pixels[index] = b; Pixels[index + 1] = g; Pixels[index + 2] = r; Pixels[index + 3] = 255;
        }

        public void Disc(int cx, int cy, int radius, byte b, byte g, byte r)
        {
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(Height - 1, cy + radius); y++)
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(Width - 1, cx + radius); x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) Pixel(x, y, b, g, r);
        }

        public RedDotDetectionResult Detect(RedDotDetector detector, IReadOnlyList<double>? map = null) =>
            detector.Detect(Width, Height, Stride, Pixels, map ?? Map);
    }
}
