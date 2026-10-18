using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class GlobeModelControlsTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 18, 18, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);

    [Fact]
    public void ModelChoicesReplaceZoomButtonsAndKeepNavigationAvailable()
    {
        var board = ReadyBoard();
        Assert.Equal(["globe-drawer-close", "globe-exit", "globe-model"], board.Buttons.Select(button => button.Id));
        Assert.False(board.ActivateButton("globe-zoom-in", At(3400)));
        Assert.False(board.ActivateButton("globe-model-night", At(3400)));
        Assert.True(board.ActivateButton("globe-model", At(3500)));
        Assert.True(board.GlobeModelExpanded);
        var model = Assert.Single(board.Buttons, button => button.Id == "globe-model");
        Assert.False(model.Enabled);
        Assert.False(board.ActivateButton("globe-model", At(3600)));
        Assert.True(Assert.Single(board.Buttons, button => button.Id == "globe-exit").Enabled);
        Assert.True(Assert.Single(board.Buttons, button => button.Id == "globe-drawer-close").Enabled);
        var choices = board.Buttons.Where(button => button.Id.StartsWith("globe-model-")).ToArray();
        Assert.Equal(["CLOUDS", "NO CLOUDS", "NIGHT"], choices.Select(button => button.Label));
        Assert.Equal(model.Bounds.X, choices[0].Bounds.X);
        Assert.All(choices, button =>
        {
            Assert.True(button.Enabled);
            Assert.Equal(BoardButtonHold.Once, button.Hold);
            Assert.Equal(model.Bounds.Width, button.Bounds.Width);
            Assert.Equal(model.Bounds.Height, button.Bounds.Height);
            Assert.True(button.Bounds.Y + button.Bounds.Height < model.Bounds.Y);
        });
        Assert.True(choices[1].Bounds.X > choices[0].Bounds.X + choices[0].Bounds.Width);
        Assert.True(choices[2].Bounds.X > choices[1].Bounds.X + choices[1].Bounds.Width);
        Assert.True(board.ActivateButton("globe-exit", At(3700)));
        Assert.Equal(BoardScreen.Menu, board.Screen);
    }

    [Fact]
    public void ModelChoiceAndExpansionSurviveDrawerAndSwipeButResetOnNewVisit()
    {
        var board = ReadyBoard();
        Assert.True(board.ActivateButton("globe-model", At(3500)));
        Assert.True(board.ActivateButton("globe-model-no-clouds", At(3600)));
        var selected = board.GetGlobeSnapshot(At(3600));
        Assert.Equal(GlobeModel.NoClouds, selected.Model);
        Assert.True(board.ActivateButton("globe-model-no-clouds", At(3700)));
        Assert.Equal(selected.Revision, board.GetGlobeSnapshot(At(3700)).Revision);
        Assert.True(board.ActivateButton("globe-drawer-close", At(4000)));
        Assert.True(board.GlobeModelExpanded);
        Assert.Single(board.Buttons);
        Assert.True(board.ActivateButton("globe-drawer-open", At(4300)));
        Assert.False(Assert.Single(board.Buttons, button => button.Id == "globe-model-night").Enabled);
        Assert.True(board.TickGlobe(At(4600)));
        Assert.Equal(GlobeModel.NoClouds, board.GetGlobeSnapshot(At(4600)).Model);
        Assert.True(board.SetButtonsVisible(false, At(4700)));
        Assert.Empty(board.Buttons);
        Assert.True(board.TickControlVisibility(At(5000)));
        Assert.True(board.SetButtonsVisible(true, At(5100)));
        Assert.Empty(board.Buttons);
        Assert.True(board.TickControlVisibility(At(5400)));
        Assert.True(board.GlobeModelExpanded);
        Assert.True(board.ActivateButton("globe-model-night", At(5500)));
        Assert.Equal(GlobeModel.Night, board.GetGlobeSnapshot(At(5500)).Model);
        board.ShowMenu(At(6000));
        board.ShowGlobe(At(6100));
        Assert.False(board.GlobeModelExpanded);
        Assert.Equal(GlobeModel.Clouds, board.GetGlobeSnapshot(At(6100)).Model);
    }

    [Fact]
    public void NewModelCaptionsNeedFreshClearEvidenceThenAFullHold()
    {
        var board = ReadyBoard();
        Assert.True(board.ActivateButton("globe-model", At(3500)));
        const string night = "globe-model-night";
        // A covered caption arriving before its first clear view cannot select a model.
        for (int ms = 3600; ms <= 4800; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([night], At(ms), At(ms), []));
        Assert.Equal(GlobeModel.Clouds, board.GetGlobeSnapshot(At(4800)).Model);
        Assert.Empty(board.ObserveHeldButtons([], At(4900), At(4900), [night]));
        for (int ms = 5000; ms < 6000; ms += 100)
            Assert.Empty(board.ObserveHeldButtons([night], At(ms), At(ms), []));
        Assert.Equal([night], board.ObserveHeldButtons([night], At(6000), At(6000), []));
        Assert.Equal(GlobeModel.Night, board.GetGlobeSnapshot(At(6000)).Model);
        Assert.Empty(board.ObserveHeldButtons([night], At(6100), At(6100), []));
    }

    [Fact]
    public void ZoomRailCoversItsRangeRetainsPositionAndDoesNotRestartPitchOrSpin()
    {
        var globe = new GlobeState();
        Assert.False(globe.SetZoomPosition(1, At(0)));
        globe.Start(At(0));
        long revision = globe.Revision;
        Assert.True(globe.SetPitchPosition(1, At(4000)));
        Assert.True(globe.SetZoomPosition(1, At(4000)));
        Assert.Equal(GlobeState.MinimumZoom, globe.GetSnapshot(At(4000)).Zoom);
        Assert.InRange(globe.GetSnapshot(At(4060)).Zoom, GlobeState.MinimumZoom, GlobeState.MaximumZoom);
        Assert.True(globe.SetZoomPosition(1, At(4060)));
        Assert.Equal(GlobeState.MaximumZoom, globe.GetSnapshot(At(4120)).Zoom);
        Assert.Equal(GlobeState.MaximumViewLatitude, globe.GetSnapshot(At(4120)).ViewLatitudeDegrees);
        Assert.True(globe.SetZoomPosition(.5, At(5000)));
        Assert.True(globe.SetPitchPosition(.25, At(5060)));
        Assert.True(globe.HandleAction("globe-rotate-left", At(5060)));
        Assert.Equal(1.8, globe.GetSnapshot(At(5120)).Zoom, precision: 8);
        Assert.Equal(-30, globe.GetSnapshot(At(5180)).ViewLatitudeDegrees);
        Assert.Equal(1.8, globe.GetSnapshot(At(60000)).Zoom, precision: 8);
        Assert.Equal(revision + 1, globe.Revision); // Only the discrete rotation changes caption references.
        Assert.True(globe.SetZoomPosition(0, At(61000)));
        Assert.Equal(GlobeState.MinimumZoom, globe.GetSnapshot(At(61120)).Zoom);
        globe.Start(At(62000));
        Assert.Equal(GlobeState.DefaultZoom, globe.GetSnapshot(At(65000)).Zoom);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-.01)]
    [InlineData(1.01)]
    public void InvalidOrStaleZoomInputsCannotMoveTheRail(double value)
    {
        var globe = new GlobeState();
        globe.Start(At(0));
        Assert.False(globe.SetZoomPosition(value, At(7000)));
        Assert.True(globe.SetZoomPosition(1, At(4000)));
        Assert.False(globe.SetZoomPosition(0, At(3999)));
        Assert.Equal(GlobeState.MaximumZoom, globe.GetSnapshot(At(4120)).Zoom);
    }

    [Fact]
    public void ZoomSharesPitchVisibilityAndInputBarriers()
    {
        var board = new BoardSession();
        board.ShowGlobe(At(0));
        Assert.False(board.SetGlobeZoomPosition(1, At(2000)));
        Assert.True(board.ActivateButton("globe-drawer-open", At(3000)));
        Assert.False(board.SetGlobeZoomPosition(1, At(3100)));
        Assert.True(board.TickGlobe(At(3300)));
        Assert.False(board.SetGlobeZoomPosition(1, At(3300)));
        Assert.True(board.SetGlobeZoomPosition(1, At(3301)));
        Assert.True(board.SetButtonsVisible(false, At(4000)));
        Assert.False(board.SetGlobeZoomPosition(0, At(4100)));
        Assert.True(board.TickControlVisibility(At(4300)));
        Assert.True(board.SetButtonsVisible(true, At(4400)));
        Assert.True(board.TickControlVisibility(At(4700)));
        Assert.False(board.SetGlobeZoomPosition(0, At(4700)));
        Assert.Equal(GlobeState.MaximumZoom, board.GetGlobeSnapshot(At(4700)).Zoom);
        Assert.True(board.SetGlobeZoomPosition(0, At(4701)));
        board.ResetInput(At(5000), preserveBoardAnimations: true);
        Assert.False(board.SetGlobeZoomPosition(1, At(4999)));
        Assert.True(board.SetGlobeZoomPosition(.5, At(5001)));
        Assert.True(board.ActivateButton("globe-drawer-close", At(5200)));
        Assert.False(board.SetGlobeZoomPosition(1, At(5300)));
        Assert.Equal(1.8, board.GetGlobeSnapshot(At(5400)).Zoom, precision: 8);
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
