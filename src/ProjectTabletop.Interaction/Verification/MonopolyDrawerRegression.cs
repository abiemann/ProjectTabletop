using ProjectTabletop.Interaction;

internal static class MonopolyDrawerRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 22, 0, 0, TimeSpan.Zero);
    private static int _milliseconds;
    private static long _eventId = 10000;

    public static void Run()
    {
        InactiveDrawerPreservesRulesAndRandomness();
        ActiveDrawerPausesAiWithoutDiscarding();
        DrawerInputBarriersAndSaveRecovery();
        OpeningReadinessAndRestoredDrawer();
        Console.WriteLine("Monopoly drawer passed: inactive snapshot/RNG preservation, modal targets, AI pause, " +
            "fresh gesture barriers, opening deadline/lifecycle, no discard route, busy/failed save recovery and successful resume.");
    }

    private static void InactiveDrawerPreservesRulesAndRandomness()
    {
        var game = new MonopolyGame(seed: 87, initialRolls: [new(1, 2)]);
        var board = new BoardSession(monopoly: game);
        int rolls = 0; board.MonopolyRollOccurred += _ => rolls++;
        board.ShowMonopoly(Next());
        var landing = board.MonopolyState;
        for (int index = 0; index < 4; index++)
        {
            Require(!board.MonopolyDrawerOpen && Button(board, "mp-exit").Label == "^",
                "The closed drawer did not show its upward caret.");
            Act(board, "mp-exit");
            CheckOpen(board, active: false);
            Require(ReferenceEquals(landing, board.MonopolyState) && !board.MonopolySaveRequested &&
                    !board.ActivateButton("mp-start-game", Next()) && !board.TickMonopoly(Next(1100)),
                "Opening an inactive drawer changed rules state or allowed its underlying Start control.");
            Act(board, "mp-exit-cancel");
            Require(!board.MonopolyDrawerOpen && ReferenceEquals(landing, board.MonopolyState),
                "Closing the landing drawer changed its game snapshot.");
        }
        Act(board, "mp-start-game");
        var setup = board.MonopolyState;
        Act(board, "mp-exit"); CheckOpen(board, active: false);
        Require(ReferenceEquals(setup, board.MonopolyState) && !board.ActivateButton("mp-ai-plus", Next()),
            "The setup drawer changed player configuration or exposed the controls underneath it.");
        Act(board, "mp-exit-cancel");
        Require(ReferenceEquals(setup, board.MonopolyState), "Closing the setup drawer mutated player configuration.");
        Act(board, "mp-ai-minus"); Act(board, "mp-human-plus"); Act(board, "mp-start"); Act(board, "mp-roll");
        var control = new BoardSession(monopoly: new MonopolyGame(seed: 87, initialRolls: [new(1, 2)]));
        control.ShowMonopoly(Next());
        Act(control, "mp-start-game"); Act(control, "mp-ai-minus"); Act(control, "mp-human-plus");
        Act(control, "mp-start"); Act(control, "mp-roll");
        Require(rolls == 1 && board.MonopolyState.Dice == new MonopolyDice(1, 2) &&
                board.ExportMonopolySave() == control.ExportMonopolySave(),
            "Inactive drawer toggles consumed a roll, changed shuffled decks or mutated the next actual game.");

        var unstarted = new BoardSession(); unstarted.ShowMonopoly(Next()); Act(unstarted, "mp-start-game");
        var unstartedState = unstarted.MonopolyState;
        Act(unstarted, "mp-exit"); Act(unstarted, "mp-exit-game");
        Require(unstarted.Screen == BoardScreen.Menu && !unstarted.MonopolyDrawerOpen &&
                unstarted.MonopolyState.Phase == MonopolyPhase.Landing &&
                unstarted.MonopolyState.HumanPlayers == unstartedState.HumanPlayers &&
                unstarted.MonopolyState.AiPlayers == unstartedState.AiPlayers && !unstarted.MonopolySaveRequested,
            "Exit Game from setup fabricated a save, discarded configuration or left an open drawer.");
        unstarted.ShowMonopoly(Next());
        Require(!unstarted.MonopolyDrawerOpen, "Returning to Monopoly reopened an old inactive drawer.");
    }

    private static void ActiveDrawerPausesAiWithoutDiscarding()
    {
        var board = new BoardSession(monopoly: new MonopolyGame(seed: 89, initialRolls: [new(1, 2)]));
        int rolls = 0; board.MonopolyRollOccurred += _ => rolls++;
        board.ShowMonopoly(Next()); Act(board, "mp-start-game");
        Act(board, "mp-human-minus"); Act(board, "mp-ai-plus"); Act(board, "mp-start");
        Require(board.TickMonopoly(Next(1100)) && rolls == 1 &&
                board.MonopolyState.ActivePlayer is { IsAi: true, Position: 3 },
            "The AI pause fixture failed to reach a pending purchase after its actual roll.");
        var interrupted = board.MonopolyState;
        string save = board.ExportMonopolySave();
        Act(board, "mp-exit"); CheckOpen(board, active: true);
        for (int index = 0; index < 5; index++)
            Require(!board.TickMonopoly(Next(1100)) && rolls == 1 &&
                    board.MonopolyState.Players.SequenceEqual(interrupted.Players) &&
                    board.MonopolyState.Properties.SequenceEqual(interrupted.Properties) &&
                    board.MonopolyState.Dice == interrupted.Dice && board.ExportMonopolySave() == save,
                "The AI continued rolling, buying or moving while the active drawer was open.");
        Require(!board.ActivateButton("mp-exit-without-saving", Next()) &&
                !board.ActivateButton("mp-exit-game", Next()) && board.Screen == BoardScreen.Monopoly,
            "An active game exposed an immediate or legacy discard route.");
        Act(board, "mp-exit-cancel");
        Require(!board.MonopolyDrawerOpen && board.MonopolyState.Phase == interrupted.Phase &&
                board.ExportMonopolySave() == save && board.TickMonopoly(Next(1100)) &&
                board.MonopolyState.Players[0].Money == 1440 && rolls == 1,
            "Closing the drawer lost the pending turn or failed to resume the AI's purchase.");
    }

    private static void DrawerInputBarriersAndSaveRecovery()
    {
        var board = new BoardSession(monopoly: new MonopolyGame(seed: 97));
        board.ShowMonopoly(Next()); Act(board, "mp-start-game"); Act(board, "mp-ai-minus");
        Act(board, "mp-human-plus"); Act(board, "mp-start");
        var preserved = board.MonopolyState;
        string save = board.ExportMonopolySave();
        var caret = Button(board, "mp-exit");
        var aim = Aim(caret);
        var together = new BoardHandSample(double.NaN, double.NaN, DateTimeOffset.MinValue, 0)
            { TrackingId = 1101, FourFingersExtended = true, FingerAim = aim, FingersTogether = true };
        var time = Next(); board.Update([together], time, time);
        time = Next(120); board.Update([together], time, time);
        Require(board.FingerSelectionFeedback.Any(item => item.ButtonId == "mp-exit" && item.Stage == BoardFingerSelectionStage.Armed),
            "The barrier fixture never armed its old caret gesture.");
        var openedAt = Next();
        Require(board.ActivateButton("mp-exit", openedAt), "The laptop route failed to open the drawer.");
        CheckOpen(board, active: true);
        var action = Button(board, "mp-save-exit");
        var separated = together with { FingerAim = Aim(action), FingersTogether = false, IndexFingerSeparated = true };
        time = Next(); Require(board.Update([separated], time, time) is null, "An old armed caret selected Save immediately after opening.");
        time = Next(120); Require(board.Update([separated], time, time) is null,
            "A held separated pose armed before the drawer saved the game without a fresh grouped pose.");
        time = Next();
        Require(board.Update([Pulse(action, ++_eventId, openedAt, openedAt.AddMilliseconds(-1))], time, time) is null &&
                !board.MonopolySaveRequested && board.MonopolySaveRequestId == 0,
            "An execute pulse or pointing anchor captured before drawer opening started a save.");
        var slideOrigin = openedAt.AddMilliseconds(200);
        time = Next(141);
        Require(board.Update([Pulse(action, ++_eventId, slideOrigin)], time, time) is null &&
                !board.MonopolySaveRequested && Button(board, "mp-save-exit").Enabled,
            "A delayed execute pulse captured during the slide selected the newly enabled drawer action.");
        Act(board, "mp-exit-cancel");
        var closedAt = Time;
        time = Next();
        Require(board.Update([Pulse(Button(board, "mp-exit"), ++_eventId, time, closedAt.AddMilliseconds(-1))], time, time) is null &&
                !board.MonopolyDrawerOpen,
            "A pointing anchor from the open drawer reopened its newly exposed caret.");
        time = Next();
        var opened = board.Update([Pulse(Button(board, "mp-exit"), ++_eventId, time)], time, time);
        Require(opened?.ButtonId == "mp-exit" && board.MonopolyDrawerOpen,
            "The barrier rejected a genuinely fresh caret selection.");
        long heldEvent = _eventId;
        time = Next(350);
        Require(board.Update([Pulse(Button(board, "mp-save-exit"), heldEvent, time)], time, time) is null &&
                !board.MonopolySaveRequested,
            "The same execute event that opened the drawer replayed on its Save action.");
        Act(board, "mp-save-exit");
        long first = board.MonopolySaveRequestId;
        Require(board.MonopolyDrawerOpen && board.MonopolySaveRequested && board.Buttons.All(button => !button.Enabled) &&
                board.ExportMonopolySave() == save && !board.ActivateButton("mp-exit-cancel", Next()) &&
                !board.CompleteMonopolySave(first - 1, true, Next()),
            "A busy save changed its payload, permitted dismissal or accepted an unrelated completion.");
        Require(board.CompleteMonopolySave(first, false, Next(), "Verification disk failure") &&
                board.MonopolyDrawerOpen && board.MonopolyState.Status.Contains("Verification disk failure") &&
                !board.CompleteMonopolySave(first, true, Next()),
            "A failed save closed the drawer, hid its failure or accepted a stale success.");
        Act(board, "mp-exit-cancel");
        Require(!board.MonopolyDrawerOpen && board.MonopolyState.Phase == preserved.Phase &&
                board.MonopolyState.Players.SequenceEqual(preserved.Players) && board.ExportMonopolySave() == save,
            "Closing after a failed save lost the interrupted game.");
        Act(board, "mp-exit"); Act(board, "mp-save-exit");
        long retry = board.MonopolySaveRequestId;
        Require(retry > first && board.CompleteMonopolySave(retry, true, Next()) &&
                board.Screen == BoardScreen.Menu && !board.MonopolyDrawerOpen,
            "A fresh successful retry did not close the drawer and navigate after persistence completed.");
        board.ShowMonopoly(Next()); Act(board, "mp-resume");
        Require(!board.MonopolyDrawerOpen && board.ExportMonopolySave() == save,
            "Resume changed the saved game or replayed its closed drawer.");
    }

    private static void CheckOpen(BoardSession board, bool active)
    {
        Require(board.MonopolyDrawerOpen && board.Screen == BoardScreen.Monopoly &&
                Button(board, "mp-exit-cancel").Label == "v" &&
                board.Buttons.Select(button => button.Id).Order().SequenceEqual(
                    new[] { "mp-exit-cancel", active ? "mp-save-exit" : "mp-exit-game" }.Order()) &&
                Button(board, active ? "mp-save-exit" : "mp-exit-game").Label == (active ? "Save and Exit" : "Exit Game") &&
                (!active || board.MonopolyState.Phase == MonopolyPhase.ExitConfirmation),
            "The drawer did not contain exactly its downward caret and single appropriate action.");
    }

    private static void OpeningReadinessAndRestoredDrawer()
    {
        var idle = new BoardSession(); idle.ShowMonopoly(Next());
        var opened = Next();
        Require(idle.ActivateButton("mp-exit", opened) && idle.MonopolyDrawerOpenedAt == opened &&
                BoardSession.MonopolyDrawerOpeningDuration == TimeSpan.FromMilliseconds(300) &&
                Button(idle, "mp-exit-cancel").Enabled && !Button(idle, "mp-exit-game").Enabled,
            "Opening timestamp/duration was missing, or the moving Exit action was already enabled.");
        long revision = idle.Revision;
        Require(!idle.TickMonopoly(opened.AddMilliseconds(299)) && idle.Revision == revision &&
                !Button(idle, "mp-exit-game").Enabled,
            "The drawer released its action before the entrance completed.");
        Require(!idle.TickMonopoly(opened.AddMilliseconds(300)) && idle.Revision == revision + 1 &&
                Button(idle, "mp-exit-game").Enabled && !idle.TickMonopoly(opened.AddMilliseconds(301)) &&
                idle.Revision == revision + 1,
            "The opening deadline failed to release once, or repeated idle ticks added input barriers.");
        idle.ShowPaint(opened.AddMilliseconds(310)); idle.ShowMonopoly(opened.AddMilliseconds(320));
        Require(!idle.MonopolyDrawerOpen && idle.MonopolyDrawerOpenedAt is null,
            "Navigation restored an inactive drawer or left its opening timestamp behind.");

        var active = new BoardSession(monopoly: new MonopolyGame(seed: 101));
        active.ShowMonopoly(Next()); Act(active, "mp-start-game"); Act(active, "mp-start");
        var previous = active.MonopolyState;
        opened = Next(); Require(active.ActivateButton("mp-exit", opened), "Could not open active lifecycle fixture.");
        active.ShowPaint(opened.AddMilliseconds(50));
        Require(!active.MonopolyDrawerOpen && active.MonopolyDrawerOpenedAt is null &&
                active.MonopolyState.Phase == MonopolyPhase.ExitConfirmation,
            "Hiding the active drawer resumed or discarded its interrupted game.");
        active.ShowMonopoly(opened.AddMilliseconds(350)); active.TickMonopoly(opened.AddMilliseconds(350));
        Require(active.MonopolyDrawerOpen && active.MonopolyDrawerOpenedAt == opened &&
                Button(active, "mp-save-exit").Enabled && !active.MonopolySaveRequested &&
                active.ActivateButton("mp-exit-cancel", opened.AddMilliseconds(360)) &&
                active.MonopolyState.Phase == previous.Phase && active.MonopolyState.Players.SequenceEqual(previous.Players),
            "Returning abandoned the active confirmation, replayed its entrance or lost the interrupted turn.");

        var immediateClose = new BoardSession(); immediateClose.ShowMonopoly(Next());
        opened = Next(); Require(immediateClose.ActivateButton("mp-exit", opened) &&
                immediateClose.ActivateButton("mp-exit-cancel", opened.AddMilliseconds(1)) &&
                !immediateClose.MonopolyDrawerOpen && immediateClose.MonopolyDrawerOpenedAt is null,
            "The close caret was blocked while the drawer was sliding in.");
    }
    private static void Act(BoardSession board, string id)
    {
        if (id is "mp-save-exit" or "mp-exit-game") Next(350);
        Require(board.ActivateButton(id, Next()), $"Drawer fixture rejected {id} during {board.MonopolyState.Phase}.");
    }
    private static BoardButton Button(BoardSession board, string id) => board.Buttons.Single(button => button.Id == id);
    private static BoardAim Aim(BoardButton button) => new(button.Bounds.X + button.Bounds.Width / 2,
        button.Bounds.Y + button.Bounds.Height / 2);
    private static BoardHandSample Pulse(BoardButton button, long id, DateTimeOffset origin, DateTimeOffset? anchor = null)
    {
        var aim = Aim(button); return new(aim.U, aim.V, origin.AddSeconds(1), id, anchor) { TrackingId = 1102 };
    }
    private static DateTimeOffset Time => Epoch.AddMilliseconds(_milliseconds);
    private static DateTimeOffset Next(int milliseconds = 20) => Epoch.AddMilliseconds(_milliseconds += milliseconds);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
