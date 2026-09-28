namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public static readonly TimeSpan MonopolyDrawerOpeningDuration = TimeSpan.FromMilliseconds(300);
    private readonly MonopolyGame _monopoly;
    private long _monopolySaveRequestId;
    private long _lastRelayedMonopolyRollSequence;
    private DateTimeOffset? _monopolyPresentationUntil;
    private DateTimeOffset _monopolyPresentationObservedAt = DateTimeOffset.MinValue;
    private bool _monopolyInactiveDrawerOpen;
    private DateTimeOffset? _monopolyDrawerOpenedAt;
    private bool _monopolyDrawerOpeningReady;
    public MonopolySnapshot MonopolyState => _monopoly.Snapshot;
    public bool MonopolySaveRequested => _monopoly.Snapshot.Phase == MonopolyPhase.Saving;
    public long MonopolySaveRequestId => _monopolySaveRequestId;
    public bool MonopolyDrawerOpen => Screen == BoardScreen.Monopoly && (_monopolyInactiveDrawerOpen ||
        MonopolyState.Phase is MonopolyPhase.ExitConfirmation or MonopolyPhase.Saving);
    public DateTimeOffset? MonopolyDrawerOpenedAt => MonopolyDrawerOpen ? _monopolyDrawerOpenedAt : null;

    /// <summary>Raised after each human or AI roll and its shared input barrier are committed.</summary>
    public event Action<MonopolyRoll>? MonopolyRollOccurred;

    /// <summary>
    /// Pauses AI progress and game controls while a roll presentation runs. Exit and its
    /// save choices remain available. Additional holds can extend, but never shorten, the deadline.
    /// </summary>
    public void HoldMonopolyPresentationUntil(DateTimeOffset until)
    {
        if (Screen != BoardScreen.Monopoly || until <= _monopolyPresentationObservedAt) return;
        _monopolyPresentationUntil = _monopolyPresentationUntil is { } previous ? Later(previous, until) : until;
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(_monopolyPresentationObservedAt);
    }

    /// <summary>Observes the supplied time, releasing controls and rejecting input from the finished hold.</summary>
    public bool IsMonopolyPresentationActive(DateTimeOffset now)
    {
        AdvanceMonopolyPresentation(now);
        return Screen == BoardScreen.Monopoly && _monopolyPresentationUntil is not null;
    }

    /// <summary>Cancels a presentation during a projection reset and rejects input captured before that reset.</summary>
    public void CancelMonopolyPresentation(DateTimeOffset now)
    {
        ClearMonopolyPresentationHold();
        _monopolyPresentationObservedAt = Later(_monopolyPresentationObservedAt, now);
        if (Screen == BoardScreen.Monopoly) MonopolyInputBarrier(now);
    }

    public void ShowMonopoly(DateTimeOffset? now = null) => Show(BoardScreen.Monopoly, now ?? DateTimeOffset.UtcNow);

    public bool TickMonopoly(DateTimeOffset now)
    {
        AdvanceMonopolyPresentation(now);
        if (Screen != BoardScreen.Monopoly || _monopolyPresentationUntil is not null) return false;
        long previousRoll = _lastRelayedMonopolyRollSequence;
        if (!_monopoly.Tick(now)) return false;
        if (_lastRelayedMonopolyRollSequence == previousRoll) MonopolyInputBarrier(now);
        return true;
    }

    public string ExportMonopolySave() => _monopoly.ExportSave();

    /// <summary>Stages a validated saved game for Resume, or restores immediately when requested.</summary>
    public bool LoadMonopolySave(string json, DateTimeOffset now, bool resume = false)
    {
        if (resume) _monopoly.LoadSave(json, now); else _monopoly.StageSave(json);
        if (resume)
        {
            ClearMonopolyPresentationHold();
            ClearMonopolyDrawerUi();
        }
        if (Screen == BoardScreen.Monopoly) MonopolyInputBarrier(now);
        return true;
    }

    /// <summary>A disk write completion can only settle the matching pending request.</summary>
    public bool CompleteMonopolySave(long requestId, bool success, DateTimeOffset now, string? error = null)
    {
        if (requestId != _monopolySaveRequestId || !MonopolySaveRequested) return false;
        _monopoly.CompleteSave(success, now, error);
        if (success) ClearMonopolyDrawerUi();
        if (Screen == BoardScreen.Monopoly)
        {
            MonopolyInputBarrier(now);
            if (success) Show(BoardScreen.Menu, now);
        }
        return true;
    }

    private bool SelectMonopolyButton(BoardButton button, DateTimeOffset now)
    {
        if (now < _monopolyPresentationObservedAt) return false;
        if (_monopolyPresentationUntil is not null && !IsMonopolyNavigation(button.Id)) return false;
        if (button.Id is "mp-save-exit" or "mp-exit-game" && !_monopolyDrawerOpeningReady) return false;
        if (button.Id == "mp-exit" && !MonopolyState.IsActiveGame)
        {
            _monopolyInactiveDrawerOpen = true;
            BeginMonopolyDrawer(now);
            ClearMonopolyPresentationHold();
            MonopolyInputBarrier(now);
            return true;
        }
        if (button.Id == "mp-exit-cancel" && _monopolyInactiveDrawerOpen)
        {
            ClearMonopolyDrawerUi();
            ClearMonopolyPresentationHold();
            MonopolyInputBarrier(now);
            return true;
        }
        bool immediateExit = button.Id == "mp-exit-game" && !MonopolyState.IsActiveGame;
        if (button.Id == "mp-exit-game" && (!MonopolyDrawerOpen || !immediateExit)) return false;
        long previousRoll = _lastRelayedMonopolyRollSequence;
        if (!_monopoly.HandleAction(immediateExit ? "mp-exit" : button.Id, now)) return false;
        if (button.Id == "mp-exit") BeginMonopolyDrawer(now);
        if (IsMonopolyNavigation(button.Id)) ClearMonopolyPresentationHold();
        if (button.Id == "mp-exit-cancel" || immediateExit) ClearMonopolyDrawerUi();
        if (button.Id == "mp-save-exit") _monopolySaveRequestId++;
        if (_lastRelayedMonopolyRollSequence == previousRoll) MonopolyInputBarrier(now);
        if (immediateExit) Show(BoardScreen.Menu, now);
        return true;
    }

    private void RelayMonopolyRoll(MonopolyRoll roll)
    {
        _lastRelayedMonopolyRollSequence = roll.Sequence;
        if (Screen == BoardScreen.Monopoly)
        {
            AdvanceMonopolyPresentation(roll.StartedAt);
            MonopolyInputBarrier(roll.StartedAt);
        }
        MonopolyRollOccurred?.Invoke(roll);
    }

    private void AdvanceMonopolyPresentation(DateTimeOffset now)
    {
        _monopolyPresentationObservedAt = Later(_monopolyPresentationObservedAt, now);
        if (MonopolyDrawerOpen && _monopolyDrawerOpenedAt is { } opened && !_monopolyDrawerOpeningReady &&
            now >= opened + MonopolyDrawerOpeningDuration)
        {
            _monopolyDrawerOpeningReady = true;
            // The moving drawer is not a live Save/Exit target. Release it once,
            // rejecting frames and gesture origins captured during its entrance.
            MonopolyInputBarrier(opened + MonopolyDrawerOpeningDuration);
        }
        if (_monopolyPresentationUntil is not { } until || now < until) return;
        _monopolyPresentationUntil = null;
        // Old frames, pulse origins and pointing anchors from the animation cannot
        // select newly enabled choices. A finger gesture must begin with a fresh grouped pose.
        MonopolyInputBarrier(until);
    }

    private void ClearMonopolyPresentationHold() => _monopolyPresentationUntil = null;
    private void BeginMonopolyDrawer(DateTimeOffset now)
    {
        _monopolyDrawerOpenedAt = now;
        _monopolyDrawerOpeningReady = false;
    }

    private void ClearMonopolyDrawerUi()
    {
        _monopolyInactiveDrawerOpen = false;
        // An interrupted active confirmation remains open when this board returns;
        // hiding it must not discard the game or silently resume an AI turn.
        if (MonopolyState.Phase is MonopolyPhase.ExitConfirmation or MonopolyPhase.Saving) return;
        _monopolyDrawerOpenedAt = null;
        _monopolyDrawerOpeningReady = false;
    }
    private static bool IsMonopolyNavigation(string id) => id is
        "mp-exit" or "mp-exit-cancel" or "mp-save-exit" or "mp-exit-game";

    private void MonopolyInputBarrier(DateTimeOffset now)
    {
        Revision++;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
    }

    private IReadOnlyList<BoardButton> MonopolyButtons()
    {
        var game = MonopolyState;
        var result = new List<BoardButton>();
        if (MonopolyDrawerOpen)
        {
            bool enabled = game.Phase != MonopolyPhase.Saving;
            result.Add(new("mp-exit-cancel", "v", new(.34, .752, .32, .06), BoardScreen.Monopoly, enabled));
            result.Add(new(game.IsActiveGame ? "mp-save-exit" : "mp-exit-game",
                game.IsActiveGame ? "Save and Exit" : "Exit Game", new(.32, .632, .36, .08),
                BoardScreen.Monopoly, enabled && _monopolyDrawerOpeningReady));
            return result.AsReadOnly();
        }
        Add("mp-exit", "^", game.Phase == MonopolyPhase.Landing
            ? new(.34, .752, .32, .06) : new(.215, .215, .26, .06));
        switch (game.Phase)
        {
            case MonopolyPhase.Landing:
                Add("mp-start-game", "Start Game", new(.335, .475, .33, .078));
                if (game.CanResume) Add("mp-resume", "Resume saved game", new(.335, .573, .33, .064));
                break;
            case MonopolyPhase.Setup:
                Add("mp-human-minus", "-", new(.36, .42, .07, .058));
                Add("mp-human-plus", "+", new(.57, .42, .07, .058));
                Add("mp-ai-minus", "-", new(.36, .53, .07, .058));
                Add("mp-ai-plus", "+", new(.57, .53, .07, .058));
                Add("mp-start", "Start", new(.365, .665, .27, .072));
                Add("mp-setup-cancel", "Cancel", new(.395, .755, .21, .048));
                break;
            case MonopolyPhase.AwaitingRoll:
                Add("mp-roll", "Roll", new(.365, .62, .27, .072));
                if (game.ActivePlayer?.InJail == true)
                {
                    Add("mp-jail-pay", "Pay $50", new(.30, .715, .19, .055));
                    Add("mp-jail-card", "Use jail card", new(.51, .715, .19, .055));
                    Add("mp-manage", "Properties", new(.395, .777, .21, .040));
                }
                else Add("mp-manage", "Properties", new(.395, .715, .21, .055));
                break;
            case MonopolyPhase.AwaitingPurchase:
                Add("mp-buy", "Buy property", new(.365, .62, .27, .072));
                Add("mp-auction", "Auction", new(.395, .715, .21, .055));
                break;
            case MonopolyPhase.AwaitingEndTurn:
                Add("mp-end-turn", "End turn", new(.365, .62, .27, .072));
                Add("mp-manage", "Properties", new(.395, .715, .21, .055));
                break;
            case MonopolyPhase.Auction:
                Add("mp-bid-10", "+ $10", new(.255, .62, .15, .064));
                Add("mp-bid-50", "+ $50", new(.425, .62, .15, .064));
                Add("mp-bid-100", "+ $100", new(.595, .62, .15, .064));
                Add("mp-pass", "Pass", new(.395, .71, .21, .055));
                break;
            case MonopolyPhase.ManageProperties:
                Add("mp-property-previous", "<", new(.245, .375, .085, .058));
                Add("mp-property-next", ">", new(.67, .375, .085, .058));
                Add("mp-build", "Build", new(.28, .52, .21, .055));
                Add("mp-sell", "Sell building", new(.51, .52, .21, .055));
                Add("mp-mortgage", "Mortgage", new(.28, .60, .21, .055));
                Add("mp-unmortgage", "Lift mortgage", new(.51, .60, .21, .055));
                if (game.AvailableActions.Contains("mp-sell-group"))
                    Add("mp-sell-group", "Sell all buildings", new(.31, .675, .38, .045));
                Add("mp-manage-back", "Done", new(.395, .73, .21, .055));
                break;
            case MonopolyPhase.Debt:
                Add("mp-manage", "Raise funds", new(.30, .62, .19, .072));
                Add("mp-bankrupt", "Bankrupt", new(.51, .62, .19, .072));
                break;
            case MonopolyPhase.GameOver:
                Add("mp-new-game", "Play again", new(.365, .62, .27, .072));
                break;
        }
        return result.AsReadOnly();
        void Add(string id, string label, BoardRect bounds) => result.Add(new(id, label, bounds,
            BoardScreen.Monopoly, game.AvailableActions.Contains(id) &&
                (_monopolyPresentationUntil is null || IsMonopolyNavigation(id))));
    }
}
