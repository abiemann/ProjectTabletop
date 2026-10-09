using System.Numerics;

namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private readonly FootballGame _football = new();
    public FootballSnapshot FootballState => _football.Snapshot;
    public long FootballInputRevision { get; private set; }

    public void ShowFootball(DateTimeOffset? now = null) => Show(BoardScreen.Football, now ?? MonotonicClock.UtcNow);

    public void TickFootball(DateTimeOffset now)
    {
        if (Screen == BoardScreen.Football) _football.Advance(now);
    }

    public void SetFootballInput(int player, Vector2? position, DateTimeOffset frameTime, float? heading = null) =>
        _football.SetPlayerInput(player, Screen == BoardScreen.Football ? position : null, frameTime, heading);

    public void ClearFootballInput(DateTimeOffset now)
    {
        _football.SetPlayerInput(0, null, now);
        _football.SetPlayerInput(1, null, now);
    }

    public void ResetFootball(DateTimeOffset now)
    {
        _football.Reset(now);
        ClearFootballInput(now);
        FootballInputRevision++;
        Revision++;
    }

    public void SetFootballMode(FootballMode mode, DateTimeOffset now)
    {
        if (mode == FootballState.Mode) return;
        _football.SetMode(mode, now);
        ClearFootballInput(now);
        FootballInputRevision++;
        ResetHoldCaptionEvidence();
        BottomDrawerGestureBarrier(now);
    }

    public void SetFootballStyle(int player, FootballKickerStyle style)
    {
        _football.SetStyle(player, style);
        Revision++;
    }

    private IReadOnlyList<BoardButton> CurrentFootballButtons() =>
    [
        new("football-exit", "EXIT", new(.06, .885, .20, .095), BoardScreen.Menu, Hold: BoardButtonHold.Once),
        new("football-reset", "RESET", new(.28, .885, .20, .095), BoardScreen.Football, Hold: BoardButtonHold.Once),
        new("football-mode", FootballState.Mode == FootballMode.HumanVsAi ? "2 PLAYERS" : "VS AI",
            new(.68, .885, .26, .095), BoardScreen.Football, Hold: BoardButtonHold.Once)
    ];

    private bool SelectFootballButton(BoardButton button, DateTimeOffset now)
    {
        switch (button.Id)
        {
            case "football-exit":
                ClearFootballInput(now);
                Show(BoardScreen.Menu, now);
                return true;
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
