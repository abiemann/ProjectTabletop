using ProjectTabletop.Interaction;

internal static class CrownDeedPresentationRegression
{
    public static void Run()
    {
        CheckRollInputRoutes();
        CheckPresentationInputBarriers();
        CheckAiPresentationPause();
        CheckNavigationAndReset();
        Console.WriteLine("CrownDeed presentation verification passed: committed roll relays from pointer/pinch/fingers/AI, " +
            "one roll input barrier, disabled game controls, AI pause, delayed frame/pulse/pose rejection, " +
            "fresh selection after expiry, save/exit navigation and projection reset cancellation.");
    }

    private static void CheckRollInputRoutes()
    {
        foreach (string route in new[] { "pointer", "pinch", "fingers" })
        {
            var board = Board();
            var previous = board.CrownDeedState;
            long revision = board.Revision;
            var events = new List<CrownDeedRoll>();
            board.CrownDeedRollOccurred += roll =>
            {
                Require(ReferenceEquals(roll.Current, board.CrownDeedState) && ReferenceEquals(roll.Previous, previous) &&
                    board.Revision == revision + 1 && board.HoveredButtonIds.Count == 0 &&
                    board.FingerSelectionFeedback.Count == 0,
                    "Roll relay preceded game/input commit or applied the roll input barrier twice.");
                events.Add(roll);
            };
            var button = Button(board, "mp-roll");
            if (route == "pointer") Require(board.ActivateButton("mp-roll", Time(100)), "Pointer Roll failed.");
            else if (route == "pinch")
                Require(board.Update([Pinch(button, 1, 100)], Time(100), Time(100))?.ButtonId == "mp-roll", "Pinch Roll failed.");
            else
            {
                var hand = Fingers(button);
                board.Update([hand], Time(100), Time(100));
                board.Update([hand], Time(200), Time(200));
                var separated = hand with { FingersTogether = false, IndexFingerSeparated = true };
                board.Update([separated], Time(220), Time(220));
                Require(board.Update([separated], Time(310), Time(310)) is
                    { ButtonId: "mp-roll", Gesture: BoardSelectionGesture.IndexSeparation }, "Finger Roll failed.");
                Require(board.Update([separated], Time(330), Time(330)) is null, "A held finger pose replayed Roll.");
            }
            Require(events is [{ Sequence: 1, Previous.Phase: CrownDeedPhase.AwaitingRoll,
                Current.Phase: CrownDeedPhase.AwaitingPurchase }] && events[0].Current.Dice == new CrownDeedDice(1, 2) &&
                events[0].Current.ActivePlayer!.Position == 3 && events[0].StartedAt == Time(route == "fingers" ? 310 : 100),
                "Roll relay lost the actual dice, committed landing or accepted action time.");
            Require(!board.ActivateButton("mp-roll", Time(400)) && !board.ActivateButton("unknown", Time(410)) &&
                events.Count == 1, "Rejected controls emitted another roll relay.");
            Require(board.ActivateButton("mp-buy", Time(420)) && events.Count == 1 &&
                events[0].Current.Properties.Single(property => property.SpaceIndex == 3).OwnerId is null,
                "Non-roll gameplay emitted a roll or changed its historical snapshot.");
        }
    }

