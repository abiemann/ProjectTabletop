namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private readonly StrykersRunGame _strykersRun = new();
    private BoardRect _strykersRunFieldBounds = new(.01, .01, .98, .98);
    public StrykersRunSnapshot StrykersRunState => _strykersRun.Snapshot;
    public float StrykersRunRoadPresentationDistance => _strykersRun.RoadPresentationDistance;
    public StrykersRunPhase StrykersRunPhase => _strykersRun.Phase;
    public long StrykersRunInputRevision { get; private set; }
    private bool StrykersRunEnded => _strykersRun.Phase is StrykersRunPhase.GameOver or StrykersRunPhase.Victory;
    public bool StrykersRunControlsVisible => StrykersRunControlsProgress > 0;
    public bool StrykersRunControlsAnimating => _strykersRun.Phase == StrykersRunPhase.Starting ||
        StrykersRunEnded &&
        _strykersRun.PhaseSeconds > StrykersRunGame.GameOverWallSeconds &&
        _strykersRun.PhaseSeconds < StrykersRunGame.GameOverPresentationSeconds;
    public bool StrykersRunRailVisible => StrykersRunRailProgress > 0;
    public bool StrykersRunRailAnimating => _strykersRun.Phase == StrykersRunPhase.Starting ||
        StrykersRunEnded &&
        _strykersRun.PhaseSeconds < StrykersRunGame.ControlTransitionSeconds;

    public double StrykersRunRailProgress
    {
        get
        {
            var state = _strykersRun;
            double progress = state.Phase switch
            {
                StrykersRunPhase.Starting => state.PhaseSeconds / StrykersRunGame.ControlTransitionSeconds,
                StrykersRunPhase.WaitingForInput or StrykersRunPhase.Playing => 1,
                StrykersRunPhase.GameOver or StrykersRunPhase.Victory => 1 - state.PhaseSeconds / StrykersRunGame.ControlTransitionSeconds,
                _ => 0
            };
            progress = Math.Clamp(progress, 0, 1);
            return progress * progress * (3 - 2 * progress);
        }
    }

    public double StrykersRunControlsProgress
    {
        get
        {
            var state = _strykersRun;
            double progress = state.Phase switch
            {
                StrykersRunPhase.Ready => 1,
                StrykersRunPhase.WaitingForInput when state.RecoveringInput => 1,
                StrykersRunPhase.Starting => 1 - state.PhaseSeconds / StrykersRunGame.ControlTransitionSeconds,
                StrykersRunPhase.GameOver or StrykersRunPhase.Victory => (state.PhaseSeconds - StrykersRunGame.GameOverWallSeconds) /
                    StrykersRunGame.ControlTransitionSeconds,
                _ => 0
            };
            progress = Math.Clamp(progress, 0, 1);
            return progress * progress * (3 - 2 * progress);
        }
    }

    public void ShowStrykersRun(DateTimeOffset? now = null) => Show(BoardScreen.StrykersRun, now ?? MonotonicClock.UtcNow);

    public void SetStrykersRunFieldBounds(BoardRect bounds, DateTimeOffset now)
    {
        if (_strykersRunFieldBounds == bounds) return;
        _strykersRunFieldBounds = bounds;
        if (Screen == BoardScreen.StrykersRun)
        {
            Revision++;
            InvalidateStrykersRunCaptions(now);
        }
    }

    public void TickStrykersRun(DateTimeOffset now)
    {
        if (Screen != BoardScreen.StrykersRun) return;
        bool visible = StrykersRunControlsVisible, moving = StrykersRunControlsAnimating;
        _strykersRun.Advance(now);
        RefreshStrykersRunCaptions(visible, moving, now);
    }

    public void SetStrykersRunInput(float? normalizedX, DateTimeOffset frameTime)
    {
        if (Screen != BoardScreen.StrykersRun) return;
        bool visible = StrykersRunControlsVisible, moving = StrykersRunControlsAnimating;
        _strykersRun.SetInput(normalizedX, frameTime);
        RefreshStrykersRunCaptions(visible, moving, frameTime);
    }

    public void ClearStrykersRunInput(DateTimeOffset now)
    {
        bool visible = StrykersRunControlsVisible, moving = StrykersRunControlsAnimating;
        _strykersRun.ClearInput(now);
        RefreshStrykersRunCaptions(visible, moving, now);
    }

    private void RefreshStrykersRunCaptions(bool wasVisible, bool wasAnimating, DateTimeOffset now)
    {
        if (Screen == BoardScreen.StrykersRun &&
            (wasVisible != StrykersRunControlsVisible || wasAnimating != StrykersRunControlsAnimating))
            InvalidateStrykersRunCaptions(now);
    }

    public void ResetStrykersRun(DateTimeOffset now) => InitializeStrykersRun(now, startRun: true);

    private void InitializeStrykersRun(DateTimeOffset now, bool startRun)
    {
        if (startRun) _strykersRun.Start(now);
        else _strykersRun.Reset(now);
        StrykersRunInputRevision++;
        InvalidateStrykersRunCaptions(now);
    }

    private void InvalidateStrykersRunCaptions(DateTimeOffset now)
    {
        // Fading captions cannot be a camera hold reference. Reappearing
        // captions must be observed clear before another press can begin.
        ResetHoldCaptionEvidence();
        _holdCaptionObservedAt = Later(_holdCaptionObservedAt, now);
        BottomDrawerGestureBarrier(now);
    }

    private IReadOnlyList<BoardButton> CurrentStrykersRunButtons()
    {
        double progress = StrykersRunControlsProgress;
        if (progress <= 0) return [];
        if (_strykersRun.RecoveringInput)
            return
            [
                new("strykers-run-exit", "EXIT", StrykersRunButtonBounds(.015, .12, besideRail: true), BoardScreen.Menu,
                    Hold: BoardButtonHold.Once),
                new("strykers-run-exit-right", "EXIT", StrykersRunButtonBounds(.865, .12, besideRail: true), BoardScreen.Menu,
                    Hold: BoardButtonHold.Once)
            ];
        bool enabled = !StrykersRunControlsAnimating;
        return
        [
            new("strykers-run-exit", "EXIT", StrykersRunButtonBounds(.10, .32), BoardScreen.Menu,
                Enabled: enabled, Hold: BoardButtonHold.Once),
            new("strykers-run-play", "PLAY", StrykersRunButtonBounds(.58, .32), BoardScreen.StrykersRun,
                Enabled: enabled, Hold: BoardButtonHold.Once)
        ];
    }

    private BoardRect StrykersRunButtonBounds(double x, double width, bool besideRail = false)
    {
        var field = _strykersRunFieldBounds;
        // The black footer occupies the final tenth of the portrait field.
        // Keep the paused exits at the board sides for a clear approach to either caption.
        return new(besideRail ? x : field.X + field.Width * x,
            field.Y + field.Height * .9075, besideRail ? width : field.Width * width, field.Height * .085);
    }

    private bool SelectStrykersRunButton(BoardButton button, DateTimeOffset now)
    {
        if (!StrykersRunControlsVisible || StrykersRunControlsAnimating) return false;
        switch (button.Id)
        {
            case "strykers-run-exit":
            case "strykers-run-exit-right":
                ClearStrykersRunInput(now);
                Show(BoardScreen.Menu, now);
                return true;
            case "strykers-run-play":
                ResetStrykersRun(now);
                return true;
            default: return false;
        }
    }
}
