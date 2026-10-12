using System.Numerics;

namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public const double FootballVictoryDurationSeconds = 10;
    public const double FootballVictoryControlsSlideSeconds = 1.5;
    private readonly FootballGame _football = new();
    private double _footballControlTop = .885;
    private double _footballVictoryControlsProgress;
    public FootballSnapshot FootballState => _football.Snapshot;
    public long FootballInputRevision { get; private set; }
    public bool FootballControlsVisible => _football.Phase is FootballPhase.Ready or FootballPhase.WaitingForPlayers ||
        _football.Phase == FootballPhase.Finished && _footballVictoryControlsProgress > 0;
    public bool FootballControlsAnimating => _football.Phase == FootballPhase.Finished &&
        _footballVictoryControlsProgress is > 0 and < 1;

    public void ShowFootball(DateTimeOffset? now = null) => Show(BoardScreen.Football, now ?? MonotonicClock.UtcNow);

    public void TickFootball(DateTimeOffset now)
    {
        if (Screen != BoardScreen.Football) return;
        bool controlsWereVisible = FootballControlsVisible;
        _football.Advance(now);
        UpdateFootballControlVisibility(controlsWereVisible, now);
    }

    public void SetFootballInput(int player, Vector2? position, DateTimeOffset frameTime, float? heading = null)
    {
        bool controlsWereVisible = FootballControlsVisible;
        _football.SetPlayerInput(player, Screen == BoardScreen.Football ? position : null, frameTime, heading);
        UpdateFootballControlVisibility(controlsWereVisible, frameTime);
    }

    public void ClearFootballInput(DateTimeOffset now)
    {
        SetFootballInput(0, null, now);
        SetFootballInput(1, null, now);
    }

    public void ResetFootball(DateTimeOffset now) => InitializeFootball(now, startMatch: true);

    private void InitializeFootball(DateTimeOffset now, bool startMatch)
    {
        if (startMatch) _football.Start(now);
        else _football.Reset(now);
        _footballVictoryControlsProgress = 0;
        ClearFootballInput(now);
        // PLAY becomes RESET in the same place. The new lettering must be seen
        // clear before it can arm, even though the controls stayed visible.
        ResetHoldCaptionEvidence();
        _holdCaptionObservedAt = Later(_holdCaptionObservedAt, now);
        BottomDrawerGestureBarrier(now);
        FootballInputRevision++;
        Revision++;
    }

    public void SetFootballMode(FootballMode mode, DateTimeOffset now)
    {
        if (mode == FootballState.Mode) return;
        _football.SetMode(mode, now);
        _footballVictoryControlsProgress = 0;
        ClearFootballInput(now);
        FootballInputRevision++;
        ResetHoldCaptionEvidence();
        _holdCaptionObservedAt = Later(_holdCaptionObservedAt, now);
        BottomDrawerGestureBarrier(now);
    }

    public void SetFootballStyle(int player, FootballKickerStyle style)
    {
        _football.SetStyle(player, style);
        Revision++;
    }

    public void SetFootballControlTop(double top, DateTimeOffset now)
    {
        if (!double.IsFinite(top) || top is < 0 or > .905) throw new ArgumentOutOfRangeException(nameof(top));
        if (Math.Abs(_footballControlTop - top) < .000001) return;
        _footballControlTop = top;
        ResetHoldCaptionEvidence();
        _holdCaptionObservedAt = Later(_holdCaptionObservedAt, now);
        BottomDrawerGestureBarrier(now);
    }

    /// <summary>Uses the renderer's celebration clock so controls appear only after its confetti settles.</summary>
    public void SetFootballVictoryPresentation(double elapsedSeconds, DateTimeOffset now)
    {
        if (!double.IsFinite(elapsedSeconds)) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        double progress = _football.Phase == FootballPhase.Finished
            ? Math.Clamp((elapsedSeconds - FootballVictoryDurationSeconds) / FootballVictoryControlsSlideSeconds, 0, 1)
            : 0;
        if (progress == _footballVictoryControlsProgress) return;
        bool controlsWereVisible = FootballControlsVisible;
        bool controlsWereAnimating = FootballControlsAnimating;
        _footballVictoryControlsProgress = progress;
        Revision++;
        if (controlsWereVisible == FootballControlsVisible && controlsWereAnimating == FootballControlsAnimating) return;
        // Moving lettering is never a hold reference. On arrival, require a new
        // clear caption and discard any delayed evidence from the entrance.
        ResetHoldCaptionEvidence();
        _holdCaptionObservedAt = Later(_holdCaptionObservedAt, now);
        BottomDrawerGestureBarrier(now);
    }

    private void UpdateFootballControlVisibility(bool controlsWereVisible, DateTimeOffset now)
    {
        if (Screen != BoardScreen.Football || controlsWereVisible == FootballControlsVisible) return;
        // Captions disappear throughout play, including short tracking recovery and
        // goal celebrations. Reappearing controls need new clear lettering before
        // they can arm; delayed evidence from their previous appearance is invalid.
        ResetHoldCaptionEvidence();
        _holdCaptionObservedAt = Later(_holdCaptionObservedAt, now);
        BottomDrawerGestureBarrier(now);
    }

    private IReadOnlyList<BoardButton> CurrentFootballButtons()
    {
        if (!FootballControlsVisible) return [];
        bool enabled = !FootballControlsAnimating;
        bool showPlay = _football.Phase is FootballPhase.Ready or FootballPhase.Finished;
        double progress = _footballVictoryControlsProgress;
        double top = enabled ? _footballControlTop :
            1.02 + (_footballControlTop - 1.02) * progress * progress * (3 - 2 * progress);
        return
        [
            new("football-exit", "EXIT", new(.06, top, .20, .095), BoardScreen.Menu, Enabled: enabled, Hold: BoardButtonHold.Once),
            new(showPlay ? "football-play" : "football-reset", showPlay ? "PLAY" : "RESET",
                new(.28, top, .20, .095), BoardScreen.Football, Enabled: enabled, Hold: BoardButtonHold.Once),
            new("football-mode", FootballState.Mode == FootballMode.HumanVsAi ? "2 PLAYERS" : "VS AI",
                new(.68, top, .26, .095), BoardScreen.Football, Enabled: enabled, Hold: BoardButtonHold.Once)
        ];
    }

    private bool SelectFootballButton(BoardButton button, DateTimeOffset now)
    {
        if (!FootballControlsVisible || FootballControlsAnimating) return false;
        switch (button.Id)
        {
            case "football-exit":
                ClearFootballInput(now);
                Show(BoardScreen.Menu, now);
                return true;
            case "football-play":
            case "football-reset":
                ResetFootball(now);
                break;
            case "football-mode":
                SetFootballMode(FootballState.Mode == FootballMode.HumanVsAi ? FootballMode.TwoHumans : FootballMode.HumanVsAi, now);
                break;
            default: return false;
        }
        BottomDrawerGestureBarrier(now);
        return true;
    }
}
