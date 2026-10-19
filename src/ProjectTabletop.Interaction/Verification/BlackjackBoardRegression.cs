using ProjectTabletop.Interaction;

internal static class BlackjackBoardRegression
{
    public static void Run()
    {
        var game = new BlackjackGame(initialShoe: Cards(8, 6, 8, 10, 3, 2, 10, 10));
        var board = new BoardSession(game);
        var pinchHits = new List<BlackjackHit>();
        board.BlackjackHitOccurred += pinchHits.Add;
        Require(board.ActivateButton("menu-scroll-down", Time(-600)) && board.TickMenu(Time(50)),
            "The Blackjack fixture could not settle the second menu page.");
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
            Button("menu").Hold == BoardButtonHold.Once && !Button("bj-reset").IsHold,
            "Hit, Stand, Double, Split and Exit should be long-press buttons; Your Chips remains a gesture.");
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
        Require(board.Screen == BoardScreen.Blackjack && board.HoveredButtonIds.Count == 0,
            "A pinch selected or highlighted the long-press Exit button.");
        for (int time = 6500; time < 7500; time += 100)
            Require(board.ObserveHeldButtons(["menu"], Time(time), Time(time)).Count == 0 &&
                board.Screen == BoardScreen.Blackjack, "Exit acted before its full one-second caption hold.");
        Require(board.ObserveHeldButtons(["menu"], Time(7500), Time(7500)).SequenceEqual(["menu"]),
            "Holding Exit's caption for one second did not return to the menu.");
        // Re-enter through the launcher card itself. Exiting a page-two board keeps
        // that launcher page, so the Blackjack card is directly selectable.
        Require(board.Screen == BoardScreen.Menu && board.MenuScrolled && !board.MenuScrolling,
            "Exiting Blackjack did not return to its settled second launcher page.");
        Select("blackjack", 7, 7600);
        Require(board.Screen == BoardScreen.Blackjack &&
            board.BlackjackState.Revision == saved.Revision && board.BlackjackState.Bankroll == saved.Bankroll,
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
        board.ResetInput(Time(8000));
        var stand = Button("bj-stand");
        Require(board.Update([Sample(stand, 8, 7990)], Time(8010), Time(8010)) is null,
            "A pre-reset pinch changed a round.");
        Require(board.ActivateButton("bj-stand", Time(8050)), "A valid laptop button failed.");
        Require(board.Update([Sample(stand, 9, 8040)], Time(8060), Time(8060)) is null,
            "An in-flight pre-click pinch applied after a laptop action.");
        Require(board.ActivateButton("menu", Time(8070)) && board.Screen == BoardScreen.Menu,
            "The laptop Exit click stopped returning to the menu immediately.");

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
        CheckBetHolds();
        CheckHitEvents();
        CheckSimultaneousCaptions();
        HoldReachRegression.Run();
        BlackjackDealRegression.Run();
        Console.WriteLine("Blackjack board verification passed: launch, shared targets, held/disabled pinch consumption, " +
            "game/navigation revisions, split, menu continuity, phase-gated Your Chips plaque and reset input barriers; " +
            "one-second wager captions, release/rearm, pointer selection, HIT event payloads, advancing split hands, " +
            "unique sequences and non-HIT suppression.");

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

    private static void CheckBetHolds()
    {
        foreach (int amount in new[] { 10, 25, 50, 100 })
        {
            var board = new BoardSession(new BlackjackGame(initialShoe: Cards(10, 10, 8, 7)));
            board.ShowBlackjack(Time(0));
            string id = $"bj-bet-{amount}";
            var coin = board.Buttons.Single(button => button.Id == id);
            long revision = board.BlackjackState.Revision;
            Require(coin is { Hold: BoardButtonHold.Once, Enabled: true } &&
                board.Update([Sample(coin, 1, 20)], Time(20), Time(20)) is null &&
                board.BlackjackState.Revision == revision && board.HoveredButtonIds.Count == 0,
                "A betting coin was not a single-action hold, or a pinch selected/highlighted it.");

            // Production requires a clear caption before accepting broken numeral shapes.
            for (int time = 100; time <= 1200; time += 100)
                Require(Observe([id], time).Count == 0, "An already covered betting coin armed without a clear reference.");
            Observe([], 1300, [id]);
            for (int time = 1400; time <= 2000; time += 100)
                Require(Observe([id], time).Count == 0, "A betting coin acted before one second.");
            Observe([], 2050, [id]);
            for (int time = 2100; time < 3100; time += 100)
                Require(Observe([id], time).Count == 0 && board.BlackjackState.Revision == revision,
                    "An intact betting caption failed to cancel a partial hold.");
            Require(Observe([id], 3100).SequenceEqual([id]) &&
                board.BlackjackState.SelectedBet == amount && board.BlackjackState.Revision == revision + 1 &&
                board.BlackjackState is { Phase: BlackjackPhase.Betting, Bankroll: 1000 },
                "A one-second betting hold did not select exactly one wager without dealing or debiting chips.");
            for (int time = 3200; time <= 4300; time += 100)
                Require(Observe([id], time).Count == 0 && board.BlackjackState.Revision == revision + 1,
                    "A betting coin repeated before the hand lifted.");
            for (int time = 4400; time <= 4800; time += 100) Observe([], time, [id]);
            for (int time = 4900; time < 5900; time += 100)
                Require(Observe([id], time).Count == 0, "A rearmed betting coin skipped its full hold interval.");
            Require(Observe([id], 5900).SequenceEqual([id]) && board.BlackjackState.Revision == revision + 2,
                "Lifting and holding the same betting coin did not rearm it.");
            Require(board.ActivateButton(id, Time(6000)) && board.ActivateButton(id, Time(6001)) &&
                board.BlackjackState.Revision == revision + 4 && board.BlackjackState.SelectedBet == amount,
                "Mouse/control betting clicks stopped being immediate and repeatable.");

            Require(board.ActivateButton("bj-deal", Time(6100)) && !board.ActivateButton(id, Time(6200)) &&
                !HoldOn(board, id, 6300), "An unavailable betting coin changed the wager during a round.");
            Require(board.ActivateButton("bj-stand", Time(7400)) && board.TickBlackjack(Time(8100)) &&
                board.TickBlackjack(Time(8800)) && board.BlackjackState.Phase == BlackjackPhase.RoundOver,
                "The betting-coin settlement fixture did not finish.");
            for (int time = 8900; time <= 10000; time += 100)
                Require(Observe([id], time).Count == 0,
                    "An unavailable-phase hold selected a returning coin before its fresh clear reference.");
            Observe([], 10100, [id]);
            for (int time = 10200; time < 11200; time += 100)
                Require(Observe([id], time).Count == 0, "A returning betting coin inherited old hold progress.");
            Require(Observe([id], 11200).SequenceEqual([id]), "A fresh hold could not select a wager after settlement.");

            IReadOnlyList<string> Observe(string[] held, int time, string[]? clear = null) =>
                board.ObserveHeldButtons(held, Time(time), Time(time), clear ?? []);
        }
    }

    // Same-row captions remain ambiguous; reaching to a separate row is checked below.
    private static void CheckSimultaneousCaptions()
    {
        foreach (var (held, other) in new[] { ("bj-bet-50", "bj-bet-100"), ("bj-hit", "bj-stand") })
        {
            var board = new BoardSession(new BlackjackGame(initialShoe: Cards(2, 6, 2, 10, 2, 3)));
            board.ShowBlackjack(Time(0));
            if (held == "bj-hit")
                Require(board.ActivateButton("bj-deal", Time(100)), "The simultaneous-caption fixture could not deal.");
            long revision = board.BlackjackState.Revision;
            for (int time = 200; time <= 2200; time += 100)
                Require(board.ObserveHeldButtons([held, other], Time(time), Time(time)).Count == 0 &&
                    board.BlackjackState.Revision == revision && board.Screen == BoardScreen.Blackjack,
                    "A Blackjack long press acted while another caption was also covered.");
            for (int time = 2300; time < 3300; time += 100)
                Require(board.ObserveHeldButtons([held], Time(time), Time(time)).Count == 0,
                    "An ambiguous hold shortened the next long press.");
            Require(board.ObserveHeldButtons([held], Time(3300), Time(3300)).SequenceEqual([held]) &&
                board.BlackjackState.Revision == revision + 1, "A Blackjack button did not act after its own second alone.");
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
