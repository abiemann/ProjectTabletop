namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private readonly MonopolyGame _monopoly;
    private long _monopolySaveRequestId;
    public MonopolySnapshot MonopolyState => _monopoly.Snapshot;
    public bool MonopolySaveRequested => _monopoly.Snapshot.Phase == MonopolyPhase.Saving;
    public long MonopolySaveRequestId => _monopolySaveRequestId;

    public void ShowMonopoly(DateTimeOffset? now = null) => Show(BoardScreen.Monopoly, now ?? DateTimeOffset.UtcNow);

    public bool TickMonopoly(DateTimeOffset now)
    {
        if (Screen != BoardScreen.Monopoly || !_monopoly.Tick(now)) return false;
        MonopolyInputBarrier(now);
        return true;
    }

    public string ExportMonopolySave() => _monopoly.ExportSave();

    /// <summary>Stages a validated saved game for Resume, or restores immediately when requested.</summary>
    public bool LoadMonopolySave(string json, DateTimeOffset now, bool resume = false)
    {
        if (resume) _monopoly.LoadSave(json, now); else _monopoly.StageSave(json);
        if (Screen == BoardScreen.Monopoly) MonopolyInputBarrier(now);
        return true;
    }

    /// <summary>A disk write completion can only settle the matching pending request.</summary>
    public bool CompleteMonopolySave(long requestId, bool success, DateTimeOffset now, string? error = null)
    {
        if (requestId != _monopolySaveRequestId || !MonopolySaveRequested) return false;
        _monopoly.CompleteSave(success, now, error);
        if (Screen == BoardScreen.Monopoly)
        {
            MonopolyInputBarrier(now);
            if (success) Show(BoardScreen.Menu, now);
        }
        return true;
    }

    private bool SelectMonopolyButton(BoardButton button, DateTimeOffset now)
    {
        bool immediateExit = button.Id == "mp-exit" && !MonopolyState.IsActiveGame;
        if (!_monopoly.HandleAction(button.Id, now)) return false;
        if (button.Id == "mp-save-exit") _monopolySaveRequestId++;
        MonopolyInputBarrier(now);
        if (immediateExit || button.Id == "mp-exit-without-saving") Show(BoardScreen.Menu, now);
        return true;
    }

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
        if (game.Phase is not MonopolyPhase.ExitConfirmation and not MonopolyPhase.Saving)
            Add("mp-exit", "Exit", new(.825, .012, .15, .038));
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
            case MonopolyPhase.ExitConfirmation:
                Add("mp-save-exit", "Save & Exit", new(.31, .48, .38, .072));
                Add("mp-exit-without-saving", "Exit without saving", new(.31, .58, .38, .072));
                Add("mp-exit-cancel", "Cancel", new(.395, .69, .21, .055));
                break;
        }
        return result.AsReadOnly();
        void Add(string id, string label, BoardRect bounds) => result.Add(new(id, label, bounds,
            BoardScreen.Monopoly, game.AvailableActions.Contains(id)));
    }
}
