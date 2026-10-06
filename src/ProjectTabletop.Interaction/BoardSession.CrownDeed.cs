namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public static readonly TimeSpan CrownDeedDrawerOpeningDuration = TimeSpan.FromMilliseconds(300);
    private readonly CrownDeedGame _crownDeed;
    private long _crownDeedSaveRequestId;
    private long _lastRelayedCrownDeedRollSequence;
    private long _lastRelayedCrownDeedDevelopmentSequence;
    private DateTimeOffset? _crownDeedPresentationUntil;
    private DateTimeOffset _crownDeedPresentationObservedAt = DateTimeOffset.MinValue;
    private bool _crownDeedInactiveDrawerOpen;
    private DateTimeOffset? _crownDeedDrawerOpenedAt;
    private bool _crownDeedDrawerOpeningReady;
    public CrownDeedSnapshot CrownDeedState => _crownDeed.Snapshot;
    public bool CrownDeedSaveRequested => _crownDeed.Snapshot.Phase == CrownDeedPhase.Saving;
    public long CrownDeedSaveRequestId => _crownDeedSaveRequestId;
    public bool CrownDeedDrawerOpen => Screen == BoardScreen.CrownDeed && (_crownDeedInactiveDrawerOpen ||
        CrownDeedState.Phase is CrownDeedPhase.ExitConfirmation or CrownDeedPhase.Saving);
    public DateTimeOffset? CrownDeedDrawerOpenedAt => CrownDeedDrawerOpen ? _crownDeedDrawerOpenedAt : null;

    /// <summary>Raised after each human or AI roll and its shared input barrier are committed.</summary>
    public event Action<CrownDeedRoll>? CrownDeedRollOccurred;

    /// <summary>Committed human/AI building changes, after the shared input barrier.</summary>
    public event Action<CrownDeedDevelopment>? CrownDeedDevelopmentOccurred;

    /// <summary>
    /// Pauses AI progress and game controls while a roll presentation runs. Exit and its
    /// save choices remain available. Additional holds can extend, but never shorten, the deadline.
    /// </summary>
    public void HoldCrownDeedPresentationUntil(DateTimeOffset until)
    {
        if (Screen != BoardScreen.CrownDeed || until <= _crownDeedPresentationObservedAt) return;
        _crownDeedPresentationUntil = _crownDeedPresentationUntil is { } previous ? Later(previous, until) : until;
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(_crownDeedPresentationObservedAt);
    }

    /// <summary>Observes the supplied time, releasing controls and rejecting input from the finished hold.</summary>
    public bool IsCrownDeedPresentationActive(DateTimeOffset now)
    {
        AdvanceCrownDeedPresentation(now);
        return Screen == BoardScreen.CrownDeed && _crownDeedPresentationUntil is not null;
    }

    /// <summary>Cancels a presentation during a projection reset and rejects input captured before that reset.</summary>
    public void CancelCrownDeedPresentation(DateTimeOffset now)
    {
        ClearCrownDeedPresentationHold();
        _crownDeedPresentationObservedAt = Later(_crownDeedPresentationObservedAt, now);
        if (Screen == BoardScreen.CrownDeed) CrownDeedInputBarrier(now);
    }

    public void ShowCrownDeed(DateTimeOffset? now = null) => Show(BoardScreen.CrownDeed, now ?? MonotonicClock.UtcNow);

    public bool TickCrownDeed(DateTimeOffset now)
    {
        AdvanceCrownDeedPresentation(now);
        if (Screen != BoardScreen.CrownDeed || _crownDeedPresentationUntil is not null) return false;
        long previousRoll = _lastRelayedCrownDeedRollSequence;
        long previousDevelopment = _lastRelayedCrownDeedDevelopmentSequence;
        if (!_crownDeed.Tick(now)) return false;
        if (_lastRelayedCrownDeedRollSequence == previousRoll &&
            _lastRelayedCrownDeedDevelopmentSequence == previousDevelopment) CrownDeedInputBarrier(now);
        return true;
    }

    public string ExportCrownDeedSave() => _crownDeed.ExportSave();

    /// <summary>Stages a validated saved game for Resume, or restores immediately when requested.</summary>
    public bool LoadCrownDeedSave(string json, DateTimeOffset now, bool resume = false)
    {
        if (resume) _crownDeed.LoadSave(json, now); else _crownDeed.StageSave(json);
        if (resume)
        {
            ClearCrownDeedPresentationHold();
            ClearCrownDeedDrawerUi();
        }
        if (Screen == BoardScreen.CrownDeed) CrownDeedInputBarrier(now);
        return true;
    }

    /// <summary>A disk write completion can only settle the matching pending request.</summary>
    public bool CompleteCrownDeedSave(long requestId, bool success, DateTimeOffset now, string? error = null)
    {
        if (requestId != _crownDeedSaveRequestId || !CrownDeedSaveRequested) return false;
        _crownDeed.CompleteSave(success, now, error);
        if (success) ClearCrownDeedDrawerUi();
        if (Screen == BoardScreen.CrownDeed)
        {
            CrownDeedInputBarrier(now);
            if (success) Show(BoardScreen.Menu, now);
        }
        return true;
    }

    private bool SelectCrownDeedButton(BoardButton button, DateTimeOffset now)
    {
        if (now < _crownDeedPresentationObservedAt) return false;
        if (_crownDeedPresentationUntil is not null && !IsCrownDeedNavigation(button.Id)) return false;
        if (button.Id is "mp-save-exit" or "mp-exit-game" && !_crownDeedDrawerOpeningReady) return false;
        if (button.Id == "mp-exit" && !CrownDeedState.IsActiveGame)
        {
            _crownDeedInactiveDrawerOpen = true;
            BeginCrownDeedDrawer(now);
            ClearCrownDeedPresentationHold();
            CrownDeedInputBarrier(now);
            return true;
        }
        if (button.Id == "mp-exit-cancel" && _crownDeedInactiveDrawerOpen)
        {
            ClearCrownDeedDrawerUi();
            ClearCrownDeedPresentationHold();
            CrownDeedInputBarrier(now);
            return true;
        }
        bool immediateExit = button.Id == "mp-exit-game" && !CrownDeedState.IsActiveGame;
        if (button.Id == "mp-exit-game" && (!CrownDeedDrawerOpen || !immediateExit)) return false;
        long previousRoll = _lastRelayedCrownDeedRollSequence;
        long previousDevelopment = _lastRelayedCrownDeedDevelopmentSequence;
        if (!_crownDeed.HandleAction(immediateExit ? "mp-exit" : button.Id, now)) return false;
        if (button.Id == "mp-exit") BeginCrownDeedDrawer(now);
        if (IsCrownDeedNavigation(button.Id)) ClearCrownDeedPresentationHold();
        if (button.Id == "mp-exit-cancel" || immediateExit) ClearCrownDeedDrawerUi();
        if (button.Id == "mp-save-exit") _crownDeedSaveRequestId++;
        if (_lastRelayedCrownDeedRollSequence == previousRoll &&
            _lastRelayedCrownDeedDevelopmentSequence == previousDevelopment) CrownDeedInputBarrier(now);
        if (immediateExit) Show(BoardScreen.Menu, now);
        return true;
    }

    private void RelayCrownDeedRoll(CrownDeedRoll roll)
    {
        _lastRelayedCrownDeedRollSequence = roll.Sequence;
        if (Screen == BoardScreen.CrownDeed)
        {
            AdvanceCrownDeedPresentation(roll.StartedAt);
            CrownDeedInputBarrier(roll.StartedAt);
        }
        CrownDeedRollOccurred?.Invoke(roll);
    }

    private void RelayCrownDeedDevelopment(CrownDeedDevelopment development)
    {
        if (development.Sequence <= _lastRelayedCrownDeedDevelopmentSequence) return;
        _lastRelayedCrownDeedDevelopmentSequence = development.Sequence;
        if (Screen == BoardScreen.CrownDeed)
        {
            AdvanceCrownDeedPresentation(development.StartedAt);
            CrownDeedInputBarrier(development.StartedAt);
        }
        CrownDeedDevelopmentOccurred?.Invoke(development);
    }

    private void AdvanceCrownDeedPresentation(DateTimeOffset now)
    {
        _crownDeedPresentationObservedAt = Later(_crownDeedPresentationObservedAt, now);
        if (CrownDeedDrawerOpen && _crownDeedDrawerOpenedAt is { } opened && !_crownDeedDrawerOpeningReady &&
            now >= opened + CrownDeedDrawerOpeningDuration)
        {
            _crownDeedDrawerOpeningReady = true;
            // The moving drawer is not a live Save/Exit target. Release it once,
            // rejecting frames and gesture origins captured during its entrance.
            CrownDeedInputBarrier(opened + CrownDeedDrawerOpeningDuration);
        }
        if (_crownDeedPresentationUntil is not { } until || now < until) return;
        _crownDeedPresentationUntil = null;
        // Old frames, pulse origins and pointing anchors from the animation cannot
        // select newly enabled choices. A finger gesture must begin with a fresh grouped pose.
        CrownDeedInputBarrier(until);
    }

    private void ClearCrownDeedPresentationHold() => _crownDeedPresentationUntil = null;
    private void BeginCrownDeedDrawer(DateTimeOffset now)
    {
        _crownDeedDrawerOpenedAt = now;
        _crownDeedDrawerOpeningReady = false;
    }

    private void ClearCrownDeedDrawerUi()
    {
        _crownDeedInactiveDrawerOpen = false;
        // An interrupted active confirmation remains open when this board returns;
        // hiding it must not discard the game or silently resume an AI turn.
        if (CrownDeedState.Phase is CrownDeedPhase.ExitConfirmation or CrownDeedPhase.Saving) return;
        _crownDeedDrawerOpenedAt = null;
        _crownDeedDrawerOpeningReady = false;
    }
    private static bool IsCrownDeedNavigation(string id) => id is
        "mp-exit" or "mp-exit-cancel" or "mp-save-exit" or "mp-exit-game";

    private void CrownDeedInputBarrier(DateTimeOffset now)
    {
        Revision++;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
    }

    private IReadOnlyList<BoardButton> CrownDeedButtons()
    {
        var game = CrownDeedState;
        var result = new List<BoardButton>();
        if (CrownDeedDrawerOpen)
        {
            bool enabled = game.Phase != CrownDeedPhase.Saving;
            result.Add(new("mp-exit-cancel", "v", new(.456, .752, .088, .06), BoardScreen.CrownDeed, enabled));
            result.Add(new(game.IsActiveGame ? "mp-save-exit" : "mp-exit-game",
                game.IsActiveGame ? "Save and Exit" : "Exit Game", new(.32, .632, .36, .08),
                BoardScreen.CrownDeed, enabled && _crownDeedDrawerOpeningReady));
            return result.AsReadOnly();
        }
        Add("mp-exit", "^", game.Phase == CrownDeedPhase.Landing
            ? new(.456, .752, .088, .06) : new(.456, .160, .088, .06));
        switch (game.Phase)
        {
            case CrownDeedPhase.Landing:
                Add("mp-start-game", "Start Game", new(.335, .475, .33, .078));
                if (game.CanResume) Add("mp-resume", "Resume saved game", new(.335, .573, .33, .064));
                break;
            case CrownDeedPhase.Setup:
                Add("mp-human-minus", "-", new(.36, .391, .07, .056));
                Add("mp-human-plus", "+", new(.57, .391, .07, .056));
                Add("mp-ai-minus", "-", new(.36, .488, .07, .056));
                Add("mp-ai-plus", "+", new(.57, .488, .07, .056));
                for (int slot = 0; slot < game.SetupPieces.Count; slot++)
                {
                    string player = slot < game.HumanPlayers ? $"P{slot + 1}" : $"A{slot - game.HumanPlayers + 1}";
                    Add($"mp-piece-next-{slot + 1}", $"{player} · {CrownDeedGame.PieceNames[game.SetupPieces[slot]]}",
                        new(.253 + slot % 3 * .170, .624 + slot / 3 * .052, .154, .044));
                }
                Add("mp-start", "Start", new(.365, .734, .27, .064));
                Add("mp-setup-cancel", "Cancel", new(.395, .810, .21, .032));
                break;
            case CrownDeedPhase.AwaitingRoll:
                Add("mp-roll", "Roll", new(.365, .62, .27, .072));
                if (game.ActivePlayer?.InJail == true)
                {
                    Add("mp-jail-pay", "Pay 50", new(.30, .695, .19, .055));
                    Add("mp-jail-card", "Use pass", new(.51, .695, .19, .055));
                    Add("mp-manage", "Properties", new(.395, .758, .21, .040));
                }
                else Add("mp-manage", "Properties", new(.395, .695, .21, .055));
                break;
            case CrownDeedPhase.AwaitingPurchase:
                Add("mp-buy", "Buy property", new(.365, .62, .27, .072));
                Add("mp-auction", "Auction", new(.395, .695, .21, .055));
                break;
            case CrownDeedPhase.AwaitingEndTurn:
                Add("mp-end-turn", "End turn", new(.365, .62, .27, .072));
                Add("mp-manage", "Properties", new(.395, .695, .21, .055));
                break;
            case CrownDeedPhase.Auction:
                Add("mp-bid-10", "+ 10", new(.255, .62, .15, .064));
                Add("mp-bid-50", "+ 50", new(.425, .62, .15, .064));
                Add("mp-bid-100", "+ 100", new(.595, .62, .15, .064));
                Add("mp-pass", "Pass", new(.395, .71, .21, .055));
                break;
            case CrownDeedPhase.ManageProperties:
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
            case CrownDeedPhase.Debt:
                Add("mp-manage", "Raise funds", new(.30, .62, .19, .072));
                Add("mp-bankrupt", "Bankrupt", new(.51, .62, .19, .072));
                break;
            case CrownDeedPhase.GameOver:
                Add("mp-new-game", "Play again", new(.365, .62, .27, .072));
                break;
        }
        return result.AsReadOnly();
        void Add(string id, string label, BoardRect bounds) => result.Add(new(id, label, bounds,
            BoardScreen.CrownDeed, game.AvailableActions.Contains(id) &&
                (_crownDeedPresentationUntil is null || IsCrownDeedNavigation(id))));
    }
}
