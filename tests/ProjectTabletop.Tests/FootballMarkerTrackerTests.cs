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
    [InlineData(258, true)]
    [InlineData(323, true)]
    [InlineData(750, true)]
    [InlineData(751, false)]
    public void WholeBoardWandIdentityOutlivesOutputButCannotPublishAStalePose(int gap, bool identityRetained)
    {
        var tracker = new FootballMarkerTracker();
        FootballTipDecision Measure(MarkerTipDetectionResult detection, int frameTime, int now) =>
            tracker.Update(Profile, detection, Field, Epoch.AddMilliseconds(frameTime), Epoch.AddMilliseconds(now));
        Assert.Equal(FootballTipAction.Hold, Measure(Full(100, 100), 0, 0).Action);
        Assert.Equal(FootballTipAction.Publish, Measure(Full(101, 100), 33, 33).Action);

        int resumedAt = 33 + gap;
        // These 258/323 ms gaps occurred in the live wand capture. The old
        // output must expire even though its identity can still match a new pair.
        var stale = Measure(Full(101, 100), 33, resumedAt);
        Assert.Equal(FootballTipAction.Hold, stale.Action);
        Assert.Equal("stale-frame", stale.Source);
        Assert.Null(stale.Tip);

        var resumed = Measure(Full(110, 102), resumedAt, resumedAt);
        Assert.Equal(identityRetained ? FootballTipAction.Publish : FootballTipAction.Hold, resumed.Action);
        Assert.Equal(identityRetained ? "full-marker" : "confirming", resumed.Source);
        if (identityRetained) Assert.Equal(new PixelPoint(110, 102), resumed.Tip!.Center);
        else Assert.Null(resumed.Tip);
        Assert.Equal(FootballTipAction.Publish, Measure(Full(111, 102), resumedAt + 33, resumedAt + 33).Action);
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
            var replacement = new MarkerTipDetectionResult([Marker(target.X, target.Y)], "black-bar-pair-candidate");
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
            new MarkerTipDetectionResult([], "no-black-bar-pair")
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
        MarkerTipObservation[] strips = [Strip(100, 92), Strip(100 + offset, 108, angle)];
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
        MarkerTipObservation[][] supporting =
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForwardShotContinuesFromMeasuredStripGeometryUnderProjectedCar(bool asymmetricInk)
    {
        // Measured geometry from the 2026-10-12 04:25:43.491Z failure. The rear
        // strip remains intact while the car's projected artwork covers the front
        // strip. The previous pair's envelope was 101px long despite ~74px ink.
        var profile = new BlackTipProfile(1, .1450980392156863, .596078431372549,
            .0007806471836419753, 3.3998357117836475, 2, .45576511276587967);
        var previous = new MarkerTipObservation(new(1404.9136860286696, 469.17181592478994),
            30.71596494738128, 2964, .8334375561418049)
        {
            Bar = new(new(1399.9353992050808, 519.3090115675432),
                new(1409.8919728522585, 419.03462028203677),
                new(1377.359651770552, 466.4358853661423),
                new(1432.4677202867874, 471.90774648343756))
        };
        var strip = new MarkerTipObservation(new(1383.5669929481107, 456.08893800652555),
            22.450917682180222, 1583.5, .9526150957537616)
        {
            Bar = new(new(1379.96728515625, 493.8459167480469),
                new(1387.107421875, 418.1609802246094),
                new(1372.2509765625, 454.9386901855469),
                new(1394.82373046875, 457.0682067871094))
        };
        var expected = new PixelPoint(1372.7302555718527, 469.64868135138124);
        if (asymmetricInk)
        {
            // Second captured forward shot, 2026-10-12 04:37:07.945Z. Projection
            // removes ink at one edge, shifting its mass centroid away from the
            // rectangle center used by complete-pair geometry.
            previous = new(new(1409.5344241338955, 509.4014521912942),
                29.92067103010745, 2812.5, .8578093578311943)
            {
                Bar = new(new(1405.1154461401843, 470.4761123879698),
                    new(1413.9534021276068, 548.3267919946186),
                    new(1437.3230583012107, 506.2467626373659),
                    new(1381.7457899665806, 512.5561417452225))
            };
            strip = new(new(1337.3238866396762, 496.2463407038306),
                22.606338099481658, 1605.5, .7597175367366015)
            {
                Bar = new(new(1341, 539), new(1341, 459), new(1324, 499), new(1358, 499))
            };
            expected = new(1371.4086291908745, 500.5581601473272);
        }
        // Three preceding measurements reproduce the saved bounded search prediction
        // without exceeding its per-frame velocity limit.
        double dx = (previous.Center.X - expected.X) * .033 / (.08 * .84);
        double dy = (previous.Center.Y - expected.Y) * .033 / (.08 * .84);
        PixelPoint Shift(PixelPoint point, int steps) => new(point.X + dx * steps, point.Y + dy * steps);
        MarkerTipObservation Preceding(int steps) => previous with
        {
            Center = Shift(previous.Center, steps),
            Bar = new(Shift(previous.Bar.End1, steps), Shift(previous.Bar.End2, steps),
                Shift(previous.Bar.Side1, steps), Shift(previous.Bar.Side2, steps))
        };
        PixelPoint? Field(PixelPoint point) => new(1 - point.X / 1920, point.Y / 1080);
        var tracker = new FootballMarkerTracker();
        FootballTipDecision Observe(MarkerTipDetectionResult result, double ms) => tracker.Update(0, profile,
            result, Field, Epoch.AddMilliseconds(ms), Epoch.AddMilliseconds(ms));
        Assert.Equal(FootballTipAction.Hold, Observe(new([Preceding(2)], "black-bar-pair-candidate"), 0).Action);
        Assert.Equal(FootballTipAction.Publish, Observe(new([Preceding(1)], "black-bar-pair-candidate"), 33).Action);
        Assert.Equal(FootballTipAction.Publish, Observe(new([previous], "black-bar-pair-candidate"), 66).Action);
        double missedAt = 66 + (asymmetricInk ? 113.0842 : 127.8868);
        var hint = tracker.GetSearchHint(Epoch.AddMilliseconds(missedAt))!;
        Assert.InRange(Math.Abs(hint.ExpectedCenter.X - expected.X), 0, .001);
        Assert.InRange(Math.Abs(hint.ExpectedCenter.Y - expected.Y), 0, .001);
        var measured = Observe(Empty() with { SupportingBars = [strip] }, missedAt);
        Assert.Equal(FootballTipAction.Publish, measured.Action);
        Assert.Equal("single-strip", measured.Source);
        Assert.InRange(measured.Tip!.Center.X, asymmetricInk ? 1359 : 1366, asymmetricInk ? 1360 : 1367);
        Assert.InRange(measured.Tip.Center.Y, asymmetricInk ? 498.9 : 454, asymmetricInk ? 499.1 : 455);
        Assert.Equal(strip.AreaPixels * 2, measured.Tip.AreaPixels);
        // No strip means no invented movement; partial evidence cannot extend expiry.
        Assert.Equal(FootballTipAction.Hold, Observe(Empty(), 227).Action);
        Assert.Equal(FootballTipAction.Hold,
            Observe(Empty() with { SupportingBars = [strip] }, 417).Action);
    }

    [Theory]
    [InlineData(20, 6)]
    [InlineData(70, 6)]
    [InlineData(46, 2)]
    [InlineData(46, 11)]
    public void PartialInkDimensionsStillRejectDifferentPhysicalMarks(double length, double width)
    {
        var tracker = Confirmed();
        var strip = Shape(100, 92, length, width, length * width, 0);
        var result = Update(tracker, Empty() with { SupportingBars = [strip] }, 66);
        Assert.Equal(FootballTipAction.Hold, result.Action);
        Assert.Null(result.Tip);
    }

    [Fact]
    public void SparseButValidPairKeepsItsMeasuredLengthWhenOneStripClears()
    {
        var training = Frame();
        Paint(training, 102, tapered: false);
        Paint(training, 134, tapered: false);
        var profile = BlackTipDetector.Learn(400, 300, 1600, training, new(100, 102));
        var sparse = Frame();
        Paint(sparse, 102, tapered: true);
        Paint(sparse, 134, tapered: true);
        var full = BlackTipDetector.Detect(400, 300, 1600, sparse, profile);
        var marker = Assert.Single(full.Candidates);
        Assert.InRange(marker.AreaPixels / (2 * 92 * 12), .54, .63);
        var clear = Frame();
        Paint(clear, 102, tapered: false);
        var partial = BlackTipDetector.Detect(400, 300, 1600, clear, profile);
        Assert.Empty(partial.Candidates);
        Assert.Single(partial.SupportingBars);
        var tracker = new FootballMarkerTracker();
        FootballTipDecision Observe(MarkerTipDetectionResult result, int ms) => tracker.Update(0, profile,
            result, Field, Epoch.AddMilliseconds(ms), Epoch.AddMilliseconds(ms));
        Assert.Equal(FootballTipAction.Hold, Observe(full, 0).Action);
        Assert.Equal(FootballTipAction.Publish, Observe(full, 33).Action);
        var continued = Observe(partial, 66);
        Assert.Equal(FootballTipAction.Publish, continued.Action);
        Assert.Equal("single-strip", continued.Source);
        Assert.Equal(new PixelPoint(100, 118), continued.Tip!.Center);

        static void Paint(byte[] pixels, int centerY, bool tapered)
        {
            // Convex, low-fill outlines remain valid detector evidence. Uneven
            // projection can remove corner ink while preserving the bar's extent.
            for (int y = centerY - 6; y <= centerY + 6; y++)
            for (int x = 54; x <= 146; x++)
            {
                if (tapered && Math.Abs(y - centerY) > 1.5 + 5 * (1 - Math.Abs(x - 100) / 46.0)) continue;
                int i = (y * 400 + x) * 4;
                pixels[i] = 8; pixels[i + 1] = 40; pixels[i + 2] = 12;
            }
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
        var previous = new MarkerTipObservation(new(1473.7963385953954, 439.91643928449474),
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
        var full = new MarkerTipDetectionResult([previous], "black-bar-pair-candidate");
        var tracker = new FootballMarkerTracker();
        FootballTipDecision Observe(MarkerTipDetectionResult result, int ms) => tracker.Update(0, profile,
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
        var detection = new MarkerTipDetectionResult([Marker(100, 100), Marker(300, 100)], "multiple-black-bar-pairs");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeBoardWandAcquiresEitherSideAndCrossesTheMidpoint(bool startOnRight)
    {
        var tracker = new FootballMarkerTracker();
        double[] positions = startOnRight ? [280, 230, 200, 170, 120] : [120, 170, 200, 230, 280];
        Assert.Equal(FootballTipAction.Hold, UpdateWand(tracker, Full(positions[0], 100), 0).Action);
        for (int i = 0; i < positions.Length; i++)
        {
            var result = UpdateWand(tracker, Full(positions[i], 100), 33 * (i + 1));
            Assert.Equal(FootballTipAction.Publish, result.Action);
            Assert.Equal(new PixelPoint(positions[i], 100), result.Tip!.Center);
            Assert.Equal(new PixelPoint(positions[i] / 400, 100.0 / 300), result.FieldPoint);
            Assert.Equal("full-marker", result.Source);
        }
    }

    [Fact]
    public void WholeBoardWandRejectsAmbiguityAcrossBothHalvesAndReconfirmsAfterwards()
    {
        foreach (var ambiguous in new[]
        {
            new MarkerTipDetectionResult([Marker(100, 100), Marker(300, 100)], "multiple-black-bar-pairs"),
            Full(100, 100) with { AmbiguousMarkerCenters = [new(300, 100)] }
        })
        {
            var tracker = new FootballMarkerTracker();
            UpdateWand(tracker, Full(100, 100), 0);
            Assert.Equal(FootballTipAction.Publish, UpdateWand(tracker, Full(100, 100), 33).Action);
            var rejected = UpdateWand(tracker, ambiguous, 66);
            Assert.Equal(FootballTipAction.Clear, rejected.Action);
            Assert.Null(rejected.Tip);
            Assert.Null(tracker.GetSearchHint(Epoch.AddMilliseconds(70)));
            Assert.Equal(FootballTipAction.Hold, UpdateWand(tracker, Partial(100, 92), 99).Action);
            Assert.Equal(FootballTipAction.Hold, UpdateWand(tracker, Full(100, 100), 132).Action);
            Assert.Equal(FootballTipAction.Publish, UpdateWand(tracker, Full(100, 100), 165).Action);
        }
    }

    [Fact]
    public void WholeBoardWandAcceptsOnlyFiniteCalibratedPositions()
    {
        var tracker = new FootballMarkerTracker();
        var outside = Full(450, 100) with { AmbiguousMarkerCenters = [new(450, 160)] };
        Assert.Equal(FootballTipAction.Hold, UpdateWand(tracker, outside, 0).Action);
        Assert.Null(tracker.GetSearchHint(Epoch.AddMilliseconds(1)));
        var inside = new MarkerTipDetectionResult([Marker(300, 100), Marker(450, 100)], "test")
            { AmbiguousMarkerCenters = [new(450, 160)] };
        Assert.Equal(FootballTipAction.Hold, UpdateWand(tracker, inside, 33).Action);
        var measured = UpdateWand(tracker, inside, 66);
        Assert.Equal(FootballTipAction.Publish, measured.Action);
        Assert.Equal(new PixelPoint(300, 100), measured.Tip!.Center);
        var missing = UpdateWand(tracker, outside, 99);
        Assert.Equal(FootballTipAction.Hold, missing.Action);
        Assert.Null(missing.Tip);

        PixelPoint? InvalidMap(PixelPoint _) => new(double.NaN, .5);
        var invalid = tracker.Update(Profile, Full(300, 100), InvalidMap,
            Epoch.AddMilliseconds(132), Epoch.AddMilliseconds(132));
        Assert.Equal(FootballTipAction.Hold, invalid.Action);
        Assert.Null(invalid.Tip);
    }

    [Fact]
    public void WholeBoardWandDoesNotPublishOldStaleFutureOrMissingReferenceFrames()
    {
        var tracker = new FootballMarkerTracker();
        UpdateWand(tracker, Full(300, 100), 0);
        Assert.Equal(FootballTipAction.Publish, UpdateWand(tracker, Full(300, 100), 33).Action);
        Assert.Equal("old-frame", UpdateWand(tracker, Full(300, 100), 33).Source);
        foreach (var (frame, now) in new[] { (66, 500), (500, 66) })
        {
            var rejected = tracker.Update(Profile, Full(300, 100), Field,
                Epoch.AddMilliseconds(frame), Epoch.AddMilliseconds(now));
            Assert.Equal(FootballTipAction.Hold, rejected.Action);
            Assert.Null(rejected.Tip);
            Assert.Equal("stale-frame", rejected.Source);
        }
        var waiting = UpdateWand(tracker, new([], "waiting-for-projected-wand-reference"), 100);
        Assert.Equal(FootballTipAction.Hold, waiting.Action);
        Assert.Null(waiting.Tip);
        Assert.Equal("waiting-reference", waiting.Source);
        Assert.Equal(FootballTipAction.Publish, UpdateWand(tracker, Full(301, 100), 133).Action);
    }

    [Fact]
    public void WholeBoardWandContinuesOneMeasuredStripOnlyWithinOriginalPairDeadline()
    {
        var tracker = new FootballMarkerTracker();
        Assert.Equal(FootballTipAction.Hold, UpdateWand(tracker, Partial(300, 92), 0).Action);
        Assert.Null(tracker.GetSearchHint(Epoch));
        Assert.Equal(FootballTipAction.Hold, UpdateWand(tracker, Full(300, 100), 33).Action);
        Assert.Equal(FootballTipAction.Publish, UpdateWand(tracker, Full(300, 100), 66).Action);
        var partial = UpdateWand(tracker, Partial(302, 92), 99);
        Assert.Equal(FootballTipAction.Publish, partial.Action);
        Assert.Equal("single-strip", partial.Source);
        Assert.Equal(new PixelPoint(302, 100), partial.Tip!.Center);
        var gap = UpdateWand(tracker, Empty(), 132);
        Assert.Equal(FootballTipAction.Hold, gap.Action);
        Assert.Null(gap.Tip);
        Assert.Equal(FootballTipAction.Publish, UpdateWand(tracker, Partial(302, 92), 400).Action);
        var expired = UpdateWand(tracker, Partial(302, 92), 417);
        Assert.Equal(FootballTipAction.Hold, expired.Action);
        Assert.Null(expired.Tip);
        Assert.Null(tracker.GetSearchHint(Epoch.AddMilliseconds(417)));
        Assert.Equal(FootballTipAction.Publish, UpdateWand(tracker, Full(302, 100), 450).Action);
    }

    private static FootballTipDecision UpdateWand(FootballMarkerTracker tracker, MarkerTipDetectionResult detection, int milliseconds) =>
        tracker.Update(Profile, detection, Field, Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(milliseconds));

    private static FootballMarkerTracker Confirmed()
    {
        var tracker = new FootballMarkerTracker();
        Update(tracker, Full(100, 100), 0);
        Assert.Equal(FootballTipAction.Publish, Update(tracker, Full(100, 100), 33).Action);
        return tracker;
    }
    private static FootballTipDecision Update(FootballMarkerTracker tracker, MarkerTipDetectionResult detection, int milliseconds) =>
        tracker.Update(0, Profile, detection, Field, Epoch.AddMilliseconds(milliseconds), Epoch.AddMilliseconds(milliseconds));
    private static MarkerTipDetectionResult Full(double x, double y, double angle = 0) => new([Marker(x, y, angle)], "black-bar-pair-candidate");
    private static MarkerTipDetectionResult Empty() => new([], "no-black-bar-pair");
    private static MarkerTipDetectionResult Partial(double x, double y, double angle = 0) => Empty() with { SupportingBars = [Strip(x, y, angle)] };
    private static MarkerTipObservation Marker(double x, double y, double angle = 0) => Shape(x, y, 46, 22, 552, angle);
    private static MarkerTipObservation Strip(double x, double y, double angle = 0) => Shape(x, y, 46, 6, 276, angle);
    private static MarkerTipObservation Shape(double x, double y, double length, double width, double area, double angle)
    {
        double ux = Math.Cos(angle * Math.PI / 180), uy = Math.Sin(angle * Math.PI / 180);
        return new(new(x, y), Math.Sqrt(area / Math.PI), area, .95)
        {
            Bar = new(new(x - ux * length / 2, y - uy * length / 2), new(x + ux * length / 2, y + uy * length / 2),
                new(x + uy * width / 2, y - ux * width / 2), new(x - uy * width / 2, y + ux * width / 2))
        };
    }
    private static MarkerTipDetectionResult Detect(byte[] pixels, BlackTipProfile profile, BlackTipSearchHint hint, byte[]? projected = null)
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
