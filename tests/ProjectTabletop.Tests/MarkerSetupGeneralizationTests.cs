using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class MarkerSetupGeneralizationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(640, 360, 28, 5, 11, 0, 160, 8)]
    [InlineData(1280, 720, 48, 11, 20, 30, 210, 30)]
    [InlineData(1920, 1080, 64, 7, 24, 90, 190, 50)]
    [InlineData(2560, 1440, 60, 16, 26, -25, 240, 60)]
    [InlineData(3840, 2160, 80, 22, 38, 67, 150, 15)]
    [InlineData(640, 480, 84, 24, 53, 155, 225, 40)]
    public void FreshInstallerCanLearnTheirOwnMarkerAndValidateAcrossTheBoard(
        int width, int height, int length, int thickness, int spacing, double angle, byte background, byte ink)
    {
        var neutral = Frame(width, height, background);
        var training = WithPair(neutral, width, height, .5, .5, length, thickness, spacing, angle, ink);
        var target = Target(width, height);
        var session = new MarkerSetupSession();
        var learningReference = Reference(width, height, neutral, Epoch.AddMilliseconds(100));
        for (int frame = 0; frame <= 20; frame++)
        {
            var time = Epoch.AddMilliseconds(frame * 50);
            var sample = BlackTipDetector.LearnPairInTarget(width, height, width * 4, training, target,
                new([learningReference], time));
            Assert.NotNull(sample.Profile);
            Assert.NotNull(sample.Marker);
            session.ObserveLearning(sample, time, time);
        }

        Assert.Equal(MarkerSetupStage.Validating, session.Stage);
        var profile = Assert.IsType<BlackTipProfile>(session.CandidateProfile);
        Assert.Equal(2, profile.BarCount);
        Assert.InRange(profile.Value, (ink - 1.0) / 255, (ink + 1.0) / 255);
        Assert.InRange(profile.SpacingRatio, spacing / (double)length * .85, spacing / (double)length * 1.15);
        Assert.InRange(profile.AspectRatio, length / (double)thickness * .80, length / (double)thickness * 1.25);

        int milliseconds = 1050;
        foreach (var position in session.Targets)
        {
            Assert.Equal(position, session.CurrentTarget);
            // The check uses real detections at a different location and heading,
            // without a tracker hint, old user profile or fabricated full-pair result.
            var held = WithPair(neutral, width, height, position.Position.X, position.Position.Y,
                length, thickness, spacing, angle + 15, ink);
            var scene = Reference(width, height, neutral, Epoch.AddMilliseconds(milliseconds + 100));
            for (int frame = 0; frame <= 12; frame++)
            {
                var time = Epoch.AddMilliseconds(milliseconds);
                var detection = BlackTipDetector.Detect(width, height, width * 4, held, profile,
                    new(ProjectionFrames: [scene], FrameTime: time));
                var marker = Assert.Single(detection.Candidates);
                var mapped = new PixelPoint(marker.Center.X / width, marker.Center.Y / height);
                Assert.InRange(MarkerTipGeometry.Distance(mapped, position.Position), 0, .015);
                session.ObserveValidation(detection, mapped, time, time);
                milliseconds += 50;
            }
        }
        Assert.Equal(session.Targets.Count, session.TargetIndex);
        Assert.NotEqual(MarkerSetupStage.Validating, session.Stage);
        Assert.Same(profile, session.CandidateProfile);
        Assert.Empty(BlackTipDetector.Detect(width, height, width * 4, neutral, profile).Candidates);

        if (width == 1280)
        {
            Assert.Equal(MarkerSetupStage.Moving, session.Stage);
            // Complete one fresh-install example all the way to save eligibility.
            // Every moving sample comes from pixels containing this learned marker.
            for (int leg = 0; leg < session.MovementTraversalCount; leg++)
            {
                var start = session.MovementTargets[leg % 2].Position;
                var end = session.MovementTargets[1 - leg % 2].Position;
                for (int frame = 0; frame <= 20 && session.MovementTraversalsCompleted == leg; frame++)
                {
                    double fraction = frame / 20.0;
                    var held = WithPair(neutral, width, height,
                        start.X + (end.X - start.X) * fraction,
                        start.Y + (end.Y - start.Y) * fraction,
                        length, thickness, spacing, angle + fraction * 20, ink);
                    var time = Epoch.AddMilliseconds(milliseconds);
                    var scene = Reference(width, height, neutral, time);
                    var detection = BlackTipDetector.Detect(width, height, width * 4, held, profile,
                        new(ProjectionFrames: [scene], FrameTime: time));
                    var marker = Assert.Single(detection.Candidates);
                    session.ObserveValidation(detection,
                        new(marker.Center.X / width, marker.Center.Y / height), time, time);
                    milliseconds += 50;
                    Assert.NotEqual(MarkerSetupStage.Ready, session.Stage);
                }
                Assert.Equal(leg + 1, session.MovementTraversalsCompleted);
                Assert.Same(profile, session.CandidateProfile);
            }
            Assert.Equal(MarkerSetupStage.LiftOff, session.Stage);
            for (int frame = 0; frame <= 20; frame++)
            {
                var time = Epoch.AddMilliseconds(milliseconds);
                var scene = Reference(width, height, neutral, time);
                var detection = BlackTipDetector.Detect(width, height, width * 4, neutral, profile,
                    new(ProjectionFrames: [scene], FrameTime: time));
                Assert.Empty(detection.Candidates);
                Assert.Empty(detection.SupportingBars);
                session.ObserveValidation(detection, null, time, time);
                milliseconds += 50;
                if (frame < 20) Assert.Equal(MarkerSetupStage.LiftOff, session.Stage);
            }
            Assert.Equal(MarkerSetupStage.Ready, session.Stage);
            Assert.Same(profile, session.CandidateProfile);
        }
    }

    [Theory]
    [InlineData(640, 1280)]
    [InlineData(1280, 2560)]
    [InlineData(2560, 640)]
    public void LearnedNormalizedGeometryTransfersBetweenCaptureResolutions(int trainingWidth, int detectionWidth)
    {
        int trainingHeight = trainingWidth * 9 / 16, detectionHeight = detectionWidth * 9 / 16;
        var neutral = Frame(trainingWidth, trainingHeight, 205);
        var training = WithPair(neutral, trainingWidth, trainingHeight, .5, .5, 48, 10, 22, 25, 25);
        var learned = BlackTipDetector.LearnPairInTarget(trainingWidth, trainingHeight, trainingWidth * 4,
            training, Target(trainingWidth, trainingHeight),
            new([Reference(trainingWidth, trainingHeight, neutral, Epoch)], Epoch));
        var profile = Assert.IsType<BlackTipProfile>(learned.Profile);

        var newNeutral = Frame(detectionWidth, detectionHeight, 170);
        var held = WithPair(newNeutral, detectionWidth, detectionHeight, .65, .35, 48, 10, 22, -35, 38);
        var result = BlackTipDetector.Detect(detectionWidth, detectionHeight, detectionWidth * 4, held, profile,
            new(ProjectionFrames: [Reference(detectionWidth, detectionHeight, newNeutral, Epoch)], FrameTime: Epoch));
        var marker = Assert.Single(result.Candidates);
        Assert.InRange(marker.Center.X / detectionWidth, .64, .66);
        Assert.InRange(marker.Center.Y / detectionHeight, .34, .36);
        Assert.Empty(result.AmbiguousMarkerCenters);
    }

    [Fact]
    public void SetupRejectsTwoPhysicalMarkersInsteadOfLearningWhicheverIsLargest()
    {
        const int width = 640, height = 360;
        var neutral = Frame(width, height, 220);
        var training = WithPair(neutral, width, height, .38, .5, 40, 9, 18, 0, 15);
        training = WithPair(training, width, height, .62, .5, 64, 15, 28, 90, 35);
        var scene = Reference(width, height, neutral, Epoch.AddMilliseconds(100));
        var session = new MarkerSetupSession();
        for (int frame = 0; frame <= 20; frame++)
        {
            var time = Epoch.AddMilliseconds(frame * 50);
            var sample = BlackTipDetector.LearnPairInTarget(width, height, width * 4, training,
                Target(width, height), new([scene], time));
            Assert.Null(sample.Profile);
            Assert.Equal("show-only-one-double-bar-in-target", sample.Reason);
            session.ObserveLearning(sample, time, time);
        }
        Assert.Equal(MarkerSetupStage.Learning, session.Stage);
        Assert.Null(session.CandidateProfile);
        Assert.Equal(0, session.AcceptedFrames);
    }

    private static PixelPoint[] Target(int width, int height) =>
        [new(width * .2, height * .2), new(width * .8, height * .2),
            new(width * .8, height * .8), new(width * .2, height * .8)];

    private static MarkerProjectionFrame Reference(int width, int height, byte[] pixels, DateTimeOffset time) =>
        new(width, height, pixels, [1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1], time);

    private static byte[] Frame(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        for (int at = 0; at < pixels.Length; at += 4)
        {
            pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
            pixels[at + 3] = 255;
        }
        return pixels;
    }

    private static byte[] WithPair(byte[] background, int width, int height, double x, double y,
        int length, int thickness, int spacing, double angle, byte ink)
    {
        var pixels = background.ToArray();
        double scale = width / 640.0, radians = angle * Math.PI / 180;
        double ux = Math.Cos(radians), uy = Math.Sin(radians);
        double cx = width * x, cy = height * y;
        double radius = (length + spacing + thickness) * scale;
        int left = Math.Max(0, (int)Math.Floor(cx - radius)), right = Math.Min(width - 1, (int)Math.Ceiling(cx + radius));
        int top = Math.Max(0, (int)Math.Floor(cy - radius)), bottom = Math.Min(height - 1, (int)Math.Ceiling(cy + radius));
        for (int row = top; row <= bottom; row++)
        for (int column = left; column <= right; column++)
        {
            double dx = column - cx, dy = row - cy;
            double along = (dx * ux + dy * uy) / scale, across = (-dx * uy + dy * ux) / scale;
            if (Math.Abs(along) > length * .5 ||
                Math.Abs(Math.Abs(across) - spacing * .5) > thickness * .5) continue;
            int at = (row * width + column) * 4;
            // Mild projected colour on otherwise dark physical ink.
            pixels[at] = (byte)(ink * .75);
            pixels[at + 1] = (byte)(ink * .9);
            pixels[at + 2] = ink;
        }
        return pixels;
    }
}
