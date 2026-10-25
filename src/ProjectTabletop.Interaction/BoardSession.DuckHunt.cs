namespace ProjectTabletop.Interaction;

public sealed record DuckHuntMicrophoneChoice(string Id, string Label);
public enum DuckHuntView { Menu, TargetPractice, Hunt }

public sealed partial class BoardSession
{
    private readonly DuckHuntGalleryGame _gallery = new();
    private readonly DuckHuntFlightGame _huntFlight = new();
    private readonly BottomDrawerState _duckHuntDrawer = new();
    public bool DuckHuntDrawerOpen => Screen == BoardScreen.DuckHunt && _duckHuntDrawer.Open;
    public DuckHuntView CurrentDuckHuntView { get; private set; }

    public DuckHuntGallerySnapshot GalleryState => _gallery.Snapshot;
    public IReadOnlyList<DuckHuntGalleryPuncture> GalleryPunctures => _gallery.Punctures;
    public DuckHuntFlightSnapshot HuntFlightState => _huntFlight.Snapshot;
    public long DuckHuntRoundRevision { get; private set; }
    public static readonly BoardRect DuckHuntTargetPracticeBounds = new(.30, .40, .40, .12);
    public static readonly BoardRect DuckHuntHuntBounds = new(.30, .56, .40, .12);
    public static readonly BoardRect DuckHuntBackBounds = new(.365, .87, .155, .12);
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
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.Hunt)
        {
            _huntFlight.Advance(now);
            return;
        }
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return;
        _gallery.Advance(now);
    }
    public void SetDuckHuntReady(bool ready, DateTimeOffset now, double boardAspect = 1)
    {
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.Hunt)
        {
            _huntFlight.SetReady(ready && double.IsFinite(boardAspect) && boardAspect > 0, now);
            return;
        }
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return;
        _gallery.SetReady(ready && double.IsFinite(boardAspect) && boardAspect > 0, now);
    }
    public double GetDuckHuntDrawerProgress(DateTimeOffset now) =>
        BottomDrawerProgress(_duckHuntDrawer, BoardScreen.DuckHunt, now);

    public bool TryDuckHuntShot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1)
    {
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.Hunt)
        {
            bool acted = _huntFlight.Shoot(aim, shotTime, boardAspect);
            if (acted && _huntFlight.Snapshot.Phase == DuckHuntFlightPhase.Launching)
            {
                DuckHuntRoundRevision++;
                SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false,
                    Later(shotTime, _duckHuntDrawer.ObservedAt));
            }
            return acted;
        }
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return false;
        int cycleBefore = _gallery.Snapshot.Cycle;
        bool accepted = _gallery.Shoot(aim, shotTime, boardAspect);
        if (_gallery.Snapshot.Cycle != cycleBefore)
        {
            DuckHuntRoundRevision++;
            SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false,
                Later(shotTime, _duckHuntDrawer.ObservedAt));
        }
        return accepted;
    }

    private void InitializeDuckHunt(DateTimeOffset now)
    {
        _gallery.Reset(now);
        _huntFlight.Reset(now);
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
