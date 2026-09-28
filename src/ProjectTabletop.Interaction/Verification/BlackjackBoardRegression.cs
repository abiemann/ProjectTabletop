using ProjectTabletop.Interaction;

internal static class BlackjackBoardRegression
{
    public static void Run()
    {
        var game = new BlackjackGame(initialShoe: Cards(8, 6, 8, 10, 3, 2, 10, 10));
        var board = new BoardSession(game);
        var pinchHits = new List<BlackjackHit>();
        board.BlackjackHitOccurred += pinchHits.Add;
        Select("blackjack", 1, 100);
        Require(board.Screen == BoardScreen.Blackjack && board.BlackjackState.Phase == BlackjackPhase.Betting,
            "The Blackjack menu item did not open the betting table.");
        var chips = Button("bj-reset");
        Require(chips.Label == "Your Chips" && chips.Enabled && chips.Bounds == new BoardRect(.742, .055, .198, .09) &&
            board.Buttons.Count(button => button.Id == "bj-reset") == 1 &&
            Button("menu") is { Label: "Exit", Enabled: true } &&
            Button("menu").Bounds == new BoardRect(.06, .055, .18, .09),
            "The chips plaque and Exit do not use their rendered control labels and bounds.");
        long navigationRevision = board.Revision;
        Select("bj-deal", 1, 140, 100);
        Require(board.BlackjackState.Phase == BlackjackPhase.Betting, "A held menu pinch dealt a round.");
        Select("bj-deal", 2, 200);
        Require(board.BlackjackState.Phase == BlackjackPhase.PlayerTurn && board.BlackjackState.Bankroll == 975 &&
            board.BlackjackState.DealerCards[1] is null, "Deal did not debit once and hide the dealer hole card.");
        Require(Button("bj-reset") is { Enabled: false, Label: "Your Chips" } &&
            Button("bj-reset").Bounds == chips.Bounds && !board.ActivateButton("bj-reset", Time(210)) &&
            board.BlackjackState.Bankroll == 975,
            "The chips plaque disappeared or allowed an active-hand bankroll reset.");
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
        Require(pinchHits is [{ Sequence: 1, RoundNumber: 1, HandIndex: 0, CardIndex: 2, Card.Rank: 10 }] &&
            pinchHits[0].StartedAt == Time(420), "Pinch HIT did not emit exactly one payload for the previous active hand.");
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
            Require(buttons.All(other => other.Id == button.Id ||
                    r.X >= other.Bounds.X + other.Bounds.Width || other.Bounds.X >= r.X + r.Width ||
                    r.Y >= other.Bounds.Y + other.Bounds.Height || other.Bounds.Y >= r.Y + r.Height),
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
        var dealerChips = settling.Buttons.Single(button => button.Id == "bj-reset");
        Require(!dealerChips.Enabled && dealerChips.Bounds == chips.Bounds,
            "The chips plaque disappeared or enabled reset during the dealer turn.");
        settling.TickBlackjack(Time(850)); // Reveal.
        Require(settling.Update([Sample(dealerChips, 19, 900)], Time(900), Time(900)) is null &&
            settling.HoveredButtonIds.Count == 0,
            "The disabled dealer-turn chips plaque highlighted or consumed a bankroll reset.");
        settling.TickBlackjack(Time(1500)); // Settlement enables a new Deal target.
        Require(settling.BlackjackState.Phase == BlackjackPhase.RoundOver, "Settlement fixture did not finish.");
        var settledChips = settling.Buttons.Single(button => button.Id == "bj-reset");
        Require(settledChips.Enabled && settledChips.Bounds == chips.Bounds &&
            settling.Update([Sample(settledChips, 19, 900)], Time(1520), Time(1520)) is null &&
            settling.BlackjackState.Bankroll == 1025,
            "Settlement moved the chips plaque or replayed its disabled-period gesture.");
        var nextDeal = settling.Buttons.Single(b => b.Id == "bj-deal");
        Require(settling.Update([Sample(nextDeal, 20, 1450)], Time(1450), Time(1550)) is null &&
            settling.BlackjackState.RoundNumber == 1, "A queued dealer-turn pinch dealt after settlement.");
        Require(settling.Update([Sample(nextDeal, 21, 1600) with { SelectionFrameTime = Time(1490) }],
            Time(1600), Time(1600)) is null, "A pre-settlement pointing anchor crossed into betting.");
        Require(settling.Update([Sample(settledChips, 22, 1650)], Time(1650), Time(1650))?.ButtonId == "bj-reset" &&
            settling.BlackjackState is { Bankroll: 1000, SelectedBet: 25, Phase: BlackjackPhase.Betting } &&
            settling.BlackjackState.Hands.Count == 0 && settling.BlackjackState.DealerCards.Count == 0,
            "A fresh Your Chips selection did not restore a clean 1,000-credit betting table.");
        Require(settling.Update([Sample(nextDeal, 23, 1700)], Time(1700), Time(1700)) is not null &&
            settling.BlackjackState.RoundNumber == 2, "Settlement barriers blocked a fresh Deal.");
        CheckHitEvents();
        BlackjackDealRegression.Run();
        Console.WriteLine("Blackjack board verification passed: launch, shared targets, held/disabled pinch consumption, " +
            "game/navigation revisions, split, menu continuity, phase-gated Your Chips plaque and reset input barriers; " +
            "HIT event payloads, advancing split hands, unique sequences and non-HIT suppression.");

        BoardButton Button(string id) => board.Buttons.Single(b => b.Id == id);
        void Select(string id, long eventId, int milliseconds, int? executed = null)
        {
            var button = Button(id);
            board.Update([Sample(button, eventId, executed ?? milliseconds)], Time(milliseconds), Time(milliseconds));
        }
    }

    private static void CheckHitEvents()
    {
        var board = new BoardSession(new BlackjackGame(initialShoe: Cards(2, 6, 2, 10, 2, 3, 4, 10, 3, 6, 3, 10, 2)));
        var hits = new List<BlackjackHit>();
        board.BlackjackHitOccurred += hit =>
        {
            Require(board.BlackjackState.Hands[hit.HandIndex].Cards[hit.CardIndex] == hit.Card &&
                board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
                "HIT notification preceded committed card state or cleared input feedback.");
            hits.Add(hit);
        };
        board.ShowBlackjack(Time(0));
        Require(!board.ActivateButton("bj-hit", Time(10)) && board.ActivateButton("bj-bet-10", Time(20)) &&
            board.ActivateButton("bj-deal", Time(100)) && hits.Count == 0, "Rejected HIT, bet or Deal emitted a HIT notification.");
        var hitButton = board.Buttons.Single(button => button.Id == "bj-hit");
        var aim = new BoardAim(hitButton.Bounds.X + hitButton.Bounds.Width / 2, hitButton.Bounds.Y + hitButton.Bounds.Height / 2);
        var grouped = new BoardHandSample(double.NaN, double.NaN, DateTimeOffset.MinValue, 0)
            { TrackingId = 71, FourFingersExtended = true, FingerAim = aim, FingersTogether = true };
        board.Update([grouped], Time(120), Time(120));
        board.Update([grouped], Time(220), Time(220));
        var separated = grouped with { FingersTogether = false, IndexFingerSeparated = true };
        board.Update([separated], Time(250), Time(250));
        Require(board.Update([separated], Time(340), Time(340))?.ButtonId == "bj-hit" &&
            hits is [{ Sequence: 1, RoundNumber: 1, HandIndex: 0, CardIndex: 2, Card.Rank: 2 }] &&
            hits[0].StartedAt == Time(340), "Finger selection did not emit the correct committed HIT card.");
        Require(board.ActivateButton("bj-hit", Time(341)) && board.ActivateButton("bj-hit", Time(342)) &&
            hits.Select(hit => hit.Sequence).SequenceEqual([1L, 2L, 3L]) &&
            hits.Select(hit => hit.CardIndex).SequenceEqual([2, 3, 4]) &&
            hits.Select(hit => hit.Card.Rank).SequenceEqual([2, 3, 4]), "Quick laptop HITs reused sequence numbers or identified stale cards.");
        board.ResetInput(Time(350));
        board.ShowMenu(Time(360)); board.ShowBlackjack(Time(370));
        Require(board.ActivateButton("bj-stand", Time(400)) && !board.ActivateButton("bj-hit", Time(410)), "Stand/disabled-HIT fixture failed.");
        Require(board.TickBlackjack(Time(1100)) && board.TickBlackjack(Time(1800)) && board.TickBlackjack(Time(2500)) &&
            board.BlackjackState.Phase == BlackjackPhase.RoundOver && hits.Count == 3,
            "Stand, input reset, navigation or dealer reveal/draw/settlement emitted a HIT notification.");
        Require(board.ActivateButton("bj-reset", Time(2600)) && board.ActivateButton("bj-deal", Time(2700)) &&
            board.ActivateButton("bj-hit", Time(2800)) && hits.Count == 4 &&
            hits[3] is { Sequence: 4, RoundNumber: 2, HandIndex: 0, CardIndex: 2, Card.Rank: 2 },
            "Game reset reused a HIT sequence or the new round emitted the wrong card.");
        Require(!board.ActivateButton("bj-hit", Time(2799)) && !board.ActivateButton("not-an-action", Time(2900)) && hits.Count == 4,
            "Rejected old-time or unknown action emitted a HIT notification.");

        var split = new BoardSession(new BlackjackGame(initialShoe: Cards(8, 6, 8, 10, 9, 2, 7, 2, 10)));
        var splitHits = new List<BlackjackHit>();
        split.BlackjackHitOccurred += splitHits.Add;
        split.ShowBlackjack(Time(0));
        Require(split.ActivateButton("bj-deal", Time(100)) && split.ActivateButton("bj-split", Time(110)) && splitHits.Count == 0 &&
            split.ActivateButton("bj-hit", Time(120)) && split.BlackjackState.Hands[0].IsBust && split.BlackjackState.ActiveHandIndex == 1 &&
            splitHits is [{ HandIndex: 0, CardIndex: 2, Card.Rank: 7 }], "A split-hand bust reported the new active hand instead of the struck hand.");
        Require(split.ActivateButton("bj-double", Time(130)) && split.TickBlackjack(Time(800)) &&
            split.TickBlackjack(Time(1500)) && split.TickBlackjack(Time(2200)) && splitHits.Count == 1,
            "Double or dealer cards emitted player HIT notifications.");
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
