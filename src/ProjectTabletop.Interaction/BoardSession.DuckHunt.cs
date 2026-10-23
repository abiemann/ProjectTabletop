namespace ProjectTabletop.Interaction;

public sealed record DuckHuntMicrophoneChoice(string Id, string Label);

public sealed partial class BoardSession
{
    private readonly DuckHuntGame _duckHunt = new();
    private readonly BottomDrawerState _duckHuntDrawer = new();
    public bool DuckHuntDrawerOpen => Screen == BoardScreen.DuckHunt && _duckHuntDrawer.Open;

    public DuckHuntSnapshot DuckHuntState => _duckHunt.Snapshot;
    public DuckHuntFeathers DuckHuntFeathers => _duckHunt.Feathers;
    public long DuckHuntRoundRevision { get; private set; }
    public static readonly BoardRect DuckHuntReplayBounds = new(
        DuckHuntLayout.ExitBounds.X + DuckHuntLayout.ExitBounds.Width,
        DuckHuntLayout.ExitBounds.Y, .245, DuckHuntLayout.ExitBounds.Height);
    public void ShowDuckHunt(DateTimeOffset? now = null) =>
        Show(BoardScreen.DuckHunt, now ?? MonotonicClock.UtcNow);
    public void TickDuckHunt(DateTimeOffset now)
    {
        AdvanceBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, now);
        if (Screen != BoardScreen.DuckHunt) return;
        bool finished = _duckHunt.Snapshot.RoundFinished;
        _duckHunt.Advance(now);
        RefreshDuckHuntResultControls(finished, now);
    }
    public void SetDuckHuntReady(bool ready, DateTimeOffset now, double boardAspect = 1)
    {
        if (Screen != BoardScreen.DuckHunt) return;
        if (ready && _duckHunt.Snapshot.Phase == DuckHuntPhase.Ready && _duckHunt.Start(now, boardAspect))
            SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false,
                Later(now, _duckHuntDrawer.ObservedAt));
        bool finished = _duckHunt.Snapshot.RoundFinished;
        _duckHunt.SetPaused(!ready, now);
        RefreshDuckHuntResultControls(finished, now);
    }
    public double GetDuckHuntDrawerProgress(DateTimeOffset now) =>
        BottomDrawerProgress(_duckHuntDrawer, BoardScreen.DuckHunt, now);

    public bool TryDuckHuntShot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1)
    {
        if (Screen != BoardScreen.DuckHunt) return false;
        bool finished = _duckHunt.Snapshot.RoundFinished;
        bool hit = _duckHunt.Shoot(aim, shotTime, boardAspect);
        RefreshDuckHuntResultControls(finished, shotTime);
        return hit;
    }

    private void RefreshDuckHuntResultControls(bool wasFinished, DateTimeOffset now)
    {
        bool finished = _duckHunt.Snapshot.RoundFinished;
        if (wasFinished == finished) return;
        now = Later(now, _duckHuntDrawer.ObservedAt);
        // Only react to the result transition, so a manually closed result drawer
        // stays closed. A late valid shot can also restore an escaped round.
        if (!SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: finished, now))
            BottomDrawerInputBarrier(_duckHuntDrawer, now);
    }

    private void InitializeDuckHunt(DateTimeOffset now)
    {
        _duckHunt.Reset(now);
        DuckHuntRoundRevision++;
    }

    private IReadOnlyList<BoardButton> CurrentDuckHuntButtons()
    {
        if (!DuckHuntDrawerOpen)
            return [new("duck-hunt-drawer-open", "^", BottomDrawerHandleBounds, BoardScreen.DuckHunt, Hold: BoardButtonHold.Once)];
        var buttons = new List<BoardButton>
        {
            new("duck-hunt-drawer-close", "v", BottomDrawerHandleBounds, BoardScreen.DuckHunt, Hold: BoardButtonHold.Once),
            new("duck-hunt-exit", "EXIT", DuckHuntLayout.ExitBounds, BoardScreen.Menu,
                Enabled: _duckHuntDrawer.OpeningReady, Hold: BoardButtonHold.Once)
        };
        if (_duckHunt.Snapshot.RoundFinished)
            buttons.Add(new("duck-hunt-replay", "PLAY AGAIN", DuckHuntReplayBounds, BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady, Hold: BoardButtonHold.Once));
        return buttons;
    }

    private bool SelectDuckHuntButton(BoardButton button, DateTimeOffset now)
    {
        if (now < _duckHuntDrawer.ObservedAt) return false;
        if (button.Id == "duck-hunt-replay" && _duckHunt.Snapshot.RoundFinished &&
            DuckHuntDrawerOpen && _duckHuntDrawer.OpeningReady)
        {
            InitializeDuckHunt(now);
            SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false, now);
            return true;
        }
        if (button.Id == "duck-hunt-drawer-open")
            return SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: true, now);
        if (button.Id == "duck-hunt-drawer-close")
            return SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false, now);
        if (!DuckHuntDrawerOpen || !_duckHuntDrawer.OpeningReady) return false;
        if (button.Id == "duck-hunt-exit") { Show(BoardScreen.Menu, now); return true; }
        return false;
    }
}
