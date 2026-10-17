using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class MarkerSetupSessionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 7, 0, 0, TimeSpan.Zero);
    private static readonly BlackTipProfile Profile = new(1, .08, .1, .001, 4, 2, .4);
    private static readonly MarkerTipObservation Marker = new(new(160, 120), 20, 1200, .9)
    { Bar = new(new(130, 120), new(190, 120), new(160, 100), new(160, 140)) };
    private static readonly MarkerTipDetectionResult Full = new([Marker], "black-bar-pair-candidate")
    { SupportingBars = [Marker, Marker] };
    private static readonly MarkerTipDetectionResult Clear = new([], "no-black-bar-pair");
    private static BlackTipLearningResult Sample(BlackTipProfile? profile = null) => new(profile ?? Profile, Marker, "double-bar-sample");
    private static void Learn(MarkerSetupSession session, int offset = 0)
    {
        for (int i = 0; i <= 20; i++)
        {
            var time = Epoch.AddMilliseconds(offset + i * 50);
            session.ObserveLearning(Sample(), time, time);
        }
    }

    [Fact]
    public void StagesMedianProfileThenRequiresEveryPositionAndClearLiftOff()
    {
        var session = new MarkerSetupSession();
        for (int i = 0; i <= 20; i++)
        {
            var time = Epoch.AddMilliseconds(i * 50);
            var sample = Profile with { Value = i % 2 == 0 ? .08 : .10,
                NormalizedArea = i % 2 == 0 ? .001 : .00108 };
            session.ObserveLearning(Sample(sample), time, time);
        }
        Assert.Equal(MarkerSetupStage.Validating, session.Stage);
        Assert.Equal(Profile, session.CandidateProfile);
        var staged = session.CandidateProfile;
        int milliseconds = 1050;
        foreach (var target in session.Targets)
        {
            Assert.Equal(target, session.CurrentTarget);
            for (int i = 0; i <= 12; i++)
            {
                var time = Epoch.AddMilliseconds(milliseconds);
                session.ObserveValidation(Full, target.Position, time, time);
                milliseconds += 50;
            }
        }
        Assert.Equal(MarkerSetupStage.Moving, session.Stage);
        for (int leg = 0; leg < session.MovementTraversalCount; leg++)
        {
            var start = session.MovementTargets[leg % 2].Position;
            var end = session.MovementTargets[1 - leg % 2].Position;
            for (int i = 0; i <= 20 && session.MovementTraversalsCompleted == leg; i++)
            {
                var time = Epoch.AddMilliseconds(milliseconds);
                session.ObserveValidation(Full, new(start.X + (end.X - start.X) * i / 20,
                    start.Y + (end.Y - start.Y) * i / 20), time, time);
                milliseconds += 50;
            }
        }
        Assert.Equal(MarkerSetupStage.LiftOff, session.Stage);
        Assert.Same(staged, session.CandidateProfile);
        for (int i = 0; i < 20; i++)
        {
            var time = Epoch.AddMilliseconds(milliseconds);
            session.ObserveValidation(Clear, null, time, time);
            milliseconds += 50;
            Assert.NotEqual(MarkerSetupStage.Ready, session.Stage);
        }
        var last = Epoch.AddMilliseconds(milliseconds);
        session.ObserveValidation(Clear, null, last, last);
        Assert.Equal(MarkerSetupStage.Ready, session.Stage);
        Assert.Equal(1, session.Progress);
        Assert.Same(staged, session.CandidateProfile);
    }

    [Fact]
    public void ReplayedStaleFutureOrWidelySeparatedLearningFramesCannotComplete()
    {
        var session = new MarkerSetupSession();
        for (int i = 0; i < 40; i++) session.ObserveLearning(Sample(), Epoch, Epoch.AddMilliseconds(i * 10));
        Assert.Equal(MarkerSetupStage.Learning, session.Stage);
        Assert.Equal(1, session.ObservedFrames);
        Assert.Null(session.CandidateProfile);
        for (int i = 0; i < 20; i++)
        {
            var time = Epoch.AddMilliseconds(1000 + i * 500);
            session.ObserveLearning(Sample(), time, time);
        }
        Assert.Equal(MarkerSetupStage.Learning, session.Stage);
        Assert.Equal(1, session.AcceptedFrames);
        Assert.True(session.MaximumFrameGap >= TimeSpan.FromMilliseconds(500));
        session.ObserveLearning(Sample(), Epoch.AddHours(1), Epoch);
        Assert.Equal(0, session.AcceptedFrames);
        session.Reset(); Learn(session);
        Assert.Equal(MarkerSetupStage.Validating, session.Stage);
    }

    [Fact]
    public void MissingAmbiguousMovingOrChangedLearningEvidenceRestartsSettling()
    {
        foreach (var interruption in new[]
        {
            new BlackTipLearningResult(null, null, "show-only-one-double-bar-in-target"),
            Sample(Profile with { NormalizedArea = .002 }),
            new BlackTipLearningResult(Profile, Marker with { Center = new(210, 120) }, "double-bar-sample")
        })
        {
            var session = new MarkerSetupSession();
            for (int i = 0; i < 20; i++)
            {
                var time = Epoch.AddMilliseconds(i * 50);
                session.ObserveLearning(Sample(), time, time);
            }
            session.ObserveLearning(interruption, Epoch.AddSeconds(1), Epoch.AddSeconds(1));
            Assert.Equal(MarkerSetupStage.Learning, session.Stage);
            Assert.Null(session.CandidateProfile);
            Assert.InRange(session.AcceptedFrames, 0, 1);
        }
    }

    [Fact]
    public void ValidationUsesFreshFullPairsNotHeldPartialOrAmbiguousPositions()
    {
        var session = new MarkerSetupSession(initialProfile: Profile);
        var centre = session.Targets[0].Position;
        for (int i = 0; i < 8; i++)
        {
            var time = Epoch.AddMilliseconds(i * 50);
            session.ObserveValidation(Full, centre, time, time);
        }
        Assert.Equal(0, session.TargetIndex); // Frame count alone is insufficient.
        var partial = Clear with { SupportingBars = [Marker] };
        session.ObserveValidation(partial, centre, Epoch.AddMilliseconds(400), Epoch.AddMilliseconds(400));
        Assert.Equal(0, session.AcceptedFrames);
        var ambiguous = Full with { Candidates = [Marker, Marker with { Center = new(260, 120) }] };
        session.ObserveValidation(ambiguous, centre, Epoch.AddMilliseconds(450), Epoch.AddMilliseconds(450));
        Assert.Equal(0, session.AcceptedFrames);
        Assert.Contains("unambiguous", session.Reason);
        session.ObserveValidation(Full, new(double.NaN, .5), Epoch.AddMilliseconds(500), Epoch.AddMilliseconds(500));
        Assert.Equal(0, session.AcceptedFrames);
        session.ObserveValidation(Full, centre, Epoch.AddMilliseconds(550), Epoch.AddMilliseconds(900));
        Assert.Equal(0, session.AcceptedFrames);
        Assert.Same(Profile, session.CandidateProfile);
    }

    [Fact]
    public void SweepAndPresentationResetCannotReusePartiallySettledProgress()
    {
        var session = new MarkerSetupSession(initialProfile: Profile);
        for (int i = 0; i < 20; i++)
        {
            var time = Epoch.AddMilliseconds(i * 50);
            session.ObserveValidation(Full, new(.42 + i % 4 * .04, .5), time, time);
        }
        Assert.Equal(0, session.TargetIndex);
        session.ResetProgress();
        Assert.Equal(0, session.AcceptedFrames);
        session.ObserveValidation(Full, new(.5, .5), Epoch.AddMilliseconds(950), Epoch.AddMilliseconds(950));
        Assert.Equal(0, session.AcceptedFrames); // Replayed presentation-frame remains old.
        Assert.Same(Profile, session.CandidateProfile);
        session.Reset();
        Assert.Equal(MarkerSetupStage.Validating, session.Stage);
        Assert.Same(Profile, session.CandidateProfile);
    }

    [Fact]
    public void LiftOffRequiresFreshClearDetectorEvidenceWithoutAnyRemainingStrip()
    {
        var session = new MarkerSetupSession([new("Centre", new(.5, .5))], Profile);
        for (int i = 0; i <= 12; i++)
        {
            var time = Epoch.AddMilliseconds(i * 50);
            session.ObserveValidation(Full, new(.5, .5), time, time);
        }
        Assert.Equal(MarkerSetupStage.LiftOff, session.Stage);
        foreach (var invalid in new[] { Clear with { SupportingBars = [Marker] },
            new MarkerTipDetectionResult([], "waiting-for-projected-bat-reference"), Full })
        {
            var time = Epoch.AddSeconds(2 + session.ObservedFrames);
            session.ObserveValidation(invalid, null, time, time);
            Assert.Equal(0, session.AcceptedFrames);
            Assert.Equal(MarkerSetupStage.LiftOff, session.Stage);
        }
        Assert.Throws<ArgumentException>(() => new MarkerSetupSession([new("Bad", new(2, .5))]));
        Assert.Throws<ArgumentException>(() => new MarkerSetupSession(initialProfile: Profile with { BarCount = 1, SpacingRatio = 0 }));
        Assert.Throws<ArgumentException>(() => new MarkerSetupSession([new("A", new(.5, .5)), new("B", new(.5, .5))]));
    }
}
