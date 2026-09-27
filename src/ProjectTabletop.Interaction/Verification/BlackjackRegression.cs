using ProjectTabletop.Interaction;

internal static class BlackjackRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);
    private static BlackjackCard Card(int rank) => new(rank, BlackjackSuit.Spades);
    private static BlackjackGame Game(params int[] ranks) => new(seed: 42, initialShoe: ranks.Select(Card));

    public static void Run()
    {
        CheckInitialStateAndGating();
        CheckNaturalsAndHiddenInformation();
        CheckHitBustAndAces();
        CheckDealerTimingAndSoft17();
        CheckDoubleAndSplit();
        CheckBankrollAndReset();
        CheckShoeAndSnapshotIsolation();
        CheckSeededFullRounds();
        Console.WriteLine("Blackjack verification passed: hidden dealer cards, naturals, payouts, aces, " +
            "dealer timing/soft 17, hit/stand/double/split, stakes, action gating, shoe recovery, reset, " +
            "immutable snapshots, and 400 deterministic full rounds.");
    }

    private static void CheckInitialStateAndGating()
    {
        var game = Game(10, 7, 8, 10);
        var initial = game.Snapshot;
        Require(initial.Phase == BlackjackPhase.Betting && initial.Bankroll == 1000 && initial.SelectedBet == 25 &&
            initial.RoundNumber == 0 && initial.Hands.Count == 0 && initial.DealerCards.Count == 0, "Initial game state.");
        Require(initial.AvailableActions.SequenceEqual(["bj-bet-10", "bj-bet-25", "bj-bet-50", "bj-bet-100", "bj-deal", "bj-reset"]),
            "Initial actions.");
        foreach (string action in new[] { "bj-hit", "bj-stand", "bj-double", "bj-split", "bj-bet-0", "bj-bet-500", "unknown" })
            Require(!game.HandleAction(action, At(0)), $"Illegal initial action: {action}.");
        Require(game.Revision == 0 && ReferenceEquals(initial, game.Snapshot), "Rejected action mutated game.");
        Do(game, "bj-bet-100", 10);
        Require(game.Snapshot.SelectedBet == 100 && game.Snapshot.Bankroll == 1000, "Selecting a bet deducted chips.");
        Do(game, "bj-deal", 20);
        Require(game.Snapshot.Bankroll == 900 && game.Snapshot.Hands[0].Bet == 100 && game.Snapshot.RoundNumber == 1,
            "Deal did not deduct exactly one bet.");
        foreach (string action in new[] { "bj-bet-10", "bj-deal", "bj-reset", "bj-split" })
            Require(!game.HandleAction(action, At(20)), $"Illegal active-round action: {action}.");
        Require(!game.HandleAction("bj-hit", At(19)), "Out-of-order action accepted.");
        Do(game, "bj-stand", 30);
        Require(game.Snapshot.AvailableActions.Count == 0, "Dealer turn allowed player actions.");
    }

    private static void CheckNaturalsAndHiddenInformation()
    {
        var natural = Game(1, 9, 13, 7);
        Do(natural, "bj-deal", 0);
        var snap = natural.Snapshot;
        Require(snap.Phase == BlackjackPhase.RoundOver && snap.Bankroll == 1037.5m && snap.Hands[0].IsNatural &&
            snap.Hands[0].Result == "BLACKJACK +37.5" && !snap.DealerHoleCardHidden, "Player natural must pay 3:2 plus stake.");
        Require(!natural.Tick(At(5000)) && natural.Snapshot.Bankroll == 1037.5m, "Natural paid twice.");

        var both = Game(1, 1, 10, 13);
        Do(both, "bj-deal", 0);
        Require(both.Snapshot.Bankroll == 1000 && both.Snapshot.Hands[0].Result == "PUSH", "Two naturals did not push.");

        var dealer = Game(10, 1, 8, 10);
        Do(dealer, "bj-deal", 0);
        Require(dealer.Snapshot.Phase == BlackjackPhase.RoundOver && dealer.Snapshot.Bankroll == 975 &&
            dealer.Snapshot.Hands[0].Result == "DEALER BLACKJACK" && dealer.Snapshot.DealerCards.All(card => card is not null),
            "Dealer peek did not settle its natural immediately.");

        var hiddenA = Game(8, 1, 9, 7);
        var hiddenB = Game(8, 1, 9, 6);
        Do(hiddenA, "bj-deal", 0);
        Do(hiddenB, "bj-deal", 0);
        foreach (var hidden in new[] { hiddenA.Snapshot, hiddenB.Snapshot })
            Require(hidden.DealerHoleCardHidden && hidden.DealerCards.Count == 2 && hidden.DealerCards[1] is null &&
                hidden.DealerTotal == 11 && hidden.DealerIsSoft, "Hidden dealer card leaked through card or total.");
        Require(hiddenA.Snapshot.DealerCards.SequenceEqual(hiddenB.Snapshot.DealerCards) &&
            hiddenA.Snapshot.Status == hiddenB.Snapshot.Status, "Hidden-card fixture changed public pre-reveal state.");
    }

    private static void CheckHitBustAndAces()
    {
        var bust = Game(10, 5, 8, 10, 10);
        Do(bust, "bj-deal", 0);
        Do(bust, "bj-hit", 10);
        Require(bust.Snapshot.Hands[0].IsBust && bust.Snapshot.Hands[0].Total == 28 &&
            bust.Snapshot.Phase == BlackjackPhase.DealerTurn, "Bust did not finish player turn.");
        Finish(bust, 10);
        Require(bust.Snapshot.Bankroll == 975 && bust.Snapshot.Hands[0].Result == "BUST" &&
            bust.Snapshot.DealerCards.Count == 2, "All-busted round drew needless dealer cards or paid.");

        var aces = Game(1, 10, 1, 7, 9, 10);
        Do(aces, "bj-deal", 0);
        Require(aces.Snapshot.Hands[0].Total == 12 && aces.Snapshot.Hands[0].IsSoft, "Two aces should total soft 12.");
        Do(aces, "bj-hit", 10);
        Require(aces.Snapshot.Hands[0].Total == 21 && aces.Snapshot.Hands[0].IsSoft && !aces.Snapshot.Hands[0].IsNatural,
            "Multi-card ace 21 was valued incorrectly or treated as natural.");
        Finish(aces, 10);
        Require(aces.Snapshot.Bankroll == 1025, "Three-card 21 did not pay the normal 1:1 rate.");

        var soft = Game(1, 10, 5, 8, 10, 4);
        Do(soft, "bj-deal", 0);
        Require(soft.Snapshot.Hands[0].Total == 16 && soft.Snapshot.Hands[0].IsSoft, "Soft 16 incorrect.");
        Do(soft, "bj-hit", 10);
        Require(soft.Snapshot.Hands[0].Total == 16 && !soft.Snapshot.Hands[0].IsSoft, "Ace did not demote from 11 to 1.");
        Require(!soft.Snapshot.AvailableActions.Contains("bj-double") && !soft.HandleAction("bj-double", At(10)),
            "Double allowed after a hit.");
        Do(soft, "bj-hit", 20);
        Do(soft, "bj-stand", 30);
        Finish(soft, 30);
        Require(soft.Snapshot.Bankroll == 1025, "Hard-ace hand did not win correctly.");

        var push = Game(10, 10, 8, 8);
        Do(push, "bj-deal", 0);
        Do(push, "bj-stand", 10);
        Finish(push, 10);
        Require(push.Snapshot.Bankroll == 1000 && push.Snapshot.Hands[0].Result == "PUSH", "Ordinary push lost or gained chips.");
    }

    private static void CheckDealerTimingAndSoft17()
    {
        var soft17 = Game(10, 1, 8, 6, 10);
        Do(soft17, "bj-deal", 0);
        Do(soft17, "bj-stand", 100);
        Require(!soft17.Tick(At(749)) && soft17.Snapshot.DealerCards[1] is null, "Dealer revealed early.");
        Require(soft17.Tick(At(750)) && !soft17.Snapshot.DealerHoleCardHidden && soft17.Snapshot.DealerTotal == 17 &&
            soft17.Snapshot.DealerIsSoft && soft17.Snapshot.Phase == BlackjackPhase.DealerTurn, "Timed reveal failed.");
        Require(!soft17.Tick(At(750)) && !soft17.Tick(At(749)) && !soft17.Tick(At(1399)), "Dealer advanced twice or backwards.");
        Require(soft17.Tick(At(1400)) && soft17.Snapshot.Phase == BlackjackPhase.RoundOver &&
            soft17.Snapshot.DealerCards.Count == 2 && soft17.Snapshot.Bankroll == 1025, "Dealer did not stand on soft 17.");

        var draws = Game(10, 5, 9, 5, 2, 4, 10);
        Do(draws, "bj-deal", 0);
        Do(draws, "bj-stand", 10);
        Require(draws.Tick(At(10000)) && draws.Snapshot.DealerCards.Count == 2, "Late frame skipped reveal pause.");
        Require(!draws.Tick(At(10000)), "Same frame ran another dealer step.");
        Require(draws.Tick(At(10650)) && draws.Snapshot.DealerCards.Count == 3 && draws.Snapshot.DealerTotal == 12,
            "First dealer draw failed.");
        Require(draws.Tick(At(11300)) && draws.Snapshot.DealerTotal == 16, "Dealer stopped below 17.");
        Require(draws.Tick(At(11950)) && draws.Snapshot.DealerTotal == 26 && draws.Snapshot.Bankroll == 975,
            "Bust was not displayed before settlement.");
        Require(draws.Tick(At(12600)) && draws.Snapshot.Bankroll == 1025, "Dealer bust settlement incorrect.");
    }

    private static void CheckDoubleAndSplit()
    {
        var doubled = Game(5, 10, 6, 8, 10);
        Do(doubled, "bj-deal", 0);
        Do(doubled, "bj-double", 10);
        Require(doubled.Snapshot.Bankroll == 950 && doubled.Snapshot.Hands[0].Bet == 50 &&
            doubled.Snapshot.Hands[0].Cards.Count == 3 && doubled.Snapshot.Phase == BlackjackPhase.DealerTurn,
            "Double did not take one additional stake and exactly one card.");
        Finish(doubled, 10);
        Require(doubled.Snapshot.Bankroll == 1050, "Doubled win payout incorrect.");

        var doubleBust = Game(10, 10, 6, 8, 10);
        Do(doubleBust, "bj-deal", 0);
        Do(doubleBust, "bj-double", 10);
        Finish(doubleBust, 10);
        Require(doubleBust.Snapshot.Bankroll == 950 && doubleBust.Snapshot.Hands[0].IsBust, "Doubled bust lost wrong stake.");

        var split = Game(8, 10, 8, 8, 3, 2, 10, 10);
        Do(split, "bj-deal", 0);
        Do(split, "bj-split", 10);
        Require(split.Snapshot.Bankroll == 950 && split.Snapshot.Hands.Count == 2 && split.Snapshot.ActiveHandIndex == 0 &&
            split.Snapshot.Hands[0].Total == 11 && split.Snapshot.Hands[1].Total == 10 &&
            !split.Snapshot.AvailableActions.Contains("bj-split"), "Split card/stake allocation incorrect.");
        Do(split, "bj-double", 20);
        Require(split.Snapshot.Bankroll == 925 && split.Snapshot.ActiveHandIndex == 1 &&
            split.Snapshot.Hands[0].Bet == 50 && split.Snapshot.Hands[0].Total == 21,
            "Double after split or transition to second hand failed.");
        Do(split, "bj-hit", 30);
        Do(split, "bj-stand", 40);
        Finish(split, 40);
        Require(split.Snapshot.Bankroll == 1075 && split.Snapshot.Hands.All(hand => hand.Result.StartsWith("WIN")),
            "Two split hands did not settle individual stakes.");

        var splitAces = Game(1, 10, 1, 9, 10, 9);
        Do(splitAces, "bj-deal", 0);
        Do(splitAces, "bj-split", 10);
        Require(splitAces.Snapshot.Phase == BlackjackPhase.DealerTurn &&
            splitAces.Snapshot.Hands.All(hand => hand.Cards.Count == 2 && !hand.IsNatural),
            "Split aces must receive one card each and no natural payout.");
        Finish(splitAces, 10);
        Require(splitAces.Snapshot.Bankroll == 1050, "Split ace 21 paid 3:2 instead of 1:1.");

        var unequalFaces = Game(10, 7, 11, 10);
        Do(unequalFaces, "bj-deal", 0);
        Require(!unequalFaces.HandleAction("bj-split", At(10)), "Equal-value but unequal-rank cards split.");

        var noResplit = Game(8, 10, 8, 9, 8, 8);
        Do(noResplit, "bj-deal", 0);
        Do(noResplit, "bj-split", 10);
        Require(!noResplit.HandleAction("bj-split", At(20)), "Second split was allowed.");
    }

    private static void CheckBankrollAndReset()
    {
        var ranks = Enumerable.Range(0, 10).SelectMany(_ => new[] { 10, 10, 7, 10 }).ToArray();
        var game = Game(ranks);
        Do(game, "bj-bet-100", 0);
        for (int round = 0; round < 10; round++)
        {
            int start = round * 2000 + 10;
            Do(game, "bj-deal", start);
            if (round == 9)
            {
                Require(game.Snapshot.Bankroll == 0 && !game.Snapshot.AvailableActions.Contains("bj-double") &&
                    !game.HandleAction("bj-double", At(start)) && !game.HandleAction("bj-reset", At(start)),
                    "Unfunded double or active-round reset allowed.");
            }
            Do(game, "bj-stand", start + 10);
            Finish(game, start + 10);
            Require(game.Snapshot.Bankroll == 900 - round * 100, "Loss incorrectly changed bankroll.");
        }
        Require(game.Snapshot.Bankroll == 0 && game.Snapshot.AvailableActions.SequenceEqual(["bj-reset"]),
            "Broke player could deal or change bet.");
        Require(!game.HandleAction("bj-deal", At(21000)), "Broke player placed a bet.");
        Do(game, "bj-reset", 21000);
        Require(game.Snapshot.Bankroll == 1000 && game.Snapshot.SelectedBet == 25 &&
            game.Snapshot.Phase == BlackjackPhase.Betting && game.Snapshot.Hands.Count == 0 &&
            game.Snapshot.DealerCards.Count == 0 && !game.Snapshot.DealerHoleCardHidden, "Reset did not restore clean betting state.");

        // With one stake remaining, a pair may still be played but cannot be split.
        var pairRanks = ranks.Take(36).Concat(new[] { 8, 10, 8, 10 }).ToArray();
        var unfundedPair = Game(pairRanks);
        Do(unfundedPair, "bj-bet-100", 0);
        for (int round = 0; round < 9; round++)
        {
            int start = round * 2000 + 10;
            Do(unfundedPair, "bj-deal", start);
            Do(unfundedPair, "bj-stand", start + 10);
            Finish(unfundedPair, start + 10);
        }
        Do(unfundedPair, "bj-deal", 19000);
        Require(!unfundedPair.HandleAction("bj-split", At(19010)), "Split accepted without a second stake.");
    }

    private static void CheckShoeAndSnapshotIsolation()
    {
        foreach (var shoe in new[] { Array.Empty<BlackjackCard>(), new[] { Card(8) }, new[] { Card(8), Card(10), Card(8), Card(7) } })
        {
            var game = new BlackjackGame(seed: 4, initialShoe: shoe);
            Do(game, "bj-deal", 0);
            if (game.Snapshot.Phase == BlackjackPhase.PlayerTurn) Do(game, "bj-stand", 10);
            Finish(game, 10);
            Require(game.Snapshot.Phase == BlackjackPhase.RoundOver && game.Snapshot.DealerCards.All(card => card is not null),
                "An exhausted injected shoe did not safely finish its round.");
        }
        Throws<ArgumentException>(() => new BlackjackGame(initialShoe: [new(0, BlackjackSuit.Clubs)]), "Invalid rank accepted.");
        Throws<ArgumentException>(() => new BlackjackGame(initialShoe: [new(3, (BlackjackSuit)99)]), "Invalid suit accepted.");

        var copy = Game(10, 7, 5, 10, 2);
        Do(copy, "bj-deal", 0);
        var before = copy.Snapshot;
        Throws<NotSupportedException>(() => ((IList<BlackjackCard>)before.Hands[0].Cards)[0] = Card(1), "Snapshot cards mutable.");
        Throws<NotSupportedException>(() => ((IList<BlackjackCard?>)before.DealerCards)[1] = Card(1), "Dealer snapshot mutable.");
        Do(copy, "bj-hit", 10);
        Require(before.Hands[0].Cards.Count == 2 && before.Hands[0].Total == 15 && before.DealerCards[1] is null &&
            before.Revision < copy.Snapshot.Revision, "Previously obtained snapshot changed with the game.");
    }

    private static void CheckSeededFullRounds()
    {
        var left = new BlackjackGame(seed: 817);
        var right = new BlackjackGame(seed: 817);
        var decisions = new Random(91);
        int time = 0;
        for (int round = 0; round < 400; round++)
        {
            if (!left.Snapshot.AvailableActions.Contains("bj-deal")) Apply("bj-reset");
            Apply("bj-deal");
            while (left.Snapshot.Phase == BlackjackPhase.PlayerTurn)
            {
                var snapshot = left.Snapshot;
                var hand = snapshot.Hands[snapshot.ActiveHandIndex];
                string action = hand.Total < 17 ? "bj-hit" : "bj-stand";
                if (snapshot.AvailableActions.Contains("bj-split") && decisions.Next(2) == 0) action = "bj-split";
                else if (snapshot.AvailableActions.Contains("bj-double") && hand.Total is 9 or 10 or 11 && decisions.Next(2) == 0)
                    action = "bj-double";
                Apply(action);
            }
            while (left.Snapshot.Phase == BlackjackPhase.DealerTurn)
            {
                var before = left.Snapshot;
                time += 650;
                Require(left.Tick(At(time)) == right.Tick(At(time)), "Seeded games advanced differently.");
                Compare(left.Snapshot, right.Snapshot);
                if (left.Snapshot.Phase == BlackjackPhase.RoundOver)
                    VerifySettlement(before.Bankroll, left.Snapshot);
            }
            decimal settledBank = left.Snapshot.Bankroll;
            Require(!left.Tick(At(++time)) && left.Snapshot.Bankroll == settledBank, "Completed round paid again.");
            Require(settledBank >= 0, "Bankroll became negative.");
        }

        void Apply(string action)
        {
            var before = left.Snapshot;
            time += 20;
            Require(left.HandleAction(action, At(time)) && right.HandleAction(action, At(time)), "Seeded legal action rejected.");
            Compare(left.Snapshot, right.Snapshot);
            var after = left.Snapshot;
            if (action == "bj-deal")
            {
                decimal staked = before.Bankroll - before.SelectedBet;
                if (after.Phase == BlackjackPhase.RoundOver) VerifySettlement(staked, after);
                else Require(after.Bankroll == staked, "Deal stake accounting failed in full round.");
            }
            else if (action is "bj-double" or "bj-split")
                Require(after.Bankroll == before.Bankroll - before.Hands[before.ActiveHandIndex].Bet,
                    "Extra-hand/double stake accounting failed in full round.");
        }
    }

    private static void VerifySettlement(decimal beforeBankroll, BlackjackSnapshot after)
    {
        decimal expected = beforeBankroll;
        bool dealerNatural = after.DealerCards.Count == 2 && after.DealerTotal == 21;
        foreach (var hand in after.Hands)
        {
            if (hand.IsBust || (dealerNatural && !hand.IsNatural)) continue;
            if (hand.IsNatural && !dealerNatural) expected += hand.Bet * 2.5m;
            else if (dealerNatural || hand.Total == after.DealerTotal) expected += hand.Bet;
            else if (after.DealerTotal > 21 || hand.Total > after.DealerTotal) expected += hand.Bet * 2;
        }
        Require(after.Bankroll == expected, "Full-round settlement did not match individual stakes and outcomes.");
    }

    private static void Compare(BlackjackSnapshot left, BlackjackSnapshot right)
    {
        Require(left.Phase == right.Phase && left.Bankroll == right.Bankroll && left.RoundNumber == right.RoundNumber &&
            left.DealerTotal == right.DealerTotal && left.DealerCards.SequenceEqual(right.DealerCards) &&
            left.Hands.Count == right.Hands.Count && left.Status == right.Status && left.Revision == right.Revision,
            "Identically seeded games diverged.");
        for (int hand = 0; hand < left.Hands.Count; hand++)
            Require(left.Hands[hand].Cards.SequenceEqual(right.Hands[hand].Cards), "Seeded hand cards diverged.");
    }

    private static void Finish(BlackjackGame game, int start)
    {
        for (int step = 1; step <= 30 && game.Snapshot.Phase == BlackjackPhase.DealerTurn; step++)
            Require(game.Tick(At(start + step * 650)), "Dealer step was unexpectedly rejected.");
        Require(game.Snapshot.Phase == BlackjackPhase.RoundOver, "Dealer never finished round.");
    }

    private static void Do(BlackjackGame game, string action, int time) =>
        Require(game.HandleAction(action, At(time)), $"Action {action} failed at {time}ms.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blackjack: " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Blackjack: " + message);
    }
}
