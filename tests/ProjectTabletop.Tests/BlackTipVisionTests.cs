using System.Text.Json;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BlackTipVisionTests
{
    private const int Width = 320, Height = 240;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);

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
        var left = new ColorTipTracker();
        var right = new ColorTipTracker();
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
        var ambiguous = FootballTipAssignment.Update(new ColorTipTracker(), 0, all, Field, Epoch, Epoch);
        Assert.Equal(FootballTipAction.Clear, ambiguous.Action);
        Assert.Null(ambiguous.Tip);
        var rightTracker = new ColorTipTracker();
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
        var tracker = new ColorTipTracker();
        var waiting = FootballTipAssignment.Update(tracker, 0,
            new ColorTipDetectionResult([], "waiting-for-projected-bat-reference"), Field, Epoch, Epoch);
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
        var trackers = new[] { new ColorTipTracker(), new ColorTipTracker() };
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
        Assert.Empty(Detect(LitPair(51, 160), profile).Candidates);
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
    public void ColorObservationsRemainCompatibleWithoutBlackBarGeometry()
    {
        var image = Frame();
        Fill(image, 150, 110, 16, 16, 20, 25, 215);
        var profile = ColorTipDetector.Learn(Width, Height, Width * 4, image, new(157, 117));
        var observation = Assert.Single(ColorTipDetector.Detect(Width, Height, Width * 4, image, profile).Candidates);
        Assert.Null(observation.Bar);
        var existing = new ColorTipObservation(new(10, 20), 3, 28, .9);
        Assert.Equal(existing, JsonSerializer.Deserialize<ColorTipObservation>(JsonSerializer.Serialize(existing)));
    }

    private static void AssertBarGeometry(ColorTipObservation observation, double cx, double cy,
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
    private static ColorTipDetectionResult Detect(byte[] image, BlackTipProfile profile) =>
        BlackTipDetector.Detect(Width, Height, Width * 4, image, profile);
    private static ColorTipDetectionOptions Options(byte[] projected) => new(ProjectionFrames:
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
