using ProjectTabletop.Interaction;

internal static class SlotSpinTimingRegression
{
    private static readonly DateTimeOffset Origin = new(2032, 5, 6, 7, 8, 9, TimeSpan.Zero);

    public static void Run()
    {
        CheckNormalSpin();
        CheckBoughtFeature();
        CheckAutomaticFreeSpins();
        Console.WriteLine("Slot spin timing passed: initial sentinel, stable landing origin through wins/features/idle, " +
            "normal/bought/automatic free-spin starts, injected timestamps and immutable snapshots.");
    }

    private static void CheckNormalSpin()
    {
        bool sawLineWins = false;
        for (int seed = 0; seed < 64 && !sawLineWins; seed++)
        {
            var game = new SlotGame(seed, 1_000_000);
            var initial = game.Snapshot;
            Require(initial.SpinNumber == 0 && initial.SpinStartedAt == DateTimeOffset.MinValue,
                "The opening board has a fictitious spin-start time.");
            Require(game.HandleAction("slot-bet-up", Origin) && game.Snapshot.SpinStartedAt == DateTimeOffset.MinValue,
                "Changing the bet started a presentation clock.");
            var start = Origin.AddSeconds(2);
            Require(game.HandleAction("slot-spin", start), "The normal fixture spin did not start.");
            var spinning = game.Snapshot;
            Require(spinning.SpinStartedAt == start && spinning.PhaseStartedAt == start &&
                spinning.PhaseDuration == SlotGame.SpinDuration, "The normal spin did not expose its injected start time.");
            Require(!game.HandleAction("slot-spin", start.AddMilliseconds(50)) && game.Snapshot.SpinStartedAt == start,
                "A rejected extra Spin changed the landing clock.");
            var now = start;
            for (int guard = 0; game.Phase != SlotPhase.Idle; guard++)
            {
                Require(guard < 2000, "The normal fixture did not return to idle.");
                var previous = game.Snapshot;
                now = Advance(game);
                var current = game.Snapshot;
                if (current.SpinNumber == spinning.SpinNumber)
                    Require(current.SpinStartedAt == start, "A later phase replaced the original reel landing clock.");
                sawLineWins |= current.Phase == SlotPhase.LineWins && current.SpinNumber == spinning.SpinNumber;
                Require(previous.SpinStartedAt <= now && spinning.SpinStartedAt == start &&
                    initial.SpinStartedAt == DateTimeOffset.MinValue, "Updating the game changed an older snapshot's start time.");
            }
            if (game.Snapshot.SpinNumber != spinning.SpinNumber) continue;
            var idle = game.Snapshot;
            Require(idle.SpinStartedAt == start && !game.Tick(now.AddSeconds(1)) && game.Snapshot.SpinStartedAt == start,
                "An idle board lost its last spin's landing origin.");
            var next = now.AddSeconds(2);
            Require(game.HandleAction("slot-spin", next) && game.Snapshot.SpinStartedAt == next &&
                idle.SpinStartedAt == start && spinning.SpinStartedAt == start,
                "The next normal spin failed to replace only its own start time.");
        }
        Require(sawLineWins, "No line-win phase was exercised by the spin continuity regression.");
    }

    private static void CheckBoughtFeature()
    {
        var game = new SlotGame(0, 1_000_000);
        var start = Origin.AddDays(3);
        Require(game.HandleAction("slot-buy", start), "The bought feature did not start.");
        var bought = game.Snapshot;
        Require(bought.SpinStartedAt == start && bought.PhaseStartedAt == start &&
            bought.PhaseDuration == SlotGame.SpinDuration, "Buy did not record its injected spin start.");
        var phases = new HashSet<SlotPhase>();
        var now = start;
        for (int guard = 0; game.Phase != SlotPhase.Idle; guard++)
        {
            Require(guard < 2000, "The bought fixture did not return to idle.");
            now = Advance(game);
            phases.Add(game.Phase);
            Require(game.Snapshot.SpinStartedAt == start && bought.SpinStartedAt == start,
                "A bought feature phase reset the spin origin or mutated its starting snapshot.");
        }
        Require(phases.Contains(SlotPhase.RespinIntro) && phases.Contains(SlotPhase.RespinEffect) &&
            phases.Contains(SlotPhase.RespinOutro), "Buy timing was not checked through its feature presentations.");
        var idle = game.Snapshot;
        var next = now.AddSeconds(1);
        Require(game.HandleAction("slot-buy", next) && game.Snapshot.SpinStartedAt == next && idle.SpinStartedAt == start,
            "A second Buy retained the old origin or changed the previous idle snapshot.");
    }

    private static void CheckAutomaticFreeSpins()
    {
        var game = new SlotGame(11, 1_000_000);
        var start = Origin.AddDays(7);
        game.Demonstrate(SlotDemo.FreeSpins);
        Require(game.HandleAction("slot-spin", start), "The free-spin fixture did not start.");
        DateTimeOffset expectedStart = start;
        int freeStarts = 0;
        for (int guard = 0; game.Phase != SlotPhase.Idle; guard++)
        {
            Require(guard < 2000, "The free-spin fixture did not return to idle.");
            var previous = game.Snapshot;
            var frozenStart = previous.SpinStartedAt;
            var now = Advance(game);
            var current = game.Snapshot;
            if (current.SpinNumber != previous.SpinNumber)
            {
                Require(current.Phase == SlotPhase.Spinning && current.FreeSpin &&
                    current.PhaseDuration == SlotGame.FreeSpinDuration, "An automatic spin lost its free-spin timing.");
                expectedStart = now;
                freeStarts++;
            }
            Require(current.SpinStartedAt == expectedStart && previous.SpinStartedAt == frozenStart,
                "An automatic free spin used a deadline/wall clock or changed a previous snapshot.");
        }
        Require(freeStarts >= SlotGame.FreeSpinsAwarded && game.Snapshot.SpinStartedAt == expectedStart,
            "Free-spin origins were not retained through the final outro and idle.");
    }

    private static DateTimeOffset Advance(SlotGame game)
    {
        // Deliberate callback delay distinguishes actual injected start time
        // from the prior phase's scheduled deadline.
        var now = game.PhaseEndsAt.AddMilliseconds(137);
        Require(game.Tick(now), "The fixture did not advance at the injected time.");
        return now;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