    private static void CheckPresentationInputBarriers()
    {
        var board = Board();
        board.CrownDeedRollOccurred += roll => board.HoldCrownDeedPresentationUntil(roll.StartedAt.AddSeconds(3.6));
        Require(board.ActivateButton("mp-roll", Time(100)), "Presentation fixture did not roll.");
        long gameRevision = board.CrownDeedState.Revision;
        Require(board.IsCrownDeedPresentationActive(Time(100)) &&
            board.Buttons.Where(button => button.Id != "mp-exit").All(button => !button.Enabled) && Button(board, "mp-exit").Enabled,
            "Roll presentation left game choices enabled or disabled Exit.");
        var buy = Button(board, "mp-buy");
        Require(!board.ActivateButton("mp-buy", Time(200)) && !board.ActivateButton("mp-auction", Time(250)) &&
            board.Update([Pinch(buy, 1, 300)], Time(300), Time(300)) is null && board.HoveredButtonIds.Count == 0,
            "A pointer, pinch or hover passed the presentation lock.");
        var together = Fingers(buy);
        board.Update([together], Time(400), Time(400));
        board.Update([together], Time(500), Time(500));
        Require(board.FingerSelectionFeedback.Count == 0, "Disabled game choices armed a finger selection.");
        board.HoldCrownDeedPresentationUntil(Time(600));
        Require(board.IsCrownDeedPresentationActive(Time(3699)) && !board.TickCrownDeed(Time(3699)),
            "A shorter hold shortened the 3.6-second presentation.");
        // Expiry can be driven without a camera frame; the completed roll remains the real game state.
        Require(!board.TickCrownDeed(Time(3700)) && !board.IsCrownDeedPresentationActive(Time(3700)) &&
            Button(board, "mp-buy").Enabled && board.CrownDeedState.Revision == gameRevision,
            "Expiry failed to release controls or modified human gameplay.");
        Require(board.Update([together], Time(3650), Time(3701)) is null && board.HoveredButtonIds.Count == 0 &&
            board.FingerSelectionFeedback.Count == 0, "A delayed frame captured during animation armed a choice.");
        Require(board.Update([Pinch(buy, 2, 3600)], Time(3702), Time(3702)) is null,
            "An unseen pinch that began during the animation executed at release.");
        Require(board.Update([Pinch(buy, 3, 3710) with { SelectionFrameTime = Time(3650) }], Time(3710), Time(3710)) is null,
            "A pointing anchor from the animation survived release.");
        var separated = together with { FingersTogether = false, IndexFingerSeparated = true };
        board.Update([separated], Time(3720), Time(3720));
        Require(board.Update([separated], Time(3810), Time(3810)) is null &&
            board.CrownDeedState.Phase == CrownDeedPhase.AwaitingPurchase,
            "A pose armed while controls were disabled executed immediately at release.");
        board.Update([together], Time(3820), Time(3820));
        board.Update([together], Time(3920), Time(3920));
        board.Update([separated], Time(3940), Time(3940));
        Require(board.Update([separated], Time(4030), Time(4030)) is { ButtonId: "mp-buy" },
            "The hold blocked a fresh grouped-to-separated gesture after expiry.");

        var pointer = Rolled();
        pointer.HoldCrownDeedPresentationUntil(Time(500));
        Require(pointer.ActivateButton("mp-buy", Time(500)), "Pointer time did not expire the hold.");
        var camera = Rolled();
        camera.HoldCrownDeedPresentationUntil(Time(500));
        Require(camera.Update([Pinch(Button(camera, "mp-buy"), 10, 501)], Time(501), Time(501)) is { ButtonId: "mp-buy" },
            "Camera time did not expire the hold for a fresh pulse.");
        var extended = Rolled();
        extended.HoldCrownDeedPresentationUntil(Time(500)); extended.HoldCrownDeedPresentationUntil(Time(800));
        Require(extended.IsCrownDeedPresentationActive(Time(500)) && !extended.IsCrownDeedPresentationActive(Time(800)),
            "A later hold failed to extend the deadline.");
    }

    private static void CheckAiPresentationPause()
    {
        var board = Board(humansOnly: false);
        var events = new List<CrownDeedRoll>();
        board.CrownDeedRollOccurred += roll =>
        {
            Require(ReferenceEquals(roll.Current, board.CrownDeedState) && board.FingerSelectionFeedback.Count == 0,
                "AI relay preceded its committed gameplay or input barrier.");
            events.Add(roll);
            if (roll.Current.ActivePlayer!.IsAi) board.HoldCrownDeedPresentationUntil(roll.StartedAt.AddSeconds(3.6));
        };
        Require(board.ActivateButton("mp-roll", Time(100)) && board.ActivateButton("mp-buy", Time(120)) &&
            board.ActivateButton("mp-end-turn", Time(140)), "AI fixture did not finish the human turn.");
        long beforeBoardRevision = board.Revision, beforeGameRevision = board.CrownDeedState.Revision;
        Require(board.TickCrownDeed(Time(1140)) && events.Count == 2 && events[1].Current.ActivePlayer!.IsAi &&
            events[1].Current.ActivePlayer!.Position == 3 && events[1].Current.ActivePlayer!.Money == 1496 &&
            board.Revision == beforeBoardRevision + 1 && board.CrownDeedState.Revision == beforeGameRevision + 1,
            "AI roll did not relay once with actual movement/rent and one input barrier.");
        long rolledRevision = board.CrownDeedState.Revision;
        Require(!board.TickCrownDeed(Time(2000)) && !board.TickCrownDeed(Time(4739)) &&
            board.CrownDeedState.Revision == rolledRevision && events.Count == 2,
            "AI progressed or generated extra events before the dice presentation finished.");
        Require(board.TickCrownDeed(Time(4740)) && board.CrownDeedState.ActivePlayerIndex == 0 &&
            board.CrownDeedState.Revision == rolledRevision + 1 && events.Count == 2 && !board.TickCrownDeed(Time(4741)) &&
            Button(board, "mp-roll").Enabled,
            "Expiry changed normal one-decision AI progress or prevented return to the human.");
    }

