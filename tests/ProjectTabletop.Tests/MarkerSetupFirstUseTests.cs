using ProjectTabletop.App;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class MarkerSetupFirstUseTests
{
    private static MarkerSetupFirstUseContext NewCamera(BoardScreen board) =>
        new(board, Ready: true, SetupActive: false, WaterTrained: false,
            Player1UsesMarker: true, Player1Trained: false, TwoHumanPlayers: false,
            Player2UsesMarker: true, Player2Trained: false);

    [Fact]
    public void WaterWaitsForUsableProjectionAndCameraWithoutConsumingTheVisit()
    {
        var setup = new MarkerSetupFirstUse();
        setup.EnterBoard(BoardScreen.WaterGarden);
        var context = NewCamera(BoardScreen.WaterGarden);
        Assert.Null(setup.NextTarget(context with { Ready = false }));
        Assert.Null(setup.NextTarget(context with { SetupActive = true }));
        Assert.Equal(0, setup.NextTarget(context));
        Assert.Null(setup.NextTarget(context with { WaterTrained = true }));
    }

    [Fact]
    public void SavedProfilesArePreservedAndAnotherUntrainedCameraCanBeOffered()
    {
        var setup = new MarkerSetupFirstUse();
        setup.EnterBoard(BoardScreen.WaterGarden);
        var context = NewCamera(BoardScreen.WaterGarden);
        Assert.Null(setup.NextTarget(context with { WaterTrained = true }));
        Assert.Equal(0, setup.NextTarget(context));
        setup.EnterBoard(BoardScreen.Football);
        var football = NewCamera(BoardScreen.Football) with { TwoHumanPlayers = true };
        Assert.Null(setup.NextTarget(football with { Player1Trained = true, Player2Trained = true }));
    }

    [Fact]
    public void PaintUsesTheSavedWaterBarWithoutRequiringItsSelectedModeOrFootballTraining()
    {
        var savedBar = new BlackTipProfile(1, .08, .1, .001, 4, 2, .4);
        var setup = new MarkerSetupFirstUse();
        setup.EnterBoard(BoardScreen.Paint);
        var context = NewCamera(BoardScreen.Paint) with
        {
            WandBarTrained = WandMarkerProfileSelection.PaintBarProfile(savedBar) is not null
        };
        Assert.Same(savedBar, WandMarkerProfileSelection.PaintBarProfile(savedBar));
        Assert.Null(setup.NextTarget(context));
        Assert.Null(setup.NextTarget(context with { WaterTrained = true }));

        // An eye-only Water Garden profile and Football's separate controllers
        // do not substitute for Paint's double-bar wand.
        context = context with { WandBarTrained = false, WaterTrained = true,
            Player1Trained = true, Player2Trained = true };
        Assert.Equal(0, setup.NextTarget(context));
        Assert.Null(WandMarkerProfileSelection.PaintBarProfile(null));
        Assert.Null(WandMarkerProfileSelection.PaintBarProfile(savedBar with { BarCount = 1, SpacingRatio = 0 }));
        Assert.Null(WandMarkerProfileSelection.PaintBarProfile(savedBar with { NormalizedArea = 0 }));
    }

    [Fact]
    public void PaintWaitsForCameraAndProjectionAndCancellationOnlyDismissesThisVisit()
    {
        var setup = new MarkerSetupFirstUse();
        setup.EnterBoard(BoardScreen.Paint);
        var context = NewCamera(BoardScreen.Paint);
        Assert.Null(setup.NextTarget(context with { Ready = false }));
        Assert.Null(setup.NextTarget(context with { SetupActive = true }));
        Assert.Equal(0, setup.NextTarget(context));
        setup.Dismiss();
        Assert.Null(setup.NextTarget(context));
        setup.EnterBoard(BoardScreen.Paint);
        Assert.Equal(0, setup.NextTarget(context));
        Assert.Null(setup.NextTarget(context with { WandBarTrained = true }));
    }

    [Theory]
    [InlineData("eye")]
    [InlineData(null)]
    public void InactiveBarCannotHideMissingActiveProfileAndSavingBarRepairsReadiness(string? mode)
    {
        var savedBar = new BlackTipProfile(1, .08, .1, .001, 4, 2, .4);
        var setup = new MarkerSetupFirstUse();
        setup.EnterBoard(BoardScreen.WaterGarden);
        var context = NewCamera(BoardScreen.WaterGarden);

        Assert.Null(WandMarkerProfileSelection.BarProfile(mode, savedBar));
        Assert.False(WandMarkerProfileSelection.HasUsableProfile(mode, savedBar, hasEyeProfile: false));
        Assert.Equal(0, setup.NextTarget(context with
        {
            WaterTrained = WandMarkerProfileSelection.HasUsableProfile(mode, savedBar, hasEyeProfile: false)
        }));

        // Guided Save commits the staged bar profile and explicitly selects bar mode.
        Assert.Same(savedBar, WandMarkerProfileSelection.BarProfile("bar", savedBar));
        Assert.Null(setup.NextTarget(context with
        {
            WaterTrained = WandMarkerProfileSelection.HasUsableProfile("bar", savedBar, hasEyeProfile: false)
        }));
        Assert.True(WandMarkerProfileSelection.HasUsableProfile("eye", savedBar, hasEyeProfile: true));
        Assert.True(WandMarkerProfileSelection.HasUsableProfile(null, null, hasEyeProfile: true));
        Assert.False(WandMarkerProfileSelection.HasUsableProfile("bar", null, hasEyeProfile: false));
    }

    [Fact]
    public void FootballTrainsOnlyRequiredMarkerPlayersAndSequencesAfterSave()
    {
        var setup = new MarkerSetupFirstUse();
        setup.EnterBoard(BoardScreen.Football);
        var context = NewCamera(BoardScreen.Football);
        Assert.Equal(1, setup.NextTarget(context));
        Assert.Null(setup.NextTarget(context with { Player1Trained = true }));
        Assert.Null(setup.NextTarget(context with { Player1UsesMarker = false }));
        context = context with { TwoHumanPlayers = true };
        Assert.Equal(1, setup.NextTarget(context));
        Assert.Equal(2, setup.NextTarget(context with { Player1Trained = true }));
        Assert.Equal(2, setup.NextTarget(context with { Player1UsesMarker = false }));
        Assert.Null(setup.NextTarget(context with { Player1Trained = true, Player2UsesMarker = false }));
        Assert.Null(setup.NextTarget(context with { Player1Trained = true, Player2Trained = true }));
    }

    [Fact]
    public void CancellingDoesNotReopenOrSkipAheadUntilAnotherBoardEntry()
    {
        var setup = new MarkerSetupFirstUse();
        setup.EnterBoard(BoardScreen.Football);
        var context = NewCamera(BoardScreen.Football) with { TwoHumanPlayers = true };
        Assert.Equal(1, setup.NextTarget(context));
        setup.Dismiss();
        Assert.Null(setup.NextTarget(context));
        Assert.Null(setup.NextTarget(context with { Player1Trained = true }));
        setup.EnterBoard(BoardScreen.WaterGarden);
        Assert.Null(setup.NextTarget(context));
        Assert.Equal(0, setup.NextTarget(NewCamera(BoardScreen.WaterGarden)));
        setup.EnterBoard(BoardScreen.Football);
        Assert.Equal(2, setup.NextTarget(context with { Player1Trained = true }));
    }

    [Fact]
    public void FootballAudioStaysSilentBeforeTrainingAndAfterUntrainedCancellation()
    {
        var setup = new MarkerSetupFirstUse();
        var context = NewCamera(BoardScreen.Football) with { Ready = false };
        Assert.True(MarkerSetupFirstUse.BlocksFootballAudio(context));
        setup.EnterBoard(BoardScreen.Football);
        context = context with { Ready = true, SetupActive = true };
        Assert.True(MarkerSetupFirstUse.BlocksFootballAudio(context));
        setup.Dismiss();
        context = context with { SetupActive = false };
        Assert.Null(setup.NextTarget(context));
        Assert.True(MarkerSetupFirstUse.BlocksFootballAudio(context));
        Assert.False(MarkerSetupFirstUse.BlocksFootballAudio(context with { Player1Trained = true }));
    }

    [Fact]
    public void FootballAudioWaitsForBothSavesAndMutesAnActiveRecheck()
    {
        var context = NewCamera(BoardScreen.Football) with { TwoHumanPlayers = true, SetupActive = true };
        Assert.True(MarkerSetupFirstUse.BlocksFootballAudio(context));
        // Saving player one must not produce a burst before player two's trainer starts.
        context = context with { Player1Trained = true, SetupActive = false };
        Assert.True(MarkerSetupFirstUse.BlocksFootballAudio(context));
        context = context with { Player2Trained = true };
        Assert.False(MarkerSetupFirstUse.BlocksFootballAudio(context));
        // Ready-to-save and a recheck still own the active session. Cancelling a
        // recheck restores audio because the previous saved profiles remain valid.
        Assert.True(MarkerSetupFirstUse.BlocksFootballAudio(context with { SetupActive = true }));
        Assert.False(MarkerSetupFirstUse.BlocksFootballAudio(context));
    }

    [Fact]
    public void FootballAudioAllowsTrainedOrFingerPlayersAndIgnoresOtherBoards()
    {
        var context = NewCamera(BoardScreen.Football);
        Assert.False(MarkerSetupFirstUse.BlocksFootballAudio(context with { Player1Trained = true, Ready = false }));
        Assert.False(MarkerSetupFirstUse.BlocksFootballAudio(context with { Player1UsesMarker = false }));
        Assert.False(MarkerSetupFirstUse.BlocksFootballAudio(context with
            { TwoHumanPlayers = true, Player1UsesMarker = false, Player2UsesMarker = false }));
        Assert.False(MarkerSetupFirstUse.BlocksFootballAudio(context with
            { TwoHumanPlayers = true, Player1Trained = true, Player2UsesMarker = false }));
        foreach (var board in new[] { BoardScreen.Menu, BoardScreen.WaterGarden, BoardScreen.Paint, BoardScreen.StrykersRun })
            Assert.False(MarkerSetupFirstUse.BlocksFootballAudio(context with { Board = board, SetupActive = true }));
    }

    [Theory]
    [InlineData(BoardScreen.Menu)]
    [InlineData(BoardScreen.Globe)]
    [InlineData(BoardScreen.StrykersRun)]
    public void UnrelatedBoardsNeverStartMarkerTraining(BoardScreen board)
    {
        var setup = new MarkerSetupFirstUse();
        setup.EnterBoard(board);
        Assert.Null(setup.NextTarget(NewCamera(board)));
    }
}
