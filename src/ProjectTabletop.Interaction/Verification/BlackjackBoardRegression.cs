using ProjectTabletop.Interaction;

internal static class BlackjackBoardRegression
{
    public static void Run()
    {
        var game = new BlackjackGame(initialShoe: Cards(8, 6, 8, 10, 3, 2, 10, 10));
        var board = new BoardSession(game);
        Select("blackjack", 1, 100);
        Require(board.Screen == BoardScreen.Blackjack && board.BlackjackState.Phase == BlackjackPhase.Betting,
            "The Blackjack menu item did not open the betting table.");
        long navigationRevision = board.Revision;
        Select("bj-deal", 1, 140, 100);
        Require(board.BlackjackState.Phase == BlackjackPhase.Betting, "A held menu pinch dealt a round.");
        Select("bj-deal", 2, 200);
        Require(board.BlackjackState.Phase == BlackjackPhase.PlayerTurn && board.BlackjackState.Bankroll == 975 &&
            board.BlackjackState.DealerCards[1] is null, "Deal did not debit once and hide the dealer hole card.");
        Require(board.Revision == navigationRevision, "A game action changed navigation revision.");
        Select("bj-hit", 2, 240, 200);
        Require(board.BlackjackState.Hands[0].Cards.Count == 2, "A held deal pinch also hit.");
        Select("bj-split", 3, 300);
        Require(board.BlackjackState.Hands.Count == 2 && board.BlackjackState.Bankroll == 950,
            "The split button did not create and stake two hands.");
        var disabledSplit = Button("bj-split");
        Require(!disabledSplit.Enabled, "A second split was offered.");
        Select("bj-split", 4, 340);
        Require(board.HoveredButtonIds.Count == 0, "A disabled action highlighted.");
        Select("bj-hit", 4, 380, 340);
        Require(board.BlackjackState.Hands[0].Cards.Count == 2, "A disabled action's pinch replayed on Hit.");
        Select("bj-hit", 5, 420);
        Require(board.BlackjackState.Hands[0].Cards.Count == 3, "A fresh Hit after disabled action failed.");
        var saved = board.BlackjackState;
        Select("menu", 6, 480);
        Select("blackjack", 7, 540);
        Require(board.BlackjackState.Revision == saved.Revision && board.BlackjackState.Bankroll == saved.Bankroll,
            "Returning from the menu reset an active round or refunded its stake.");

        // All rendered controls use these exact, nonoverlapping UV rectangles.
        foreach (var buttons in new[] { board.Buttons, new BoardSession(new BlackjackGame()).BettingButtons() })
        foreach (var button in buttons)
        {
            var r = button.Bounds;
            Require(r.X >= .05 && r.Y >= .05 && r.X + r.Width <= .95 && r.Y + r.Height <= .95,
                "A Blackjack target reaches the edge of the board.");
            Require(buttons.Count(other => other.Bounds.Contains(r.X + r.Width / 2, r.Y + r.Height / 2)) == 1,
                "Blackjack hit targets overlap.");
        }

        // Pointer actions must invalidate old camera gestures too.
        board.ResetInput(Time(600));
        var stand = Button("bj-stand");
        Require(board.Update([Sample(stand, 8, 590)], Time(610), Time(610)) is null,
            "A pre-reset pinch changed a round.");
        Require(board.ActivateButton("bj-stand", Time(650)), "A valid laptop button failed.");
        Require(board.Update([Sample(stand, 9, 640)], Time(700), Time(700)) is null,
            "An in-flight pre-click pinch applied after a laptop action.");

        var settling = new BoardSession(new BlackjackGame(initialShoe: Cards(10, 10, 8, 7)));
        settling.ShowBlackjack(Time(0));
        settling.ActivateButton("bj-deal", Time(100));
        settling.ActivateButton("bj-stand", Time(200));
        settling.TickBlackjack(Time(850)); // Reveal.
        settling.TickBlackjack(Time(1500)); // Settlement enables a new Deal target.
        Require(settling.BlackjackState.Phase == BlackjackPhase.RoundOver, "Settlement fixture did not finish.");
        var nextDeal = settling.Buttons.Single(b => b.Id == "bj-deal");
        Require(settling.Update([Sample(nextDeal, 20, 1450)], Time(1450), Time(1550)) is null &&
            settling.BlackjackState.RoundNumber == 1, "A queued dealer-turn pinch dealt after settlement.");
        Require(settling.Update([Sample(nextDeal, 21, 1600) with { SelectionFrameTime = Time(1490) }],
            Time(1600), Time(1600)) is null, "A pre-settlement pointing anchor crossed into betting.");
        Require(settling.Update([Sample(nextDeal, 22, 1700)], Time(1700), Time(1700)) is not null &&
            settling.BlackjackState.RoundNumber == 2, "Settlement barriers blocked a fresh Deal.");
        Console.WriteLine("Blackjack board verification passed: launch, shared targets, held/disabled pinch consumption, " +
            "game/navigation revisions, split, menu continuity, reset and laptop/gesture input barriers.");

        BoardButton Button(string id) => board.Buttons.Single(b => b.Id == id);
        void Select(string id, long eventId, int milliseconds, int? executed = null)
        {
            var button = Button(id);
            board.Update([Sample(button, eventId, executed ?? milliseconds)], Time(milliseconds), Time(milliseconds));
        }
    }

    private static IReadOnlyList<BoardButton> BettingButtons(this BoardSession board)
    {
        board.ShowBlackjack(Time(0));
        return board.Buttons;
    }
    private static BlackjackCard[] Cards(params int[] ranks) => ranks.Select((rank, index) =>
        new BlackjackCard(rank, (BlackjackSuit)(index % 4))).ToArray();
    private static DateTimeOffset Time(int milliseconds) =>
        new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static BoardHandSample Sample(BoardButton button, long id, int at) => new(
        button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2,
        Time(at + 1000), id);
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
