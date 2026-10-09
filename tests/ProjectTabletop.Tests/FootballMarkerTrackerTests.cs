using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class FootballMarkerTrackerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly BlackTipProfile Profile = new(1, .16, .4, .0023, 46.0 / 6, 2, 16.0 / 46);
    private static PixelPoint? Field(PixelPoint point) => point.X >= 0 && point.X <= 400 && point.Y >= 0 && point.Y <= 300
        ? new(point.X / 400, point.Y / 300) : null;

    [Fact]
    public void FullMarkerReappearsImmediatelyWithoutInventingPoseDuringGaps()
    {
        var tracker = new FootballMarkerTracker();
        Assert.Equal(FootballTipAction.Hold, Update(tracker, Full(100, 100), 0).Action);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(101, 100), 33).Action);
        var gap = Update(tracker, Empty(), 100);
        Assert.Equal(FootballTipAction.Hold, gap.Action);
        Assert.Null(gap.Tip);
        var returned = Update(tracker, Full(110, 102, 60), 450);
        Assert.Equal(FootballTipAction.Publish, returned.Action);
        Assert.Equal(new PixelPoint(110, 102), returned.Tip!.Center);
        Assert.Equal("full-marker", returned.Source);
        Assert.Equal(FootballTipAction.Hold, Update(tracker, Full(112, 102), 1230).Action);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(113, 102), 1263).Action);
    }

    [Fact]
    public void OnlyRecentConfirmedPairMayContinueFromOneMeasuredStrip()
    {
        var tracker = new FootballMarkerTracker();
        Assert.Equal(FootballTipAction.Hold, Update(tracker, Partial(100, 92), 0).Action);
        Assert.Null(tracker.GetSearchHint(Epoch));
        tracker.Acquire(Marker(100, 100), Epoch);
        Assert.Equal(FootballTipAction.Hold, Update(tracker, Partial(100, 92), 16).Action);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(100, 100), 33).Action);
        var measured = Update(tracker, Partial(102, 92), 66);
        Assert.Equal(FootballTipAction.Publish, measured.Action);
        Assert.Equal("single-strip", measured.Source);
        Assert.Equal(new PixelPoint(102, 100), measured.Tip!.Center);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Partial(103, 92), 200).Action);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Partial(103, 92), 380).Action);
        Assert.Equal(FootballTipAction.Hold, Update(tracker, Partial(103, 92), 384).Action);
        Assert.Null(tracker.GetSearchHint(Epoch.AddMilliseconds(384)));
        // Partial observations do not renew the 350ms full-pair deadline.
        Assert.Equal(FootballTipAction.Hold, Update(tracker, Partial(103, 92), 420).Action);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(103, 100), 450).Action);
    }

    [Fact]
    public void PartialPairRejectsUncertainSideRotationCompetingStripsAndOffBoardCenters()
    {
        foreach (var partial in new[]
        {
            Partial(100, 100), // Equidistant between the two possible sides.
            Partial(100, 92, 60),
            new ColorTipDetectionResult([], "no-black-bar-pair")
                { SupportingBars = [Strip(100, 92), Strip(100, 108)] },
            Partial(100, 55)
        })
        {
            var tracker = Confirmed();
            Assert.NotEqual(FootballTipAction.Publish, Update(tracker, partial, 66).Action);
        }
        // Use field bounds stricter than camera bounds to exercise inferred centre mapping.
        PixelPoint? Cropped(PixelPoint point) => point.Y >= 100 ? Field(point) : null;
        var trackerAtCrop = Confirmed();
        var result = trackerAtCrop.Update(0, Profile, Partial(100, 91), Cropped,
            Epoch.AddMilliseconds(66), Epoch.AddMilliseconds(66));
        Assert.Equal(FootballTipAction.Hold, result.Action);
        Assert.Null(result.Tip);
    }

    [Fact]
    public void AmbiguityClearsIdentityAndEachPlayerOwnsOnlyItsHalf()
    {
        var detection = new ColorTipDetectionResult([Marker(100, 100), Marker(300, 100)], "multiple-black-bar-pairs");
        for (int player = 0; player < 2; player++)
        {
            var tracker = new FootballMarkerTracker();
            tracker.Update(player, Profile, detection, Field, Epoch, Epoch);
            var published = tracker.Update(player, Profile, detection, Field, Epoch.AddMilliseconds(33), Epoch.AddMilliseconds(33));
            Assert.Equal(FootballTipAction.Publish, published.Action);
            Assert.Equal(player == 0 ? 100 : 300, published.Tip!.Center.X);
        }
        var confirmed = Confirmed();
        var ambiguous = Full(100, 100) with { AmbiguousMarkerCenters = [new(150, 150)] };
        Assert.Equal(FootballTipAction.Clear, Update(confirmed, ambiguous, 66).Action);
        Assert.Null(confirmed.GetSearchHint(Epoch.AddMilliseconds(70)));
        Assert.Equal(FootballTipAction.Hold, Update(confirmed, Partial(100, 92), 99).Action);
        Assert.Equal(FootballTipAction.Hold, Update(confirmed, Full(100, 100), 132).Action);
    }

    [Fact]
    public void OldStaleAndMissingProjectionFramesNeverRefreshAController()
    {
        var tracker = Confirmed();
        Assert.Equal(FootballTipAction.Hold, Update(tracker, Full(100, 100), 33).Action);
        var stale = tracker.Update(0, Profile, Full(100, 100), Field, Epoch.AddMilliseconds(66), Epoch.AddMilliseconds(500));
        Assert.Equal("stale-frame", stale.Source);
        var reference = Update(tracker, new([], "waiting-for-projected-bat-reference"), 100);
        Assert.Equal(FootballTipAction.Hold, reference.Action);
        Assert.Null(reference.Tip);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(101, 100), 133).Action);
        var singleProfile = Profile with { BarCount = 1, SpacingRatio = 0 };
        Assert.Equal(FootballTipAction.Hold, tracker.Update(0, singleProfile, Partial(101, 92), Field,
            Epoch.AddMilliseconds(166), Epoch.AddMilliseconds(166)).Action);
        tracker.Reset();
        Assert.Null(tracker.GetSearchHint(Epoch.AddMilliseconds(200)));
    }

    [Fact]
    public void RecentPoseRecoversBothLitStripsLocallyWithoutHidingGlobalAmbiguity()
    {
        var training = Frame();
        PaintPair(training, 100, 120, 40);
        var profile = BlackTipDetector.Learn(400, 300, 1600, training, new(100, 112));
        var measured = Assert.Single(BlackTipDetector.Detect(400, 300, 1600, training, profile).Candidates);
        var hint = new BlackTipSearchHint(measured, measured.Center, Epoch);
        var bright = Frame();
        PaintPair(bright, 104, 120, 110);
        Assert.Empty(BlackTipDetector.Detect(400, 300, 1600, bright, profile).Candidates);
        var recovered = Detect(bright, profile, hint);
        Assert.Equal("local-contrast", recovered.Source);
        Assert.InRange(Assert.Single(recovered.Candidates).Center.X, 103, 105);
        Assert.Equal(2, recovered.SupportingBars.Count);
        Assert.Empty(Detect(bright, profile, hint with { LastFullFrame = Epoch.AddMilliseconds(-400) }).Candidates);
        var tooBright = Frame();
        PaintPair(tooBright, 100, 120, 170);
        Assert.Empty(Detect(tooBright, profile, hint).Candidates);
        PaintPair(bright, 185, 220, 40);
        var two = Detect(bright, profile, hint);
        Assert.Equal(2, two.Candidates.Count);
        var tracker = new FootballMarkerTracker();
        tracker.Acquire(measured, Epoch);
        Assert.Equal(FootballTipAction.Clear, tracker.Update(0, profile, two, Field,
            Epoch.AddMilliseconds(33), Epoch.AddMilliseconds(33)).Action);
    }

    [Fact]
    public void LocalSearchStillVetoesProjectedAndMixedPairsAndRejectsTripleStrips()
    {
        var training = Frame();
        PaintPair(training, 100, 120, 40);
        var profile = BlackTipDetector.Learn(400, 300, 1600, training, new(100, 112));
        var measured = Assert.Single(BlackTipDetector.Detect(400, 300, 1600, training, profile).Candidates);
        var hint = new BlackTipSearchHint(measured, measured.Center, Epoch);
        var bright = Frame();
        PaintPair(bright, 100, 120, 110);
        Assert.Empty(Detect(bright, profile, hint, bright).Candidates);
        var oneProjected = Frame();
        PaintStrip(oneProjected, 100, 112, 110);
        Assert.Empty(Detect(bright, profile, hint, oneProjected).Candidates);
        Assert.Single(Detect(bright, profile, hint, Frame()).Candidates);
        PaintStrip(bright, 100, 144, 110);
        var ambiguous = Detect(bright, profile, hint);
        Assert.Empty(ambiguous.Candidates);
        Assert.NotEmpty(ambiguous.AmbiguousMarkerCenters);
    }

    private static FootballMarkerTracker Confirmed()
    {
        var tracker = new FootballMarkerTracker();
        Update(tracker, Full(100, 100), 0);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(100, 100), 33).Action);
        return tracker;
    }
    private static FootballTipDecision Update(FootballMarkerTracker tracker, ColorTipDetectionResult detection, int milliseconds) =>
        tracker.Update(0, Profile, detection, Field, Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(milliseconds));
    private static ColorTipDetectionResult Full(double x, double y, double angle = 0) => new([Marker(x, y, angle)], "black-bar-pair-candidate");
    private static ColorTipDetectionResult Empty() => new([], "no-black-bar-pair");
    private static ColorTipDetectionResult Partial(double x, double y, double angle = 0) => Empty() with { SupportingBars = [Strip(x, y, angle)] };
    private static ColorTipObservation Marker(double x, double y, double angle = 0) => Shape(x, y, 46, 22, 552, angle);
    private static ColorTipObservation Strip(double x, double y, double angle = 0) => Shape(x, y, 46, 6, 276, angle);
    private static ColorTipObservation Shape(double x, double y, double length, double width, double area, double angle)
    {
        double ux = Math.Cos(angle * Math.PI / 180), uy = Math.Sin(angle * Math.PI / 180);
        return new(new(x, y), Math.Sqrt(area / Math.PI), area, .95)
        {
            Bar = new(new(x - ux * length / 2, y - uy * length / 2), new(x + ux * length / 2, y + uy * length / 2),
                new(x + uy * width / 2, y - ux * width / 2), new(x - uy * width / 2, y + ux * width / 2))
        };
    }
    private static ColorTipDetectionResult Detect(byte[] pixels, BlackTipProfile profile, BlackTipSearchHint hint, byte[]? projected = null)
    {
        var time = Epoch.AddMilliseconds(33);
        IReadOnlyList<EyeTipProjectionFrame>? frames = projected is null ? null :
            [new(400, 300, projected, [1.0 / 400, 0, 0, 0, 1.0 / 300, 0, 0, 0, 1], time)];
        return BlackTipDetector.DetectEach(400, 300, 1600, pixels, [profile], new(ProjectionFrames: frames, FrameTime: time), [hint])[0];
    }
    private static byte[] Frame()
    {
        var pixels = new byte[400 * 300 * 4];
        for (int y = 0; y < 300; y++)
        for (int x = 0; x < 400; x++)
        {
            int i = (y * 400 + x) * 4;
            byte grain = (byte)((x * 3 + y * 7) % 11);
            pixels[i] = (byte)(120 + grain); pixels[i + 1] = (byte)(200 + grain);
            pixels[i + 2] = (byte)(130 + grain); pixels[i + 3] = 255;
        }
        return pixels;
    }
    private static void PaintPair(byte[] pixels, int x, int y, byte value)
    { PaintStrip(pixels, x, y - 8, value); PaintStrip(pixels, x, y + 8, value); }
    private static void PaintStrip(byte[] pixels, int x, int y, byte value)
    {
        for (int cy = y - 3; cy <= y + 3; cy++)
        for (int cx = x - 23; cx <= x + 23; cx++)
        {
            int i = (cy * 400 + cx) * 4;
            pixels[i] = 8; pixels[i + 1] = value; pixels[i + 2] = 12;
        }
    }
}
