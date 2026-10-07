namespace ProjectTabletop.Interaction;

/// <summary>A newly dealt player HIT card, after gameplay and input barriers have been updated.</summary>
public sealed record BlackjackHit(long Sequence, int RoundNumber, int HandIndex, int CardIndex,
    BlackjackCard Card, DateTimeOffset StartedAt);

/// <summary>An accepted DEAL, with the public game snapshots before and after the action.</summary>
public sealed record BlackjackDeal(long Sequence, BlackjackSnapshot Previous, BlackjackSnapshot Current,
    DateTimeOffset StartedAt);

public sealed partial class BoardSession
{
    private readonly BlackjackGame _blackjack;
    private long _blackjackHitSequence;
    private long _blackjackDealSequence;
    private DateTimeOffset? _blackjackPresentationUntil;
    private DateTimeOffset _blackjackPresentationObservedAt = DateTimeOffset.MinValue;

    /// <summary>Raised once for each successful HIT from any input route; sequence numbers survive resets.</summary>
    public event Action<BlackjackHit>? BlackjackHitOccurred;

    /// <summary>Raised after DEAL state and input barriers are committed, once per accepted action.</summary>
    public event Action<BlackjackDeal>? BlackjackDealOccurred;

    public BoardSession(BlackjackGame? blackjack = null, CrownDeedGame? crownDeed = null, GlobeState? globe = null,
        SlotGame? slots = null, RouletteGame? roulette = null)
    {
        _blackjack = blackjack ?? new BlackjackGame();
        _slots = slots ?? new SlotGame();
        _roulette = roulette ?? new RouletteGame();
        _crownDeed = crownDeed ?? new CrownDeedGame();
        _crownDeed.RollOccurred += RelayCrownDeedRoll;
        _crownDeed.DevelopmentOccurred += RelayCrownDeedDevelopment;
        _globe = globe ?? new GlobeState();
    }
    public BlackjackSnapshot BlackjackState => _blackjack.Snapshot;

    /// <summary>
    /// Temporarily disables game controls and dealer progress while a presentation runs.
    /// Navigation remains available. The hold expires using the next Update, ActivateButton or
    /// TickBlackjack supplied time; an additional hold can extend, but never shorten, its deadline.
    /// </summary>
    public void HoldBlackjackPresentationUntil(DateTimeOffset until)
    {
        if (Screen != BoardScreen.Blackjack || until <= _blackjackPresentationObservedAt) return;
        _blackjackPresentationUntil = _blackjackPresentationUntil is { } previous ? Later(previous, until) : until;
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(_blackjackPresentationObservedAt);
    }

    public bool TickBlackjack(DateTimeOffset now)
    {
        AdvanceBlackjackPresentation(now);
        if (Screen != BoardScreen.Blackjack) return false;
        if (_blackjackPresentationUntil is not null) return false;
        var phase = _blackjack.Snapshot.Phase;
        if (!_blackjack.Tick(now)) return false;
        if (_blackjack.Snapshot.Phase != phase)
        {
            // A camera pinch made while dealer controls were disabled must not
            // land on a newly enabled bet or Deal target after settlement.
            _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
            _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
            HoveredButtonIds = Array.Empty<string>();
            InvalidateFingerSelection(now);
        }
        return true;
    }

    // Mouse/touch and pinch selections share the same enabled targets and rules.
    public bool ActivateButton(string id, DateTimeOffset now)
    {
        AdvanceBlackjackPresentation(now);
        AdvanceCrownDeedPresentation(now);
        AdvanceBottomDrawers(now);
        AdvanceSlots(now);
        AdvanceRoulette(now);
        AdvanceMenuScroll(now);
        var button = Buttons.FirstOrDefault(item => item.Id == id && item.Enabled);
        if (button is null || !SelectButton(button, now, pointerAction: true)) return false;
        // A laptop/touch press consumes the same single-action readiness even
        // if no camera frame arrives while the resulting control is disabled.
        if (button.Hold == BoardButtonHold.Once) ResetHoldCaptionEvidence([button.Id]);
        return true;
    }

