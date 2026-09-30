using ProjectTabletop.Interaction;

internal static class SlotDragonHatchRegression
{
    private static readonly DateTimeOffset Origin = new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);

    public static void Run()
    {
        CheckPowerTimelineAndRetention();
        CheckInsufficientTriggers();
        CheckFreeSpinReset();
        CheckDeterministicClock();
        Console.WriteLine("Dragon hatch verification passed: first-power timestamps, all egg colours, staged rainbow, " +
            "repeat suppression, immutable history, outro/idle retention, spin/buy/free-spin reset and injected clock.");
    }

    private static void CheckPowerTimelineAndRetention()
    {
        var colours = new HashSet<SlotSymbol>();
        bool rainbow = false, repeated = false, spinReset = false, buyReset = false;
        for (int seed = 0; seed < 128 && (colours.Count < 3 || !rainbow || !repeated || !spinReset || !buyReset); seed++)
        {
            var game = new SlotGame(seed, 1_000_000);
            var now = Origin.AddHours(seed);
            game.Demonstrate(SlotDemo.Respins);
            Require(game.HandleAction("slot-spin", now), "The fixture spin did not start.");
            Require(game.Snapshot.DragonHatches.Count == 0, "A result's eggs hatched before its reels landed.");
            while (game.Phase != SlotPhase.RespinIntro) now = Advance(game);
            var intro = game.Snapshot;
            Require(intro.DragonHatches.Count == 0, "Queued powers hatched during the respin introduction.");
            bool onlyRainbow = intro.Grid.Count(cell => cell.IsEgg) == 1 &&
                intro.Grid.Any(cell => cell.Symbol == SlotSymbol.EggRainbow);
            var firstHatches = new List<SlotDragonHatch>();
            var initialEffects = new List<SlotEffect>();
            for (int guard = 0; game.Phase != SlotPhase.RespinOutro; guard++)
            {
                Require(guard < 1000, "The fixture never finished its respins.");
                var previous = game.Snapshot;
                var frozen = previous.DragonHatches.ToArray();
                // A delayed callback must report the time it really applied the
                // power, rather than an earlier deadline or the system clock.
                now = game.PhaseEndsAt.AddMilliseconds(73);
                Require(!game.Tick(game.PhaseEndsAt.AddTicks(-1)) && previous.DragonHatches.SequenceEqual(frozen),
                    "A power was revealed before its presentation phase ended.");
                Require(game.Tick(now), "The fixture did not advance at the injected clock time.");
                var current = game.Snapshot;
                if (current.Phase == SlotPhase.RespinEffect)
                {
                    var effectCell = current.EffectCell!.Value;
                    var egg = current.Cell(effectCell.Reel, effectCell.Row).Symbol;
                    if (egg != SlotSymbol.EggRainbow)
                    {
                        var expected = egg switch
                        {
                            SlotSymbol.EggGreen => SlotEffect.Expand,
                            SlotSymbol.EggBlue => SlotEffect.Boost,
                            SlotSymbol.EggRed => SlotEffect.Collect,
                            _ => SlotEffect.None
                        };
                        Require(current.Effect == expected && expected != SlotEffect.None, "An egg hatched the wrong dragon power.");
                        colours.Add(egg);
                    }
                    if (firstHatches.Any(hatch => hatch.Power == current.Effect)) repeated = true;
                    else firstHatches.Add(new(current.Effect, now));
                    if (onlyRainbow && initialEffects.Count < 3)
                    {
                        initialEffects.Add(current.Effect);
                        Require(current.DragonHatches.Count == initialEffects.Count,
                            "Rainbow revealed a later dragon before that power executed.");
                    }
                    Require(current.PhaseDuration == SlotGame.RespinEffectDuration, "Hatching changed power presentation timing.");
                }
                Require(current.DragonHatches.SequenceEqual(firstHatches),
                    "A dragon was omitted, revealed early, duplicated, or had its first hatch time restarted.");
                Require(current.DragonHatches.All(hatch => hatch.HatchedAt <= now), "A snapshot contains a future dragon reveal.");
                Require(previous.DragonHatches.SequenceEqual(frozen), "Updating the game changed an earlier snapshot's hatch history.");
                Require(!game.Tick(now.AddTicks(-1)) && game.Snapshot.DragonHatches.SequenceEqual(firstHatches),
                    "A backwards clock changed hatch history.");
            }
            if (onlyRainbow)
            {
                Require(initialEffects.SequenceEqual([SlotEffect.Expand, SlotEffect.Boost, SlotEffect.Collect]),
                    "Rainbow did not hatch its three powers in their executed order.");
                rainbow = true;
            }
            Require(firstHatches.Count > 0, "A successful respin feature never hatched a dragon.");
            var outro = game.Snapshot;
            Require(outro.DragonHatches.SequenceEqual(firstHatches), "Hatches were lost before the respin win presentation.");
            while (game.Phase != SlotPhase.Idle) now = Advance(game);
            // A naturally triggered free-spin sequence can start another spin;
            // that is deliberately covered by the separate reset regression.
            if (game.Snapshot.SpinNumber != intro.SpinNumber) continue;
            Require(game.Snapshot.DragonHatches.SequenceEqual(firstHatches), "Hatched dragons disappeared upon returning to idle.");
            Require(!game.Tick(now.AddMinutes(1)) && game.Snapshot.DragonHatches.SequenceEqual(firstHatches),
                "Idle wall time cleared or restarted a dragon.");
            var idle = game.Snapshot;
            if (idle.DragonHatches is IList<SlotDragonHatch> list)
            {
                bool rejected = false;
                try { list[0] = new(SlotEffect.None, now); }
                catch (NotSupportedException) { rejected = true; }
                Require(rejected && game.Snapshot.DragonHatches.SequenceEqual(firstHatches),
                    "A caller could mutate the snapshot's hatch history.");
            }
            Require(game.HandleAction(seed % 2 == 0 ? "slot-spin" : "slot-buy", now.AddMinutes(1)), "The next spin did not start.");
            Require(game.Snapshot.DragonHatches.Count == 0 && idle.DragonHatches.SequenceEqual(firstHatches) &&
                outro.DragonHatches.SequenceEqual(firstHatches), "Spin/Buy failed to reset dragons independently of prior snapshots.");
            if (seed % 2 == 0) spinReset = true;
            else buyReset = true;
        }
        Require(colours.SetEquals([SlotSymbol.EggGreen, SlotSymbol.EggBlue, SlotSymbol.EggRed]) && rainbow && repeated && spinReset && buyReset,
            "The fixtures did not cover all three egg colours, staged rainbow, a repeated power and both reset actions.");
    }

    private static void CheckInsufficientTriggers()
    {
        bool eggWithoutCoins = false, coinsWithoutEgg = false;
        for (int seed = 0; seed < 512 && (!eggWithoutCoins || !coinsWithoutEgg); seed++)
        {
            var game = new SlotGame(seed, 1_000_000);
            game.HandleAction("slot-spin", Origin);
            var spin = game.Snapshot;
            int eggs = spin.Grid.Count(cell => cell.IsEgg), coins = spin.Grid.Count(cell => cell.Symbol == SlotSymbol.Coin);
            bool tooFewCoins = eggs > 0 && coins < 3, noEgg = eggs == 0 && coins >= 3;
            if (!tooFewCoins && !noEgg) continue;
            while (game.Phase != SlotPhase.Idle)
            {
                Require(game.Snapshot.DragonHatches.Count == 0, "An insufficient egg/coin trigger hatched a dragon.");
                Advance(game);
                if (game.Snapshot.SpinNumber != spin.SpinNumber) break;
            }
            Require(game.Snapshot.DragonHatches.Count == 0, "A non-triggering spin retained a phantom dragon.");
            eggWithoutCoins |= tooFewCoins;
            coinsWithoutEgg |= noEgg;
        }
        Require(eggWithoutCoins && coinsWithoutEgg, "Insufficient-trigger fixtures did not exercise both missing requirements.");
    }

    private static void CheckFreeSpinReset()
    {
        for (int seed = 0; seed < 128; seed++)
        {
            var game = new SlotGame(seed, 1_000_000);
            game.Demonstrate(SlotDemo.FreeSpins);
            game.HandleAction("slot-spin", Origin);
            for (int guard = 0; game.Phase != SlotPhase.Idle && guard < 2000; guard++)
            {
                var previous = game.Snapshot;
                Advance(game);
                var current = game.Snapshot;
                if (current.Phase == SlotPhase.Spinning && current.FreeSpin && previous.DragonHatches.Count > 0)
                {
                    Require(current.DragonHatches.Count == 0 && previous.DragonHatches.Count > 0,
                        "An automatic free spin failed to clear the prior spin's dragons.");
                    return;
                }
            }
        }
        throw new InvalidOperationException("No automatic free-spin reset after a hatch was exercised.");
    }

    private static void CheckDeterministicClock()
    {
        var first = new SlotGame(0, 1_000_000);
        var second = new SlotGame(0, 1_000_000);
        var offset = TimeSpan.FromDays(17);
        first.Demonstrate(SlotDemo.Respins);
        second.Demonstrate(SlotDemo.Respins);
        first.HandleAction("slot-spin", Origin);
        second.HandleAction("slot-spin", Origin + offset);
        while (first.Phase != SlotPhase.Idle)
        {
            DateTimeOffset now = first.PhaseEndsAt.AddMilliseconds(117);
            Require(first.Tick(now) && second.Tick(now + offset), "The identical seeded clocks diverged.");
            var a = first.Snapshot;
            var b = second.Snapshot;
            Require(a.Phase == b.Phase && a.Grid.SequenceEqual(b.Grid) && a.Balance == b.Balance &&
                a.DragonHatches.SequenceEqual(b.DragonHatches.Select(hatch => hatch with { HatchedAt = hatch.HatchedAt - offset })),
                "Dragon timestamps used wall time, changed game randomness, or depended on the absolute clock date.");
        }
    }

    private static DateTimeOffset Advance(SlotGame game)
    {
        var now = game.PhaseEndsAt;
        Require(game.Tick(now), "The fixture did not advance at its phase deadline.");
        return now;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
