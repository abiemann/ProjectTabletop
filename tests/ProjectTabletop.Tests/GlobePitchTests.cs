using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class GlobePitchTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);

    [Fact]
    public void RailCoversTheBoundedPitchRangeAndRetainsItsPositionWithoutInput()
    {
        var globe = new GlobeState(homeLatitudeDegrees: 34);
        Assert.False(globe.SetPitchPosition(1, At(0)));
        globe.Start(At(0));
        long revision = globe.Revision;
        Assert.Equal(34, globe.GetSnapshot(At(3000)).ViewLatitudeDegrees);
        Assert.True(globe.SetPitchPosition(0, At(3000)));
        Assert.Equal(34, globe.GetSnapshot(At(3000)).ViewLatitudeDegrees);
        double midway = globe.GetSnapshot(At(3060)).ViewLatitudeDegrees;
        Assert.InRange(midway, -GlobeState.MaximumViewLatitude, 34);
        Assert.NotEqual(-GlobeState.MaximumViewLatitude, midway);
        Assert.True(globe.SetPitchPosition(0, At(3060)));
        Assert.Equal(-GlobeState.MaximumViewLatitude, globe.GetSnapshot(At(3120)).ViewLatitudeDegrees);
        Assert.True(globe.SetPitchPosition(.5, At(4000)));
        Assert.Equal(0, globe.GetSnapshot(At(4120)).ViewLatitudeDegrees);
        Assert.True(globe.SetPitchPosition(1, At(5000)));
        Assert.Equal(GlobeState.MaximumViewLatitude, globe.GetSnapshot(At(5120)).ViewLatitudeDegrees);
        Assert.Equal(GlobeState.MaximumViewLatitude, globe.GetSnapshot(At(60000)).ViewLatitudeDegrees);
        Assert.Equal(revision, globe.Revision);
        globe.Start(At(70000));
        Assert.Equal(34, globe.GetSnapshot(At(70000)).ViewLatitudeDegrees);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-.01)]
    [InlineData(1.01)]
    public void InvalidPositionsDoNotChangePitchOrAdvanceItsClock(double position)
    {
        var globe = new GlobeState();
        globe.Start(At(0));
        var before = globe.GetSnapshot(At(4000));
        Assert.False(globe.SetPitchPosition(position, At(6000)));
        Assert.Equal(before, globe.GetSnapshot(At(4000)));
        Assert.True(globe.SetPitchPosition(1, At(4000)));
        Assert.False(globe.SetPitchPosition(0, At(3999)));
        Assert.Equal(GlobeState.MaximumViewLatitude, globe.GetSnapshot(At(4120)).ViewLatitudeDegrees);
    }

    [Fact]
    public void PitchAndZoomTransitionsRemainIndependent()
    {
        var globe = new GlobeState();
        var control = new GlobeState();
        globe.Start(At(0));
        control.Start(At(0));
        Assert.True(globe.HandleAction("globe-zoom-in", At(4000)));
        Assert.True(control.HandleAction("globe-zoom-in", At(4000)));
        Assert.True(globe.SetPitchPosition(1, At(4050)));
        Assert.True(globe.SetPitchPosition(.25, At(4090)));
        for (int ms = 4090; ms <= 4500; ms += 10)
        {
            Assert.Equal(control.GetSnapshot(At(ms)).Zoom, globe.GetSnapshot(At(ms)).Zoom);
            Assert.Equal(control.GetSnapshot(At(ms)).RotationDegrees, globe.GetSnapshot(At(ms)).RotationDegrees);
        }
        Assert.Equal(-30, globe.GetSnapshot(At(4210)).ViewLatitudeDegrees);

        // Zooming midway through a new pitch transition must not restart it.
        Assert.True(globe.SetPitchPosition(1, At(5000)));
        double pitch = globe.GetSnapshot(At(5060)).ViewLatitudeDegrees;
        Assert.True(globe.HandleAction("globe-zoom-in", At(5060)));
        Assert.Equal(pitch, globe.GetSnapshot(At(5060)).ViewLatitudeDegrees);
        Assert.Equal(GlobeState.MaximumViewLatitude, globe.GetSnapshot(At(5120)).ViewLatitudeDegrees);
        Assert.Equal(GlobeState.DefaultZoom * GlobeState.ZoomStep * GlobeState.ZoomStep,
            globe.GetSnapshot(At(5410)).Zoom);
    }

    [Fact]
    public void PitchWaitsForGlobeEntranceAndSettledDrawer()
    {
        var board = new BoardSession();
        Assert.False(board.SetGlobePitchPosition(1, At(0)));
        board.ShowGlobe(At(0));
        Assert.False(board.SetGlobePitchPosition(1, At(100)));
        Assert.True(board.ActivateButton("globe-drawer-open", At(100)));
        Assert.False(board.SetGlobePitchPosition(1, At(399)));
        Assert.True(board.TickGlobe(At(400)));
        Assert.False(board.GlobePitchInputReady(At(400)));
        Assert.False(board.SetGlobePitchPosition(1, At(2999)));
        Assert.True(board.SetGlobePitchPosition(1, At(3000)));
        Assert.Equal(GlobeState.MaximumViewLatitude, board.GetGlobeSnapshot(At(3120)).ViewLatitudeDegrees);
    }

    [Fact]
    public void CloseHideAndResetRejectOldFramesButPreserveTheChosenPitch()
    {
        var board = ReadyBoard();
        Assert.True(board.SetGlobePitchPosition(1, At(3400)));
        Assert.True(board.ActivateButton("globe-drawer-close", At(4000)));
        Assert.False(board.GlobePitchInputReady(At(4100)));
        Assert.False(board.SetGlobePitchPosition(0, At(4100)));
        Assert.Equal(GlobeState.MaximumViewLatitude, board.GetGlobeSnapshot(At(4200)).ViewLatitudeDegrees);
        Assert.True(board.ActivateButton("globe-drawer-open", At(4300)));
        Assert.True(board.TickGlobe(At(4600)));
        Assert.False(board.SetGlobePitchPosition(0, At(3999)));
        Assert.False(board.SetGlobePitchPosition(0, At(4600)));
        Assert.True(board.SetGlobePitchPosition(0, At(4601)));

        Assert.True(board.SetButtonsVisible(false, At(5000)));
        Assert.False(board.SetGlobePitchPosition(1, At(5100)));
        Assert.True(board.TickControlVisibility(At(5300)));
        Assert.False(board.SetGlobePitchPosition(1, At(5400)));
        Assert.True(board.SetButtonsVisible(true, At(5500)));
        Assert.False(board.SetGlobePitchPosition(1, At(5700)));
        Assert.True(board.TickControlVisibility(At(5800)));
        Assert.False(board.SetGlobePitchPosition(1, At(5799)));
        Assert.False(board.SetGlobePitchPosition(1, At(5800)));
        Assert.Equal(-GlobeState.MaximumViewLatitude, board.GetGlobeSnapshot(At(5800)).ViewLatitudeDegrees);
        Assert.True(board.SetGlobePitchPosition(.5, At(5801)));

        board.ResetInput(At(6000), preserveBoardAnimations: true);
        Assert.False(board.SetGlobePitchPosition(1, At(5999)));
        Assert.False(board.SetGlobePitchPosition(1, At(6000)));
        Assert.True(board.SetGlobePitchPosition(.75, At(6001)));
        Assert.Equal(30, board.GetGlobeSnapshot(At(6121)).ViewLatitudeDegrees);
        board.ShowMenu(At(6500));
        Assert.False(board.SetGlobePitchPosition(0, At(6600)));
        board.ShowGlobe(At(7000));
        Assert.Equal(0, board.GetGlobeSnapshot(At(10000)).ViewLatitudeDegrees);
        Assert.False(board.SetGlobePitchPosition(1, At(10000)));
    }

    [Fact]
    public void ContinuousPitchInputPreservesAnIndependentModelHold()
    {
        var board = ReadyBoard();
        Assert.True(board.ActivateButton("globe-model", At(3500)));
        const string model = "globe-model-night";
        board.ObserveHeldButtons([], At(4000), At(4000), [model]);
        long revision = board.Revision;
        long globeRevision = board.GetGlobeSnapshot(At(4000)).Revision;
        for (int ms = 4100; ms < 5100; ms += 100)
        {
            Assert.Empty(board.ObserveHeldButtons([model], At(ms), At(ms), []));
            Assert.True(board.SetGlobePitchPosition((ms - 4100) / 1000d, At(ms + 1)));
            Assert.Equal(revision, board.Revision);
            Assert.Equal(globeRevision, board.GetGlobeSnapshot(At(ms + 1)).Revision);
        }
        Assert.NotEmpty(board.HoldProgress(At(5001)));
        Assert.Equal([model], board.ObserveHeldButtons([model], At(5100), At(5100), []));
        Assert.True(board.GlobePitchAvailable(At(5100)));
        Assert.False(board.GlobePitchInputReady(At(5100)));
        Assert.Equal(GlobeModel.Night, board.GetGlobeSnapshot(At(5450)).Model);
        Assert.True(board.SetGlobePitchPosition(.5, At(5101)));
        Assert.Equal(0, board.GetGlobeSnapshot(At(5221)).ViewLatitudeDegrees);
    }

    [Fact]
    public void ModelChoiceKeepsTheStationaryRailAvailableWhileRejectingEarlierCameraFrames()
    {
        var board = ReadyBoard();
        Assert.True(board.ActivateButton("globe-model", At(3350)));
        Assert.True(board.GlobePitchAvailable(At(3350)));
        Assert.False(board.GlobePitchInputReady(At(3350)));
        var boundary = board.GlobePitchReferenceNotBefore;
        Assert.True(board.ActivateButton("globe-model-night", At(3400)));
        Assert.True(board.GlobePitchAvailable(At(3400)));
        Assert.False(board.GlobePitchInputReady(At(3399)));
        Assert.False(board.GlobePitchInputReady(At(3400)));
        Assert.False(board.SetGlobePitchPosition(1, At(3400)));
        Assert.Equal(boundary, board.GlobePitchReferenceNotBefore);
        Assert.True(board.GlobePitchInputReady(At(3401)));
        Assert.True(board.SetGlobePitchPosition(1, At(3401)));

        Assert.True(board.ActivateButton("globe-model-clouds", At(3500)));
        Assert.True(board.GlobePitchAvailable(At(3500)));
        Assert.False(board.GlobePitchInputReady(At(3500)));
        Assert.Equal(boundary, board.GlobePitchReferenceNotBefore);
        Assert.True(board.GlobePitchInputReady(At(3501)));
    }

    [Fact]
    public void PitchReferenceBoundaryTracksRailVisibilityWithoutFollowingZoom()
    {
        var board = ReadyBoard();
        Assert.Equal(At(3300), board.GlobePitchReferenceNotBefore);
        Assert.True(board.SetGlobeZoomPosition(1, At(3400)));
        Assert.True(board.SetGlobePitchPosition(.75, At(3401)));
        Assert.Equal(At(3300), board.GlobePitchReferenceNotBefore);

        Assert.True(board.SetButtonsVisible(false, At(4000)));
        Assert.Equal(At(4000), board.GlobePitchReferenceNotBefore);
        Assert.True(board.TickControlVisibility(At(4300)));
        Assert.Equal(At(4300), board.GlobePitchReferenceNotBefore);
        Assert.True(board.SetButtonsVisible(true, At(4500)));
        Assert.Equal(At(4500), board.GlobePitchReferenceNotBefore);
        Assert.True(board.TickControlVisibility(At(4800)));
        Assert.Equal(At(4800), board.GlobePitchReferenceNotBefore);

        Assert.True(board.ActivateButton("globe-drawer-close", At(5000)));
        Assert.Equal(At(5000), board.GlobePitchReferenceNotBefore);
        Assert.True(board.ActivateButton("globe-drawer-open", At(5200)));
        Assert.Equal(At(5200), board.GlobePitchReferenceNotBefore);
        Assert.True(board.TickGlobe(At(5500)));
        Assert.Equal(At(5500), board.GlobePitchReferenceNotBefore);
        Assert.True(board.SetGlobeZoomPosition(0, At(5600)));
        Assert.Equal(At(5500), board.GlobePitchReferenceNotBefore);
    }

    private static BoardSession ReadyBoard()
    {
        var board = new BoardSession();
        board.ShowGlobe(At(0));
        Assert.True(board.ActivateButton("globe-drawer-open", At(3000)));
        Assert.True(board.TickGlobe(At(3300)));
        return board;
    }
}
