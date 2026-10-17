using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

internal readonly record struct MarkerSetupFirstUseContext(BoardScreen Board, bool Ready, bool SetupActive,
    bool WaterTrained, bool Player1UsesMarker, bool Player1Trained, bool TwoHumanPlayers,
    bool Player2UsesMarker, bool Player2Trained, bool WandBarTrained = false);

// One board visit can train its required controllers in order. Cancelling is a
// choice for this visit, not a persistent change to the saved controller modes.
internal sealed class MarkerSetupFirstUse
{
    private BoardScreen? _board;
    private bool _dismissed;

    public void EnterBoard(BoardScreen board) { _board = board; _dismissed = false; }
    public void Dismiss() => _dismissed = true;

    public int? NextTarget(MarkerSetupFirstUseContext context)
    {
        if (_dismissed || _board != context.Board || !context.Ready || context.SetupActive) return null;
        return UntrainedTarget(context);
    }

    // Audio waits even before the first-use wizard can start, and cancelling
    // that wizard does not make an unsaved controller ready for play.
    public static bool BlocksFootballAudio(MarkerSetupFirstUseContext context) =>
        context.Board == BoardScreen.Football && (context.SetupActive || UntrainedTarget(context) is not null);

    private static int? UntrainedTarget(MarkerSetupFirstUseContext context)
    {
        if (context.Board == BoardScreen.WaterGarden) return context.WaterTrained ? null : 0;
        if (context.Board == BoardScreen.Paint) return context.WandBarTrained ? null : 0;
        if (context.Board != BoardScreen.Football) return null;
        if (context.Player1UsesMarker && !context.Player1Trained) return 1;
        return context.TwoHumanPlayers && context.Player2UsesMarker && !context.Player2Trained ? 2 : null;
    }
}
