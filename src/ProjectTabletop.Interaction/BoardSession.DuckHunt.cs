namespace ProjectTabletop.Interaction;

public sealed record DuckHuntMicrophoneChoice(string Id, string Label);
public enum DuckHuntView { Menu, TargetPractice, Hunt }

public sealed partial class BoardSession
{
    private readonly DuckHuntGame _duckHunt = new();
    private readonly BottomDrawerState _duckHuntDrawer = new();
    public bool DuckHuntDrawerOpen => Screen == BoardScreen.DuckHunt && _duckHuntDrawer.Open;
    public DuckHuntView CurrentDuckHuntView { get; private set; }

    public DuckHuntSnapshot DuckHuntState => _duckHunt.Snapshot;
    public DuckHuntFeathers DuckHuntFeathers => _duckHunt.Feathers;
    public long DuckHuntRoundRevision { get; private set; }
    public static readonly BoardRect DuckHuntTargetPracticeBounds = new(.30, .40, .40, .12);
    public static readonly BoardRect DuckHuntHuntBounds = new(.30, .56, .40, .12);
    public static readonly BoardRect DuckHuntBackBounds = new(.365, .87, .155, .12);
    public static readonly BoardRect DuckHuntReplayBounds = new(
        DuckHuntBackBounds.X + DuckHuntBackBounds.Width + .01,
        DuckHuntLayout.ExitBounds.Y, .245, DuckHuntLayout.ExitBounds.Height);
    public void ShowDuckHunt(DateTimeOffset? now = null) =>
        Show(BoardScreen.DuckHunt, now ?? MonotonicClock.UtcNow);
    public void ShowDuckHuntTargetPractice(DateTimeOffset? now = null) =>
        ShowDuckHuntView(DuckHuntView.TargetPractice, now ?? MonotonicClock.UtcNow);
    public void ShowDuckHuntHunt(DateTimeOffset? now = null) =>
        ShowDuckHuntView(DuckHuntView.Hunt, now ?? MonotonicClock.UtcNow);

    private void ShowDuckHuntView(DuckHuntView view, DateTimeOffset now)
    {
        if (Screen != BoardScreen.DuckHunt) Show(BoardScreen.DuckHunt, now);
        CurrentDuckHuntView = view;
        InitializeDuckHunt(now);
        ClearBottomDrawerUi(_duckHuntDrawer);
        _duckHuntDrawer.ObservedAt = Later(_duckHuntDrawer.ObservedAt, now);
        // No old caption, click snapshot or hand selection may cross a view change.
        BottomDrawerInputBarrier(_duckHuntDrawer, now);
    }

    public void TickDuckHunt(DateTimeOffset now)
    {
        AdvanceBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, now);
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return;
        bool finished = _duckHunt.Snapshot.RoundFinished;
        _duckHunt.Advance(now);
        RefreshDuckHuntResultControls(finished, now);
    }
    public void SetDuckHuntReady(bool ready, DateTimeOffset now, double boardAspect = 1)
    {
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return;
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
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return false;
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

    private void InitializeDuckHuntMenu(DateTimeOffset now)
    {
        CurrentDuckHuntView = DuckHuntView.Menu;
        InitializeDuckHunt(now);
    }

    private IReadOnlyList<BoardButton> CurrentDuckHuntButtons()
    {
        var buttons = new List<BoardButton>();
        if (CurrentDuckHuntView == DuckHuntView.Menu)
        {
            buttons.Add(new("duck-hunt-target-practice", "TARGET PRACTICE", DuckHuntTargetPracticeBounds,
                BoardScreen.DuckHunt, Hold: BoardButtonHold.Once));
            buttons.Add(new("duck-hunt-hunt", "HUNT", DuckHuntHuntBounds,
                BoardScreen.DuckHunt, Hold: BoardButtonHold.Once));
        }
        buttons.Add(new(DuckHuntDrawerOpen ? "duck-hunt-drawer-close" : "duck-hunt-drawer-open",
            DuckHuntDrawerOpen ? "v" : "^", BottomDrawerHandleBounds, BoardScreen.DuckHunt, Hold: BoardButtonHold.Once));
        if (!DuckHuntDrawerOpen) return buttons;
        buttons.Add(new("duck-hunt-exit", "EXIT", DuckHuntLayout.ExitBounds, BoardScreen.Menu,
            Enabled: _duckHuntDrawer.OpeningReady, Hold: BoardButtonHold.Once));
        if (CurrentDuckHuntView != DuckHuntView.Menu)
            buttons.Add(new("duck-hunt-back", "BACK", DuckHuntBackBounds, BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady, Hold: BoardButtonHold.Once));
        if (CurrentDuckHuntView == DuckHuntView.TargetPractice && _duckHunt.Snapshot.RoundFinished)
            buttons.Add(new("duck-hunt-replay", "PLAY AGAIN", DuckHuntReplayBounds, BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady, Hold: BoardButtonHold.Once));
        return buttons;
    }

    private bool SelectDuckHuntButton(BoardButton button, DateTimeOffset now)
    {
        if (now < _duckHuntDrawer.ObservedAt) return false;
        if (CurrentDuckHuntView == DuckHuntView.Menu &&
            button.Id is "duck-hunt-target-practice" or "duck-hunt-hunt")
        {
            ShowDuckHuntView(button.Id == "duck-hunt-target-practice"
                ? DuckHuntView.TargetPractice : DuckHuntView.Hunt, now);
            return true;
        }
        if (CurrentDuckHuntView == DuckHuntView.TargetPractice &&
            button.Id == "duck-hunt-replay" && _duckHunt.Snapshot.RoundFinished &&
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
        if (button.Id == "duck-hunt-back" && CurrentDuckHuntView != DuckHuntView.Menu)
        {
            ShowDuckHuntView(DuckHuntView.Menu, now);
            return true;
        }
        if (button.Id == "duck-hunt-exit") { Show(BoardScreen.Menu, now); return true; }
        return false;
    }
}
