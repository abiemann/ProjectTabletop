using ProjectTabletop.Interaction;

internal static class SlotBoardRegression
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckLayout();
        CheckPointerPressPresentation();
        CheckLongPressSpin();
        CheckInterruptedSpinHold();
        CheckClearedCaptionHolds();
        CheckHoldCaptionReadiness();
        CheckPointerConsumesCaptionReadiness();
        CheckExitDuringFeature();
        CheckSafetyAndGestures();
        Console.WriteLine("Slot board verification passed: bottom-row long-press controls with clear gaps, a one-second Spin, " +
            "controls locked while reels turn, fingers left on Spin never re-spin, Exit during features, bets, Buy, " +
            "fresh enabled-caption readiness, the one-caption safety rule, ignored pinches and accepted-press presentation records.");
    }

    private static void CheckLayout()
    {
        var board = new BoardSession(slots: new SlotGame(1));
        board.ShowSlots(Origin);
        Require(board.SlotsLastButtonPress is null, "Opening Slots invented a player press.");
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

    private static void CheckPointerPressPresentation()
    {
        var game = new SlotGame(15);
        var board = new BoardSession(slots: game);
        board.ShowSlots(Origin);
        Require(!board.ActivateButton("slot-bet-down", Origin.AddMilliseconds(100)) && board.SlotsLastButtonPress is null,
            "A disabled pointer action invented a press.");
        var now = Origin.AddMilliseconds(200);
        Require(board.ActivateButton("slot-bet-up", now), "The pointer could not raise the bet.");
        var first = RequirePress(board, "slot-bet-up", BoardSession.SlotBetUpBounds, now, 1);
        Require(!board.ActivateButton("slot-bet-down", now.AddMilliseconds(-1)) && ReferenceEquals(board.SlotsLastButtonPress, first),
            "A rejected stale action replaced the last accepted press.");
        now = now.AddMilliseconds(100);
        Require(board.ActivateButton("slot-bet-down", now), "The pointer could not lower the bet.");
        var second = RequirePress(board, "slot-bet-down", BoardSession.SlotBetDownBounds, now, 2);
        Require(first.ButtonId == "slot-bet-up" && first.StartedAt == Origin.AddMilliseconds(200) && first.Sequence == 1,
            "A subsequent press mutated an earlier presentation record.");
        now = now.AddMilliseconds(100);
        Require(board.ActivateButton("slot-exit", now) && ReferenceEquals(board.SlotsLastButtonPress, second),
            "Exit replaced the last gameplay-button press.");
        now = now.AddMilliseconds(100);
        board.ShowSlots(now);
        Require(ReferenceEquals(board.SlotsLastButtonPress, second), "Reopening Slots reset its press sequence.");
        now = now.AddMilliseconds(100);
        Require(board.ActivateButton("slot-buy", now), "The pointer could not buy the feature.");
        var bought = RequirePress(board, "slot-buy", BoardSession.SlotBuyBounds, now, 3);
        Require(!board.ActivateButton("slot-spin", now.AddMilliseconds(100)) && ReferenceEquals(board.SlotsLastButtonPress, bought),
            "A disabled Spin replaced the Buy press.");
        now = RunToIdle(board, game, now.AddMilliseconds(100));
        Require(ReferenceEquals(board.SlotsLastButtonPress, bought), "Feature phase changes invented another button press.");
        board.DemonstrateSlots(SlotDemo.FreeSpins);
        now = now.AddMilliseconds(100);
        Require(board.ActivateButton("slot-spin", now), "The pointer could not start the free-spin demonstration.");
        var spin = RequirePress(board, "slot-spin", BoardSession.SlotSpinBounds, now, 4);
        long spinNumber = game.Snapshot.SpinNumber;
        RunToIdle(board, game, now);
        Require(game.Snapshot.SpinNumber > spinNumber && ReferenceEquals(board.SlotsLastButtonPress, spin),
            "Automatic free spins replaced the player's last press.");
    }

    private static void CheckLongPressSpin()
    {
        var game = new SlotGame(4);
        var board = new BoardSession(slots: game);
        board.ShowSlots(Origin);
        var now = Origin;
        for (int held = 0; held < 1000; held += 100)
            Require(Hold(board, "slot-spin", now.AddMilliseconds(held)).Count == 0 && board.SlotsLastButtonPress is null,
                "Spin or its press presentation acted before a full second.");
        now = now.AddMilliseconds(1000);
        Require(Hold(board, "slot-spin", now).SequenceEqual(["slot-spin"]) && game.Phase == SlotPhase.Spinning &&
            game.Balance == SlotGame.StartingBalance - 20, "A one-second hold did not spin once for the bet.");
        var firstPress = RequirePress(board, "slot-spin", BoardSession.SlotSpinBounds, now, 1);
        // Replacing a rendered reference must not lift fingers from a spent place.
        board.ResetHoldCaptionEvidence();
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
            Require(Hold(board, "slot-spin", now.AddMilliseconds(held)).Count == 0 && game.Snapshot.SpinNumber == spins &&
                ReferenceEquals(board.SlotsLastButtonPress, firstPress),
                "Fingers left resting on Spin started another spin.");
        now = now.AddMilliseconds(2500);
        // Lifting for more than 350 ms re-arms it; another full second spins again.
        for (int gap = 0; gap <= 400; gap += 100) Hold(board, "slot-spin", now.AddMilliseconds(gap), []);
        now = now.AddMilliseconds(500);
        for (int held = 0; held < 1000; held += 100) Hold(board, "slot-spin", now.AddMilliseconds(held));
        Require(Hold(board, "slot-spin", now.AddMilliseconds(1000)).SequenceEqual(["slot-spin"]) &&
            game.Snapshot.SpinNumber == spins + 1, "Lifting and holding Spin again did not spin.");
        RequirePress(board, "slot-spin", BoardSession.SlotSpinBounds, now.AddMilliseconds(1000), 2);
        now = RunToIdle(board, game, now.AddMilliseconds(1000));

        // Bet + and Buy act once each through their own long press.
        now = LongPress(board, "slot-bet-up", now);
        Require(game.Bet == 40, "Bet + did not raise the bet.");
        RequirePress(board, "slot-bet-up", BoardSession.SlotBetUpBounds, now, 3);
        now = LongPress(board, "slot-bet-down", now);
        Require(game.Bet == 20, "Bet - did not lower the bet.");
        RequirePress(board, "slot-bet-down", BoardSession.SlotBetDownBounds, now, 4);
        decimal before = game.Balance;
        now = LongPress(board, "slot-buy", now);
        Require(game.Balance == before - 20 * SlotGame.BuyMultiplier && game.Phase == SlotPhase.Spinning,
            "Buy did not charge its price and start the feature.");
        RequirePress(board, "slot-buy", BoardSession.SlotBuyBounds, now, 5);
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
            Require(board.ObserveHeldButtons(["slot-spin", "slot-buy"], now.AddMilliseconds(held), now.AddMilliseconds(held)).Count == 0 &&
                board.SlotsLastButtonPress is null,
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
        var lastPress = board.SlotsLastButtonPress;
        long spins = game.Snapshot.SpinNumber;
        var sample = new BoardHandSample(spin.Bounds.X + spin.Bounds.Width / 2, spin.Bounds.Y + spin.Bounds.Height / 2,
            now.AddMilliseconds(1100), 99);
        Require(board.Update([sample], now.AddMilliseconds(100), now.AddMilliseconds(100)) is null &&
            game.Snapshot.SpinNumber == spins && ReferenceEquals(board.SlotsLastButtonPress, lastPress),
            "A pinch pressed the long-press Spin.");
    }

    private static void CheckInterruptedSpinHold()
    {
        foreach (int gap in new[] { 351, 1500 })
        {
            var game = new SlotGame(4);
            var board = new BoardSession(slots: game);
            board.ShowSlots(Origin);
            for (int held = 0; held <= 400; held += 100)
                Require(Hold(board, "slot-spin", Origin.AddMilliseconds(held)).Count == 0,
                    "The interrupted Spin fixture activated before its gap.");
            var returned = Origin.AddMilliseconds(400 + gap);
            Require(board.HoldProgress(returned).Count == 0,
                "A Spin rim remained visible after its camera evidence expired.");
            Require(board.ObserveHeldButtons(["slot-spin"], Origin.AddMilliseconds(400), returned).Count == 0,
                "A stale positive frame activated Spin during a camera gap.");
            Require(Hold(board, "slot-spin", returned).Count == 0 && game.Snapshot.SpinNumber == 0 &&
                board.SlotsLastButtonPress is null && board.HoldProgress(returned).Count == 0,
                "A returning positive frame revived expired Spin progress or activated immediately.");
            Require(board.ObserveHeldButtons(["slot-spin"], returned, returned.AddMilliseconds(50)).Count == 0 &&
                board.ObserveHeldButtons(["slot-spin"], returned.AddMilliseconds(-1), returned.AddMilliseconds(50)).Count == 0,
                "A repeated or out-of-order frame advanced the restarted Spin hold.");
            for (int held = 100; held < 1000; held += 100)
                Require(Hold(board, "slot-spin", returned.AddMilliseconds(held)).Count == 0 &&
                    game.Snapshot.SpinNumber == 0 && board.SlotsLastButtonPress is null,
                    "Pre-gap evidence shortened the restarted Spin hold.");
            var activated = returned.AddMilliseconds(1000);
            Require(Hold(board, "slot-spin", activated).SequenceEqual(["slot-spin"]) &&
                game.Snapshot.SpinNumber == 1 && game.Balance == SlotGame.StartingBalance - game.Bet,
                "A full fresh second after the gap did not spin exactly once.");
            RequirePress(board, "slot-spin", BoardSession.SlotSpinBounds, activated, 1);
        }
    }

    private static void CheckClearedCaptionHolds()
    {
        var game = new SlotGame(4);
        var board = new BoardSession(slots: game);
        board.ShowSlots(Origin);
        IReadOnlyList<string> Observe(int time, bool positive, IReadOnlyCollection<string>? cleared = null) =>
            board.ObserveHeldButtons(positive ? ["slot-spin"] : [], Origin.AddMilliseconds(time),
                Origin.AddMilliseconds(time), cleared ?? []);

        // Short positive bursts separated by explicitly intact captions cannot
        // accumulate a wall-clock second, even though every gap is only 100 ms.
        for (int time = 0; time <= 2000; time += 200)
        {
            Require(Observe(time, true).Count == 0 && game.Snapshot.SpinNumber == 0 &&
                game.Balance == SlotGame.StartingBalance && board.SlotsLastButtonPress is null,
                "Short positives across clear captions accumulated a Spin activation.");
            Require(Observe(time + 100, false, ["slot-spin"]).Count == 0 &&
                board.HoldProgress(Origin.AddMilliseconds(time + 100)).Count == 0,
                "A freshly clear caption retained partial Spin rim progress.");
        }

        // Unknown/missing evidence still tolerates a short camera dropout. A
        // repeated, out-of-order or stale clear cannot erase newer coverage.
        Require(Observe(2200, true).Count == 0 && Observe(2300, true).Count == 0 &&
            Observe(2400, false).Count == 0 && Observe(2500, true).Count == 0 &&
            Observe(2600, false, []).Count == 0 && Observe(2700, true).Count == 0,
            "A fresh Spin hold activated before a full second.");
        Require(board.ObserveHeldButtons([], Origin.AddMilliseconds(2700), Origin.AddMilliseconds(2750), ["slot-spin"]).Count == 0 &&
            board.ObserveHeldButtons([], Origin.AddMilliseconds(2699), Origin.AddMilliseconds(2750), ["slot-spin"]).Count == 0 &&
            board.HoldProgress(Origin.AddMilliseconds(2750)) is [{ ButtonId: "slot-spin", Progress: > .54 and < .56 }],
            "Repeated or out-of-order clear evidence released a newer Spin hold.");
        Require(board.ObserveHeldButtons([], Origin.AddMilliseconds(2400), Origin.AddMilliseconds(2800), ["slot-spin"]).Count == 0,
            "A stale clear frame activated a control.");
        for (int time = 2800; time < 3200; time += 100)
            Require(Observe(time, true).Count == 0 && game.Snapshot.SpinNumber == 0,
                "A fresh Spin hold acted before its own full second.");
        Require(Observe(3200, true).SequenceEqual(["slot-spin"]) && game.Snapshot.SpinNumber == 1 &&
            game.Balance == SlotGame.StartingBalance - game.Bet,
            "Unknown short gaps or obsolete clear frames prevented a valid one-second Spin.");
        RequirePress(board, "slot-spin", BoardSession.SlotSpinBounds, Origin.AddMilliseconds(3200), 1);
    }

    private static void CheckHoldCaptionReadiness()
    {
        var game = new SlotGame(4);
        var board = new BoardSession(slots: game);
        board.ShowSlots(Origin);
        IReadOnlyList<string> Observe(int time, bool positive, bool clear = false) =>
            board.ObserveHeldButtons(positive ? ["slot-spin"] : [], Origin.AddMilliseconds(time),
                Origin.AddMilliseconds(time), clear ? ["slot-spin"] : []);

        // A persistently corrupted startup reference cannot invent the initial press.
        for (int time = 0; time <= 1500; time += 100)
            Require(Observe(time, true).Count == 0 && board.HoldProgress(Origin.AddMilliseconds(time)).Count == 0 &&
                game.Snapshot.SpinNumber == 0 && game.Balance == SlotGame.StartingBalance && board.SlotsLastButtonPress is null,
                "An uncleared startup caption accumulated a rim or started Spin.");
        Require(Observe(1600, false, clear: true).Count == 0, "A clear caption activated Spin.");
        for (int time = 1700; time < 2700; time += 100)
            Require(Observe(time, true).Count == 0, "A freshly armed Spin acted before a full second.");
        Require(Observe(2700, true).SequenceEqual(["slot-spin"]) && game.Snapshot.SpinNumber == 1 &&
            game.Balance == SlotGame.StartingBalance - game.Bet, "An enabled clear caption did not arm a genuine full Spin hold.");
        var firstPress = RequirePress(board, "slot-spin", BoardSession.SlotSpinBounds, Origin.AddMilliseconds(2700), 1);

        // Disabled captions can look clear, and unknown frames can release the
        // spent place, but neither proves the next enabled appearance is clear.
        int settled = 2700;
        for (int guard = 0; game.Phase != SlotPhase.Idle; guard++)
        {
            Require(guard < 10_000, "The strict-caption spin never settled.");
            settled += 100;
            Require(Observe(settled, false, clear: guard % 2 == 0).Count == 0,
                "Disabled or unknown caption evidence activated a control.");
        }
        long spins = game.Snapshot.SpinNumber;
        decimal balance = game.Balance;
        for (int offset = 100; offset <= 1600; offset += 100)
            Require(Observe(settled + offset, true).Count == 0 &&
                board.HoldProgress(Origin.AddMilliseconds(settled + offset)).Count == 0 &&
                game.Snapshot.SpinNumber == spins && game.Balance == balance && ReferenceEquals(board.SlotsLastButtonPress, firstPress),
                "Disabled or unknown observations re-armed automatic Spin after the round.");

        int cleared = settled + 1700;
        Require(Observe(cleared, false, clear: true).Count == 0, "The next enabled clear caption activated Spin.");
        for (int offset = 100; offset < 1100; offset += 100)
            Require(Observe(cleared + offset, true).Count == 0, "A re-armed Spin did not require another complete second.");
        Require(Observe(cleared + 1100, true).SequenceEqual(["slot-spin"]) &&
            game.Snapshot.SpinNumber == spins + 1 && game.Balance == balance - game.Bet,
            "Fresh enabled clearance followed by a full hold failed to start the next spin.");
        RequirePress(board, "slot-spin", BoardSession.SlotSpinBounds, Origin.AddMilliseconds(cleared + 1100), 2);
    }

    private static void CheckPointerConsumesCaptionReadiness()
    {
        var game = new SlotGame(4);
        var board = new BoardSession(slots: game);
        board.ShowSlots(Origin);
        board.ObserveHeldButtons([], Origin, Origin, ["slot-spin"]);
        var now = Origin.AddMilliseconds(100);
        Require(board.ActivateButton("slot-spin", now), "The pointer could not start the readiness fixture.");
        while (game.Phase != SlotPhase.Idle)
        {
            now = game.PhaseEndsAt > now ? game.PhaseEndsAt : now;
            board.TickSlots(now);
        }
        long spins = game.Snapshot.SpinNumber;
        var press = board.SlotsLastButtonPress;
        for (int offset = 100; offset <= 1500; offset += 100)
        {
            var time = now.AddMilliseconds(offset);
            Require(board.ObserveHeldButtons(["slot-spin"], time, time, []).Count == 0 &&
                board.HoldProgress(time).Count == 0 && game.Snapshot.SpinNumber == spins &&
                ReferenceEquals(press, board.SlotsLastButtonPress),
                "A pointer press left old caption readiness for an automatic camera-driven repeat.");
        }
    }

    private static SlotButtonPress RequirePress(BoardSession board, string id, BoardRect bounds, DateTimeOffset now, long sequence)
    {
        Require(board.SlotsLastButtonPress is { } press && press.ButtonId == id && press.Bounds == bounds &&
            press.StartedAt == now && press.Sequence == sequence, $"{id}: the accepted press has incorrect identity, bounds, time or sequence.");
        return board.SlotsLastButtonPress!;
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
