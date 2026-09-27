namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private readonly BlackjackGame _blackjack;

    public BoardSession(BlackjackGame? blackjack = null) => _blackjack = blackjack ?? new BlackjackGame();
    public BlackjackSnapshot BlackjackState => _blackjack.Snapshot;
    public bool TickBlackjack(DateTimeOffset now)
    {
        if (Screen != BoardScreen.Blackjack) return false;
        var phase = _blackjack.Snapshot.Phase;
        if (!_blackjack.Tick(now)) return false;
        if (_blackjack.Snapshot.Phase != phase)
        {
            // A camera pinch made while dealer controls were disabled must not
            // land on a newly enabled bet or Deal target after settlement.
            _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
            _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
            HoveredButtonIds = Array.Empty<string>();
        }
        return true;
    }

    // Mouse/touch and pinch selections share the same enabled targets and rules.
    public bool ActivateButton(string id, DateTimeOffset now)
    {
        var button = Buttons.FirstOrDefault(item => item.Id == id && item.Enabled);
        if (button is null || !SelectButton(button, now)) return false;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        return true;
    }

    private bool SelectButton(BoardButton button, DateTimeOffset now)
    {
        if (Screen == BoardScreen.Blackjack && button.Id != "menu")
        {
            if (!_blackjack.HandleAction(button.Id, now)) return false;
        }
        else
        {
            Screen = button.Destination;
            Revision++;
        }
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        return true;
    }

    private IReadOnlyList<BoardButton> BlackjackButtons()
    {
        var game = _blackjack.Snapshot;
        var result = new List<BoardButton>
        {
            new("menu", "Back to menu", new(.06, .055, .23, .08), BoardScreen.Menu)
        };
        if (game.Phase is BlackjackPhase.Betting or BlackjackPhase.RoundOver)
        {
            int[] bets = [10, 25, 50, 100];
            for (int i = 0; i < bets.Length; i++)
                Add($"bj-bet-{bets[i]}", bets[i].ToString(), new(.08 + i * .14, .775, .12, .095));
            Add("bj-deal", game.Phase == BlackjackPhase.Betting ? "Deal" : "Deal again", new(.68, .775, .24, .095));
            Add("bj-reset", "Reset chips", new(.74, .16, .20, .065));
        }
        else
        {
            string[] ids = ["hit", "stand", "double", "split"];
            string[] labels = ["Hit", "Stand", "Double", "Split"];
            for (int i = 0; i < ids.Length; i++)
                Add("bj-" + ids[i], labels[i], new(.08 + i * .215, .775, .195, .095));
        }
        return result.AsReadOnly();

        void Add(string id, string label, BoardRect bounds) => result.Add(new(id, label, bounds,
            BoardScreen.Blackjack, game.AvailableActions.Contains(id)));
    }
}
