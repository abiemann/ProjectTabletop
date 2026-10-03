using ProjectTabletop.Interaction;

internal static class RouletteBoardRegression
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 3, 13, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckLayoutAndBetSelection();
        CheckHoldsAndReadiness();
        CheckExitAndResume();
        Console.WriteLine("Roulette board verification passed: 49 disjoint betting targets, four chip denominations, " +
            "five bottom caption holds, gesture betting but no gesture Spin, fresh clear-caption readiness, " +
            "ambiguous/released hold rejection, busy controls, navigation retention and exactly-once resumed settlement.");
    }

    private static void CheckLayoutAndBetSelection()
    {
        var game = new RouletteGame(1);
        var board = new BoardSession(roulette: game);
        board.ShowRoulette(Origin);
        Require(board.Title == "Roulette" && board.Buttons.Count == 58, "Roulette controls or title are incomplete.");
        var buttons = board.Buttons;
        Require(buttons.Select(button => button.Id).Distinct().Count() == buttons.Count &&
            buttons.All(button => button.Bounds.X > 0 && button.Bounds.Y > 0 &&
                button.Bounds.X + button.Bounds.Width < 1 && button.Bounds.Y + button.Bounds.Height < 1),
            "A roulette target is duplicated or leaves the board.");
        for (int first = 0; first < buttons.Count; first++)
            for (int second = first + 1; second < buttons.Count; second++)
                Require(!Overlaps(buttons[first].Bounds, buttons[second].Bounds),
                    "Roulette controls overlap: " + buttons[first].Id + " / " + buttons[second].Id);
        var holds = buttons.Where(button => button.IsHold).ToArray();
        Require(holds.Select(button => button.Id).SequenceEqual(
            ["roulette-exit", "roulette-undo", "roulette-clear", "roulette-rebet", "roulette-spin"]) &&
            holds.All(button => button.Hold == BoardButtonHold.Once && button.Bounds.Y == .855),
            "Roulette footer controls do not share the one-second single-action rule.");
        for (int index = 1; index < holds.Length; index++)
            Require(holds[index].Bounds.X - holds[index - 1].Bounds.X - holds[index - 1].Bounds.Width >= .019,
                "Roulette hold captions do not have clear hand gaps.");
        Require(RouletteGame.BetTargets.All(target => !buttons.Single(button => button.Id == target.Id).IsHold),
            "A tiny betting cell requires an ambiguous caption hold.");
        var one = BoardSession.RouletteBetBounds(new(RouletteBetKind.Straight, 1));
        var three = BoardSession.RouletteBetBounds(new(RouletteBetKind.Straight, 3));
        var thirtySix = BoardSession.RouletteBetBounds(new(RouletteBetKind.Straight, 36));
        Require(one.X == three.X && one.Y > three.Y && thirtySix.Y == three.Y && thirtySix.X > three.X,
            "The conventional three-row betting table is transposed or reversed.");
        Require(!board.ActivateButton("roulette-spin", Origin.AddMilliseconds(100)), "The board enabled Spin without a wager.");
        var betTime = Origin.AddMilliseconds(200);
        Require(board.ActivateButton("roulette-number-17", betTime) && game.TotalBet == 5 && game.Balance == 995,
            "Pointer selection did not reserve a straight-number chip.");
        var red = board.Buttons.Single(button => button.Id == "roulette-red").Bounds;
        var gestureAt = Origin.AddMilliseconds(400);
        var gesture = new BoardHandSample(red.X + red.Width / 2, red.Y + red.Height / 2, gestureAt.AddSeconds(1), 1);
        Require(board.Update([gesture], gestureAt, gestureAt)?.ButtonId == "roulette-red" && game.TotalBet == 10,
            "A fresh gesture could not place an outside bet.");
        Require(board.Update([gesture], gestureAt.AddMilliseconds(100), gestureAt.AddMilliseconds(100)) is null && game.TotalBet == 10,
            "A held betting gesture placed duplicate chips.");
        var spin = BoardSession.RouletteSpinBounds;
        var spinAt = Origin.AddMilliseconds(600);
        var pinchSpin = new BoardHandSample(spin.X + spin.Width / 2, spin.Y + spin.Height / 2, spinAt.AddSeconds(1), 2);
        Require(board.Update([pinchSpin], spinAt, spinAt) is null && game.Phase == RoulettePhase.Betting,
            "A pinch bypassed the Spin caption hold.");
    }

    private static void CheckHoldsAndReadiness()
    {
        var game = new RouletteGame(7);
        var board = new BoardSession(roulette: game);
        board.ShowRoulette(Origin);
        Require(board.ActivateButton("roulette-red", Origin.AddMilliseconds(50)), "Hold fixture could not place its wager.");
        for (int time = 100; time <= 1300; time += 100)
            Require(Observe(time, ["roulette-spin"], []).Count == 0, "An unarmed caption started Roulette.");
        Observe(1400, [], ["roulette-spin", "roulette-clear"]);
        for (int time = 1500; time <= 2700; time += 100)
            Require(Observe(time, ["roulette-spin", "roulette-clear"], []).Count == 0,
                "Two covered captions started Roulette or cleared its wagers.");
        for (int time = 2800; time <= 3200; time += 100)
            Require(Observe(time, ["roulette-spin"], []).Count == 0, "A partial Roulette hold activated.");
        Observe(3300, [], ["roulette-spin"]);
        Require(board.HoldProgress(Origin.AddMilliseconds(3300)).Count == 0, "Intact letters did not cancel partial Roulette progress.");
        for (int time = 3400; time < 4400; time += 100)
            Require(Observe(time, ["roulette-spin"], []).Count == 0, "Roulette spun before its full fresh second.");
        Require(Observe(4400, ["roulette-spin"], []).SequenceEqual(["roulette-spin"]) && game.Phase == RoulettePhase.Spinning,
            "A one-second caption hold did not commit Roulette.");
        Require(board.Buttons.Where(button => button.Enabled).Select(button => button.Id).SequenceEqual(["roulette-exit"]),
            "A betting action remained available while the wheel was spinning.");
        var end = game.PhaseEndsAt;
        for (var now = Origin.AddMilliseconds(4500); now <= end.AddMilliseconds(500); now += TimeSpan.FromMilliseconds(100))
            Require(board.ObserveHeldButtons(["roulette-spin"], now, now, []).Count == 0,
                "Resting fingers repeated Spin across settlement.");
        Require(game.Snapshot.RoundNumber == 1 && game.Snapshot.History.Count == 1 && game.Phase == RoulettePhase.Betting,
            "Held input restarted or failed to settle the first round.");
        Require(board.ActivateButton("roulette-red", end.AddSeconds(1)), "Could not place a post-settlement wager.");
        for (int time = 1100; time <= 2300; time += 100)
        {
            var now = end.AddMilliseconds(time);
            Require(board.ObserveHeldButtons(["roulette-spin"], now, now, []).Count == 0,
                "Old covered-caption evidence armed a later wager.");
        }
        var clearedAt = end.AddMilliseconds(2400);
        for (int time = 0; time <= 400; time += 100)
            board.ObserveHeldButtons([], clearedAt.AddMilliseconds(time), clearedAt.AddMilliseconds(time), ["roulette-spin"]);
        var started = clearedAt.AddMilliseconds(500);
        for (int time = 0; time < 1000; time += 100)
            Require(board.ObserveHeldButtons(["roulette-spin"], started.AddMilliseconds(time), started.AddMilliseconds(time), []).Count == 0,
                "Fresh Roulette hold activated early after release.");
        Require(board.ObserveHeldButtons(["roulette-spin"], started.AddSeconds(1), started.AddSeconds(1), []).SequenceEqual(["roulette-spin"]) &&
            game.Snapshot.RoundNumber == 2, "A released and freshly held Spin did not start the next round.");

        IReadOnlyList<string> Observe(int milliseconds, string[] held, string[] cleared) =>
            board.ObserveHeldButtons(held, Origin.AddMilliseconds(milliseconds), Origin.AddMilliseconds(milliseconds), cleared);
    }

    private static void CheckExitAndResume()
    {
        var game = new RouletteGame(12);
        var board = new BoardSession(roulette: game);
        board.ShowRoulette(Origin);
        Require(board.ActivateButton("roulette-red", Origin.AddMilliseconds(100)) &&
            board.ActivateButton("roulette-spin", Origin.AddMilliseconds(200)), "Exit fixture could not start.");
        var active = game.Snapshot;
        Require(board.ActivateButton("roulette-exit", Origin.AddSeconds(1)) && board.Screen == BoardScreen.Menu,
            "Exit was unavailable during a spin.");
        var returnedAt = Origin.AddSeconds(12);
        Require(!board.TickRoulette(returnedAt) && game.Snapshot == active, "An inactive board was changed by a background presentation tick.");
        board.ShowRoulette(returnedAt);
        Require(board.TickRoulette(returnedAt) && game.Snapshot.Outcome == active.Outcome &&
            game.Snapshot.RoundStartedAt == active.RoundStartedAt && game.Snapshot.History.Count == 1,
            "Returning to a spin lost, rerolled or failed to settle its pending wager.");
        decimal expected = active.Balance + RouletteGame.Payout(new(RouletteBetKind.Red), 5, active.Outcome!.Value);
        Require(game.Balance == expected && !board.TickRoulette(returnedAt.AddSeconds(1)) && game.Balance == expected,
            "Navigation settled the same wager more than once.");
        Require(board.ActivateButton("roulette-rebet", returnedAt.AddSeconds(2)) && game.TotalBet == 5,
            "A completed round could not restore its previous slip.");
    }

    private static bool Overlaps(BoardRect a, BoardRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
