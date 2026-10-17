using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class MarkerSetupMovementTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly BlackTipProfile Profile = new(1, .08, .1, .001, 4, 2, .4);
    private static readonly MarkerTipObservation Marker = new(new(160, 120), 20, 1200, .9)
    { Bar = new(new(130, 120), new(190, 120), new(160, 100), new(160, 140)) };
    private static readonly MarkerTipDetectionResult Full = new([Marker], "black-bar-pair-candidate")
    { SupportingBars = [Marker, Marker] };
    private static readonly MarkerTipDetectionResult Clear = new([], "no-black-bar-pair");

    private sealed class Run
    {
        public readonly MarkerSetupSession Session;
        public int Milliseconds;
        public Run(IReadOnlyList<MarkerSetupTarget>? targets = null)
        {
            Session = new(targets, Profile);
            foreach (var target in Session.Targets)
                for (int i = 0; i <= 12; i++) Observe(target.Position);
        }
        public void Observe(PixelPoint? point, MarkerTipDetectionResult? detection = null, int advance = 50)
        {
            var time = Epoch.AddMilliseconds(Milliseconds);
            Session.ObserveValidation(detection ?? Full, point, time, time);
            Milliseconds += advance;
        }
        public PixelPoint At(double fraction)
        {
            int leg = Session.MovementTraversalsCompleted;
            var start = Session.MovementTargets[leg % 2].Position;
            var end = Session.MovementTargets[1 - leg % 2].Position;
            return new(start.X + (end.X - start.X) * fraction, start.Y + (end.Y - start.Y) * fraction);
        }
        public void Sweep(int frameTime = 50, int missingEvery = 0)
        {
            int leg = Session.MovementTraversalsCompleted;
            for (int i = 0; i <= 20 && Session.MovementTraversalsCompleted == leg; i++)
                Observe(At(i / 20.0), missingEvery > 0 && i % missingEvery == 1 ? Clear : Full, frameTime);
        }
    }

    [Fact]
    public void FiveStationaryTargetsCannotCompleteSetupWithoutBothMeasuredSweeps()
    {
        var run = new Run();
        var session = run.Session;
        Assert.Equal(MarkerSetupStage.Moving, session.Stage);
        Assert.Equal(new[] { "Left", "Right" }, session.MovementTargets.Select(target => target.Name));
        Assert.Equal(2, session.MovementTraversalCount);
        for (int i = 0; i < 80; i++) run.Observe(run.At(0));
        Assert.Equal(0, session.MovementTraversalsCompleted);
        Assert.Equal(MarkerSetupStage.Moving, session.Stage);
        session.ResetProgress();
        run.Sweep();
        Assert.Equal(1, session.MovementTraversalsCompleted);
        Assert.Equal(MarkerSetupStage.Moving, session.Stage);
        Assert.InRange(session.Progress, .5, .5001);
        run.Sweep();
        Assert.Equal(2, session.MovementTraversalsCompleted);
        Assert.Equal(MarkerSetupStage.LiftOff, session.Stage);
        Assert.Same(Profile, session.CandidateProfile);
        for (int i = 0; i <= 20; i++) run.Observe(null, Clear);
        Assert.Equal(MarkerSetupStage.Ready, session.Stage);
        Assert.Same(Profile, session.CandidateProfile);
    }

    [Fact]
    public void EndpointTeleportsAndStationaryDestinationDoNotFillRoute()
    {
        var run = new Run();
        for (int i = 0; i < 30; i++) run.Observe(run.At(1));
        Assert.Equal(0, run.Session.AcceptedFrames);
        for (int i = 0; i < 30; i++) run.Observe(run.At(i % 2));
        Assert.Equal(0, run.Session.MovementTraversalsCompleted);
        Assert.Equal(MarkerSetupStage.Moving, run.Session.Stage);
        Assert.Contains("jumped", run.Session.Reason);
    }

    [Fact]
    public void PartialHeldAndAmbiguousPositionsCannotAdvanceMovement()
    {
        foreach (var invalid in new[]
        {
            Clear with { SupportingBars = [Marker] },
            Full with { Reason = "held-marker-position" },
            Full with { Candidates = [Marker, Marker with { Center = new(260, 120) }] },
            Full with { AmbiguousMarkerCenters = [new(260, 120)] }
        })
        {
            var run = new Run();
            run.Observe(run.At(0));
            for (int i = 1; i <= 20; i++) run.Observe(run.At(i / 20.0), invalid);
            Assert.Equal(0, run.Session.MovementTraversalsCompleted);
            Assert.Equal(0, run.Session.AcceptedFrames);
            Assert.Same(Profile, run.Session.CandidateProfile);
        }
    }

    [Fact]
    public void SmallBoundedDropoutCanRecoverButRepeatedOrLongDropoutsCannotPass()
    {
        var oneDrop = new Run();
        oneDrop.Sweep(missingEvery: 100); // One missing sample at 50 ms.
        Assert.Equal(1, oneDrop.Session.MovementTraversalsCompleted);
        var recurring = new Run();
        recurring.Sweep(missingEvery: 2);
        Assert.Equal(0, recurring.Session.MovementTraversalsCompleted);
        var sustained = new Run();
        sustained.Observe(sustained.At(0));
        for (int i = 0; i < 4; i++) sustained.Observe(sustained.At(.1), Clear);
        Assert.Equal(0, sustained.Session.AcceptedFrames);
        Assert.Contains("lost", sustained.Session.Reason);
    }

    [Fact]
    public void StaleReplayedAndCameraGapFramesCannotFinishAnInProgressSweep()
    {
        var run = new Run();
        for (int i = 0; i <= 10; i++) run.Observe(run.At(i / 20.0));
        int accepted = run.Session.AcceptedFrames;
        var previous = Epoch.AddMilliseconds(run.Milliseconds - 50);
        for (int i = 0; i < 20; i++)
            run.Session.ObserveValidation(Full, run.At(.9), previous, previous);
        Assert.Equal(accepted, run.Session.AcceptedFrames);
        var stale = Epoch.AddMilliseconds(run.Milliseconds);
        run.Session.ObserveValidation(Full, run.At(.9), stale, stale.AddMilliseconds(300));
        Assert.Equal(0, run.Session.AcceptedFrames);
        Assert.Equal(0, run.Session.MovementTraversalsCompleted);
        run.Milliseconds += 350;
        run.Observe(run.At(0));
        run.Milliseconds += 175;
        run.Observe(run.At(.1));
        Assert.Equal(0, run.Session.AcceptedFrames);
        Assert.Contains("paused", run.Session.Reason);
    }

    [Fact]
    public void ResetDuringReturnKeepsCompletedOutboundCheckButDiscardsCurrentMeasurements()
    {
        var run = new Run();
        run.Sweep();
        for (int i = 0; i < 8; i++) run.Observe(run.At(i / 20.0));
        run.Session.ResetProgress();
        Assert.Equal(1, run.Session.MovementTraversalsCompleted);
        Assert.Equal(.5, run.Session.Progress);
        Assert.Equal(0, run.Session.AcceptedFrames);
        run.Sweep();
        Assert.Equal(MarkerSetupStage.LiftOff, run.Session.Stage);
        run.Session.Reset();
        Assert.Equal(MarkerSetupStage.Validating, run.Session.Stage);
        Assert.Equal(0, run.Session.MovementTraversalsCompleted);
        Assert.Same(Profile, run.Session.CandidateProfile);
    }

    [Fact]
    public void TooFastTooSlowAndLeavingTheRouteRequireAnotherSweep()
    {
        var fast = new Run();
        fast.Sweep(frameTime: 20);
        Assert.Equal(0, fast.Session.MovementTraversalsCompleted);
        var slow = new Run();
        for (int i = 0; i < 170; i++) slow.Observe(slow.At(i / 200.0));
        Assert.Equal(0, slow.Session.MovementTraversalsCompleted);
        Assert.Equal(0, slow.Session.AcceptedFrames);
        var offRoute = new Run();
        offRoute.Observe(offRoute.At(0));
        for (int i = 1; i < 10; i++) offRoute.Observe(new(.22, .5 + i * .04));
        Assert.Equal(0, offRoute.Session.MovementTraversalsCompleted);
        Assert.Equal(0, offRoute.Session.AcceptedFrames);
    }

    [Fact]
    public void ChoosesLongestDistinctRouteAndSingleTargetFixtureHasNoMovementRoute()
    {
        var run = new Run([new("Middle", new(.25, .5)), new("Left", new(.14, .5)),
            new("Right", new(.36, .5)), new("Near", new(.25, .73)), new("Far", new(.25, .27))]);
        Assert.Equal(new[] { "Near", "Far" }, run.Session.MovementTargets.Select(target => target.Name));
        run.Sweep(); run.Sweep();
        Assert.Equal(MarkerSetupStage.LiftOff, run.Session.Stage);
        var single = new Run([new("Middle", new(.5, .5))]);
        Assert.Empty(single.Session.MovementTargets);
        Assert.Equal(0, single.Session.MovementTraversalCount);
        Assert.Equal(MarkerSetupStage.LiftOff, single.Session.Stage);
    }
}
