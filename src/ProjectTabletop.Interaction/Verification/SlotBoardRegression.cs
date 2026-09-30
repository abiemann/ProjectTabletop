using ProjectTabletop.Interaction;

internal static class SlotBoardRegression
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckLayout();
        CheckLongPressSpin();
        CheckExitDuringFeature();
        CheckSafetyAndGestures();
        Console.WriteLine("Slot board verification passed: bottom-row long-press controls with clear gaps, a one-second Spin, " +
            "controls locked while reels turn, fingers left on Spin never re-spin, Exit during features, bets, Buy, " +
            "the one-caption safety rule and ignored pinches.");
    }

    private static void CheckLayout()
    {
        var board = new BoardSession(slots: new SlotGame(1));
        board.ShowSlots(Origin);
        var buttons = board.Buttons;
        Require(buttons.Select(button => button.Id).SequenceEqual(["slot-exit", "slot-bet-down", "slot-bet-up", "slot-buy", "slot-spin"]),
            "The slot controls are not Exit, Bet -, Bet +, Buy and Spin in order.");
        Require(buttons.All(button => button.Hold == BoardButtonHold.Once && button.Bounds.Y >= .85 &&
            button.Bounds.Y + button.Bounds.Height < 1 && button.Bounds.X > 0 && button.Bounds.X + button.Bounds.Width < 1 &&
            button.Bounds.Height >= .11), "A slot control is not a tall long-press button on the viewer's edge row.");
        for (int index = 1; index < buttons.Count; index++)
            Require(buttons[index].Bounds.X - (buttons[index - 1].Bounds.X + buttons[index - 1].Bounds.Width) >= .015,
                "Neighbouring slot controls are too close for the one-caption safety rule.");
        Require(buttons.Single(button => button.Id == "slot-spin").Bounds.Width >= .2, "Spin is not the widest control.");
        Require(buttons.Single(button => button.Id == "slot-bet-down") is { Enabled: false } &&
            buttons.Single(button => button.Id == "slot-bet-up") is { Enabled: true },
            "Bet - or Bet + was offered wrongly at the minimum bet.");
    }

    private static void CheckLongPressSpin()
    {
        var game = new SlotGame(4);
        var board = new BoardSession(slots: game);
        board.ShowSlots(Origin);
        var now = Origin;
        for (int held = 0; held < 1000; held += 100)
            Require(Hold(board, "slot-spin", now.AddMilliseconds(held)).Count == 0, "Spin acted before a full second.");
        now = now.AddMilliseconds(1000);
        Require(Hold(board, "slot-spin", now).SequenceEqual(["slot-spin"]) && game.Phase == SlotPhase.Spinning &&
            game.Balance == SlotGame.StartingBalance - 20, "A one-second hold did not spin once for the bet.");
        Require(board.Buttons.Where(button => button.Id != "slot-exit").All(button => !button.Enabled) &&
            board.Buttons.Single(button => button.Id == "slot-exit").Enabled,
            "Controls other than Exit stayed enabled while the reels turned.");
        // Fingers stay on Spin through the whole round: it must not spin again.
        long spins = game.Snapshot.SpinNumber;
        for (int guard = 0; game.Phase != SlotPhase.Idle; guard++)
        {
            Require(guard < 200, "The spin never settled.");
            now = now.AddMilliseconds(100);
            Hold(board, "slot-spin", now);
        }
        for (int held = 0; held < 2500; held += 100)
            Require(Hold(board, "slot-spin", now.AddMilliseconds(held)).Count == 0 && game.Snapshot.SpinNumber == spins,
                "Fingers left resting on Spin started another spin.");
        now = now.AddMilliseconds(2500);
        // Lifting for more than 350 ms re-arms it; another full second spins again.
        for (int gap = 0; gap <= 400; gap += 100) Hold(board, "slot-spin", now.AddMilliseconds(gap), []);
        now = now.AddMilliseconds(500);
        for (int held = 0; held < 1000; held += 100) Hold(board, "slot-spin", now.AddMilliseconds(held));
        Require(Hold(board, "slot-spin", now.AddMilliseconds(1000)).SequenceEqual(["slot-spin"]) &&
            game.Snapshot.SpinNumber == spins + 1, "Lifting and holding Spin again did not spin.");
        now = RunToIdle(board, game, now.AddMilliseconds(1000));

        // Bet + and Buy act once each through their own long press.
        now = LongPress(board, "slot-bet-up", now);
        Require(game.Bet == 40, "Bet + did not raise the bet.");
        now = LongPress(board, "slot-bet-down", now);
        Require(game.Bet == 20, "Bet - did not lower the bet.");
        decimal before = game.Balance;
        now = LongPress(board, "slot-buy", now);
        Require(game.Balance == before - 20 * SlotGame.BuyMultiplier && game.Phase == SlotPhase.Spinning,
            "Buy did not charge its price and start the feature.");
        RunToIdle(board, game, now);
    }

    private static void CheckExitDuringFeature()
    {
        var game = new SlotGame(8);
        var board = new BoardSession(slots: game);
        board.ShowSlots(Origin);
        game.Demonstrate(SlotDemo.Respins);
        var now = LongPress(board, "slot-spin", Origin);
        while (game.Phase != SlotPhase.RespinIntro)
        {
            now = game.PhaseEndsAt;
            board.TickSlots(now);
        }
        now = LongPress(board, "slot-exit", now.AddMilliseconds(400));
        Require(board.Screen == BoardScreen.Menu, "Exit did not leave during Dragonfire Respins.");
        // The machine keeps its state and resumes when reopened.
        board.ShowSlots(now);
        Require(game.Snapshot.InRespins, "Reopening the slot machine lost the running feature.");
        RunToIdle(board, game, now);
    }

    private static void CheckSafetyAndGestures()
    {
        var game = new SlotGame(12);
        var board = new BoardSession(slots: game);
        board.ShowSlots(Origin);
        var now = Origin;
        // A hand covering two captions activates nothing and restarts the hold.
        for (int held = 0; held <= 1500; held += 100)
            Require(board.ObserveHeldButtons(["slot-spin", "slot-buy"], now.AddMilliseconds(held), now.AddMilliseconds(held)).Count == 0,
                "Two covered captions activated a slot control.");
        now = now.AddMilliseconds(1600);
        for (int held = 0; held < 1000; held += 100)
            Require(Hold(board, "slot-spin", now.AddMilliseconds(held)).Count == 0,
                "The hold did not restart after an ambiguous two-caption frame.");
        Require(Hold(board, "slot-spin", now.AddMilliseconds(1000)).SequenceEqual(["slot-spin"]),
            "Spin did not act after a full second as the only covered caption.");
        now = RunToIdle(board, game, now.AddMilliseconds(1000));

        // Pinches and four-finger gestures never press a long-press slot control.
        var spin = board.Buttons.Single(button => button.Id == "slot-spin");
        long spins = game.Snapshot.SpinNumber;
        var sample = new BoardHandSample(spin.Bounds.X + spin.Bounds.Width / 2, spin.Bounds.Y + spin.Bounds.Height / 2,
            now.AddMilliseconds(1100), 99);
        Require(board.Update([sample], now.AddMilliseconds(100), now.AddMilliseconds(100)) is null &&
            game.Snapshot.SpinNumber == spins, "A pinch pressed the long-press Spin.");
    }

    private static IReadOnlyList<string> Hold(BoardSession board, string id, DateTimeOffset now, string[]? held = null) =>
        board.ObserveHeldButtons(held ?? [id], now, now);

    private static DateTimeOffset LongPress(BoardSession board, string id, DateTimeOffset now)
    {
        // Lift first so a previously spent place re-arms.
        for (int gap = 0; gap <= 400; gap += 100) board.ObserveHeldButtons([], now.AddMilliseconds(gap), now.AddMilliseconds(gap));
        now = now.AddMilliseconds(500);
        for (int held = 0; held < 1000; held += 100) board.ObserveHeldButtons([id], now.AddMilliseconds(held), now.AddMilliseconds(held));
        Require(board.ObserveHeldButtons([id], now.AddMilliseconds(1000), now.AddMilliseconds(1000)).SequenceEqual([id]),
            $"A one-second hold on {id} did not act.");
        return now.AddMilliseconds(1000);
    }

    private static DateTimeOffset RunToIdle(BoardSession board, SlotGame game, DateTimeOffset now)
    {
        board.ShowSlots(now);
        for (int guard = 0; game.Phase != SlotPhase.Idle; guard++)
        {
            Require(guard < 10_000, "The slot machine never returned to idle.");
            now = game.PhaseEndsAt > now ? game.PhaseEndsAt : now;
            board.TickSlots(now);
        }
        return now;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
