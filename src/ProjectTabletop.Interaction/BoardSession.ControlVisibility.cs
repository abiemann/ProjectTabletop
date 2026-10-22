namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public static readonly TimeSpan ControlFadeDuration = TimeSpan.FromMilliseconds(300);
    private DateTimeOffset _controlsInputReadyAfter = DateTimeOffset.MinValue;
    private DateTimeOffset? _controlFadeStartedAt;
    private double _controlFadeFromOpacity = 1;

    /// <summary>Target visibility. Rendering samples the shared fade; input waits until it settles.</summary>
    public bool ButtonsVisible { get; private set; } = true;

    /// <summary>The launcher, Roulette and Duck Hunt stay visible; Stryker's Run owns its control transitions.</summary>
    public bool ControlSwipesEnabled => Screen is not (BoardScreen.Menu or BoardScreen.Roulette or BoardScreen.StrykersRun or BoardScreen.DuckHunt);

    /// <summary>Current controls for rendering, including their dynamic enabled state while fading.</summary>
    public IReadOnlyList<BoardButton> GetVisibleButtons(DateTimeOffset now) =>
        ButtonsVisible || ControlVisibilityAnimating(now) ? CurrentButtons : [];

    /// <summary>Pure presentation sample shared by every control on the board.</summary>
    public double GetButtonsOpacity(DateTimeOffset now)
    {
        double target = ButtonsVisible ? 1 : 0;
        if (_controlFadeStartedAt is not { } started) return target;
        double progress = Math.Clamp((now - started) / ControlFadeDuration, 0, 1);
        double eased = progress * progress * (3 - 2 * progress);
        return _controlFadeFromOpacity + (target - _controlFadeFromOpacity) * eased;
    }

    public bool ControlVisibilityAnimating(DateTimeOffset now) =>
        _controlFadeStartedAt is { } started && now < started + ControlFadeDuration;

    /// <summary>Settles once, rejecting gestures and caption evidence from the moving controls.</summary>
    public bool TickControlVisibility(DateTimeOffset now)
    {
        if (_controlFadeStartedAt is not { } started || now < started + ControlFadeDuration) return false;
        _controlFadeStartedAt = null;
        var ready = started + ControlFadeDuration;
        _controlsInputReadyAfter = Later(_controlsInputReadyAfter, ready);
        ResetHoldCaptionEvidence();
        BottomDrawerGestureBarrier(ready);
        return true;
    }

    /// <summary>Hides or restores controls without changing the board, game or drawer state.
    /// Restored captions require fresh clear evidence before another hold can begin.
    /// The main menu, Roulette and Stryker's Run retain their existing control behavior.</summary>
    public bool SetButtonsVisible(bool visible, DateTimeOffset now)
    {
        TickControlVisibility(now);
        if (!ControlSwipesEnabled || ButtonsVisible == visible || now < _controlsInputReadyAfter)
            return false;
        _controlFadeFromOpacity = GetButtonsOpacity(now);
        _controlFadeStartedAt = now;
        ButtonsVisible = visible;
        _controlsInputReadyAfter = now;
        ClearHolds();
        BottomDrawerGestureBarrier(now);
        return true;
    }

    private void ResetControlVisibility(DateTimeOffset now)
    {
        if (!ButtonsVisible || _controlFadeStartedAt is not null)
            _controlsInputReadyAfter = Later(_controlsInputReadyAfter, now);
        ButtonsVisible = true;
        _controlFadeStartedAt = null;
        _controlFadeFromOpacity = 1;
    }
}
