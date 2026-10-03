using ProjectTabletop.Interaction;

internal static class RouletteRegression
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckWheelAndPayouts();
        CheckReservationsAndRefunds();
        CheckSettlementAndRebet();
        CheckSeededClock();
        CheckInsufficientCredits();
        Console.WriteLine("Roulette verification passed: 37 single-zero pockets, all straight/outside/dozen/column payouts, " +
            "reserved multi-bets, exact undo/clear refunds, affordability, atomic rebet, seven-second once-only settlement, " +
            "immutable history, virtual refill and seeded clock invariance.");
    }

    private static void CheckWheelAndPayouts()
    {
        Require(RouletteGame.WheelOrder.Count == 37 && RouletteGame.WheelOrder.Order().SequenceEqual(Enumerable.Range(0, 37)),
            "The wheel does not contain exactly one of each European pocket.");
        Require(RouletteGame.WheelOrder.Take(5).SequenceEqual([0, 32, 15, 19, 4]) && RouletteGame.WheelOrder[^1] == 26,
            "The physical single-zero wheel order changed.");
        int[] red = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];
        Require(Enumerable.Range(0, 37).Where(RouletteGame.IsRed).SequenceEqual(red), "Roulette pocket colours are incorrect.");
        Require(RouletteGame.BetTargets.Count == 49 && RouletteGame.BetTargets.Select(target => target.Id).Distinct().Count() == 49,
            "A betting target is missing or duplicated.");
        foreach (var target in RouletteGame.BetTargets)
        {
            decimal[] returns = Enumerable.Range(0, 37).Select(number => RouletteGame.Payout(target, 1, number)).ToArray();
            int winners = target.Kind == RouletteBetKind.Straight ? 1 :
                target.Kind is RouletteBetKind.Dozen or RouletteBetKind.Column ? 12 : 18;
            Require(returns.Count(amount => amount > 0) == winners && returns.Sum() == 36,
                target.Id + " has incorrect gross odds or number of winning pockets.");
            if (target.Kind != RouletteBetKind.Straight)
                Require(returns[0] == 0, target.Id + " incorrectly pays on zero.");
            for (int number = 0; number < 37; number++)
                Require(RouletteGame.Payout(target, 7, number) == returns[number] * 7,
                    "Payout does not scale exactly with the reserved stake.");
        }
        Require(RouletteGame.Payout(new(RouletteBetKind.Straight, 0), 5, 0) == 180 &&
            RouletteGame.Payout(new(RouletteBetKind.Red), 5, 32) == 10 &&
            RouletteGame.Payout(new(RouletteBetKind.Black), 5, 32) == 0 &&
            RouletteGame.Payout(new(RouletteBetKind.Even), 5, 18) == 10 &&
            RouletteGame.Payout(new(RouletteBetKind.Odd), 5, 19) == 10 &&
            RouletteGame.Payout(new(RouletteBetKind.Low), 5, 18) == 10 &&
            RouletteGame.Payout(new(RouletteBetKind.Low), 5, 19) == 0 &&
            RouletteGame.Payout(new(RouletteBetKind.High), 5, 19) == 10,
            "A conventional number/colour/even-money boundary pays incorrectly.");
        for (int group = 1; group <= 3; group++)
        {
            Require(Enumerable.Range(0, 37).Where(number => RouletteGame.Payout(new(RouletteBetKind.Dozen, group), 1, number) > 0)
                .SequenceEqual(Enumerable.Range((group - 1) * 12 + 1, 12)), "A dozen contains the wrong numbers.");
            Require(Enumerable.Range(0, 37).Where(number => RouletteGame.Payout(new(RouletteBetKind.Column, group), 1, number) > 0)
                .SequenceEqual(Enumerable.Range(0, 12).Select(row => group + row * 3)), "A column contains the wrong numbers.");
        }
    }

    private static void CheckReservationsAndRefunds()
    {
        var game = new RouletteGame(1, 40);
        Require(!game.HandleAction("roulette-spin", Origin), "An empty slip could spin.");
        Act(game, "roulette-red"); Act(game, "roulette-red"); Act(game, "roulette-black");
        var placed = game.Snapshot;
        Require(placed.Balance == 25 && placed.TotalBet == 15 && placed.Bets.Count == 2 &&
            placed.Bets.Single(bet => bet.Id == "roulette-red").Amount == 10, "Repeated chips did not reserve credits correctly.");
        Act(game, "roulette-undo");
        Require(game.Balance == 30 && game.TotalBet == 10, "Undo did not return precisely the latest wager.");
        Act(game, "roulette-clear");
        Require(game.Balance == 40 && game.TotalBet == 0 && !game.HandleAction("roulette-undo", Origin) &&
            !game.HandleAction("roulette-clear", Origin), "Clear refunded a stake twice.");
        Require(placed.Balance == 25 && placed.Bets.Count == 2 && placed.TotalBet == 15, "A previous slip snapshot was mutated.");
        var poor = new RouletteGame(2, 3);
        Require(!poor.HandleAction("roulette-red", Origin), "A chip larger than the balance was accepted.");
        Act(poor, "roulette-chip-1");
        for (int chip = 0; chip < 3; chip++) Act(poor, "roulette-number-0");
        long revision = poor.Revision;
        Require(poor.Balance == 0 && !poor.HandleAction("roulette-number-0", Origin) && poor.Revision == revision,
            "A multi-bet slip overspent the balance.");
        Act(poor, "roulette-clear");
        Require(poor.Balance == 3 && !poor.HandleAction("roulette-number-37", Origin) &&
            !poor.HandleAction("roulette-column-0", Origin) && !poor.HandleAction("roulette-chip-3", Origin),
            "An invalid target/chip was accepted or a reserved stake was lost.");
        Require(!poor.HandleAction("roulette-red", Origin.AddTicks(-1)), "A backwards-clock wager was accepted.");
        var capped = new RouletteGame(3, 20000);
        Act(capped, "roulette-chip-100");
        for (int chip = 0; chip < 100; chip++) Act(capped, "roulette-red");
        Require(capped.TotalBet == RouletteGame.MaximumRoundBet && !capped.HandleAction("roulette-black", Origin) &&
            capped.Balance == 10000, "The per-round wager limit accepted or charged an extra chip.");
    }

    private static void CheckSettlementAndRebet()
    {
        var game = new RouletteGame(42);
        Act(game, "roulette-chip-1");
        for (int number = 0; number < 37; number++) Act(game, $"roulette-number-{number}");
        Act(game, "roulette-spin");
        var spinning = game.Snapshot;
        Require(spinning.Phase == RoulettePhase.Spinning && spinning.TotalBet == 37 && spinning.Balance == 963 &&
            spinning.RoundStartedAt == Origin && spinning.SpinDuration == TimeSpan.FromSeconds(7) &&
            spinning.Outcome == RouletteGame.WheelOrder[spinning.PocketIndex!.Value], "Spin charged twice or lost its deterministic presentation data.");
        Require(game.AvailableActions().Count == 0 && !game.HandleAction("roulette-clear", Origin) &&
            !game.HandleAction("roulette-spin", Origin) && !game.HandleAction("roulette-chip-100", Origin),
            "Committed stakes or chips changed while the wheel was spinning.");
        DateTimeOffset end = Origin + RouletteGame.SpinDuration;
        Require(!game.Tick(end.AddTicks(-1)) && game.Balance == 963, "Winnings arrived before the wheel settled.");
        Require(game.Tick(end), "The round did not settle at its exact deadline.");
        var result = game.Snapshot;
        Require(result.Phase == RoulettePhase.Betting && result.Balance == 999 && result.TotalBet == 0 &&
            result.LastWin == 36 && result.LastProfit == -1 && result.LastTotalBet == 37 && result.History.Count == 1,
            "All 37 straight bets did not return exactly 36 credits, including the winning stake.");
        Require(!game.Tick(end) && !game.Tick(end.AddSeconds(1)) && game.Balance == 999,
            "A settled result credited twice.");
        Require(spinning.Bets.Count == 37 && spinning.History.Count == 0 && spinning.Phase == RoulettePhase.Spinning,
            "Settlement mutated the earlier in-flight snapshot.");
        Act(game, "roulette-rebet", end.AddSeconds(1));
        Require(game.TotalBet == 37 && game.Balance == 962 && game.Snapshot.Bets.SequenceEqual(result.LastBets),
            "Rebet did not reserve the exact previous slip.");
        Act(game, "roulette-undo", end.AddSeconds(1));
        Require(game.Balance == 999 && game.TotalBet == 0, "Undo did not reverse the whole atomic Rebet operation.");
        Act(game, "roulette-rebet", end.AddSeconds(1)); Act(game, "roulette-spin", end.AddSeconds(1));
        Require(game.Tick(game.PhaseEndsAt) && game.Balance == 998 && game.Snapshot.History.Select(item => item.RoundNumber).SequenceEqual([2L, 1L]),
            "A later round lost accounting or newest-first history.");
        if (result.LastBets is IList<RouletteBet> mutable)
        {
            bool rejected = false;
            try { mutable[0] = new(new(RouletteBetKind.Black), 999); }
            catch (NotSupportedException) { rejected = true; }
            Require(rejected, "A consumer can mutate retained wagers.");
        }
    }

    private static void CheckSeededClock()
    {
        var first = new RouletteGame(1234, 100000);
        var second = new RouletteGame(1234, 100000);
        var offset = TimeSpan.FromDays(31);
        for (int round = 0; round < 64; round++)
        {
            var now = Origin.AddSeconds(round * 8);
            Require(!first.HandleAction("invalid", now), "An invalid action was accepted.");
            Act(first, "roulette-red", now); Act(second, "roulette-red", now + offset);
            Act(first, "roulette-spin", now); Act(second, "roulette-spin", now + offset);
            Require(first.Snapshot.Outcome == second.Snapshot.Outcome && first.Snapshot.PocketIndex == second.Snapshot.PocketIndex &&
                first.Snapshot.RoundStartedAt + offset == second.Snapshot.RoundStartedAt,
                "Rejected actions or clock dates changed the seeded wheel outcome.");
            first.Tick(now + RouletteGame.SpinDuration); second.Tick(now + offset + RouletteGame.SpinDuration);
            Require(first.Balance == second.Balance && first.Snapshot.History.SequenceEqual(second.Snapshot.History),
                "Identical seeded rounds diverged in accounting.");
        }
        Require(first.Snapshot.History.Count == 12 && first.Snapshot.History[0].RoundNumber == 64 &&
            first.Snapshot.History[^1].RoundNumber == 53, "Recent result history is unbounded or out of order.");
    }

    private static void CheckInsufficientCredits()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var game = new RouletteGame(seed, 1);
            Act(game, "roulette-chip-1"); Act(game, "roulette-red"); Act(game, "roulette-spin");
            game.Tick(game.PhaseEndsAt);
            if (game.Balance != 0) continue;
            Require(!game.HandleAction("roulette-rebet", Origin + RouletteGame.SpinDuration) && game.TotalBet == 0,
                "An unaffordable Rebet partially reserved its slip.");
            Act(game, "roulette-refill", Origin + RouletteGame.SpinDuration);
            Require(game.Balance == RouletteGame.StartingBalance && !game.AvailableActions().Contains("roulette-refill"),
                "Virtual refill failed or remained available with credits.");
            return;
        }
        throw new InvalidOperationException("No losing one-credit fixture was exercised.");
    }

    private static void Act(RouletteGame game, string id, DateTimeOffset? now = null) =>
        Require(game.HandleAction(id, now ?? Origin), "Roulette fixture action failed: " + id);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
