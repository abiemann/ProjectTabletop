namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private readonly StrykersRunGame _strykersRun = new();
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

    public void TickStrykersRun(DateTimeOffset now)
    {
        if (Screen != BoardScreen.StrykersRun) return;
        bool visible = StrykersRunControlsVisible, moving = StrykersRunControlsAnimating;
        _strykersRun.Advance(now);
        if (visible != StrykersRunControlsVisible || moving != StrykersRunControlsAnimating)
            InvalidateStrykersRunCaptions(now);
    }

    public void SetStrykersRunInput(float? normalizedX, DateTimeOffset frameTime)
    {
        if (Screen == BoardScreen.StrykersRun) _strykersRun.SetInput(normalizedX, frameTime);
    }

    public void ClearStrykersRunInput(DateTimeOffset now) => _strykersRun.ClearInput(now);

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
        // Fading captions cannot be a camera hold reference. Reappearing PLAY
        // must be observed clear before another press can begin.
        ResetHoldCaptionEvidence();
        _holdCaptionObservedAt = Later(_holdCaptionObservedAt, now);
        BottomDrawerGestureBarrier(now);
    }

    private IReadOnlyList<BoardButton> CurrentStrykersRunButtons()
    {
        double progress = StrykersRunControlsProgress;
        if (progress <= 0) return [];
        bool enabled = !StrykersRunControlsAnimating;
        return
        [
            new("strykers-run-exit", "EXIT", new(.10, .855, .32, .085), BoardScreen.Menu,
                Enabled: enabled, Hold: BoardButtonHold.Once),
            new("strykers-run-play", "PLAY", new(.58, .855, .32, .085), BoardScreen.StrykersRun,
                Enabled: enabled, Hold: BoardButtonHold.Once)
        ];
    }

    private bool SelectStrykersRunButton(BoardButton button, DateTimeOffset now)
    {
        if (!StrykersRunControlsVisible || StrykersRunControlsAnimating) return false;
        switch (button.Id)
        {
            case "strykers-run-exit":
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
