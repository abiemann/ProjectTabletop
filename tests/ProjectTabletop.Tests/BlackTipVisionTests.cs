using System.Text.Json;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BlackTipVisionTests
{
    private const int Width = 320, Height = 240;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SetupFindsOnlyOneCompletePhysicalPairInsideItsTarget()
    {
        PixelPoint[] target = [new(100, 70), new(220, 70), new(220, 170), new(100, 170)];
        var image = Frame();
        Pair(image, 160, 120);
        BlackTipLearningResult LearnTarget(byte[] camera, byte[]? projection = null) =>
            BlackTipDetector.LearnPairInTarget(Width, Height, Width * 4, camera, target,
                projection is null ? null : new([new(Width, Height, projection,
                    [1.0 / Width, 0, 0, 0, 1.0 / Height, 0, 0, 0, 1], Epoch)], Epoch));
        var found = LearnTarget(image, Frame());
        Assert.Equal(2, found.Profile!.BarCount);
        Assert.True(found.Profile.IsValid);
        Assert.InRange(found.Marker!.Center.X, 159, 161);
        Assert.InRange(found.Marker.Center.Y, 119, 121);
        Assert.Null(LearnTarget(image, image).Profile);
        var projectedPartner = Frame();
        Bar(projectedPartner, 160, 112, 46, 6);
        Assert.Null(LearnTarget(image, projectedPartner).Profile);
        var third = image.ToArray();
        Bar(third, 160, 144, 46, 6);
        Assert.Null(LearnTarget(third).Profile);
        var lone = Frame();
        Bar(lone, 160, 112, 46, 6);
        Assert.Null(LearnTarget(lone).Profile);
        var clipped = Frame();
        Pair(clipped, 112, 120);
        Assert.Null(LearnTarget(clipped).Profile);
        var outside = image.ToArray();
        Pair(outside, 45, 120);
        Assert.NotNull(LearnTarget(outside).Profile);
        var waiting = BlackTipDetector.LearnPairInTarget(Width, Height, Width * 4, image, target,
            new([], Epoch));
        Assert.Null(waiting.Profile);
        Assert.Equal("waiting-for-projected-reference", waiting.Reason);
        Assert.Throws<ArgumentException>(() => BlackTipDetector.LearnPairInTarget(Width, Height,
            Width * 4, image, [new(double.NaN, 0), new(2, 2), new(3, 3)]));
    }

    [Fact]
    public void SetupReportsMultipleIndependentPairsInsteadOfChoosingTheNearest()
    {
        var image = Frame();
        Pair(image, 90, 120);
        Pair(image, 230, 120);
        var learned = BlackTipDetector.LearnPairInTarget(Width, Height, Width * 4, image,
            [new(30, 60), new(290, 60), new(290, 180), new(30, 180)]);
        Assert.Null(learned.Profile);
        Assert.Null(learned.Marker);
        Assert.Equal("show-only-one-double-bar-in-target", learned.Reason);
    }

    [Fact]
    public void BlackCrossbarLearnsShapeAndSurvivesRotationLightingAndResolutionChanges()
    {
        var image = Frame();
        Bar(image, 160, 100, 30, 8);
        // The uncoloured cardboard handle touches the top bar, but must not join its component.
        Fill(image, 157, 105, 6, 45, 125, 175, 200);
        var profile = Learn(image, 160, 100);
        Assert.True(profile.IsValid);
        Assert.InRange(profile.AspectRatio, 3.4, 4.2);
        Assert.Equal(profile, JsonSerializer.Deserialize<BlackTipProfile>(JsonSerializer.Serialize(profile)));
        foreach (double angle in new[] { 0, 20, 45, 70, 90, 140 })
        foreach (byte brightness in new byte[] { 9, 20, 40 })
        {
            var rotated = Frame();
            Bar(rotated, 163, 123, 30, 8, angle, brightness);
            var found = Assert.Single(Detect(rotated, profile).Candidates);
            Assert.InRange(found.Center.X, 162, 164);
            Assert.InRange(found.Center.Y, 122, 124);
        }
        var larger = Frame(640, 480);
        Bar(larger, 320, 240, 60, 16, 35, width: 640, height: 480);
        Assert.Single(BlackTipDetector.Detect(640, 480, 640 * 4, larger, profile).Candidates);
    }

    [Fact]
    public void EmptyHubRoundHolesRingsAndIncorrectSizeCannotBecomeBats()
    {
        var training = Frame();
        Bar(training, 160, 120, 30, 8);
        var profile = Learn(training, 160, 120);
        var emptyHub = Frame();
        Disk(emptyHub, 160, 120, 25, 175);
        Assert.Empty(Detect(emptyHub, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(emptyHub, 160, 120));
        var hole = emptyHub.ToArray();
        Disk(hole, 160, 120, 9, 18);
        Assert.Empty(Detect(hole, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(hole, 160, 120));
        var ring = emptyHub.ToArray();
        Bar(ring, 160, 120, 40, 16);
        Bar(ring, 160, 120, 32, 8, brightness: 175);
        Assert.Empty(Detect(ring, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(ring, 160, 120));
        var wrongSizes = Frame();
        Bar(wrongSizes, 60, 70, 12, 3);
        Bar(wrongSizes, 240, 160, 68, 18);
        Assert.Empty(Detect(wrongSizes, profile).Candidates);
        var lowContrast = Frame();
        Fill(lowContrast, 120, 90, 80, 60, 24, 24, 24);
        Bar(lowContrast, 160, 120, 30, 8);
        Assert.Empty(Detect(lowContrast, profile).Candidates);
    }

    [Fact]
    public void ProjectedBlackArtworkIsVetoedWhilePhysicalCrossbarRemainsVisible()
    {
        var training = Frame();
        Bar(training, 160, 120, 30, 8);
        var profile = Learn(training, 160, 120);
        var projected = Frame();
        Bar(projected, 70, 110, 30, 8, 20);
        Bar(projected, 240, 70, 28, 8, 90);
        Disk(projected, 160, 70, 16, 20); // Projected football panel or tyre.
        Disk(projected, 220, 170, 24, 175); // Pale centre of the rendered kicker.
        var options = Options(projected);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, projected, profile, options).Candidates);
        Assert.Throws<InvalidOperationException>(() => BlackTipDetector.Learn(Width, Height, Width * 4,
            projected, new(70, 110), new(options.ProjectionFrames, options.FrameTime)));
        foreach ((int x, int y) in new[] { (160, 125), (220, 170) })
        {
            var camera = projected.ToArray();
            Bar(camera, x, y, 30, 8, 35);
            var physical = Assert.Single(BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, options).Candidates);
            Assert.InRange(physical.Center.X, x - 1, x + 1);
            Assert.InRange(physical.Center.Y, y - 1, y + 1);
            Assert.True(BlackTipDetector.Learn(Width, Height, Width * 4, camera, new(x, y),
                new(options.ProjectionFrames, options.FrameTime)).IsValid);
        }
        var movedReference = projected.ToArray();
        Bar(movedReference, 160, 125, 30, 8);
        var newest = Options(movedReference);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, movedReference, profile, newest).Candidates);
    }

    [Fact]
    public void MissingOrStaleProjectedReferenceDisarmsInsteadOfAcceptingArtwork()
    {
        var training = Frame();
        Bar(training, 160, 120, 30, 8);
        var profile = Learn(training, 160, 120);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, training, profile,
            new(ProjectionFrames: [], FrameTime: Epoch)).Candidates);
        var expired = Options(Frame()) with { FrameTime = Epoch.AddSeconds(2) };
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, training, profile, expired).Candidates);
        Assert.Throws<InvalidOperationException>(() => BlackTipDetector.Learn(Width, Height, Width * 4,
            training, new(160, 120), new([], Epoch)));
        Assert.Throws<ArgumentException>(() => BlackTipDetector.Detect(Width, Height, Width * 4,
            training, profile, new(ProjectionFrames: Options(Frame()).ProjectionFrames)));
    }

    [Fact]
    public void IdenticalBatsStayInTheirAssignedHalvesAndOcclusionCannotSwapPlayers()
    {
        var training = Frame();
        Bar(training, 75, 120, 30, 8);
        var profile = Learn(training, 75, 120);
        var left = new MarkerTipTracker();
        var right = new MarkerTipTracker();
        for (int frame = 0; frame < 12; frame++)
        {
            var image = Frame();
            int lx = 75 + frame * 4, rx = 245 - frame * 4;
            Bar(image, lx, 100, 30, 8, frame * 7);
            Bar(image, rx, 145, 30, 8, 90 - frame * 5);
            var all = Detect(image, profile);
            Assert.Equal(2, all.Candidates.Count);
            var now = Epoch.AddMilliseconds(frame * 40);
            var first = FootballTipAssignment.Update(left, 0, all, Field, now, now);
            var second = FootballTipAssignment.Update(right, 1, all, Field, now, now);
            var expected = frame > 0 ? FootballTipAction.Publish : FootballTipAction.Hold;
            Assert.Equal(expected, first.Action);
            Assert.Equal(expected, second.Action);
            if (frame == 0) continue;
            Assert.InRange(first.Tip!.Center.X, lx - 1, lx + 1);
            Assert.InRange(second.Tip!.Center.X, rx - 1, rx + 1);
            Assert.InRange(first.FieldPoint!.Value.X, (lx - 1.0) / Width, (lx + 1.0) / Width);
        }
        var occluded = Frame();
        Bar(occluded, 200, 145, 30, 8, 35);
        var lostAt = Epoch.AddMilliseconds(480);
        var onlyRight = Detect(occluded, profile);
        // A missing bar holds the last input instead of immediately pausing play.
        Assert.Equal(FootballTipAction.Hold, FootballTipAssignment.Update(left, 0, onlyRight, Field, lostAt, lostAt).Action);
        Assert.Equal(FootballTipAction.Publish, FootballTipAssignment.Update(right, 1, onlyRight, Field, lostAt, lostAt).Action);
        Bar(occluded, 117, 100, 30, 8, 70);
        var back = Detect(occluded, profile);
        var backAt = lostAt.AddMilliseconds(40);
        Assert.Equal(FootballTipAction.Hold, FootballTipAssignment.Update(left, 0, back, Field, backAt, backAt).Action);
        backAt = backAt.AddMilliseconds(40);
        var returned = FootballTipAssignment.Update(left, 0, back, Field, backAt, backAt);
        Assert.Equal(FootballTipAction.Publish, returned.Action);
        Assert.InRange(returned.Tip!.Center.X, 116, 118);
        Assert.NotEqual(FootballTipAction.Publish,
            FootballTipAssignment.Update(left, 0, back, Field, backAt, backAt.AddMilliseconds(400)).Action);
    }

    [Fact]
    public void TwoSimilarCrossbarsInOneHalfAreAmbiguousAndNeverChosenGlobally()
    {
        var image = Frame();
        Bar(image, 90, 120, 30, 8);
        var profile = Learn(image, 90, 120);
        Bar(image, 125, 120, 30, 8);
        Bar(image, 245, 120, 30, 8);
        var all = Detect(image, profile);
        Assert.Equal(3, all.Candidates.Count);
        var ambiguous = FootballTipAssignment.Update(new MarkerTipTracker(), 0, all, Field, Epoch, Epoch);
        Assert.Equal(FootballTipAction.Clear, ambiguous.Action);
        Assert.Null(ambiguous.Tip);
        var rightTracker = new MarkerTipTracker();
        Assert.Equal(FootballTipAction.Hold, FootballTipAssignment.Update(rightTracker, 1, all, Field, Epoch, Epoch).Action);
        var later = Epoch.AddMilliseconds(40);
        Assert.Equal(FootballTipAction.Publish, FootballTipAssignment.Update(rightTracker, 1, all, Field, later, later).Action);
    }

    [Fact]
    public void ProjectionWaitClearsAndOffPitchBarsDoNotCount()
    {
        var image = Frame();
        Bar(image, 110, 120, 30, 8);
        var profile = Learn(image, 110, 120);
        var tracker = new MarkerTipTracker();
        var waiting = FootballTipAssignment.Update(tracker, 0,
            new MarkerTipDetectionResult([], "waiting-for-projected-bat-reference"), Field, Epoch, Epoch);
        Assert.Equal(FootballTipAction.Clear, waiting.Action);
        // A second bar in the left half, but beside the pitch, is not this player's input.
        Bar(image, 40, 120, 30, 8);
        var all = Detect(image, profile);
        Assert.Equal(2, all.Candidates.Count);
        static PixelPoint? Pitch(PixelPoint camera) => camera.X < 70 ? null : Field(camera);
        Assert.Equal(FootballTipAction.Hold, FootballTipAssignment.Update(tracker, 0, all, Pitch, Epoch, Epoch).Action);
        var later = Epoch.AddMilliseconds(40);
        var tracked = FootballTipAssignment.Update(tracker, 0, all, Pitch, later, later);
        Assert.Equal(FootballTipAction.Publish, tracked.Action);
        Assert.InRange(tracked.Tip!.Center.X, 109, 111);
    }

    [Fact]
    public void DetectEachSharesOneFrameAcrossProfiles()
    {
        var image = Frame();
        Bar(image, 90, 120, 30, 8);
        Bar(image, 230, 120, 44, 8);
        var shortBar = Learn(image, 90, 120);
        var longBar = Learn(image, 230, 120);
        var results = BlackTipDetector.DetectEach(Width, Height, Width * 4, image, [shortBar, longBar]);
        Assert.Equal(2, results.Count);
        Assert.Equal(Detect(image, shortBar).Candidates.Select(tip => tip.Center), results[0].Candidates.Select(tip => tip.Center));
        Assert.Equal(Detect(image, longBar).Candidates.Select(tip => tip.Center), results[1].Candidates.Select(tip => tip.Center));
    }

    [Fact]
    public void SlenderPrintedBarLearnsUnderSaturatedProjectorLightAndReportsItsAxes()
    {
        var training = Frame();
        Bar(training, 160, 120, 46, 4);
        TintDarkInk(training);
        var profile = Learn(training, 160, 120);
        Assert.True(profile.IsValid);
        Assert.InRange(profile.AspectRatio, 10, 13);
        foreach (double angle in new[] { 0, 20, 45, 70, 90, 140, 175 })
        {
            var rotated = Frame();
            Bar(rotated, 163, 123, 46, 4, angle);
            TintDarkInk(rotated);
            var measured = Assert.Single(Detect(rotated, profile).Candidates);
            AssertBarGeometry(measured, 163, 123, 46, 4, angle, 1.8);
        }
        var roundHole = Frame();
        Disk(roundHole, 160, 120, 8, 20);
        TintDarkInk(roundHole);
        Assert.Empty(Detect(roundHole, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(roundHole, 160, 120));
        var projected = Frame();
        Bar(projected, 160, 120, 46, 4, 70);
        TintDarkInk(projected);
        var options = Options(projected);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, projected, profile, options).Candidates);
        Assert.Throws<InvalidOperationException>(() => BlackTipDetector.Learn(Width, Height, Width * 4,
            projected, new(160, 120), new(options.ProjectionFrames, options.FrameTime)));
    }

    [Fact]
    public void ThinBlackBarSurvivesUnevenSaturatedProjectionWithoutAcceptingBrightColour()
    {
        var training = Frame();
        Bar(training, 160, 120, 46, 4);
        TintDarkInk(training);
        var profile = Learn(training, 160, 120);
        foreach (double angle in new[] { 0, 25, 70, 90, 140 })
        {
            var camera = Frame();
            Bar(camera, 160, 120, 46, 4, angle);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 4;
                if (camera[i] != 20 || camera[i + 1] != 20 || camera[i + 2] != 20) continue;
                // A stationary real bar varied from V=38 to 89 with high saturation. The
                // former V=.24 saturation exception cut holes in otherwise dark ink.
                camera[i] = 8; camera[i + 1] = (byte)(46 + (x + y * 3) % 44); camera[i + 2] = 12;
            }
            var measured = Assert.Single(Detect(camera, profile).Candidates);
            AssertBarGeometry(measured, 160, 120, 46, 4, angle, 1.8);
            var reference = Options(Frame());
            Assert.Single(BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, reference).Candidates);
            // The same silhouette in the rendered image remains forbidden artwork.
            Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, Options(camera)).Candidates);
        }

        var brighterInk = Frame();
        Bar(brighterInk, 160, 120, 46, 4);
        TintInk(brighterInk, 82);
        Assert.True(Learn(brighterInk, 160, 120).IsValid);

        var brightColour = Frame();
        Bar(brightColour, 160, 120, 46, 4);
        TintInk(brightColour, 160);
        Assert.Empty(Detect(brightColour, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(brightColour, 160, 120));

        static void TintInk(byte[] image, byte value)
        {
            for (int i = 0; i < image.Length; i += 4)
            {
                if (image[i] != 20 || image[i + 1] != 20 || image[i + 2] != 20) continue;
                image[i] = 8; image[i + 1] = value; image[i + 2] = 12;
            }
        }
    }

    [Fact]
    public void ThinBarToleratesPixelEdgeVariationButBrokenAndBroadConcaveMarksDoNot()
    {
        var training = Frame();
        Bar(training, 160, 120, 46, 4);
        var profile = Learn(training, 160, 120);
        var ragged = training.ToArray();
        VaryEdges(ragged, 46, 4, 1);
        // The measured silhouette has solidity about .67: only a one-pixel variation
        // along each edge, yet the old scale-independent .74 guard rejected it.
        var observation = Assert.Single(Detect(ragged, profile).Candidates);
        AssertBarGeometry(observation, 160, 120, 46, 4, 0, 1.8);
        Assert.Single(BlackTipDetector.Detect(Width, Height, Width * 4, ragged, profile, Options(Frame())).Candidates);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, ragged, profile, Options(ragged)).Candidates);

        var broken = ragged.ToArray();
        var clear = Frame();
        for (int y = 116; y <= 124; y++)
        for (int x = 155; x <= 165; x++)
            Array.Copy(clear, (y * Width + x) * 4, broken, (y * Width + x) * 4, 4);
        Assert.Empty(Detect(broken, profile).Candidates);

        var broad = Frame();
        Bar(broad, 160, 120, 80, 12);
        var broadProfile = Learn(broad, 160, 120);
        VaryEdges(broad, 80, 12, 3);
        Assert.Empty(Detect(broad, broadProfile).Candidates);

        static void VaryEdges(byte[] image, int length, int thickness, int narrowHalfWidth)
        {
            var background = Frame();
            for (int y = 120 - thickness / 2; y <= 120 + thickness / 2; y++)
            for (int x = 160 - length / 2; x <= 160 + length / 2; x++)
            {
                int along = Math.Abs(x - 160);
                if (Math.Abs(y - 120) <= narrowHalfWidth || along > length / 2 - 6 || along < 3) continue;
                Array.Copy(background, (y * Width + x) * 4, image, (y * Width + x) * 4, 4);
            }
        }
    }

    [Fact]
    public void ParallelPairLearnsFromEitherStripAndPreservesLegacyProfiles()
    {
        var legacy = JsonSerializer.Deserialize<BlackTipProfile>("""
            {"Version":1,"Value":0.1,"Saturation":0.2,"NormalizedArea":0.001,"AspectRatio":4}
            """);
        Assert.NotNull(legacy);
        Assert.True(legacy.IsValid);
        Assert.Equal(1, legacy.BarCount);
        Assert.Equal(0, legacy.SpacingRatio);

        var training = Frame();
        Pair(training, 160, 120);
        foreach (int y in new[] { 112, 128 })
        {
            var profile = Learn(training, 160, y);
            Assert.True(profile.IsValid);
            Assert.Equal(2, profile.BarCount);
            Assert.InRange(profile.SpacingRatio, .32, .38);
            Assert.InRange(profile.AspectRatio, 7, 8.5);
            Assert.Equal(profile, JsonSerializer.Deserialize<BlackTipProfile>(JsonSerializer.Serialize(profile)));
            foreach (double angle in new[] { 0, 20, 45, 70, 90, 140, 175 })
            {
                var moved = Frame();
                Pair(moved, 167, 125, angle);
                var observation = Assert.Single(Detect(moved, profile).Candidates);
                AssertBarGeometry(observation, 167, 125, 46, 22, angle, 2.5);
            }
            Assert.False((profile with { BarCount = 3 }).IsValid);
            Assert.False((profile with { SpacingRatio = 0 }).IsValid);
        }
    }

    [Fact]
    public void PairRequiresBothAlignedStripsAtLearnedSpacingAndRejectsAmbiguousPartners()
    {
        var training = Frame();
        Pair(training, 160, 120);
        var profile = Learn(training, 160, 112);
        var missing = Frame();
        Bar(missing, 160, 112, 46, 6);
        Assert.Empty(Detect(missing, profile).Candidates);
        var wrongSpacing = Frame();
        Pair(wrongSpacing, 160, 120, spacing: 36);
        Assert.Empty(Detect(wrongSpacing, profile).Candidates);
        var crossed = Frame();
        Bar(crossed, 160, 100, 46, 6);
        Bar(crossed, 160, 135, 46, 6, 60);
        Assert.Empty(Detect(crossed, profile).Candidates);
        var alongInsteadOfAcross = Frame();
        Bar(alongInsteadOfAcross, 120, 120, 46, 6);
        Bar(alongInsteadOfAcross, 180, 120, 46, 6);
        Assert.Empty(Detect(alongInsteadOfAcross, profile).Candidates);
        var ambiguous = Frame();
        foreach (int y in new[] { 100, 116, 132 }) Bar(ambiguous, 160, y, 46, 6);
        Assert.Empty(Detect(ambiguous, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(ambiguous, 160, 116));

        var distracted = training.ToArray();
        Bar(distracted, 55, 50, 46, 6, 50);
        var found = Assert.Single(Detect(distracted, profile).Candidates);
        Assert.InRange(found.Center.X, 159, 161);
        Assert.InRange(found.Center.Y, 119, 121);
    }

    [Fact]
    public void PairedMarkerVetoesProjectedStripsAndAssignsTwoHumanMarkersByHalf()
    {
        var training = Frame();
        Pair(training, 160, 120);
        var profile = Learn(training, 160, 112);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, training, profile, Options(training)).Candidates);
        Assert.Throws<InvalidOperationException>(() => BlackTipDetector.Learn(Width, Height, Width * 4,
            training, new(160, 112), new(Options(training).ProjectionFrames, Epoch)));
        var projectedStrip = Frame();
        Bar(projectedStrip, 160, 112, 46, 6);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, training, profile, Options(projectedStrip)).Candidates);
        var closePair = Frame();
        Bar(closePair, 160, 115, 46, 6);
        Bar(closePair, 160, 124, 46, 6);
        var closeProfile = Learn(closePair, 160, 115);
        Assert.Equal(2, closeProfile.BarCount);
        var closeProjectedStrip = Frame();
        Bar(closeProjectedStrip, 160, 115, 46, 6);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, closePair, closeProfile,
            Options(closeProjectedStrip)).Candidates);

        var carReference = Frame();
        Fill(carReference, 133, 94, 54, 52, 145, 165, 185);
        Fill(carReference, 140, 99, 40, 12, 120, 140, 160);
        Fill(carReference, 121, 98, 7, 14, 18, 18, 18);
        Fill(carReference, 195, 127, 7, 14, 18, 18, 18);
        Disk(carReference, 160, 120, 13, 205);
        var onCar = carReference.ToArray();
        Pair(onCar, 160, 120);
        Assert.Single(BlackTipDetector.Detect(Width, Height, Width * 4, onCar, profile, Options(carReference)).Candidates);

        var players = Frame();
        Pair(players, 75, 100, 30);
        Pair(players, 245, 145, 110);
        var detection = BlackTipDetector.Detect(Width, Height, Width * 4, players, profile, Options(Frame()));
        Assert.Equal(2, detection.Candidates.Count);
        var trackers = new[] { new MarkerTipTracker(), new MarkerTipTracker() };
        for (int player = 0; player < 2; player++)
        {
            Assert.Equal(FootballTipAction.Hold,
                FootballTipAssignment.Update(trackers[player], player, detection, Field, Epoch, Epoch).Action);
            var later = Epoch.AddMilliseconds(40);
            var tracked = FootballTipAssignment.Update(trackers[player], player, detection, Field, later, later);
            Assert.Equal(FootballTipAction.Publish, tracked.Action);
            Assert.InRange(tracked.Tip!.Center.X, (player == 0 ? 75 : 245) - 1, (player == 0 ? 75 : 245) + 1);
        }
    }

    [Fact]
    public void PairRecoversOneGoalLitStripOnlyWithAnUnambiguousNormallyDarkAnchor()
    {
        var training = LitPair(51, 51);
        var profile = Learn(training, 160, 112);
        Assert.Equal(2, profile.BarCount);
        var litPartner = LitPair(51, 117);
        var recovered = Assert.Single(BlackTipDetector.Detect(Width, Height, Width * 4,
            litPartner, profile, Options(LitBackground())).Candidates);
        AssertBarGeometry(recovered, 160, 120, 46, 22, 0, 1.8);
        Assert.Empty(Detect(LitPair(117, 117), profile).Candidates);
        // A measured rectangular partner may be bright in green but dark in
        // luminance; a partner bright in luminance still cannot qualify.
        Assert.Single(Detect(LitPair(51, 160), profile).Candidates);
        Assert.Empty(Detect(LitPair(51, 220), profile).Candidates);
        // Single-bar profiles keep their original darkness threshold.
        var single = Assert.Single(Detect(litPartner, profile with { BarCount = 1, SpacingRatio = 0 }).Candidates);
        Assert.InRange(single.Center.Y, 111, 113);

        var third = training.ToArray();
        LitStrip(third, 144, 117);
        var originalPair = Assert.Single(Detect(third, profile).Candidates);
        Assert.InRange(originalPair.Center.Y, 119, 121);
        var ambiguous = LitBackground();
        LitStrip(ambiguous, 112, 117);
        LitStrip(ambiguous, 128, 51);
        LitStrip(ambiguous, 144, 117);
        Assert.Empty(Detect(ambiguous, profile).Candidates);

        // Neither an entirely projected pair nor one projected partner can supply evidence.
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, litPartner, profile, Options(litPartner)).Candidates);
        var projectedPartner = LitBackground();
        LitStrip(projectedPartner, 128, 117);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, litPartner, profile, Options(projectedPartner)).Candidates);
        var projectedAnchor = LitBackground();
        LitStrip(projectedAnchor, 112, 51);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, litPartner, profile, Options(projectedAnchor)).Candidates);

        static byte[] LitBackground()
        {
            var image = Frame();
            for (int i = 0; i < image.Length; i += 4)
            { image[i] += 30; image[i + 1] += 65; image[i + 2] += 30; }
            return image;
        }
        static void LitStrip(byte[] image, int y, byte value) => Fill(image, 137, y - 3, 47, 7, 8, value, 12);
        static byte[] LitPair(byte first, byte second)
        {
            var image = LitBackground();
            LitStrip(image, 112, first); LitStrip(image, 128, second);
            return image;
        }
    }

    [Fact]
    public void PairGapToleratesHalfPixelEdgeUncertaintyButRejectsCloserStrips()
    {
        var training = Frame();
        Bar(training, 160, 106, 60, 20, brightness: 8);
        Bar(training, 160, 134, 60, 20, brightness: 8);
        var profile = Learn(training, 160, 106);
        Assert.Equal(2, profile.BarCount);
        var camera = training.ToArray();
        // A tiny one-pixel edge bump changes the minimum rectangle's thickness
        // more than its actual ink centre, as in the stationary projected-water frame.
        Fill(camera, 156, 117, 9, 1, 8, 8, 8);
        var found = Assert.Single(BlackTipDetector.Detect(Width, Height, Width * 4,
            camera, profile, Options(Frame())).Candidates);
        Assert.InRange(found.Center.X, 159, 161);
        Assert.InRange(found.Center.Y, 119, 121);
        var tooClose = Frame();
        Bar(tooClose, 160, 106, 60, 20, brightness: 8);
        Fill(tooClose, 156, 117, 9, 1, 8, 8, 8);
        Bar(tooClose, 160, 133, 60, 20, brightness: 8);
        Assert.Empty(Detect(tooClose, profile).Candidates);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, Options(camera)).Candidates);
    }

    [Fact]
    public void PairSeparatesDarkInkFromTouchingProjectedCracksWithoutLearningTheCracks()
    {
        var training = Frame();
        Bar(training, 160, 106, 60, 16, brightness: 8);
        Bar(training, 160, 134, 60, 16, brightness: 8);
        var profile = Learn(training, 160, 106);
        Assert.Equal(2, profile.BarCount);
        var projected = Frame();
        // A dark rock seam touches the lower strip. At the normal minimum threshold
        // it makes that strip too thick and too close to its neighbour to form a pair.
        Fill(projected, 164, 118, 12, 13, 40, 40, 40);
        var camera = projected.ToArray();
        Bar(camera, 160, 106, 60, 16, brightness: 8);
        Bar(camera, 160, 134, 60, 16, brightness: 8);
        var result = BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, Options(projected));
        var measured = Assert.Single(result.Candidates);
        Assert.Equal("dark-core", result.Source);
        AssertBarGeometry(measured, 160, 120, 60, 44, 0, 2);
        foreach (int y in new[] { 106, 134 })
        {
            var learned = BlackTipDetector.Learn(Width, Height, Width * 4, camera, new(160, y),
                new(Options(projected).ProjectionFrames, Epoch));
            Assert.Equal(2, learned.BarCount);
            Assert.InRange(learned.AspectRatio, 3.6, 3.9);
            Assert.InRange(learned.NormalizedArea / profile.NormalizedArea, .98, 1.02);
        }
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, projected, profile, Options(projected)).Candidates);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, Options(camera)).Candidates);
        var onePhysical = projected.ToArray();
        Bar(onePhysical, 160, 106, 60, 16, brightness: 8);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, Options(onePhysical)).Candidates);
        // A clean player marker elsewhere must not hide a recoverable second marker.
        Bar(camera, 65, 106, 60, 16, brightness: 8);
        Bar(camera, 65, 134, 60, 16, brightness: 8);
        var both = BlackTipDetector.Detect(Width, Height, Width * 4, camera, profile, Options(projected));
        Assert.True(both.Candidates.Count == 2, JsonSerializer.Serialize(both));
    }

    [Fact]
    public void DarkCoreRecoveryCannotChooseAnAmbiguousPairOrInventAMissingStrip()
    {
        var training = Frame();
        Bar(training, 160, 106, 60, 16, brightness: 8);
        Bar(training, 160, 134, 60, 16, brightness: 8);
        var profile = Learn(training, 160, 106);
        var camera = Frame();
        foreach (int y in new[] { 106, 134, 162 })
        {
            Fill(camera, 164, y - 19, 12, 16, 40, 40, 40);
            Bar(camera, 160, y, 60, 16, brightness: 8);
        }
        var ambiguous = Detect(camera, profile);
        Assert.Empty(ambiguous.Candidates);
        Assert.NotEmpty(ambiguous.AmbiguousMarkerCenters);
        Assert.Throws<InvalidOperationException>(() => Learn(camera, 160, 134));
        var missing = Frame();
        Fill(missing, 164, 115, 12, 16, 40, 40, 40);
        Bar(missing, 160, 134, 60, 16, brightness: 8);
        Assert.Empty(Detect(missing, profile).Candidates);
        // Lowering the threshold must not turn one rectangle with a lighter interior
        // stripe into a newly learned pair, even when its cores have the right spacing.
        var splitSingle = Frame();
        Bar(splitSingle, 160, 120, 60, 44, brightness: 8);
        Fill(splitSingle, 129, 115, 63, 11, 40, 40, 40);
        Assert.Empty(Detect(splitSingle, profile).Candidates);
        Assert.Throws<InvalidOperationException>(() => Learn(splitSingle, 160, 106));
        // Keep ambiguity already measured at the normal threshold even if a darker
        // mask would remove the third, brighter strip.
        var normalAmbiguity = training.ToArray();
        Bar(normalAmbiguity, 160, 162, 60, 16, brightness: 40);
        Assert.Empty(Detect(normalAmbiguity, profile).Candidates);
        Assert.NotEmpty(Detect(normalAmbiguity, profile).AmbiguousMarkerCenters);
        Assert.Throws<InvalidOperationException>(() => Learn(normalAmbiguity, 160, 134));
    }

    [Fact]
    public void BarGeometryUsesRawCoordinatesAfterIndependentImageScales()
    {
        // The detector reduces this to 1920 x 360, giving different X and Y scale factors.
        const int width = 3841, height = 721;
        var training = Frame(width, height);
        Bar(training, 1800, 360, 180, 24, 25, width: width, height: height);
        var profile = BlackTipDetector.Learn(width, height, width * 4, training, new(1800, 360));
        foreach (double angle in new[] { 15, 45, 90, 130 })
        {
            var rotated = Frame(width, height);
            Bar(rotated, 1900, 370, 180, 24, angle, width: width, height: height);
            var measured = Assert.Single(BlackTipDetector.Detect(width, height, width * 4, rotated, profile).Candidates);
            AssertBarGeometry(measured, 1900, 370, 180, 24, angle, 4);
        }
    }

    [Fact]
    public void MarkerObservationsRoundTripWithOptionalBlackBarGeometry()
    {
        var observation = new MarkerTipObservation(new(10, 20), 3, 28, .9);
        Assert.Null(observation.Bar);
        Assert.Equal(observation, JsonSerializer.Deserialize<MarkerTipObservation>(JsonSerializer.Serialize(observation)));
        var withBar = observation with { Bar = new(new(5, 20), new(15, 20), new(10, 18), new(10, 22)) };
        Assert.Equal(withBar, JsonSerializer.Deserialize<MarkerTipObservation>(JsonSerializer.Serialize(withBar)));
    }

    private static void AssertBarGeometry(MarkerTipObservation observation, double cx, double cy,
        double length, double thickness, double angle, double tolerance)
    {
        var bar = Assert.IsType<BlackBarGeometry>(observation.Bar);
        double dx = bar.End2.X - bar.End1.X, dy = bar.End2.Y - bar.End1.Y;
        double sx = bar.Side2.X - bar.Side1.X, sy = bar.Side2.Y - bar.Side1.Y;
        double measuredLength = Math.Sqrt(dx * dx + dy * dy), measuredThickness = Math.Sqrt(sx * sx + sy * sy);
        Assert.InRange(measuredLength, length - tolerance, length + tolerance);
        Assert.InRange(measuredThickness, thickness - tolerance, thickness + tolerance);
        Assert.InRange((bar.End1.X + bar.End2.X) * .5, cx - tolerance, cx + tolerance);
        Assert.InRange((bar.End1.Y + bar.End2.Y) * .5, cy - tolerance, cy + tolerance);
        Assert.InRange((bar.Side1.X + bar.Side2.X) * .5, cx - tolerance, cx + tolerance);
        Assert.InRange((bar.Side1.Y + bar.Side2.Y) * .5, cy - tolerance, cy + tolerance);
        Assert.InRange(Math.Abs(dx * sx + dy * sy) / (measuredLength * measuredThickness), 0, .001);
        double radians = angle * Math.PI / 180;
        Assert.InRange(Math.Abs(dx * Math.Cos(radians) + dy * Math.Sin(radians)) / measuredLength, .999, 1.000001);
    }

    private static void TintDarkInk(byte[] image)
    {
        for (int i = 0; i < image.Length; i += 4)
        {
            if (image[i] != 20 || image[i + 1] != 20 || image[i + 2] != 20) continue;
            // The real printed marker's centre measured V=40-58 and S=199-255 out of 255.
            image[i] = 8; image[i + 1] = 52; image[i + 2] = 12;
        }
    }

    [Fact]
    public void SearchAreaCropMeasuresLikeTheFullFrameAndIgnoresTheRoomBeyondIt()
    {
        const int width = 640, height = 400;
        var training = Frame(width, height);
        WidePair(training, 400, 200, 0);
        var profile = BlackTipDetector.Learn(width, height, width * 4, training, new(400, 192));
        Assert.Equal(2, profile.BarCount);
        var image = Frame(width, height);
        WidePair(image, 420, 210, 35);
        WidePair(image, 100, 300, 100);
        var full = BlackTipDetector.DetectEach(width, height, width * 4, image, [profile])[0];
        Assert.Equal(2, full.Candidates.Count);
        // The crop starts away from the frame origin, exercising every raw/image offset.
        PixelPoint[] grass = [new(360, 150), new(480, 150), new(480, 270), new(360, 270)];
        var cropped = BlackTipDetector.DetectEach(width, height, width * 4, image, [profile], searchArea: grass)[0];
        var inside = Assert.Single(cropped.Candidates);
        var reference = full.Candidates.Single(item => item.Center.X > 300);
        Assert.Equal(reference.Center.X, inside.Center.X, 6);
        Assert.Equal(reference.Center.Y, inside.Center.Y, 6);
        Assert.Equal(reference.AreaPixels, inside.AreaPixels, 6);
        Assert.Equal(reference.Score, inside.Score, 6);
        foreach (var (expected, actual) in new[] { (reference.Bar!.End1, inside.Bar!.End1), (reference.Bar.End2, inside.Bar.End2),
            (reference.Bar.Side1, inside.Bar.Side1), (reference.Bar.Side2, inside.Bar.Side2) })
        {
            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
        }

        // The projected-scene veto samples the cropped camera image at the same raw pixels.
        double[] map = [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1];
        var projectedMarker = new MarkerTipDetectionOptions(ProjectionFrames:
            [new(width, height, image, map, Epoch)], FrameTime: Epoch);
        Assert.Empty(BlackTipDetector.DetectEach(width, height, width * 4, image, [profile], projectedMarker,
            searchArea: grass)[0].Candidates);
        var emptyBoard = new MarkerTipDetectionOptions(ProjectionFrames:
            [new(width, height, Frame(width, height), map, Epoch)], FrameTime: Epoch);
        Assert.Single(BlackTipDetector.DetectEach(width, height, width * 4, image, [profile], emptyBoard,
            searchArea: grass)[0].Candidates);
    }

    [Fact]
    public void HighResolutionCropPreservesRawBarGeometryAndProjectedArtworkRejection()
    {
        // Both the full frame and the offset crop require reduction, with independently
        // rounded X/Y dimensions. Resampling can shift edges by a raw camera pixel.
        const int width = 3841, height = 1441;
        var training = Frame(width, height);
        Bar(training, 2400, 900, 120, 16, width: width, height: height);
        var profile = BlackTipDetector.Learn(width, height, width * 4, training, new(2400, 900));
        PixelPoint[] grass = [new(2300, 830), new(2670, 830), new(2670, 1110), new(2300, 1110)];
        double[] map = [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1];
        var emptyBoard = new MarkerTipDetectionOptions(ProjectionFrames:
            [new(width, height, Frame(width, height), map, Epoch)], FrameTime: Epoch);
        foreach (double angle in new[] { 35, 90 })
        {
            var camera = Frame(width, height);
            Bar(camera, 2500, 950, 120, 16, angle, width: width, height: height);
            Bar(camera, 600, 300, 120, 16, 15, width: width, height: height);
            var full = BlackTipDetector.DetectEach(width, height, width * 4, camera, [profile], emptyBoard)[0];
            Assert.Equal(2, full.Candidates.Count);
            var expected = full.Candidates.Single(item => item.Center.X > 2000);
            var cropped = BlackTipDetector.DetectEach(width, height, width * 4, camera, [profile], emptyBoard,
                searchArea: grass)[0];
            var actual = Assert.Single(cropped.Candidates);
            AssertBarGeometry(expected, 2500, 950, 120, 16, angle, 4);
            AssertBarGeometry(actual, 2500, 950, 120, 16, angle, 4);
            Assert.InRange(actual.Center.X, expected.Center.X - 2, expected.Center.X + 2);
            Assert.InRange(actual.Center.Y, expected.Center.Y - 2, expected.Center.Y + 2);
            Assert.InRange(actual.AreaPixels / expected.AreaPixels, .85, 1.15);

            var projectedMarker = new MarkerTipDetectionOptions(ProjectionFrames:
                [new(width, height, camera, map, Epoch)], FrameTime: Epoch);
            Assert.Empty(BlackTipDetector.DetectEach(width, height, width * 4, camera, [profile], projectedMarker,
                searchArea: grass)[0].Candidates);
        }
    }

    [Fact]
    public void CroppedRecentHintRecoversLocallyLitPairWithoutAcceptingProjectedOrExpiredEvidence()
    {
        const int width = 640, height = 400;
        var training = LitPair(400, 200, 0, 20);
        var profile = BlackTipDetector.Learn(width, height, width * 4, training, new(400, 192));
        Assert.Equal(2, profile.BarCount);
        var acquired = Assert.Single(BlackTipDetector.Detect(width, height, width * 4, training, profile).Candidates);
        var camera = LitPair(420, 210, 20, 75);
        PixelPoint[] grass = [new(360, 150), new(480, 150), new(480, 270), new(360, 270)];
        double[] map = [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1];
        var options = new MarkerTipDetectionOptions(ProjectionFrames:
            [new(width, height, Background(), map, Epoch)], FrameTime: Epoch.AddMilliseconds(80));
        var hint = new BlackTipSearchHint(acquired, new(420, 210), Epoch);

        // Both strips have brightened beyond the ordinary threshold. Neither the crop
        // nor a stale remembered position may independently turn them into a marker.
        Assert.Empty(BlackTipDetector.DetectEach(width, height, width * 4, camera, [profile], options,
            searchArea: grass)[0].Candidates);
        Assert.Empty(BlackTipDetector.DetectEach(width, height, width * 4, camera, [profile], options,
            [hint with { LastFullFrame = Epoch.AddMilliseconds(-400) }], grass)[0].Candidates);
        var full = BlackTipDetector.DetectEach(width, height, width * 4, camera, [profile], options, [hint])[0];
        var cropped = BlackTipDetector.DetectEach(width, height, width * 4, camera, [profile], options, [hint], grass)[0];
        Assert.Equal("local-contrast", full.Source);
        Assert.Equal("local-contrast", cropped.Source);
        var expected = Assert.Single(full.Candidates);
        var actual = Assert.Single(cropped.Candidates);
        AssertBarGeometry(actual, 420, 210, 46, 22, 20, 2.5);
        Assert.Equal(expected.Center.X, actual.Center.X, 6);
        Assert.Equal(expected.Center.Y, actual.Center.Y, 6);
        Assert.Equal(expected.AreaPixels, actual.AreaPixels, 6);

        var projectedMarker = options with { ProjectionFrames = [new(width, height, camera, map, Epoch)] };
        Assert.Empty(BlackTipDetector.DetectEach(width, height, width * 4, camera, [profile], projectedMarker,
            [hint], grass)[0].Candidates);

        static byte[] Background()
        {
            var image = Frame(width, height);
            for (int i = 0; i < image.Length; i += 4)
            { image[i] = 150; image[i + 1] = 195; image[i + 2] = 165; }
            return image;
        }

        static byte[] LitPair(int cx, int cy, double angle, byte brightness)
        {
            var image = Background();
            double radians = angle * Math.PI / 180;
            int dx = (int)Math.Round(-Math.Sin(radians) * 8), dy = (int)Math.Round(Math.Cos(radians) * 8);
            Bar(image, cx - dx, cy - dy, 46, 6, angle, brightness, width, height);
            Bar(image, cx + dx, cy + dy, 46, 6, angle, brightness, width, height);
            return image;
        }
    }

    [Fact]
    public void ProjectorTintRecoveryRequiresFreshMeasuredDarkInk()
    {
        var background = Frame();
        Fill(background, 0, 0, Width, Height, 160, 160, 160);
        var training = background.ToArray();
        Bar(training, 160, 112, 46, 6, brightness: 51);
        Bar(training, 160, 128, 46, 6, brightness: 51);
        var profile = Learn(training, 160, 112);
        var full = Assert.Single(Detect(training, profile).Candidates);
        var hint = new BlackTipSearchHint(full, full.Center, Epoch);
        var tinted = training.ToArray();
        // Observed under the red car: max channel 163, luminance only 88.
        Fill(tinted, 137, 109, 47, 7, 41, 59, 163);
        var options = Options(background) with { FrameTime = Epoch.AddMilliseconds(33) };
        MarkerTipDetectionResult DetectTinted(byte[] camera, MarkerTipDetectionOptions context,
            BlackTipSearchHint? searchHint) => BlackTipDetector.DetectEach(Width, Height, Width * 4,
                camera, [profile], context, [searchHint])[0];

        var recovered = DetectTinted(tinted, options, hint);
        Assert.Equal("measured-edges", recovered.Source);
        AssertBarGeometry(Assert.Single(recovered.Candidates), 160, 120, 46, 22, 0, 2.5);
        // Fresh physical ink plus measured edges permits global reacquisition,
        // independently of an absent or expired prior pose.
        Assert.Single(DetectTinted(tinted, options, null).Candidates);
        Assert.Single(DetectTinted(tinted, options with { FrameTime = Epoch.AddMilliseconds(400) }, hint).Candidates);
        var bothTinted = tinted.ToArray();
        Fill(bothTinted, 137, 125, 47, 7, 41, 59, 163);
        Assert.Empty(DetectTinted(bothTinted, options, hint).Candidates);

        // A rendered pair or either rendered partner cannot supply physical ink.
        Assert.Empty(DetectTinted(tinted, Options(tinted) with { FrameTime = options.FrameTime }, hint).Candidates);
        var projectedTint = background.ToArray();
        Fill(projectedTint, 137, 109, 47, 7, 41, 59, 163);
        Assert.Empty(DetectTinted(tinted, Options(projectedTint) with { FrameTime = options.FrameTime }, hint).Candidates);
        var projectedAnchor = background.ToArray();
        Bar(projectedAnchor, 160, 128, 46, 6, brightness: 51);
        Assert.Empty(DetectTinted(tinted, Options(projectedAnchor) with { FrameTime = options.FrameTime }, hint).Candidates);
        // A distant prior pose does not suppress freshly measured global ink.
        Assert.Single(DetectTinted(tinted, options, hint with { ExpectedCenter = new(60, 60) }).Candidates);
    }

    [Fact]
    public void FaintProjectedStripesCannotExplainHighContrastPhysicalInk()
    {
        var projection = Frame();
        Fill(projection, 0, 0, Width, Height, 160, 160, 160);
        Bar(projection, 160, 120, 46, 6, brightness: 136);
        var camera = projection.ToArray();
        Bar(camera, 160, 120, 46, 6);
        var profile = Learn(camera, 160, 120);
        // The weak rendered stripe has exactly the physical bar's shape. Pure
        // normalized correlation wrongly vetoes it despite a 5.8x contrast gap.
        var physical = Assert.Single(BlackTipDetector.Detect(Width, Height, Width * 4,
            camera, profile, Options(projection)).Candidates);
        AssertBarGeometry(physical, 160, 120, 46, 6, 0, 1);

        // Real projected ink still vetoes, including an ordinary exposure change.
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4,
            camera, profile, Options(camera)).Candidates);
        var brighterCamera = camera.ToArray();
        Fill(brighterCamera, 0, 0, Width, Height, 240, 240, 240);
        Bar(brighterCamera, 160, 120, 46, 6);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4,
            brighterCamera, profile, Options(camera)).Candidates);
    }

    [Fact]
    public void PooledDetectionBuffersNeverCarryPixelsBetweenFrames()
    {
        var training = Frame();
        Pair(training, 160, 120);
        var profile = Learn(training, 160, 112);
        var first = Frame();
        Pair(first, 120, 110, 25);
        var expected = Detect(first, profile);
        Assert.Single(expected.Candidates);
        var crowded = Frame();
        Pair(crowded, 90, 80, 60);
        Pair(crowded, 230, 160, 140);
        Bar(crowded, 260, 60, 30, 8, 10);
        Assert.Equal(2, Detect(crowded, profile).Candidates.Count);
        var again = Detect(first, profile);
        Assert.Equal(expected.Candidates, again.Candidates);
        Assert.Equal(expected.SupportingBars, again.SupportingBars);
    }

    [Fact]
    public void MeasuredEdgesRecoverInkWithBrightInteriorAndAttachedCracksAcrossPoses()
    {
        var training = Frame();
        Bar(training, 160, 106, 60, 18);
        Bar(training, 160, 134, 60, 18);
        var profile = Learn(training, 160, 134);
        foreach (double angle in new[] { 0, 25, 90, 140 })
        foreach (int cx in new[] { 120, 200 })
        {
            var projected = Frame();
            EdgeFixture(projected, cx, 120, angle, sceneryOnly: true);
            var image = projected.ToArray();
            EdgeFixture(image, cx, 120, angle);
            var result = BlackTipDetector.Detect(Width, Height, Width * 4, image, profile, Options(projected));
            var found = Assert.Single(result.Candidates);
            Assert.Equal("measured-edges", result.Source);
            Assert.InRange(found.Center.X, cx - 2, cx + 2);
            Assert.InRange(found.Center.Y, 118, 122);
            var cropped = BlackTipDetector.DetectEach(Width, Height, Width * 4, image, [profile], Options(projected),
                searchArea: [new(cx - 45, 70), new(cx + 45, 70), new(cx + 45, 170), new(cx - 45, 170)])[0];
            Assert.Equal(result.Candidates, cropped.Candidates);
            Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, projected, profile, Options(projected)).Candidates);
            Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, image, profile, Options(image)).Candidates);
            var projectedPartner = projected.ToArray();
            EdgeFixture(projectedPartner, cx, 120, angle, omitAnchor: true);
            Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, image, profile, Options(projectedPartner)).Candidates);
        }
    }

    [Fact]
    public void MeasuredEdgesRecoverProjectedColourOnlyBesideFreshPhysicalBlackInk()
    {
        var training = Frame();
        Bar(training, 160, 106, 60, 18);
        Bar(training, 160, 134, 60, 18);
        var profile = Learn(training, 160, 134);
        var image = Frame();
        EdgeFixture(image, 160, 120, 0);
        TintInk(image, 120);
        var result = BlackTipDetector.Detect(Width, Height, Width * 4, image, profile, Options(Frame()));
        Assert.Single(result.Candidates);
        Assert.Equal("measured-edges", result.Source);
        // Every recovered strip still passes the projected-artwork veto.
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, image, profile, Options(image)).Candidates);
        var projectedPartner = image.ToArray();
        Fill(projectedPartner, 129, 124, 63, 22, 210, 210, 210);
        Assert.Empty(BlackTipDetector.Detect(Width, Height, Width * 4, image, profile, Options(projectedPartner)).Candidates);
        // Both tinted strips have luminance edges, but no ordinary dark anchor.
        TintInk(image, Height);
        Assert.Empty(Detect(image, profile).Candidates);

        static void TintInk(byte[] pixels, int bottom)
        {
            for (int y = 0; y < bottom; y++)
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 4;
                if (pixels[offset] < 50 && pixels[offset + 1] < 50 && pixels[offset + 2] < 50)
                    pixels[offset] = 130; // Blue projection: high max RGB, low luminance.
            }
        }
    }

    [Fact]
    public void MeasuredEdgesNeedTwoSolidStripsAndCannotResolveThreePartners()
    {
        var training = Frame();
        Bar(training, 160, 106, 60, 18);
        Bar(training, 160, 134, 60, 18);
        var profile = Learn(training, 160, 134);
        foreach (int kind in new[] { 0, 1, 2, 3 })
        {
            var image = Frame();
            Bar(image, 160, 134, 60, 18);
            if (kind == 0) // A long crack supplies neither the second side nor the ends.
                Bar(image, 160, 102, 100, 3, brightness: 30);
            if (kind == 1) // A hollow rectangle has edges but no dark interior.
            {
                Bar(image, 160, 106, 60, 18);
                Bar(image, 160, 106, 52, 12, brightness: 110);
            }
            if (kind == 2) // A broad dark patch does not become a bar at an ROI boundary.
                Bar(image, 160, 99, 130, 35);
            if (kind == 3) // A lighter stripe inside one wide block is not a clear gap.
            {
                Bar(image, 160, 120, 60, 46);
                Bar(image, 160, 120, 60, 10, brightness: 40);
                Bar(image, 160, 106, 9, 7, brightness: 90);
            }
            Assert.Empty(Detect(image, profile).Candidates);
        }
        var three = Frame();
        EdgeFixture(three, 160, 120, 0);
        Bar(three, 160, 162, 60, 18);
        Bar(three, 160, 162, 9, 7, brightness: 90);
        var ambiguous = Detect(three, profile);
        Assert.Empty(ambiguous.Candidates);
        Assert.NotEmpty(ambiguous.AmbiguousMarkerCenters);
    }

    [Fact]
    public void MeasuredEdgeRecoveryPreservesAnotherPlayerAndRawCoordinatesInReducedCrops()
    {
        var training = Frame();
        Bar(training, 160, 106, 60, 18);
        Bar(training, 160, 134, 60, 18);
        var profile = Learn(training, 160, 134);
        var twoPlayers = Frame();
        Bar(twoPlayers, 75, 106, 60, 18);
        Bar(twoPlayers, 75, 134, 60, 18);
        EdgeFixture(twoPlayers, 230, 120, 0);
        var players = Detect(twoPlayers, profile).Candidates.OrderBy(item => item.Center.X).ToArray();
        Assert.Equal(2, players.Length);
        Assert.InRange(players[0].Center.X, 74, 76);
        Assert.InRange(players[1].Center.X, 228, 232);

        const int width = 2560, height = 1920;
        var projected = Frame(width, height);
        EdgeFixture(projected, 1280, 960, 25, sceneryOnly: true, width: width, height: height);
        var image = projected.ToArray();
        EdgeFixture(image, 1280, 960, 25, width: width, height: height);
        var options = new MarkerTipDetectionOptions(ProjectionFrames:
            [new(width, height, projected, [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1], Epoch)], FrameTime: Epoch);
        var reduced = BlackTipDetector.DetectEach(width, height, width * 4, image,
            [profile with { NormalizedArea = profile.NormalizedArea / 64 }], options,
            searchArea: [new(1200, 880), new(1360, 880), new(1360, 1040), new(1200, 1040)])[0];
        var found = Assert.Single(reduced.Candidates);
        Assert.Equal("measured-edges", reduced.Source);
        Assert.InRange(found.Center.X, 1278, 1282);
        Assert.InRange(found.Center.Y, 958, 962);
    }

    private static void EdgeFixture(byte[] image, int cx, int cy, double angle,
        bool sceneryOnly = false, bool omitAnchor = false, int width = Width, int height = Height)
    {
        double radians = angle * Math.PI / 180;
        (int X, int Y) Point(double u, double v) => ((int)Math.Round(cx + u * Math.Cos(radians) - v * Math.Sin(radians)),
            (int)Math.Round(cy + u * Math.Sin(radians) + v * Math.Cos(radians)));
        var crack = Point(27, -23);
        Bar(image, crack.X, crack.Y, 25, 5, angle + 35, 30, width, height);
        if (sceneryOnly) return;
        var upper = Point(0, -14);
        var lower = Point(0, 14);
        Bar(image, upper.X, upper.Y, 60, 18, angle, width: width, height: height);
        // Projected bright pebbles corrupt the threshold contour's centroid.
        Bar(image, upper.X, upper.Y, 9, 7, angle, 90, width, height);
        if (!omitAnchor) Bar(image, lower.X, lower.Y, 60, 18, angle, width: width, height: height);
    }

    [Fact]
    public void RecoveredThirdStripInvalidatesAnEarlierCleanPair()
    {
        var training = Frame();
        Bar(training, 160, 106, 60, 18);
        Bar(training, 160, 134, 60, 18);
        var profile = Learn(training, 160, 134) with { SpacingRatio = 25.0 / 60 };
        var image = Frame();
        Bar(image, 160, 95, 60, 16);
        Bar(image, 160, 120, 60, 16);
        Assert.Single(Detect(image, profile).Candidates);
        Bar(image, 160, 145, 60, 16);
        // This attached dark patch leaves a valid single component but makes
        // its contour too thick for pairing. Its observed ink edges still fit.
        Fill(image, 177, 153, 14, 14, 30, 30, 30);
        var result = Detect(image, profile);
        Assert.Empty(result.Candidates);
        Assert.NotEmpty(result.AmbiguousMarkerCenters);
    }

    [Fact]
    public void ContainedStripMeasurementsHaveSymmetricIdentityWithoutMergingSeparateInk()
    {
        var same = typeof(BlackTipDetector).GetMethod("SameStrip",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .CreateDelegate<Func<MarkerTipObservation, MarkerTipObservation, bool>>();
        // Exact geometry from two stationary-camera failures: the second fit is a
        // projected-texture subdivision of the first, not a third physical strip.
        foreach (var (whole, part) in new[]
        {
            (Strip(951.90, 610.56, 71, 17, -17.2), Strip(947.34, 611.80, 48, 19, -7.05)),
            (Strip(954.69, 610.51, 63, 17, -11.31), Strip(946.37, 612.99, 46, 19, -1.34))
        })
        {
            Assert.True(same(whole, part));
            Assert.True(same(part, whole));
        }
        var first = Strip(160, 120, 63, 18, 0);
        foreach (var separate in new[]
        {
            Strip(160, 147, 63, 18, 0), // A genuinely separate parallel partner.
            Strip(220, 120, 48, 18, 0), // Adjacent collinear ink, without containment.
            Strip(160, 120, 20, 18, 0), // An unrelated small mark inside the outline.
            Strip(160, 120, 63, 5, 0),  // A thin crack is not the same measured bar.
            Strip(160, 120, 63, 18, 25)
        })
        {
            Assert.False(same(first, separate));
            Assert.False(same(separate, first));
        }
        var consolidate = typeof(BlackTipDetector).GetMethod("ConsolidateStrips",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        List<MarkerTipObservation> Group(IReadOnlyList<MarkerTipObservation> input,
            out Dictionary<MarkerTipObservation, IReadOnlyList<MarkerTipObservation>> aliases)
        {
            object?[] arguments = [input, null];
            var result = (List<MarkerTipObservation>)consolidate.Invoke(null, arguments)!;
            aliases = (Dictionary<MarkerTipObservation, IReadOnlyList<MarkerTipObservation>>)arguments[1]!;
            return result;
        }
        var complete = Strip(160, 120, 71, 18, 0) with { Score = .739 };
        var left = Strip(150, 120, 48, 19, 8) with { Score = .741 };
        var right = Strip(170, 120, 48, 19, -8) with { Score = .742 };
        Assert.False(same(left, right));
        foreach (var order in new[]
        {
            new[] { complete, left, right }, [complete, right, left], [left, complete, right],
            [left, right, complete], [right, complete, left], [right, left, complete]
        })
        {
            Assert.Equal(right, Assert.Single(Group(order, out var aliases)));
            Assert.Contains(left, aliases[right]);
            Assert.Contains(complete, aliases[right]);
        }
        Assert.Equal(3, Group([first, Strip(160, 147, 63, 18, 0), Strip(160, 174, 63, 18, 0)], out _).Count);

        var image = Frame();
        foreach (int cx in new[] { 120, 195 })
        {
            Bar(image, cx, 106, 60, 18);
            Bar(image, cx, 134, 60, 18);
        }
        var profile = Learn(image, 120, 134);
        Assert.Equal(2, Detect(image, profile).Candidates.Count);

        static MarkerTipObservation Strip(double x, double y, double length, double width, double angle)
        {
            double ux = Math.Cos(angle * Math.PI / 180), uy = Math.Sin(angle * Math.PI / 180);
            PixelPoint Point(double u, double v) => new(x + ux * u - uy * v, y + uy * u + ux * v);
            return new(new(x, y), Math.Sqrt(length * width / Math.PI), length * width, .8)
            { Bar = new(Point(-length * .5, 0), Point(length * .5, 0), Point(0, -width * .5), Point(0, width * .5)) };
        }
    }

    private static void WidePair(byte[] image, int cx, int cy, double angle)
    {
        const int width = 640, height = 400;
        double radians = angle * Math.PI / 180;
        int dx = (int)Math.Round(-Math.Sin(radians) * 8), dy = (int)Math.Round(Math.Cos(radians) * 8);
        Bar(image, cx - dx, cy - dy, 46, 6, angle, width: width, height: height);
        Bar(image, cx + dx, cy + dy, 46, 6, angle, width: width, height: height);
    }

    private static void Pair(byte[] image, int cx, int cy, double angle = 0, int spacing = 16)
    {
        double radians = angle * Math.PI / 180;
        int dx = (int)Math.Round(-Math.Sin(radians) * spacing * .5);
        int dy = (int)Math.Round(Math.Cos(radians) * spacing * .5);
        Bar(image, cx - dx, cy - dy, 46, 6, angle);
        Bar(image, cx + dx, cy + dy, 46, 6, angle);
    }

    private static PixelPoint? Field(PixelPoint camera) => new PixelPoint(camera.X / Width, camera.Y / Height);

    private static BlackTipProfile Learn(byte[] image, int x, int y) =>
        BlackTipDetector.Learn(Width, Height, Width * 4, image, new(x, y));
    private static MarkerTipDetectionResult Detect(byte[] image, BlackTipProfile profile) =>
        BlackTipDetector.Detect(Width, Height, Width * 4, image, profile);
    private static MarkerTipDetectionOptions Options(byte[] projected) => new(ProjectionFrames:
        [new(Width, Height, projected, [1.0 / Width, 0, 0, 0, 1.0 / Height, 0, 0, 0, 1], Epoch)], FrameTime: Epoch);

    private static byte[] Frame(int width = Width, int height = Height)
    {
        var result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            int grain = (x * 7 + y * 13) % 13;
            result[i] = (byte)(55 + grain); result[i + 1] = (byte)(112 + grain);
            result[i + 2] = (byte)(68 + grain); result[i + 3] = 255;
        }
        return result;
    }

    private static void Bar(byte[] image, int cx, int cy, int length, int thickness, double degrees = 0,
        byte brightness = 20, int width = Width, int height = Height)
    {
        double radians = degrees * Math.PI / 180, c = Math.Cos(radians), s = Math.Sin(radians);
        int extent = length + thickness;
        for (int y = Math.Max(0, cy - extent); y <= Math.Min(height - 1, cy + extent); y++)
        for (int x = Math.Max(0, cx - extent); x <= Math.Min(width - 1, cx + extent); x++)
        {
            double u = (x - cx) * c + (y - cy) * s, v = -(x - cx) * s + (y - cy) * c;
            if (Math.Abs(u) > length / 2.0 || Math.Abs(v) > thickness / 2.0) continue;
            int i = (y * width + x) * 4;
            image[i] = image[i + 1] = image[i + 2] = brightness;
        }
    }

    private static void Disk(byte[] image, int cx, int cy, int radius, byte brightness)
    {
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(Height - 1, cy + radius); y++)
        for (int x = Math.Max(0, cx - radius); x <= Math.Min(Width - 1, cx + radius); x++)
        {
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius) continue;
            int i = (y * Width + x) * 4;
            image[i] = image[i + 1] = image[i + 2] = brightness;
        }
    }

    private static void Fill(byte[] image, int left, int top, int width, int height, byte blue, byte green, byte red)
    {
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
        {
            int i = (y * Width + x) * 4;
            image[i] = blue; image[i + 1] = green; image[i + 2] = red;
        }
    }
}
