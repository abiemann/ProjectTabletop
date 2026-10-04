using ProjectTabletop.Interaction;

internal static class SlotRegression
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckPaylines();
        CheckLineEvaluation();
        CheckActionsAndBets();
        CheckTimedPresentation();
        CheckRespins();
        CheckFreeSpins();
        CheckVault();
        CheckBuyAndRefill();
        CheckDeterminism();
        SlotDragonHatchRegression.Run();
        SlotSpinTimingRegression.Run();
        var stats = Simulate(seed: 20260930, spins: 400_000);
        var buy = SimulateBuys(seed: 7, buys: 20_000);
        Console.WriteLine($"Slot simulation: RTP {stats.Rtp:P2}, hits {stats.HitRate:P1}, respins 1/{stats.RespinsEvery:0}, " +
            $"free spins 1/{stats.FreeSpinsEvery:0}, vault 1/{stats.VaultEvery:0}, buy {buy:P1}.");
        Require(stats.Rtp is >= .93 and <= .985, $"Return to player {stats.Rtp:P2} is outside 93–98.5%.");
        Require(stats.HitRate is >= .22 and <= .45, $"Hit frequency {stats.HitRate:P1} is outside 22–45%.");
        Require(stats.RespinsEvery is >= 70 and <= 220, $"Respins every {stats.RespinsEvery:0} spins is outside 70–220.");
        Require(stats.FreeSpinsEvery is >= 150 and <= 600, $"Free spins every {stats.FreeSpinsEvery:0} spins is outside 150–600.");
        Require(stats.VaultEvery is >= 120 and <= 700, $"The vault every {stats.VaultEvery:0} spins is outside 120–700.");
        Require(buy is >= .88 and <= 1.0, $"Feature Buy returns {buy:P1} of its price, outside 88–100%.");
        Console.WriteLine($"Slot verification passed: 40 unique paylines, wild substitution, bet/buy/refill gating, timed phases, " +
            $"respin expand/boost/collect/grand, elixir free spins, three-gem vault and determinism. " +
            $"Simulated RTP {stats.Rtp:P2}, hits {stats.HitRate:P1}, respins 1/{stats.RespinsEvery:0}, " +
            $"free spins 1/{stats.FreeSpinsEvery:0}, vault 1/{stats.VaultEvery:0}, buy {buy:P1}.");
    }

    private static void CheckPaylines()
    {
        Require(SlotGame.Paylines.Count == SlotGame.PaylineCount, "The machine does not have 40 paylines.");
        Require(SlotGame.Paylines.Select(line => string.Concat(line)).Distinct().Count() == SlotGame.PaylineCount,
            "Two paylines repeat.");
        Require(SlotGame.Paylines.All(line => line.Count == SlotGame.Reels && line.All(row => row is >= 0 and <= 2) &&
            line.Zip(line.Skip(1)).All(pair => Math.Abs(pair.First - pair.Second) <= 1)),
            "A payline leaves the three rows or jumps a row.");
        Require(string.Concat(SlotGame.Paylines[0]) == "11111" && string.Concat(SlotGame.Paylines[3]) == "01210",
            "The first paylines are not the familiar middle line and V.");
    }

    private static void CheckLineEvaluation()
    {
        var grid = Grid(
            "A A W A K",
            "Q Q Q Q Q",
            "J K T J K");
        var wins = SlotGame.Evaluate(grid, 20);
        // Middle line (index 0) pays five Queens; the top line (index 1) pays four Aces through a wild.
        Require(wins.Any(win => win.Line == 0 && win.Symbol == SlotSymbol.Queen && win.Count == 5 &&
            win.Amount == SlotGame.Paytable[SlotSymbol.Queen][2] * 20), "Five Queens on the middle line did not pay.");
        Require(wins.Any(win => win.Line == 1 && win.Symbol == SlotSymbol.Ace && win.Count == 4 &&
            win.Amount == SlotGame.Paytable[SlotSymbol.Ace][1] * 20), "A wild did not extend four Aces on the top line.");
        Require(!wins.Any(win => win.Line == 2), "The bottom line paid without three matching symbols.");

        var wilds = Grid(
            "W W W K Q",
            "T J Q K A",
            "A K Q J T");
        var top = SlotGame.Evaluate(wilds, 20).Single(win => win.Line == 1);
        Require(SlotGame.Paytable[SlotSymbol.Crown][0] > SlotGame.Paytable[SlotSymbol.King][1] &&
            top.Symbol == SlotSymbol.Wild && top.Count == 3 && top.Amount == SlotGame.Paytable[SlotSymbol.Crown][0] * 20,
            "Three leading wilds did not pay as three Crowns when that beat four Kings.");
        var broken = Grid(
            "K K C K K",
            "T J Q K A",
            "A K Q J T");
        Require(!SlotGame.Evaluate(broken, 20).Any(win => win.Line == 1), "A coin failed to break a payline.");
    }

    private static void CheckActionsAndBets()
    {
        var game = new SlotGame(1);
        var now = Origin;
        Require(game.Snapshot.Balance == SlotGame.StartingBalance && game.Snapshot.Bet == 20, "The machine did not start at 2,000 credits and bet 20.");
        Require(game.AvailableActions().SequenceEqual(["slot-spin", "slot-bet-up", "slot-buy"]),
            "The opening actions are wrong: " + string.Join(",", game.AvailableActions()));
        Require(game.HandleAction("slot-bet-up", now) && game.Bet == 40 && game.BuyCost == 40 * SlotGame.BuyMultiplier,
            "Bet + did not raise the bet to 40 and its Buy price with it.");
        Require(!game.AvailableActions().Contains("slot-buy"), "Buy was offered without credits for it.");
        Require(game.HandleAction("slot-bet-down", now) && game.Bet == 20, "Bet - did not lower the bet.");
        Require(!game.HandleAction("slot-bet-down", now), "Bet - went below the minimum.");
        Require(!game.HandleAction("slot-unknown", now), "An unknown action was accepted.");
        Require(game.HandleAction("slot-spin", now) && game.Phase == SlotPhase.Spinning && game.Balance == 1980,
            "Spin did not take the bet and start the reels.");
        Require(game.AvailableActions().Count == 0 && !game.HandleAction("slot-spin", now),
            "Controls stayed available while the reels spun.");
        Require(!game.Tick(now - TimeSpan.FromSeconds(1)), "A clock moving backwards advanced the machine.");
    }

    private static void CheckTimedPresentation()
    {
        var game = new SlotGame(3);
        var now = Origin;
        game.HandleAction("slot-spin", now);
        var spin = game.Snapshot;
        Require(spin.PhaseDuration == SlotGame.SpinDuration && spin.SpinNumber == 1 && !spin.FreeSpin,
            "The spin phase did not report its timing.");
        Require(!game.Tick(now + SlotGame.SpinDuration - TimeSpan.FromMilliseconds(1)), "The reels settled early.");
        for (int reel = 1; reel < SlotGame.Reels; reel++)
            Require(SlotGame.ReelStop(reel, false) > SlotGame.ReelStop(reel - 1, false) &&
                SlotGame.ReelStop(reel, false) < SlotGame.SpinDuration, "Reels do not stop left to right within the spin.");
        now = RunToIdle(game, now);
        Require(game.Phase == SlotPhase.Idle && game.AvailableActions().Contains("slot-spin"), "The spin never settled.");
        Require(game.Snapshot.Balance == 1980 + game.Snapshot.RoundWin, "The round's wins were not credited exactly once.");
    }

    private static void CheckRespins()
    {
        // Every egg colour appears across seeds; each run must obey the respin rules.
        var seen = new HashSet<SlotEffect>();
        bool grand = false;
        for (int seed = 0; seed < 400 && (seen.Count < 3 || !grand); seed++)
        {
            var game = new SlotGame(seed, 1_000_000);
            var now = Origin;
            game.Demonstrate(SlotDemo.Respins);
            game.HandleAction("slot-spin", now);
            decimal before = game.Balance;
            decimal credited = game.Snapshot.LineWins.Sum(win => win.Amount);
            now = Step(game, now);
            if (game.Phase == SlotPhase.LineWins) now = Step(game, now);
            Require(game.Phase == SlotPhase.RespinIntro && game.Snapshot.InRespins && game.Snapshot.RespinsLeft == 3,
                $"Seed {seed}: an egg with five coins did not start three respins.");
            var held = game.Snapshot.Grid.Count(cell => cell.IsBonus);
            Require(game.Snapshot.Grid.Where((cell, index) => index % SlotGame.Rows is 0 or 4).All(cell => cell.Symbol == SlotSymbol.Empty) &&
                game.Snapshot.Grid.All(cell => cell.IsBonus || cell.Symbol == SlotSymbol.Empty),
                $"Seed {seed}: the respin grid kept ordinary symbols or used locked rows.");
            while (game.Phase != SlotPhase.RespinOutro)
            {
                var previous = game.Snapshot;
                now = Step(game, now);
                var current = game.Snapshot;
                if (current.Phase == SlotPhase.RespinEffect)
                {
                    seen.Add(current.Effect);
                    switch (current.Effect)
                    {
                        case SlotEffect.Expand:
                            Require(previous.RowCount == 3 && current.RowCount == 5 && current.FirstRow == 0,
                                $"Seed {seed}: Expand did not open five rows.");
                            break;
                        case SlotEffect.Boost:
                            Require(current.BoostMultiplier is 2 or 3 or 5, $"Seed {seed}: Boost used ×{current.BoostMultiplier}.");
                            for (int index = 0; index < current.Grid.Count; index++)
                                if (previous.Grid[index] is { Symbol: SlotSymbol.Coin, Jackpot: SlotJackpot.None } coin)
                                    Require(current.Grid[index].Value == coin.Value * current.BoostMultiplier,
                                        $"Seed {seed}: Boost did not multiply a coin.");
                            break;
                        case SlotEffect.Collect:
                            var cell = current.EffectCell!.Value;
                            int collector = SlotGame.Index(cell.Reel, cell.Row);
                            decimal others = previous.Grid.Where((_, index) => index != collector).Sum(item => item.Value);
                            Require(current.Grid[collector].Value == previous.Grid[collector].Value + others &&
                                current.EffectAmount == others, $"Seed {seed}: Collect did not gather every other value.");
                            break;
                    }
                }
                else if (current.Phase == SlotPhase.Respinning)
                {
                    Require(current.FreshCells.All(position => previous.Grid[SlotGame.Index(position.Reel, position.Row)].Symbol == SlotSymbol.Empty &&
                        current.IsActiveRow(position.Row)), $"Seed {seed}: a respin landed on a held or locked cell.");
                    Require(current.FreshCells.Count > 0 ? current.RespinsLeft == 3 : current.RespinsLeft == previous.RespinsLeft - 1 ||
                        previous.Phase != SlotPhase.Respinning && current.RespinsLeft == 2,
                        $"Seed {seed}: the respin counter did not reset on a landing or count down without one.");
                    Require(current.Grid.Count(item => item.IsBonus) >= held, $"Seed {seed}: a held symbol was lost.");
                    held = current.Grid.Count(item => item.IsBonus);
                }
            }
            var outro = game.Snapshot;
            decimal grandAward = outro.GrandFill ? 1000 * outro.Bet : 0;
            Require(outro.RespinTotal == outro.Grid.Sum(cell => cell.Value) + grandAward,
                $"Seed {seed}: the respin total is not the sum of the held values.");
            Require(game.Balance == before + credited + outro.RespinTotal, $"Seed {seed}: respin winnings were not credited once.");
            grand |= outro.GrandFill;
            now = RunToIdle(game, now);
            Require(!game.Snapshot.InRespins && game.Snapshot.RowCount == 3, $"Seed {seed}: the machine stayed in respins.");
        }
        Require(seen.SetEquals([SlotEffect.Expand, SlotEffect.Boost, SlotEffect.Collect]),
            "Not every egg effect appeared across 400 respin rounds.");
    }

    private static void CheckFreeSpins()
    {
        var game = new SlotGame(11, 1_000_000);
        var now = Origin;
        game.Demonstrate(SlotDemo.FreeSpins);
        game.HandleAction("slot-spin", now);
        decimal afterBet = game.Balance;
        Require(game.Snapshot.Grid.Count(cell => cell.Symbol == SlotSymbol.Elixir) == 3, "The demonstration did not land three elixirs.");
        while (game.Phase != SlotPhase.FreeSpinsIntro) now = Step(game, now);
        Require(game.Snapshot.InFreeSpins && game.Snapshot.FreeSpinsRemaining == 5, "Three elixirs did not award five free spins.");
        int played = 0, extra = 0;
        while (game.Phase != SlotPhase.FreeSpinsOutro)
        {
            now = Step(game, now);
            var state = game.Snapshot;
            if (state.Phase == SlotPhase.Spinning)
            {
                played++;
                Require(state.FreeSpin && state.FreeSpinsPlayed == played, "A free spin was not counted.");
                extra += state.Grid.Count(cell => cell.Symbol == SlotSymbol.Elixir);
                Require(state.ElixirLevel == Math.Min(5, extra), "Elixirs did not raise the elixir level.");
            }
        }
        Require(played == 5 + extra, $"Free spins played {played}, expected {5 + extra}.");
        var outro = game.Snapshot;
        Require(game.Balance == afterBet + outro.RoundWin && outro.FreeSpinsWin <= outro.RoundWin,
            "Free spins were charged or credited incorrectly.");
        now = RunToIdle(game, now);
        Require(!game.Snapshot.InFreeSpins && game.Phase == SlotPhase.Idle, "Free spins did not end.");
    }

    private static void CheckVault()
    {
        var tiers = new HashSet<SlotJackpot>();
        for (int seed = 0; seed < 300 && tiers.Count < 2; seed++)
        {
            var game = new SlotGame(seed, 1_000_000);
            var now = Origin;
            game.Demonstrate(SlotDemo.Vault);
            game.HandleAction("slot-spin", now);
            Require(game.Snapshot.Keys.All(lit => lit), $"Seed {seed}: the fifth key did not light.");
            while (game.Phase != SlotPhase.VaultIntro) now = Step(game, now);
            Require(game.Snapshot.Keys.All(lit => !lit), $"Seed {seed}: the keys did not reset when the vault opened.");
            decimal before = game.Balance;
            while (game.Phase != SlotPhase.VaultOutro) now = Step(game, now);
            var outro = game.Snapshot;
            var gems = outro.VaultChests.Where(chest => chest is not null).Select(chest => chest!.Gem).ToArray();
            Require(gems.Count(gem => gem == outro.VaultAward) == 3 &&
                gems.Where(gem => gem != outro.VaultAward).GroupBy(gem => gem).All(group => group.Count() <= 2),
                $"Seed {seed}: the vault did not stop at the first three matching gems.");
            Require(game.Balance == before + SlotGame.JackpotMultipliers[outro.VaultAward] * outro.Bet,
                $"Seed {seed}: the vault did not pay its jackpot.");
            tiers.Add(outro.VaultAward);
            RunToIdle(game, now);
        }
        Require(tiers.Count >= 2, "The vault never paid more than one jackpot tier.");
    }

    private static void CheckBuyAndRefill()
    {
        var game = new SlotGame(5);
        var now = Origin;
        Require(game.HandleAction("slot-buy", now) && game.Balance == 2000 - 20 * SlotGame.BuyMultiplier, "Buy did not charge its price.");
        now = Step(game, now);
        Require(game.Phase == SlotPhase.RespinIntro && game.Snapshot.Grid.Count(cell => cell.IsBonus) == 6,
            "A bought feature did not start respins with six held symbols.");
        RunToIdle(game, now);
        // Buy pays no line wins, so its filler symbols must never show one.
        for (int seed = 0; seed < 2000; seed++)
        {
            var bought = new SlotGame(seed, 1_000_000m);
            bought.HandleAction("slot-buy", Origin);
            var grid = bought.Snapshot.Grid;
            Require(SlotGame.Evaluate(grid, bought.Bet).Count == 0 && grid.Count(cell => cell.IsBonus) == 6,
                "A bought feature showed an unpaid line win or lost a held symbol.");
        }

        var broke = new SlotGame(9, 30);
        now = Origin;
        broke.HandleAction("slot-spin", now);
        now = RunToIdle(broke, now);
        if (broke.Balance < 20)
        {
            Require(broke.AvailableActions().SequenceEqual(["slot-refill"]), "An empty bankroll did not offer only Refill.");
            Require(broke.HandleAction("slot-refill", now) && broke.Balance == 30, "Refill did not restore the bankroll.");
        }
        var poor = new SlotGame(2, 60);
        poor.HandleAction("slot-bet-up", Origin);
        Require(poor.Bet == 40, "Bet + was refused with enough credits.");
        poor.HandleAction("slot-spin", Origin);
        RunToIdle(poor, Origin);
        Require(poor.Balance >= poor.Bet || poor.Balance < 20, "The bet was not lowered to what the bankroll can afford.");
    }

    private static void CheckDeterminism()
    {
        string Play(int seed)
        {
            var game = new SlotGame(seed, 100_000);
            var now = Origin;
            var log = new List<string>();
            for (int spin = 0; spin < 60; spin++)
            {
                game.HandleAction("slot-spin", now);
                now = RunToIdle(game, now);
                log.Add($"{game.Balance}:{string.Concat(game.Snapshot.Grid.Select(cell => (int)cell.Symbol))}");
            }
            return string.Join("|", log);
        }
        Require(Play(42) == Play(42) && Play(42) != Play(43), "Seeded machines are not deterministic.");
    }

    private sealed record Stats(double Rtp, double HitRate, double RespinsEvery, double FreeSpinsEvery, double VaultEvery);

    private static Stats Simulate(int seed, int spins)
    {
        var game = new SlotGame(seed, 1_000_000_000m);
        var now = Origin;
        long hits = 0, respins = 0, free = 0, vaults = 0;
        decimal start = game.Balance;
        for (int spin = 0; spin < spins; spin++)
        {
            game.HandleAction("slot-spin", now);
            bool hit = false;
            while (game.Phase != SlotPhase.Idle)
            {
                now = game.PhaseEndsAt;
                game.Tick(now);
                switch (game.Phase)
                {
                    case SlotPhase.RespinIntro: respins++; hit = true; break;
                    case SlotPhase.FreeSpinsIntro: free++; hit = true; break;
                    case SlotPhase.VaultIntro: vaults++; hit = true; break;
                    case SlotPhase.LineWins: hit = true; break;
                }
            }
            if (hit) hits++;
        }
        decimal wagered = spins * 20m;
        double rtp = (double)((game.Balance - start + wagered) / wagered);
        return new(rtp, hits / (double)spins, spins / (double)Math.Max(1, respins),
            spins / (double)Math.Max(1, free), spins / (double)Math.Max(1, vaults));
    }

    private static double SimulateBuys(int seed, int buys)
    {
        var game = new SlotGame(seed, 1_000_000_000m);
        var now = Origin;
        decimal start = game.Balance;
        for (int buy = 0; buy < buys; buy++)
        {
            game.HandleAction("slot-buy", now);
            while (game.Phase != SlotPhase.Idle)
            {
                now = game.PhaseEndsAt;
                game.Tick(now);
            }
        }
        decimal paid = buys * game.BuyCost;
        return (double)((game.Balance - start + paid) / paid);
    }

    private static DateTimeOffset Step(SlotGame game, DateTimeOffset now)
    {
        now = game.PhaseEndsAt > now ? game.PhaseEndsAt : now;
        Require(game.Tick(now), "The machine did not advance when its phase ended.");
        return now;
    }

    private static DateTimeOffset RunToIdle(SlotGame game, DateTimeOffset now)
    {
        for (int guard = 0; game.Phase != SlotPhase.Idle; guard++)
        {
            Require(guard < 10_000, "The machine never returned to idle.");
            now = Step(game, now);
        }
        return now;
    }

    // The three base rows, top first: T J Q K A are royals, W a wild and C a coin.
    private static SlotCell[] Grid(params string[] rows)
    {
        var grid = Enumerable.Repeat(SlotCell.Empty, SlotGame.Reels * SlotGame.Rows).ToArray();
        for (int row = 0; row < rows.Length; row++)
        {
            var symbols = rows[row].Split(' ');
            for (int reel = 0; reel < SlotGame.Reels; reel++)
                grid[SlotGame.Index(reel, SlotGame.BaseFirstRow + row)] = symbols[reel] switch
                {
                    "T" => new(SlotSymbol.Ten), "J" => new(SlotSymbol.Jack), "Q" => new(SlotSymbol.Queen),
                    "K" => new(SlotSymbol.King), "A" => new(SlotSymbol.Ace), "W" => new(SlotSymbol.Wild),
                    "C" => new(SlotSymbol.Coin, 20), _ => throw new ArgumentException(symbols[reel])
                };
        }
        return grid;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
