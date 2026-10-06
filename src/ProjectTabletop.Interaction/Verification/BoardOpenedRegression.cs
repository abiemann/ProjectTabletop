using ProjectTabletop.Interaction;

internal static class BoardOpenedRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 28, 23, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        ExplicitAndPointerLaunches();
        GestureLaunches();
        GameAndDrawerActionsAreNotLaunches();
        Console.WriteLine("Board-open notifications passed: explicit reopen, pointer, pinch and sideways-index launches, " +
            "committed navigation, held gesture rejection and game/drawer/save isolation.");
    }

    private static void ExplicitAndPointerLaunches()
    {
        var board = new BoardSession();
        var opened = Observe(board);
        Require(opened.Count == 0, "Constructing a session fabricated a board launch.");
        board.ShowCrownDeed(Time(0));
        var state = board.CrownDeedState;
        board.ShowCrownDeed(Time(10));
        Require(opened.SequenceEqual([BoardScreen.CrownDeed, BoardScreen.CrownDeed]) &&
                ReferenceEquals(state, board.CrownDeedState),
            "Explicit CrownDeed launch/reopen did not notify once each or changed the game.");
        Require(!board.ActivateButton("crown-deed", Time(20)) && opened.Count == 2,
            "A rejected menu action on CrownDeed raised a launch.");
        board.ShowMenu(Time(30));
        Require(opened.SequenceEqual([BoardScreen.CrownDeed, BoardScreen.CrownDeed, BoardScreen.Menu]),
            "Explicit navigation away omitted its new-screen notification.");
        Require(board.ActivateButton("crown-deed", Time(40)) && opened.Count == 4 && opened[^1] == BoardScreen.CrownDeed,
            "The menu pointer route did not emit exactly one CrownDeed launch.");
        board.ResetInput(Time(50));
        Require(opened.Count == 4, "An input reset restarted the board notification.");
    }

    private static void GestureLaunches()
    {
        foreach (bool fingers in new[] { false, true })
        {
            var board = new BoardSession();
            var opened = Observe(board);
            var button = board.Buttons.Single(item => item.Id == "crown-deed");
            double u = button.Bounds.X + button.Bounds.Width / 2;
            double v = button.Bounds.Y + button.Bounds.Height / 2;
            BoardNavigation? navigation;
            if (fingers)
            {
                var grouped = new BoardHandSample(u, v, DateTimeOffset.MinValue, 0)
                {
                    TrackingId = 47, FingerAim = new(u, v), FourFingersExtended = true, FingersTogether = true
                };
                Require(At(board, 10, grouped) is null && At(board, 110, grouped) is null && opened.Count == 0,
                    "A grouped hover raised a board launch before selection.");
                var separated = grouped with { FingersTogether = false, IndexFingerSeparated = true };
                Require(At(board, 210, separated) is null, "A single sideways-index frame selected prematurely.");
                navigation = At(board, 290, separated);
                Require(At(board, 390, separated) is null && At(board, 490, separated) is null,
                    "A held separated hand repeated its menu navigation.");
            }
            else
            {
                Require(At(board, 10, new(u, v, DateTimeOffset.MinValue, 0)) is null && opened.Count == 0,
                    "Menu hover raised a launch before the pinch.");
                var pinched = new BoardHandSample(u, v, Time(1020), 1);
                navigation = At(board, 20, pinched);
                Require(At(board, 30, pinched) is null && At(board, 40, pinched) is null,
                    "A held pinch repeated its menu navigation.");
            }
            Require(navigation is { Previous: BoardScreen.Menu, Current: BoardScreen.CrownDeed, ButtonId: "crown-deed" } &&
                    navigation.Gesture == (fingers ? BoardSelectionGesture.IndexSeparation : BoardSelectionGesture.Pinch) &&
                    opened.SequenceEqual([BoardScreen.CrownDeed]),
                "An accepted menu gesture omitted or duplicated its CrownDeed launch notification.");
            Require(board.ActivateButton("mp-exit", Time(600)) && opened.Count == 1,
                "Opening the inactive drawer was treated as a new board.");
            board.TickCrownDeed(Time(900));
            Require(board.ActivateButton("mp-exit-game", Time(910)) &&
                    opened.SequenceEqual([BoardScreen.CrownDeed, BoardScreen.Menu]),
                "Exit Game did not notify the actual navigation to Menu once.");
        }
    }

    private static void GameAndDrawerActionsAreNotLaunches()
    {
        var board = new BoardSession(crownDeed: new CrownDeedGame(seed: 109, initialRolls: [new(1, 2)]));
        var opened = Observe(board);
        board.ShowCrownDeed(Time(0));
        int milliseconds = 0;
        foreach (string action in new[] { "mp-start-game", "mp-ai-minus", "mp-human-plus", "mp-start", "mp-roll", "mp-buy", "mp-exit" })
            Require(board.ActivateButton(action, Time(milliseconds += 20)) && opened.Count == 1,
                $"The accepted {action} action raised a spurious board-open notification.");
        Require(board.CrownDeedState.Phase == CrownDeedPhase.ExitConfirmation && board.CrownDeedDrawerOpen,
            "The event fixture never reached the active save drawer.");
        board.TickCrownDeed(Time(milliseconds += 300));
        Require(board.ActivateButton("mp-save-exit", Time(milliseconds += 20)) && opened.Count == 1,
            "Beginning an asynchronous save prematurely reported navigation.");
        long request = board.CrownDeedSaveRequestId;
        Require(board.CompleteCrownDeedSave(request, false, Time(milliseconds += 20), "Fixture failure") && opened.Count == 1 &&
                board.ActivateButton("mp-exit-cancel", Time(milliseconds += 20)) && opened.Count == 1,
            "Save failure or closing the drawer was treated as a board launch.");
        Require(board.ActivateButton("mp-exit", Time(milliseconds += 20)), "The retry drawer did not open.");
        board.TickCrownDeed(Time(milliseconds += 300));
        Require(board.ActivateButton("mp-save-exit", Time(milliseconds += 20)) && opened.Count == 1,
            "Retrying Save and Exit reported navigation before persistence completed.");
        request = board.CrownDeedSaveRequestId;
        Require(board.CompleteCrownDeedSave(request, true, Time(milliseconds += 20)) &&
                opened.SequenceEqual([BoardScreen.CrownDeed, BoardScreen.Menu]) &&
                !board.CompleteCrownDeedSave(request, true, Time(milliseconds += 20)) && opened.Count == 2,
            "A completed save omitted, duplicated or replayed its Menu launch notification.");
    }

    private static List<BoardScreen> Observe(BoardSession board)
    {
        var result = new List<BoardScreen>();
        long previousRevision = board.Revision;
        board.BoardOpened += screen =>
        {
            Require(board.Screen == screen && board.Revision > previousRevision && board.HoveredButtonIds.Count == 0,
                "Board-open subscribers observed an uncommitted screen, revision or hover reset.");
            previousRevision = board.Revision;
            result.Add(screen);
        };
        return result;
    }
    private static BoardNavigation? At(BoardSession board, int milliseconds, BoardHandSample hand) =>
        board.Update([hand], Time(milliseconds), Time(milliseconds));
    private static DateTimeOffset Time(int milliseconds) => Epoch.AddMilliseconds(milliseconds);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
