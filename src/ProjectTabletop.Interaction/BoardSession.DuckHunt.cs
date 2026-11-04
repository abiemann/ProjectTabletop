namespace ProjectTabletop.Interaction;

public sealed record DuckHuntMicrophoneChoice(string Id, string Label);
public enum DuckHuntView { Menu, TargetPractice, Hunt, ClayPigeon }

public sealed partial class BoardSession
{
    private readonly DuckHuntGalleryGame _gallery = new();
    private readonly DuckHuntFlightGame _huntFlight = new();
    private readonly DuckHuntClayGame _clay = new();
    private readonly BottomDrawerState _duckHuntDrawer = new();
    private double _huntFlightBoardAspect = 1;
    public bool DuckHuntDrawerOpen => Screen == BoardScreen.DuckHunt && _duckHuntDrawer.Open;
    public DuckHuntView CurrentDuckHuntView { get; private set; }

    public DuckHuntGallerySnapshot GalleryState => _gallery.Snapshot;
    public IReadOnlyList<DuckHuntGalleryPuncture> GalleryPunctures => _gallery.Punctures;
    public IReadOnlyList<DuckHuntGalleryScorePopup> GalleryScorePopups => _gallery.ScorePopups;
    public DuckHuntFlightSnapshot HuntFlightState => _huntFlight.Snapshot;
    public DuckHuntClaySnapshot ClayState => _clay.Snapshot;
    public IReadOnlyList<DuckHuntClayTarget> ClayTargets => _clay.Targets;
    public long DuckHuntRoundRevision { get; private set; }
    public static readonly BoardRect DuckHuntTargetPracticeBounds = new(.30, .40, .40, .12);
    public static readonly BoardRect DuckHuntHuntBounds = new(.30, .56, .40, .12);
    public static readonly BoardRect DuckHuntClayPigeonBounds = new(.30, .72, .40, .12);
    public static readonly BoardRect DuckHuntBackBounds = new(.365, .87, .155, .12);
    public static readonly BoardRect DuckHuntPlayBounds = new(.530, .87, .155, .12);
    public static readonly BoardRect DuckHuntPlayAgainBounds = new(.530, .87, .245, .12);
    public void ShowDuckHunt(DateTimeOffset? now = null) =>
        Show(BoardScreen.DuckHunt, now ?? MonotonicClock.UtcNow);
    public void ShowDuckHuntTargetPractice(DateTimeOffset? now = null) =>
        ShowDuckHuntView(DuckHuntView.TargetPractice, now ?? MonotonicClock.UtcNow);
    public void ShowDuckHuntHunt(DateTimeOffset? now = null) =>
        ShowDuckHuntView(DuckHuntView.Hunt, now ?? MonotonicClock.UtcNow);
    public void ShowDuckHuntClayPigeon(DateTimeOffset? now = null) =>
        ShowDuckHuntView(DuckHuntView.ClayPigeon, now ?? MonotonicClock.UtcNow);

