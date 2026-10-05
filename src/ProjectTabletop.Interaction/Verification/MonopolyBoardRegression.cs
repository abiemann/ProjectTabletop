using ProjectTabletop.Interaction;

internal static class MonopolyBoardRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 16, 0, 0, TimeSpan.Zero);
    private static int _milliseconds;
    private static long _eventId;

    public static void Run()
    {
        CheckSetupAndSharedTargets();
        CheckGameGesturesAndBarriers();
        CheckAiTurnInputBarrier();
        CheckExitSaveAndResume();
        CheckBackgroundSaveKeepsOtherBoard();
        Console.WriteLine("Monopoly board verification passed: 2-6 human/AI setup, shared pinch and finger controls, " +
            "disabled/held/stale event barriers, turn actions, exit drawer, failed save recovery, resume and background save isolation.");
    }

    private static void CheckSetupAndSharedTargets()
    {
        for (int humans = 0; humans <= 6; humans++)
        for (int ais = 0; ais <= 6 - humans; ais++)
        {
            int count = humans + ais;
            if (count < 2) continue;
            var board = new BoardSession(monopoly: new MonopolyGame(seed: 17));
            Pinch(board, "monopoly");
            Require(board.Screen == BoardScreen.Monopoly && board.Title == "Crown & Deed" &&
                board.MonopolyState.Phase == MonopolyPhase.Landing,
                "The replacement menu entry did not open the Monopoly landing board.");
            Require(board.Buttons.Any(button => button.Label == "Start Game"), "The landing board lacks Start Game.");
            Targets(board);
            Separate(board, "mp-start-game");
            Require(board.MonopolyState.Phase == MonopolyPhase.Setup, "The grouped-to-separated finger gesture did not open setup.");
            while (board.MonopolyState.HumanPlayers > humans) Pinch(board, "mp-human-minus");
            while (board.MonopolyState.AiPlayers > ais) Pinch(board, "mp-ai-minus");
            SetCount(board, "human", humans, () => board.MonopolyState.HumanPlayers);
            SetCount(board, "ai", ais, () => board.MonopolyState.AiPlayers);
            Targets(board);
            Require(board.MonopolyState.HumanPlayers == humans && board.MonopolyState.AiPlayers == ais &&
                board.Buttons.Single(button => button.Id == "mp-start").Enabled,
                "A valid player mixture did not enable Start.");
            Require(board.Buttons.Count(button => button.Id.StartsWith("mp-piece-next-", StringComparison.Ordinal)) == count,
                "A setup player is missing an independent silver-piece control.");
            var beforePieces = board.MonopolyState;
            long beforeRevision = board.Revision;
            string pieceId = $"mp-piece-next-{count}";
            Separate(board, pieceId);
            Require(board.Revision > beforeRevision && board.MonopolyState.SetupPieces[count - 1] != beforePieces.SetupPieces[count - 1] &&
                board.MonopolyState.SetupPieces.Distinct().Count() == count &&
                Button(board, pieceId).Label.StartsWith(ais > 0 ? $"A{ais} · " : $"P{humans} · ", StringComparison.Ordinal),
                "Shared piece selection lost its caption identity, uniqueness or input/reference barrier.");
            int[] pieces = board.MonopolyState.SetupPieces.ToArray();
            Pinch(board, "mp-start");
            Require(board.MonopolyState.Players.Count == count && board.MonopolyState.Players.Count(player => player.IsAi) == ais &&
                board.MonopolyState.Players.Count(player => !player.IsAi) == humans &&
                board.MonopolyState.Phase == MonopolyPhase.AwaitingRoll,
                $"The {humans}-human/{ais}-AI setup produced the wrong game.");
            Require(board.MonopolyState.Players.Select(player => player.PieceIndex).SequenceEqual(pieces),
                "Shared setup selected different pieces from the started players.");
            Targets(board);
        }
        var one = new BoardSession(); one.ShowMonopoly(Next()); Pinch(one, "mp-start-game"); Pinch(one, "mp-ai-minus");
        var start = one.Buttons.Single(button => button.Id == "mp-start");
        Require(!start.Enabled && !one.ActivateButton("mp-start", Next()), "A one-player game was selectable.");
        long id = ++_eventId;
        var time = Next();
        Require(one.Update([Sample(start, id, time)], time, time) is null && one.HoveredButtonIds.Count == 0,
            "A disabled Start control highlighted or selected.");
        Pinch(one, "mp-human-plus");
        time = Next();
        start = one.Buttons.Single(button => button.Id == "mp-start");
        Require(one.Update([Sample(start, id, time)], time, time) is null && one.MonopolyState.Phase == MonopolyPhase.Setup,
            "An event consumed over disabled Start replayed after setup enabled it.");
    }

    private static void CheckGameGesturesAndBarriers()
    {
        var board = new BoardSession(monopoly: new MonopolyGame(seed: 23, initialRolls: [new(1, 2), new(1, 2)]));
        Pinch(board, "monopoly");
        long navigationRevision = board.Revision;
        long held = _eventId;
        var time = Next();
        var startGame = Button(board, "mp-start-game");
        Require(board.Update([Sample(startGame, held, time)], time, time) is null &&
            board.MonopolyState.Phase == MonopolyPhase.Landing, "The held menu pinch started game setup.");
        Pinch(board, "mp-start-game"); Pinch(board, "mp-ai-minus"); Pinch(board, "mp-human-plus"); Separate(board, "mp-start");
        Require(board.Revision > navigationRevision && board.Screen == BoardScreen.Monopoly,
            "Setup controls did not refresh the acquisition scene or changed the active board.");
        var roll = Button(board, "mp-roll");
        Require(roll.Label == "Roll" && roll.Enabled && roll.Bounds.Width > roll.Bounds.Height,
            "Roll is missing its requested pill target and caption.");
        long gameRevision = board.MonopolyState.Revision;
        var point = Aim(roll); time = Next();
        Require(board.Update([new(point.U, point.V, DateTimeOffset.MinValue, 0)], time, time) is null &&
            board.MonopolyState.Revision == gameRevision && board.HoveredButtonIds.SequenceEqual(["mp-roll"]),
            "Merely hovering the dice button rolled the dice or missed the shared target.");
        Separate(board, "mp-roll");
        Require(board.MonopolyState.Phase == MonopolyPhase.AwaitingPurchase && board.MonopolyState.ActivePlayer!.Position == 3,
            "Finger selection failed to roll and move to the purchase square.");
        Pinch(board, "mp-buy");
        Require(board.MonopolyState.Properties.Single(property => property.SpaceIndex == 3).OwnerId == board.MonopolyState.Players[0].Id,
            "The projected Buy control did not purchase the landed property.");
        Separate(board, "mp-end-turn"); Pinch(board, "mp-roll");
        Require(board.MonopolyState.Players[0].Money == 1444 && board.MonopolyState.Players[1].Money == 1496,
            "Shared projected turn controls failed to transfer rent.");
        board.ResetInput(Next());
        time = Next();
        var end = Button(board, "mp-end-turn");
        Require(board.Update([Sample(end, ++_eventId, time.AddMilliseconds(-40))], time, time) is null,
            "A pre-reset execute pulse ended the next turn.");
        Require(board.ActivateButton("mp-end-turn", Next()), "The laptop route rejected an enabled End Turn.");
        time = Next();
        roll = Button(board, "mp-roll");
        Require(board.Update([Sample(roll, ++_eventId, time) with { SelectionFrameTime = time.AddMilliseconds(-80) }], time, time) is null,
            "An open-hand anchor from before a laptop action crossed into the new turn.");
        Targets(board);
    }

    private static void CheckExitSaveAndResume()
    {
        var board = Started();
        Pinch(board, "mp-roll"); Pinch(board, "mp-buy");
        var preserved = board.MonopolyState;
        Separate(board, "mp-exit");
        Require(board.Screen == BoardScreen.Monopoly && board.MonopolyDrawerOpen &&
            board.MonopolyState.Phase == MonopolyPhase.ExitConfirmation &&
            board.Buttons.All(button => button.Id != "mp-roll") &&
            Button(board, "mp-exit-cancel").Label == "v" && Button(board, "mp-save-exit").Label == "Save and Exit" &&
            board.Buttons.All(button => button.Id != "mp-exit-without-saving"),
            "The active drawer navigated before saving or exposed the old discard route.");
        Targets(board);
        Pinch(board, "mp-exit-cancel");
        Require(board.Screen == BoardScreen.Monopoly && !board.MonopolyDrawerOpen &&
            Button(board, "mp-exit").Label == "^" && board.MonopolyState.Phase == preserved.Phase &&
            board.MonopolyState.Players.SequenceEqual(preserved.Players), "Cancel exit changed the game instead of keeping it open.");
        Pinch(board, "mp-exit"); Separate(board, "mp-save-exit");
        Require(board.MonopolySaveRequested && board.MonopolyState.Phase == MonopolyPhase.Saving,
            "Save and exit did not queue a save while keeping the board open.");
        long firstRequest = board.MonopolySaveRequestId;
        string saved = board.ExportMonopolySave();
        Require(!board.ActivateButton("mp-save-exit", Next()) && !board.ActivateButton("mp-roll", Next()) &&
            !board.CompleteMonopolySave(firstRequest - 1, success: true, Next()),
            "Busy or stale save actions changed the open game.");
        Require(board.CompleteMonopolySave(firstRequest, success: false, Next(), "Disk is full") &&
            !board.MonopolySaveRequested && board.Screen == BoardScreen.Monopoly && board.MonopolyDrawerOpen &&
            board.MonopolyState.Phase == MonopolyPhase.ExitConfirmation && board.MonopolyState.Status.Contains("Disk is full"),
            "A failed save closed the game, left it busy or hid its error.");
        Require(!board.CompleteMonopolySave(firstRequest, success: true, Next()), "A failed request later accepted a stale success.");
        Separate(board, "mp-exit-cancel");
        Require(board.MonopolyState.Phase == preserved.Phase, "Cancel after a failed save lost the original turn.");
        Pinch(board, "mp-exit"); Pinch(board, "mp-save-exit");
        long secondRequest = board.MonopolySaveRequestId;
        Require(secondRequest > firstRequest && board.CompleteMonopolySave(secondRequest, success: true, Next()) &&
            board.Screen == BoardScreen.Menu && !board.MonopolySaveRequested,
            "A successful save did not navigate to the launcher exactly once.");
        Require(!board.CompleteMonopolySave(secondRequest, success: true, Next()), "A consumed successful save completed twice.");
        Pinch(board, "monopoly");
        Require(board.MonopolyState.Phase == MonopolyPhase.Landing && board.MonopolyState.CanResume &&
            board.Buttons.Any(button => button.Id == "mp-resume" && button.Enabled),
            "A saved game did not offer Resume on the landing board.");
        Separate(board, "mp-resume");
        Require(board.MonopolyState.Phase == preserved.Phase && board.MonopolyState.Players.SequenceEqual(preserved.Players) &&
            board.MonopolyState.Properties.SequenceEqual(preserved.Properties), "Resume lost cash, ownership or the interrupted turn.");

        var loaded = new BoardSession();
        Require(loaded.LoadMonopolySave(saved, Next()), "A valid persisted game was rejected.");
        loaded.ShowMonopoly(Next());
        Require(loaded.MonopolyState.Phase == MonopolyPhase.Landing && loaded.MonopolyState.CanResume,
            "Loading a disk save started gameplay before the user selected Resume.");
        Pinch(loaded, "mp-resume");
        Require(loaded.MonopolyState.Phase == preserved.Phase && loaded.MonopolyState.Players.SequenceEqual(preserved.Players),
            "A fresh board did not resume the persisted game.");
        Pinch(loaded, "mp-exit"); Pinch(loaded, "mp-save-exit");
        Require(loaded.Screen == BoardScreen.Monopoly && loaded.MonopolySaveRequested &&
            loaded.CompleteMonopolySave(loaded.MonopolySaveRequestId, success: true, Next()) && loaded.Screen == BoardScreen.Menu,
            "The resumed game's Save and Exit route did not retain the game until its save completed.");
        var landing = new BoardSession(); landing.ShowMonopoly(Next()); Pinch(landing, "mp-exit");
        Require(landing.Screen == BoardScreen.Monopoly && landing.MonopolyDrawerOpen &&
            Button(landing, "mp-exit-game").Label == "Exit Game" && !landing.MonopolySaveRequested,
            "The unstarted board did not open an Exit Game drawer without a save request.");
        Pinch(landing, "mp-exit-game");
        Require(landing.Screen == BoardScreen.Menu && !landing.MonopolyDrawerOpen,
            "Exit Game from an unstarted board failed to close the drawer and return to the launcher.");
    }

    private static void CheckAiTurnInputBarrier()
    {
        var board = new BoardSession(monopoly: new MonopolyGame(seed: 19, initialRolls: [new(1, 2), new(1, 2)]));
        board.ShowMonopoly(Next()); Pinch(board, "mp-start-game"); Pinch(board, "mp-start");
        Pinch(board, "mp-roll"); Pinch(board, "mp-buy"); Pinch(board, "mp-end-turn");
        Require(board.MonopolyState.ActivePlayer!.IsAi, "The human turn did not pass to its configured AI player.");
        var aiRoll = Button(board, "mp-roll");
        Require(!aiRoll.Enabled && !board.ActivateButton("mp-roll", Next()), "A human input overrode the AI's pending roll.");
        long eventId = ++_eventId;
        var time = Next();
        Require(board.Update([Sample(aiRoll, eventId, time)], time, time) is null && board.HoveredButtonIds.Count == 0,
            "The AI's disabled Roll control accepted or highlighted a human gesture.");
        Require(board.TickMonopoly(Next(1100)) && board.MonopolyState.Phase == MonopolyPhase.AwaitingEndTurn &&
            board.TickMonopoly(Next(1100)) && board.MonopolyState.ActivePlayerIndex == 0,
            "The AI failed to pay rent and return control to its human opponent.");
        time = Next(); var humanRoll = Button(board, "mp-roll");
        Require(humanRoll.Enabled && board.Update([Sample(humanRoll, eventId, time)], time, time) is null,
            "An event consumed during an AI turn replayed on the human's newly enabled Roll control.");
        time = Next();
        Require(board.Update([Sample(humanRoll, ++_eventId, time) with { SelectionFrameTime = time.AddMilliseconds(-1200) }], time, time) is null,
            "A pointing anchor retained during AI play crossed into the next human turn.");
    }

    private static BoardSession Started()
    {
        var board = new BoardSession(monopoly: new MonopolyGame(seed: 17, initialRolls: [new(1, 2)]));
        board.ShowMonopoly(Next()); Pinch(board, "mp-start-game"); Pinch(board, "mp-ai-minus");
        Pinch(board, "mp-human-plus"); Pinch(board, "mp-start"); return board;
    }
    private static void CheckBackgroundSaveKeepsOtherBoard()
    {
        var board = Started();
        Pinch(board, "mp-roll"); Pinch(board, "mp-buy"); Pinch(board, "mp-exit"); Pinch(board, "mp-save-exit");
        long request = board.MonopolySaveRequestId;
        string save = board.ExportMonopolySave();
        board.ShowPaint(Next());
        long paintRevision = board.Revision;
        Require(board.CompleteMonopolySave(request, success: true, Next()) && board.Screen == BoardScreen.Paint &&
            board.Revision == paintRevision, "Completing a background Monopoly save restarted or left the active Paint board.");
        Require(board.LoadMonopolySave(save, Next()) && board.Screen == BoardScreen.Paint && board.Revision == paintRevision,
            "Staging a Monopoly disk save restarted or left the active Paint board.");
        board.ShowMonopoly(Next());
        Require(board.MonopolyState.CanResume && board.MonopolyState.Phase == MonopolyPhase.Landing,
            "A background save completion lost its resumable game.");
        Pinch(board, "mp-resume");
        Require(board.MonopolyState.Players[0].Money == 1440 && board.MonopolyState.Players[0].Position == 3,
            "Returning from Paint could not resume the game saved in the background.");
    }
    private static void SetCount(BoardSession board, string kind, int count, Func<int> current)
    {
        // Reduce first so the target mixture never briefly exceeds six players.
        while (current() > count) Pinch(board, "mp-" + kind + "-minus");
        while (current() < count) Pinch(board, "mp-" + kind + "-plus");
    }
    private static BoardButton Button(BoardSession board, string id) => board.Buttons.Single(button => button.Id == id);
    private static BoardAim Aim(BoardButton button) => new(button.Bounds.X + button.Bounds.Width / 2,
        button.Bounds.Y + button.Bounds.Height / 2);
    private static BoardHandSample Sample(BoardButton button, long id, DateTimeOffset time)
    {
        var aim = Aim(button); return new(aim.U, aim.V, time.AddSeconds(1), id) { TrackingId = 901 };
    }
    private static void Pinch(BoardSession board, string id)
    {
        if (id is "mp-save-exit" or "mp-exit-game") Next(350);
        var time = Next(); var button = Button(board, id);
        Require(board.Update([Sample(button, ++_eventId, time)], time, time)?.ButtonId == id,
            $"Shared pinch rejected {id} during {board.MonopolyState.Phase}.");
    }
    private static void Separate(BoardSession board, string id)
    {
        if (id is "mp-save-exit" or "mp-exit-game") Next(350);
        var aim = Aim(Button(board, id));
        var hand = new BoardHandSample(double.NaN, double.NaN, DateTimeOffset.MinValue, 0)
            { TrackingId = 902, FourFingersExtended = true, FingerAim = aim, FingersTogether = true };
        var time = Next(); board.Update([hand], time, time);
        time = Next(120); board.Update([hand], time, time);
        var separated = hand with { FingersTogether = false, IndexFingerSeparated = true };
        time = Next(30); board.Update([separated], time, time);
        time = Next(100);
        Require(board.Update([separated], time, time) is { Gesture: BoardSelectionGesture.IndexSeparation } result && result.ButtonId == id,
            $"Shared index separation rejected {id} during {board.MonopolyState.Phase}.");
        // An unchanged separated pose cannot execute again in the next controls.
        time = Next(20); Require(board.Update([separated], time, time) is null, "Held index separation executed twice.");
    }
    private static DateTimeOffset Next(int step = 20) => Epoch.AddMilliseconds(_milliseconds += step);
    private static void Targets(BoardSession board)
    {
        var buttons = board.Buttons;
        Require(buttons.Select(button => button.Id).Distinct().Count() == buttons.Count, "A Monopoly target ID was duplicated.");
        foreach (var button in buttons)
        {
            var r = button.Bounds;
            Require(r.X >= 0 && r.Y >= 0 && r.Width > .02 && r.Height > .02 && r.X + r.Width <= 1 && r.Y + r.Height <= 1,
                $"Monopoly control {button.Id} extends outside the board or is too small.");
            Require(buttons.Count(other => other.Bounds.Contains(r.X + r.Width / 2, r.Y + r.Height / 2)) == 1,
                $"Monopoly control {button.Id} overlaps another hit target.");
            Require(!button.Label.Contains('\uFFFD') && !button.Label.Contains("GTA", StringComparison.Ordinal),
                "A Monopoly control retained corrupt text or the old game title.");
        }
    }
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
