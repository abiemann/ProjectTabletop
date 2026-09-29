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
        // Deal is a one-time long-press: pinches, held or fresh, never deal.
        Select("bj-deal", 1, 140, 100);
        Select("bj-deal", 2, 200);
        Require(board.BlackjackState.Phase == BlackjackPhase.Betting && Button("bj-deal").Hold == BoardButtonHold.Once,
            "A pinch dealt with the long-press Deal button.");
        Require(Hold("bj-deal", 300) && board.BlackjackState.Phase == BlackjackPhase.PlayerTurn &&
            board.BlackjackState.Bankroll == 975 && board.BlackjackState.DealerCards[1] is null,
            "Deal did not debit once and hide the dealer hole card.");
        // Split now covers most of Deal's place: resting fingers must lift before it can act.
        Require(!Hold("bj-split", 1400) && board.BlackjackState.Hands.Count == 1,
            "Fingers still resting on Deal's place split the new hand.");
        Lift(2500);
        Require(Button("bj-reset") is { Enabled: false, Label: "Your Chips" } &&
            Button("bj-reset").Bounds == chips.Bounds && !board.ActivateButton("bj-reset", Time(2900)) &&
            board.BlackjackState.Bankroll == 975,
            "The chips plaque disappeared or allowed an active-hand bankroll reset.");
        Require(board.Revision == navigationRevision, "A game action changed navigation revision.");
        // Round actions are long-press hold buttons: pinches, held or fresh, never act on them.
        Require(Button("bj-hit").Hold == BoardButtonHold.Once && Button("bj-stand").Hold == BoardButtonHold.Once &&
            Button("bj-double").Hold == BoardButtonHold.Once && Button("bj-split").Hold == BoardButtonHold.Once &&
            !Button("menu").IsHold && !Button("bj-reset").IsHold,
            "Hit, Stand, Double and Split should be long-press buttons; Exit and Your Chips gestures.");
        Select("bj-hit", 2, 3000, 2950);
        Select("bj-hit", 3, 3020);
        Require(board.BlackjackState.Hands[0].Cards.Count == 2 && board.HoveredButtonIds.Count == 0,
            "A pinch hit or hovered the long-press Hit button.");
        Require(Hold("bj-split", 3100) && board.BlackjackState.Hands.Count == 2 && board.BlackjackState.Bankroll == 950,
            "The split button did not create and stake two hands.");
        Require(!Button("bj-split").Enabled && !Hold("bj-split", 4200), "A second split was offered or held.");
        Require(board.BlackjackState.Hands[0].Cards.Count == 2, "A disabled action's hold hit.");
        Require(Hold("bj-hit", 5300) && board.BlackjackState.Hands[0].Cards.Count == 3,
            "A fresh long-press Hit after a disabled action failed.");
        Require(pinchHits is [{ Sequence: 1, RoundNumber: 1, HandIndex: 0, CardIndex: 2, Card.Rank: 10 }] &&
            pinchHits[0].StartedAt == Time(6300), "A long-press HIT did not emit exactly one payload for the previous active hand.");
        var saved = board.BlackjackState;
        Select("menu", 6, 6400);
        Select("blackjack", 7, 6460);
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
        board.ResetInput(Time(6500));
        var stand = Button("bj-stand");
        Require(board.Update([Sample(stand, 8, 6490)], Time(6510), Time(6510)) is null,
            "A pre-reset pinch changed a round.");
        Require(board.ActivateButton("bj-stand", Time(6550)), "A valid laptop button failed.");
        Require(board.Update([Sample(stand, 9, 6540)], Time(6600), Time(6600)) is null,
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
        Require(HoldOn(settling, "bj-deal", 1700) && settling.BlackjackState.RoundNumber == 2,
            "Settlement barriers blocked a fresh long-press Deal.");
        CheckHitEvents();
        CheckSimultaneousCaptions();
        BlackjackDealRegression.Run();
        Console.WriteLine("Blackjack board verification passed: launch, shared targets, held/disabled pinch consumption, " +
            "game/navigation revisions, split, menu continuity, phase-gated Your Chips plaque and reset input barriers; " +
            "HIT event payloads, advancing split hands, unique sequences and non-HIT suppression.");

        BoardButton Button(string id) => board.Buttons.Single(b => b.Id == id);
        bool Hold(string id, int start) => HoldOn(board, id, start);
        void Lift(int start)
        {
            for (int time = start; time <= start + 400; time += 100) board.ObserveHeldButtons([], Time(time), Time(time));
        }
        void Select(string id, long eventId, int milliseconds, int? executed = null)
        {
            var button = Button(id);
            board.Update([Sample(button, eventId, executed ?? milliseconds)], Time(milliseconds), Time(milliseconds));
        }
    }

    // Safety: any second covered caption, even an ordinary button's, stops a long press.
    private static void CheckSimultaneousCaptions()
    {
        var board = new BoardSession(new BlackjackGame(initialShoe: Cards(2, 6, 2, 10, 2, 3)));
        board.ShowBlackjack(Time(0));
        Require(board.ActivateButton("bj-deal", Time(100)), "The simultaneous-caption fixture could not deal.");
        for (int time = 200; time <= 2200; time += 100)
            Require(board.ObserveHeldButtons(["bj-hit", "menu"], Time(time), Time(time)).Count == 0 &&
                board.BlackjackState.Hands[0].Cards.Count == 2 && board.Screen == BoardScreen.Blackjack,
                "Hit acted while Exit's caption was also covered.");
        for (int time = 2300; time < 3300; time += 100)
            Require(board.ObserveHeldButtons(["bj-hit"], Time(time), Time(time)).Count == 0,
                "An ambiguous hold shortened the next long press.");
        Require(board.ObserveHeldButtons(["bj-hit"], Time(3300), Time(3300)).SequenceEqual(["bj-hit"]) &&
            board.BlackjackState.Hands[0].Cards.Count == 3, "Hit did not act after its own second alone.");
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
        Require(board.Update([separated], Time(250), Time(250)) is null && board.Update([separated], Time(340), Time(340)) is null &&
            hits.Count == 0 && board.FingerSelectionFeedback.Count == 0,
            "The finger gesture selected or armed the long-press Hit button.");
        IReadOnlyList<string> held = [];
        for (int time = 400; time <= 1400 && held.Count == 0; time += 100)
            held = board.ObserveHeldButtons(["bj-hit"], Time(time), Time(time));
        Require(held.SequenceEqual(["bj-hit"]) &&
            hits is [{ Sequence: 1, RoundNumber: 1, HandIndex: 0, CardIndex: 2, Card.Rank: 2 }] &&
            hits[0].StartedAt == Time(1400), "A long-press did not emit the correct committed HIT card.");
        Require(board.ActivateButton("bj-hit", Time(1441)) && board.ActivateButton("bj-hit", Time(1442)) &&
            hits.Select(hit => hit.Sequence).SequenceEqual([1L, 2L, 3L]) &&
            hits.Select(hit => hit.CardIndex).SequenceEqual([2, 3, 4]) &&
            hits.Select(hit => hit.Card.Rank).SequenceEqual([2, 3, 4]), "Quick laptop HITs reused sequence numbers or identified stale cards.");
        board.ResetInput(Time(1450));
        board.ShowMenu(Time(1460)); board.ShowBlackjack(Time(1470));
        Require(board.ActivateButton("bj-stand", Time(1500)) && !board.ActivateButton("bj-hit", Time(1510)), "Stand/disabled-HIT fixture failed.");
        Require(board.TickBlackjack(Time(2200)) && board.TickBlackjack(Time(2900)) && board.TickBlackjack(Time(3600)) &&
            board.BlackjackState.Phase == BlackjackPhase.RoundOver && hits.Count == 3,
            "Stand, input reset, navigation or dealer reveal/draw/settlement emitted a HIT notification.");
        Require(board.ActivateButton("bj-reset", Time(3700)) && board.ActivateButton("bj-deal", Time(3800)) &&
            board.ActivateButton("bj-hit", Time(3900)) && hits.Count == 4 &&
            hits[3] is { Sequence: 4, RoundNumber: 2, HandIndex: 0, CardIndex: 2, Card.Rank: 2 },
            "Game reset reused a HIT sequence or the new round emitted the wrong card.");
        Require(!board.ActivateButton("bj-hit", Time(3899)) && !board.ActivateButton("not-an-action", Time(4000)) && hits.Count == 4,
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

    // Up to one second of caption evidence over a hold button, stopping at its activation.
    private static bool HoldOn(BoardSession session, string id, int start)
    {
        IReadOnlyList<string> activated = [];
        for (int time = start; time <= start + 1000 && activated.Count == 0; time += 100)
            activated = session.ObserveHeldButtons([id], Time(time), Time(time));
        return activated.SequenceEqual([id]);
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
