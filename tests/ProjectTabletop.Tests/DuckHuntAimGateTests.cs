using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class DuckHuntAimGateTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
    private static readonly BoardAim Aim = new(.5, .5);
    private static DateTimeOffset At(double milliseconds) => Origin.AddMilliseconds(milliseconds);

    [Fact]
    public void CurrentSnapshotCanAcquireDotWithoutAnyPriorObservation()
    {
        var gate = new DuckHuntAimGate();
        // Audio confirms after 60 ms; its latest camera image is 15 ms older.
        var shot = gate.EvaluateSnapshot(Aim, At(45), At(0), At(60), At(62));
        Assert.NotNull(shot);
        Assert.Equal(Aim, shot.Value.Aim);
        Assert.Equal(At(45), shot.Value.ClickedAt);
    }

    [Fact]
    public void EveryClickUsesItsOwnSnapshotEvenWhenAimMoves()
    {
        var gate = new DuckHuntAimGate();
        Assert.Equal(Aim, gate.EvaluateSnapshot(Aim, At(45), At(0), At(60), At(65))!.Value.Aim);
        var moved = new BoardAim(.75, .2);
        Assert.Equal(moved, gate.EvaluateSnapshot(moved, At(345), At(300), At(360), At(365))!.Value.Aim);
    }

    [Fact]
    public void MissingOrAmbiguousSnapshotCannotReusePreviousAimOrRetryTheSameClick()
    {
        var gate = new DuckHuntAimGate();
        Assert.NotNull(gate.EvaluateSnapshot(Aim, At(45), At(0), At(60), At(65)));
        // The detector represents both missing and ambiguous dots with no aim.
        Assert.Null(gate.EvaluateSnapshot(null, At(345), At(300), At(360), At(365)));
        Assert.Null(gate.EvaluateSnapshot(Aim, At(380), At(300), At(390), At(395)));
        Assert.NotNull(gate.EvaluateSnapshot(Aim, At(645), At(600), At(660), At(665)));
    }

    [Fact]
    public void RepeatedAndOutOfOrderAudioCannotShootAgain()
    {
        var gate = new DuckHuntAimGate();
        Assert.NotNull(gate.EvaluateSnapshot(Aim, At(345), At(300), At(360), At(365)));
        Assert.Null(gate.EvaluateSnapshot(Aim, At(375), At(300), At(380), At(385)));
        Assert.Null(gate.EvaluateSnapshot(Aim, At(355), At(290), At(365), At(385)));
    }

    [Theory]
    [InlineData(-1, 0, 60, 65)] // Snapshot predates the actual click.
    [InlineData(61, 0, 60, 65)] // Snapshot arrived after confirmation/selection.
    [InlineData(45, 0, 60, 59)] // Confirmation is in the future.
    [InlineData(45, 61, 60, 65)] // Onset is after confirmation.
    [InlineData(145, 0, 150, 151)] // Old audio cannot use a newly visible dot.
    [InlineData(45, 0, 60, 196)] // Neither old audio nor old pixels stay valid.
    public void InvalidTimingCannotSupplyAShot(double frame, double onset, double confirmed, double now)
    {
        var gate = new DuckHuntAimGate();
        Assert.Null(gate.EvaluateSnapshot(Aim, At(frame), At(onset), At(confirmed), At(now)));
    }

    [Fact]
    public void FreshnessBoundaryStillAllowsAnImmediateSnapshotDecision()
    {
        var gate = new DuckHuntAimGate();
        Assert.NotNull(gate.EvaluateSnapshot(Aim, At(125), At(0), At(145), At(150)));
    }

    [Fact]
    public void RejectedSnapshotCannotBeRetriedWithNewerPixels()
    {
        var gate = new DuckHuntAimGate();
        Assert.Null(gate.EvaluateSnapshot(Aim, At(-5), At(0), At(60), At(65)));
        Assert.Null(gate.EvaluateSnapshot(Aim, At(65), At(0), At(70), At(75)));
    }

    [Fact]
    public void InvalidFutureAudioDoesNotConsumeALaterRealClick()
    {
        var gate = new DuckHuntAimGate();
        Assert.Null(gate.EvaluateSnapshot(Aim, At(1000), At(1000), At(1060), At(65)));
        Assert.NotNull(gate.EvaluateSnapshot(Aim, At(345), At(300), At(360), At(365)));
    }

    [Fact]
    public void ResetRejectsInFlightAudioAndPreResetPixels()
    {
        var gate = new DuckHuntAimGate();
        gate.Reset(At(20));
        Assert.Null(gate.EvaluateSnapshot(Aim, At(45), At(0), At(60), At(65)));
        Assert.Null(gate.EvaluateSnapshot(Aim, At(15), At(25), At(60), At(65)));
        Assert.NotNull(gate.EvaluateSnapshot(Aim, At(80), At(70), At(90), At(95)));
    }

    [Theory]
    [InlineData(double.NaN, .5)]
    [InlineData(.5, double.PositiveInfinity)]
    [InlineData(-.1, .5)]
    [InlineData(.5, 1.1)]
    public void InvalidAimConsumesThatClickWithoutSupplyingAShot(double u, double v)
    {
        var gate = new DuckHuntAimGate();
        Assert.Null(gate.EvaluateSnapshot(new(u, v), At(45), At(0), At(60), At(65)));
        Assert.Null(gate.EvaluateSnapshot(Aim, At(65), At(0), At(70), At(75)));
    }
}
