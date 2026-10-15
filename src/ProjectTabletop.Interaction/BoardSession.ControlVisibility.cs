namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private DateTimeOffset _controlsInputReadyAfter = DateTimeOffset.MinValue;

    /// <summary>Whether the current board's available controls are shown.</summary>
    public bool ButtonsVisible { get; private set; } = true;

    /// <summary>The launcher stays visible; Stryker's Run owns its automatic control transitions.</summary>
    public bool ControlSwipesEnabled => Screen is not (BoardScreen.Menu or BoardScreen.StrykersRun);

    /// <summary>Hides or restores controls without changing the board, game or drawer state.
    /// Restored captions require fresh clear evidence before another hold can begin.
    /// The main menu and Stryker's Run retain their existing control behavior.</summary>
    public bool SetButtonsVisible(bool visible, DateTimeOffset now)
    {
        if (!ControlSwipesEnabled || ButtonsVisible == visible || now < _controlsInputReadyAfter)
            return false;
        ButtonsVisible = visible;
        _controlsInputReadyAfter = now;
        ClearHolds();
        BottomDrawerGestureBarrier(now);
        return true;
    }
}