    private void ShowDuckHuntView(DuckHuntView view, DateTimeOffset now)
    {
        if (Screen != BoardScreen.DuckHunt) Show(BoardScreen.DuckHunt, now);
        CurrentDuckHuntView = view;
        InitializeDuckHunt(now);
        ClearBottomDrawerUi(_duckHuntDrawer);
        _duckHuntDrawer.ObservedAt = Later(_duckHuntDrawer.ObservedAt, now);
        // No old caption, click snapshot or hand selection may cross a view change.
        if (view is DuckHuntView.TargetPractice or DuckHuntView.Hunt)
            SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: true, now);
        else BottomDrawerInputBarrier(_duckHuntDrawer, now);
    }

    public void TickDuckHunt(DateTimeOffset now)
    {
        AdvanceBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, now);
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.ClayPigeon)
        {
            bool wasFinished = _clay.Snapshot.RoundFinished;
            _clay.Advance(now);
            FinishDuckHuntClay(wasFinished, now);
            return;
        }
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.Hunt)
        {
            bool wasFinished = _huntFlight.Snapshot.RoundFinished;
            _huntFlight.Advance(now);
            FinishDuckHuntFlight(wasFinished, now);
            return;
        }
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return;
        bool galleryWasFinished = _gallery.Snapshot.RoundFinished;
        _gallery.Advance(now);
        FinishDuckHuntGallery(galleryWasFinished, now);
    }
    public void SetDuckHuntReady(bool ready, DateTimeOffset now, double boardAspect = 1)
    {
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.ClayPigeon)
        {
            bool wasFinished = _clay.Snapshot.RoundFinished;
            bool clayWasPaused = _clay.Snapshot.Paused;
            _clay.SetReady(ready && double.IsFinite(boardAspect) && boardAspect > 0, now);
            FinishDuckHuntClay(wasFinished, now);
            if (clayWasPaused != _clay.Snapshot.Paused)
                ResetHoldCaptionEvidence(["duck-hunt-play-again"], now);
            return;
        }
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.Hunt)
        {
            bool wasFinished = _huntFlight.Snapshot.RoundFinished;
            bool flightWasPaused = _huntFlight.Snapshot.Paused;
            _huntFlightBoardAspect = boardAspect;
            _huntFlight.SetReady(ready && double.IsFinite(boardAspect) && boardAspect > 0, now);
            FinishDuckHuntFlight(wasFinished, now);
            if (flightWasPaused != _huntFlight.Snapshot.Paused)
                ResetHoldCaptionEvidence(["duck-hunt-play"], now);
            return;
        }
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return;
        bool galleryWasFinished = _gallery.Snapshot.RoundFinished;
        bool wasPaused = _gallery.Snapshot.Paused;
        _gallery.SetReady(ready && double.IsFinite(boardAspect) && boardAspect > 0, now);
        FinishDuckHuntGallery(galleryWasFinished, now);
        if (wasPaused != _gallery.Snapshot.Paused)
            ResetHoldCaptionEvidence(["duck-hunt-play"], now);
    }
    public double GetDuckHuntDrawerProgress(DateTimeOffset now) =>
        BottomDrawerProgress(_duckHuntDrawer, BoardScreen.DuckHunt, now);

    public bool TryDuckHuntShot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1,
        DateTimeOffset? sceneTime = null)
    {
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.ClayPigeon)
        {
            bool wasFinished = _clay.Snapshot.RoundFinished;
            bool hit = _clay.Shoot(aim, shotTime, boardAspect, sceneTime);
            FinishDuckHuntClay(wasFinished, Later(shotTime, _duckHuntDrawer.ObservedAt));
            return hit;
        }
        if (Screen == BoardScreen.DuckHunt && CurrentDuckHuntView == DuckHuntView.Hunt)
        {
            bool wasFinished = _huntFlight.Snapshot.RoundFinished;
            bool hit = _huntFlight.Shoot(aim, shotTime, boardAspect, sceneTime);
            FinishDuckHuntFlight(wasFinished, Later(shotTime, _duckHuntDrawer.ObservedAt));
            return hit;
        }
        if (Screen != BoardScreen.DuckHunt || CurrentDuckHuntView != DuckHuntView.TargetPractice) return false;
        bool galleryWasFinished = _gallery.Snapshot.RoundFinished;
        bool galleryHit = _gallery.Shoot(aim, shotTime, boardAspect, sceneTime);
        FinishDuckHuntGallery(galleryWasFinished, Later(shotTime, _duckHuntDrawer.ObservedAt));
        return galleryHit;
    }

    private void InitializeDuckHunt(DateTimeOffset now)
    {
        _gallery.Reset(now);
        _huntFlight.Reset(now);
        _clay.Reset(now);
        _huntFlightBoardAspect = 1;
        DuckHuntRoundRevision++;
    }

    private void FinishDuckHuntClay(bool wasFinished, DateTimeOffset now)
    {
        if (wasFinished || !_clay.Snapshot.RoundFinished) return;
        OpenDuckHuntReplayDrawer(now);
    }

    private void FinishDuckHuntFlight(bool wasFinished, DateTimeOffset now)
    {
        if (wasFinished || !_huntFlight.Snapshot.RoundFinished) return;
        OpenDuckHuntReplayDrawer(now);
    }

    private void FinishDuckHuntGallery(bool wasFinished, DateTimeOffset now)
    {
        if (wasFinished || !_gallery.Snapshot.RoundFinished) return;
        OpenDuckHuntReplayDrawer(now);
    }

    private void OpenDuckHuntReplayDrawer(DateTimeOffset now)
    {
        // A newly displayed replay caption needs fresh evidence even if the
        // player already opened the drawer during the round.
        if (!SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: true, now))
            BottomDrawerInputBarrier(_duckHuntDrawer, now);
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
            buttons.Add(new("duck-hunt-clay-pigeon", "CLAY PIGEON", DuckHuntClayPigeonBounds,
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
        bool playVisible = CurrentDuckHuntView == DuckHuntView.TargetPractice && _gallery.Snapshot.PlayVisible ||
            CurrentDuckHuntView == DuckHuntView.Hunt && _huntFlight.Snapshot.PlayVisible;
        bool playPaused = CurrentDuckHuntView == DuckHuntView.Hunt ? _huntFlight.Snapshot.Paused : _gallery.Snapshot.Paused;
        if (playVisible)
            buttons.Add(new("duck-hunt-play", "PLAY", DuckHuntPlayBounds, BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady && !playPaused, Hold: BoardButtonHold.Once));
        if (CurrentDuckHuntView == DuckHuntView.ClayPigeon && _clay.Snapshot.RoundFinished)
            buttons.Add(new("duck-hunt-play-again", "PLAY AGAIN", DuckHuntPlayAgainBounds, BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady && !_clay.Snapshot.Paused, Hold: BoardButtonHold.Once));
        return buttons;
    }

    private bool SelectDuckHuntButton(BoardButton button, DateTimeOffset now)
    {
        if (now < _duckHuntDrawer.ObservedAt) return false;
        if (CurrentDuckHuntView == DuckHuntView.Menu &&
            button.Id is "duck-hunt-target-practice" or "duck-hunt-hunt" or "duck-hunt-clay-pigeon")
        {
            ShowDuckHuntView(button.Id switch
            {
                "duck-hunt-target-practice" => DuckHuntView.TargetPractice,
                "duck-hunt-hunt" => DuckHuntView.Hunt,
                _ => DuckHuntView.ClayPigeon
            }, now);
            return true;
        }
        if (button.Id == "duck-hunt-drawer-open")
            return SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: true, now);
        if (button.Id == "duck-hunt-drawer-close")
            return SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false, now);
        if (!DuckHuntDrawerOpen || !_duckHuntDrawer.OpeningReady) return false;
        if (button.Id == "duck-hunt-play-again" && CurrentDuckHuntView == DuckHuntView.ClayPigeon)
        {
            if (!_clay.Restart(now)) return false;
            DuckHuntRoundRevision++;
            SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false, now);
            return true;
        }
        if (button.Id == "duck-hunt-play" && CurrentDuckHuntView is DuckHuntView.TargetPractice or DuckHuntView.Hunt)
        {
            bool started = CurrentDuckHuntView == DuckHuntView.Hunt
                ? _huntFlight.Start(now, _huntFlightBoardAspect) : _gallery.Start(now);
            if (!started) return false;
            DuckHuntRoundRevision++;
            SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false, now);
            return true;
        }
        if (button.Id == "duck-hunt-back" && CurrentDuckHuntView != DuckHuntView.Menu)
        {
            ShowDuckHuntView(DuckHuntView.Menu, now);
            return true;
        }
        if (button.Id == "duck-hunt-exit") { Show(BoardScreen.Menu, now); return true; }
        return false;
    }
}
