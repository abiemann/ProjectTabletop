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

    [Theory]
    [InlineData(33, 80)]
    [InlineData(66, 120)]
    [InlineData(100, 170)]
    [InlineData(150, 205)]
    public void FastMeasuredFullPairsStayAcquiredThroughAccelerationAndReversal(int interval, int distance)
    {
        var tracker = new FootballMarkerTracker();
        PixelPoint? CameraField(PixelPoint point) => new(point.X / 3200, point.Y / 1200);
        FootballTipDecision Measure(double x, int time) => tracker.Update(0, Profile, Full(x, 500),
            CameraField, Epoch.AddMilliseconds(time), Epoch.AddMilliseconds(time));
        Assert.Equal(FootballTipAction.Hold, Measure(800, 0).Action);
        Assert.Equal(FootballTipAction.Publish, Measure(800, 33).Action);

        int frameTime = 33;
        // Accelerate from rest, continue the shot, then reverse for the next shot.
        // Every pose must be an actual current pair, never the predicted search position.
        foreach (double x in new[] { 800 - distance, 800 - distance * 2, 800 - distance, 800 })
        {
            var measured = Measure(x, frameTime += interval);
            Assert.Equal(FootballTipAction.Publish, measured.Action);
            Assert.Equal("full-marker", measured.Source);
            Assert.Equal(new PixelPoint(x, 500), measured.Tip!.Center);
        }
    }

    [Fact]
    public void FastPairAllowanceStillRequiresConfirmationForDistantOrExpiredMarkers()
    {
        PixelPoint? CameraField(PixelPoint point) => new(point.X / 3200, point.Y / 1200);
        foreach (int gap in new[] { 33, 250, 600, 751 })
        {
            var tracker = new FootballMarkerTracker();
            tracker.Update(0, Profile, Full(800, 500), CameraField, Epoch, Epoch);
            tracker.Update(0, Profile, Full(800, 500), CameraField,
                Epoch.AddMilliseconds(33), Epoch.AddMilliseconds(33));
            var time = Epoch.AddMilliseconds(33 + gap);
            // Seven lengths exceeds the capped full-pair reach, even after a long gap.
            var target = new PixelPoint(gap == 751 ? 810 : 1122, 500);
            var replacement = new ColorTipDetectionResult([Marker(target.X, target.Y)], "black-bar-pair-candidate");
            var pending = tracker.Update(0, Profile, replacement, CameraField, time, time);
            Assert.Equal(FootballTipAction.Hold, pending.Action);
            Assert.Equal("confirming", pending.Source);
            Assert.Null(pending.Tip);
            var confirmedAt = time.AddMilliseconds(33);
            var confirmed = tracker.Update(0, Profile, replacement, CameraField, confirmedAt, confirmedAt);
            Assert.Equal(FootballTipAction.Publish, confirmed.Action);
            Assert.Equal(target, confirmed.Tip!.Center);
        }
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
                { SupportingBars = [Strip(94, 92), Strip(106, 92)] },
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

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(false, 1, 3)]
    [InlineData(true, 1, 3)]
    public void SupportingStripsAgreeingOnOnePoseDoNotClearTracking(bool reverse, double offset, double angle)
    {
        var tracker = Confirmed();
        ColorTipObservation[] strips = [Strip(100, 92), Strip(100 + offset, 108, angle)];
        if (reverse) Array.Reverse(strips);
        var detection = Empty() with { SupportingBars = strips };
        var measured = Update(tracker, detection, 66);
        Assert.Equal(FootballTipAction.Publish, measured.Action);
        Assert.Equal("single-strip", measured.Source);
        Assert.InRange(measured.Tip!.Center.X, 99, 103);
        Assert.InRange(measured.Tip.Center.Y, 99, 101);
        Assert.NotNull(tracker.GetSearchHint(Epoch.AddMilliseconds(70)));
        Assert.Equal(FootballTipAction.Publish, Update(tracker, detection, 383).Action);
        // Agreeing partial strips are still not a new complete-pair measurement.
        Assert.Equal(FootballTipAction.Hold, Update(tracker, detection, 384).Action);
        Assert.Null(tracker.GetSearchHint(Epoch.AddMilliseconds(384)));
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(100, 100), 450).Action);
    }

    [Theory]
    [InlineData(108, 108, 0)]
    [InlineData(100, 108, 15)]
    [InlineData(106, 92, 0)]
    public void ConflictingPartialPosesHoldWithoutForcingFullMarkerReacquisition(double x, double y, double angle)
    {
        var tracker = Confirmed();
        var detection = Empty() with { SupportingBars = [Strip(100, 92), Strip(x, y, angle)] };
        var result = Update(tracker, detection, 66);
        Assert.Equal(FootballTipAction.Hold, result.Action);
        Assert.Equal("uncertain-strips", result.Source);
        Assert.Null(result.Tip);
        Assert.NotNull(tracker.GetSearchHint(Epoch.AddMilliseconds(70)));
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(100, 100), 100).Action);
    }

    [Fact]
    public void PartialPoseAgreementDoesNotHideExtraStripsOrIncompatibleSizes()
    {
        ColorTipObservation[][] supporting =
        [
            [Strip(100, 92), Strip(100, 108), Strip(101, 108)],
            [Strip(100, 92), Shape(100, 108, 46, 9.8, 450.8, 0)],
            [Strip(100, 92), Shape(100, 109.6, 55.2, 6, 331.2, 0)]
        ];
        foreach (var strips in supporting)
        {
            var tracker = Confirmed();
            var result = Update(tracker, Empty() with { SupportingBars = strips }, 66);
            Assert.Equal(FootballTipAction.Hold, result.Action);
            Assert.Equal("uncertain-strips", result.Source);
            Assert.Null(result.Tip);
            Assert.NotNull(tracker.GetSearchHint(Epoch.AddMilliseconds(70)));
            // Repeated uncertain reconstructions cannot extend the last complete
            // pair's deadline or publish a controller position.
            Assert.Equal(FootballTipAction.Hold, Update(tracker, Empty() with { SupportingBars = strips }, 383).Action);
            Assert.Equal("missing", Update(tracker, Empty() with { SupportingBars = strips }, 384).Source);
            Assert.Null(tracker.GetSearchHint(Epoch.AddMilliseconds(384)));
        }
    }

    [Fact]
    public void ProjectorDistortedPairFromCapturedFrameDoesNotDisarmAnAcquiredPlayer()
    {
        // Exact measured geometry from the 2026-10-12 01:35:00.969Z failure.
        // Both strips belong to one wide pair; projection reduced one contour's
        // width and area enough that their partial reconstructions disagreed.
        var profile = new BlackTipProfile(1, .1450980392156863, .596078431372549,
            .0007806471836419753, 3.3998357117836475, 2, .45576511276587967);
        var previous = new ColorTipObservation(new(1473.7963385953954, 439.91643928449474),
            33.65332933072042, 3558, .9110018317345157)
        {
            Bar = new(new(1474.1113730073787, 480.24131363437886),
                new(1473.4813041834122, 399.59156493461063),
                new(1442.2828790950327, 440.1626353196544),
                new(1505.309798095758, 439.67024324933516))
        };
        var distorted = Empty() with
        {
            SupportingBars =
            [
                new(new(1456.129547553093, 435.3634349030471), 23.96975895919152, 1805, .9055692970341418)
                {
                    Bar = new(new(1454.6690673828125, 395.7400207519531),
                        new(1457.4019775390625, 476.3592224121094),
                        new(1443.761474609375, 436.4656982421875),
                        new(1468.3095703125, 435.633544921875))
                },
                new(new(1488.400234741784, 432.7585513078471), 17.787636910694122, 994, .6323561302998125)
                {
                    Bar = new(new(1488.5, 476), new(1488.5, 395), new(1481, 435.5), new(1496, 435.5))
                }
            ]
        };
        PixelPoint? RotatedField(PixelPoint point) => new(1 - point.X / 1920, point.Y / 1080);
        var full = new ColorTipDetectionResult([previous], "black-bar-pair-candidate");
        var tracker = new FootballMarkerTracker();
        FootballTipDecision Observe(ColorTipDetectionResult result, int ms) => tracker.Update(0, profile,
            result, RotatedField, Epoch.AddMilliseconds(ms), Epoch.AddMilliseconds(ms));
        Assert.Equal(FootballTipAction.Hold, Observe(full, 0).Action);
        Assert.Equal(FootballTipAction.Publish, Observe(full, 33).Action);
        var uncertain = Observe(distorted, 80);
        Assert.Equal(FootballTipAction.Hold, uncertain.Action);
        Assert.Equal("uncertain-strips", uncertain.Source);
        Assert.Null(uncertain.Tip);
        Assert.NotNull(tracker.GetSearchHint(Epoch.AddMilliseconds(81)));
        var recovered = Observe(full, 100);
        Assert.Equal(FootballTipAction.Publish, recovered.Action);
        Assert.Equal("full-marker", recovered.Source);
        Assert.Equal(previous, recovered.Tip);
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