    private static void CheckNavigationAndReset()
    {
        foreach (bool gesture in new[] { false, true })
        {
            var board = Rolled();
            board.HoldCrownDeedPresentationUntil(Time(5000));
            bool exited = gesture
                ? board.Update([Pinch(Button(board, "mp-exit"), 1, 300)], Time(300), Time(300))?.ButtonId == "mp-exit"
                : board.ActivateButton("mp-exit", Time(300));
            Require(exited && board.Screen == BoardScreen.CrownDeed && board.CrownDeedState.Phase == CrownDeedPhase.ExitConfirmation &&
                !board.IsCrownDeedPresentationActive(Time(300)) && Button(board, "mp-exit-cancel").Enabled,
                "Presentation blocked Exit/save choices or left their animation lock behind.");
            Require(board.ActivateButton("mp-exit-cancel", Time(400)) && Button(board, "mp-buy").Enabled,
                "Cancel Exit restored the old presentation lock.");
            board.HoldCrownDeedPresentationUntil(Time(5000));
            Require(board.ActivateButton("mp-exit", Time(500)) && board.ActivateButton("mp-save-exit", Time(800)),
                "A roll presentation blocked saving the already committed game.");
            string saved = board.ExportCrownDeedSave();
            Require(board.CompleteCrownDeedSave(board.CrownDeedSaveRequestId, true, Time(900)) && board.Screen == BoardScreen.Menu,
                "Successful save failed to leave the board.");
            board.LoadCrownDeedSave(saved, Time(1000), resume: true); board.ShowCrownDeed(Time(1010));
            Require(!board.IsCrownDeedPresentationActive(Time(1010)) && board.CrownDeedState.ActivePlayer!.Position == 3 &&
                board.CrownDeedState.Dice == new CrownDeedDice(1, 2) && Button(board, "mp-buy").Enabled,
                "Save/load retained the animation hold or saved an uncommitted previous roll.");
            board.HoldCrownDeedPresentationUntil(Time(5000));
            Require(board.ActivateButton("mp-exit", Time(1020)) && board.ActivateButton("mp-save-exit", Time(1320)) &&
                board.CompleteCrownDeedSave(board.CrownDeedSaveRequestId, true, Time(1330)) &&
                board.Screen == BoardScreen.Menu, "The drawer's saving exit was blocked by the presentation.");
        }

        var reset = Rolled();
        reset.HoldCrownDeedPresentationUntil(Time(5000)); reset.ResetInput(Time(300));
        Require(!reset.IsCrownDeedPresentationActive(Time(300)) && Button(reset, "mp-buy").Enabled,
            "Camera reset retained the roll presentation lock.");
        reset.HoldCrownDeedPresentationUntil(Time(5000)); reset.ShowPaint(Time(400)); reset.ShowCrownDeed(Time(500));
        Require(!reset.IsCrownDeedPresentationActive(Time(500)) && Button(reset, "mp-buy").Enabled,
            "Programmatic board navigation retained the roll presentation lock.");
        reset.HoldCrownDeedPresentationUntil(Time(499));
        Require(Button(reset, "mp-buy").Enabled, "An already expired hold disabled controls.");
        reset.HoldCrownDeedPresentationUntil(Time(5000)); reset.CancelCrownDeedPresentation(Time(600));
        Require(!reset.IsCrownDeedPresentationActive(Time(600)) &&
            reset.Update([Pinch(Button(reset, "mp-buy"), 10, 590)], Time(601), Time(601)) is null &&
            reset.Update([Pinch(Button(reset, "mp-buy"), 11, 620)], Time(620), Time(620)) is { ButtonId: "mp-buy" },
            "Projection cancellation retained the hold, replayed old input or rejected fresh input.");
        reset.ShowPaint(Time(700)); long paintRevision = reset.Revision;
        reset.CancelCrownDeedPresentation(Time(710));
        Require(reset.Screen == BoardScreen.Paint && reset.Revision == paintRevision &&
            reset.Update([Pinch(Button(reset, "menu"), 12, 705)], Time(720), Time(720))?.Current == BoardScreen.Menu,
            "Cancelling an inactive CrownDeed presentation changed or invalidated the active board.");
    }

    private static BoardSession Board(bool humansOnly = true)
    {
        var board = new BoardSession(crownDeed: new CrownDeedGame(seed: 31, initialRolls: [new(1, 2), new(1, 2)]));
        board.ShowCrownDeed(Time(0));
        Require(board.ActivateButton("mp-start-game", Time(10)), "Could not open CrownDeed setup.");
        if (humansOnly)
        {
            Require(board.ActivateButton("mp-ai-minus", Time(20)) && board.ActivateButton("mp-human-plus", Time(30)),
                "Could not configure two human players.");
        }
        Require(board.ActivateButton("mp-start", Time(40)), "Could not start CrownDeed.");
        return board;
    }
    private static BoardSession Rolled()
    {
        var board = Board(); Require(board.ActivateButton("mp-roll", Time(100)), "Could not roll fixture dice."); return board;
    }
    private static BoardButton Button(BoardSession board, string id) => board.Buttons.Single(button => button.Id == id);
    private static BoardHandSample Pinch(BoardButton button, long id, int at) => new(
        button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2, Time(at + 1000), id);
    private static BoardHandSample Fingers(BoardButton button) => new(double.NaN, double.NaN, DateTimeOffset.MinValue, 0)
    {
        TrackingId = 71, FourFingersExtended = true, FingersTogether = true,
        FingerAim = new(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2)
    };
    private static DateTimeOffset Time(int milliseconds) =>
        new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