    private bool SelectButton(BoardButton button, DateTimeOffset now, bool pointerAction = false)
    {
        AdvanceBlackjackPresentation(now);
        AdvanceCrownDeedPresentation(now);
        AdvanceBottomDrawers(now);
        AdvanceMenuScroll(now);
        BlackjackHit? hit = null;
        BlackjackDeal? deal = null;
        bool openedBoard = false;
        if (Screen == BoardScreen.Menu && button.Id is "menu-scroll-down" or "menu-scroll-up")
            return SelectMenuScroll(button, now);
        if (Screen == BoardScreen.Roulette)
            return SelectRouletteButton(button, now);
        if (Screen == BoardScreen.CrownDeed)
        {
            return SelectCrownDeedButton(button, now);
        }
        if (Screen == BoardScreen.Globe)
        {
            return SelectGlobeButton(button, now);
        }
        if (Screen == BoardScreen.WaterGarden)
        {
            return SelectWaterGardenButton(button, now);
        }
        if (Screen == BoardScreen.Slots)
        {
            return SelectSlotsButton(button, now);
        }
        if (Screen == BoardScreen.PhotoCopy)
        {
            if (now < _photoCopyDrawer.ObservedAt) return false;
            if (button.Id == "photo-drawer-open")
                return SelectBottomDrawer(_photoCopyDrawer, BoardScreen.PhotoCopy, open: true, now);
            if (button.Id == "photo-drawer-close")
                return SelectBottomDrawer(_photoCopyDrawer, BoardScreen.PhotoCopy, open: false, now);
            // The hidden object-field shutter remains independent of the drawer.
            // Every visible action needs an open, settled current control row.
            if (button.Id != PhotoCopyShutter.Id && (!PhotoCopyDrawerOpen || !_photoCopyDrawer.OpeningReady)) return false;
        }
        bool licensesRequested = Screen == BoardScreen.Settings && button.Id == "licenses";
        if (licensesRequested)
        {
            // The application owns the readable dialog. This is an action on
            // Settings, not navigation; preserve the board and its references.
        }
        else if (button.Id == "paint-save")
        {
            if (Screen != BoardScreen.Paint || !PaintSaveEnabled) return false;
            // Save reports an action while preserving the active canvas and its
            // session revision. The application owns the image export.
        }
        else if (TryGetPhotoCopyAction(button.Id, out var photoAction))
        {
            if (Screen != BoardScreen.PhotoCopy || (photoAction == PhotoCopyAction.Save
                ? !PhotoCopyHasSwirl : !PhotoCopyShutterEnabled || PhotoCopyHasSwirl)) return false;
            // Capturing or saving a photo must not navigate or restart the capture session.
        }
        else if (Screen == BoardScreen.Blackjack && button.Id != "menu")
        {
            if (_blackjackPresentationUntil is not null) return false;
            var beforeDeal = button.Id == "bj-deal" ? _blackjack.Snapshot : null;
            int hitHandIndex = button.Id == "bj-hit" ? _blackjack.Snapshot.ActiveHandIndex : -1;
            if (!_blackjack.HandleAction(button.Id, now)) return false;
            if (beforeDeal is not null)
                deal = new(++_blackjackDealSequence, beforeDeal, _blackjack.Snapshot, now);
            if (hitHandIndex >= 0)
            {
                var after = _blackjack.Snapshot;
                var cards = after.Hands[hitHandIndex].Cards;
                hit = new(++_blackjackHitSequence, after.RoundNumber, hitHandIndex, cards.Count - 1, cards[^1], now);
            }
        }
        else
        {
            ClearBlackjackPresentationHold();
            ClearCrownDeedPresentationHold();
            ClearCrownDeedDrawerUi();
            // Clear intentionally restarts the captured result, but keeps its
            // drawer in place so the new Swirl/Copy captions remain available.
            if (Screen != BoardScreen.PhotoCopy || button.Id != "capture-again") ClearBottomDrawers(now);
            Screen = button.Destination;
            if (Screen == BoardScreen.Globe) _globe.Start(now);
            Revision++;
            NavigationRevision++;
            if (Screen == BoardScreen.WaterGarden) RequestWaterGardenReset();
            openedBoard = true;
        }
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
        if (pointerAction) _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        if (openedBoard) BoardOpened?.Invoke(Screen);
        if (hit is not null) BlackjackHitOccurred?.Invoke(hit);
        if (deal is not null) BlackjackDealOccurred?.Invoke(deal);
        if (licensesRequested) LicensesRequested?.Invoke();
        return true;
    }

    private void AdvanceBlackjackPresentation(DateTimeOffset now)
    {
        _blackjackPresentationObservedAt = Later(_blackjackPresentationObservedAt, now);
        if (_blackjackPresentationUntil is not { } until || now < until) return;
        _blackjackPresentationUntil = null;
        // A delayed camera pulse or old pointing anchor cannot become a click when
        // controls reappear. Finger selection must also acquire a fresh together pose.
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, until);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, until);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, until);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(until);
    }

    private void ClearBlackjackPresentationHold() => _blackjackPresentationUntil = null;

    private IReadOnlyList<BoardButton> BlackjackButtons()
    {
        var game = _blackjack.Snapshot;
        var result = new List<BoardButton>
        {
            new("menu", "Exit", new(.06, .055, .18, .09), BoardScreen.Menu)
        };
        Add("bj-reset", "Your Chips", new(.742, .055, .198, .09));
        if (game.Phase is BlackjackPhase.Betting or BlackjackPhase.RoundOver)
        {
            int[] bets = [10, 25, 50, 100];
            for (int i = 0; i < bets.Length; i++)
                Add($"bj-bet-{bets[i]}", bets[i].ToString(), new(.08 + i * .14, .775, .12, .095));
            // Deal is a one-time long-press, like the round actions that replace it.
            Add("bj-deal", game.Phase == BlackjackPhase.Betting ? "Deal" : "Deal again", new(.68, .775, .24, .095),
                BoardButtonHold.Once);
        }
        else
        {
            // Round actions are long-press hold buttons: each acts once after a
            // second of covered caption, again only after the fingers lift.
            string[] ids = ["hit", "stand", "double", "split"];
            string[] labels = ["Hit", "Stand", "Double", "Split"];
            for (int i = 0; i < ids.Length; i++)
                Add("bj-" + ids[i], labels[i], new(.08 + i * .215, .775, .195, .095), BoardButtonHold.Once);
        }
        return result.AsReadOnly();

        void Add(string id, string label, BoardRect bounds, BoardButtonHold hold = BoardButtonHold.None) =>
            result.Add(new(id, label, bounds, BoardScreen.Blackjack,
                _blackjackPresentationUntil is null && game.AvailableActions.Contains(id), hold));
    }
}
