using ProjectTabletop.Interaction;

internal static class PaintBoardRegression
{
    private static readonly BoardRect Handle = new(.01, .87, .18, .12);

    public static void Run()
    {
        CheckMenuAndDrawerLayout();
        CheckHandleRelease();
        CheckSettledCaptionHolds();
        CheckSaveReadiness();
        CheckResetAction();
        CheckSwipesAndReset();
        Console.WriteLine("Paint interaction verification passed: bottom-left drawer, uppercase caption holds, " +
            "settlement/release/clear-lettering barriers, single Save and Reset without navigation, shared swipe fades and input reset.");
    }

    private static void CheckMenuAndDrawerLayout()
    {
        var board = new BoardSession();
        Require(board.Buttons.All(button => button.Id != "paint") &&
            !board.ActivateButton("paint", Time(-1100)), "Paint was selectable on the first menu page.");
        Require(board.ActivateButton("menu-scroll-down", Time(-1000)) && board.TickMenu(Time(-350)),
            "The menu could not reveal Paint before its navigation checks.");
        var paint = board.Buttons.Single(button => button.Id == "paint");
        Require(paint.Label == "Paint" && paint.Destination == BoardScreen.Paint &&
            board.Buttons[1].Id == "paint" && paint.Bounds.X == .52 &&
            Math.Abs(paint.Bounds.Y - .25) < 1e-9 && board.Buttons[0].Id == "roulette",
            "Paint must sit beside Roulette on the second menu page.");
        Require(board.ActivateButton("paint", Time(0)) && board.Screen == BoardScreen.Paint && board.Title == "Paint",
            "The Paint menu target did not launch Paint.");
        Require(board.Buttons.Single() is { Id: "paint-drawer-open", Label: "^", Hold: BoardButtonHold.Once, Enabled: true } up &&
            up.Bounds == Handle && !board.PaintDrawerOpen && !board.PaintSaveEnabled &&
            board.GetPaintDrawerProgress(Time(50)) == 0 && !board.ActivateButton("menu", Time(50)) &&
            !board.ActivateButton("paint-reset", Time(50)) && !board.ActivateButton("paint-save", Time(50)),
            "Paint exposed an action before opening the drawer.");
        long navigation = board.NavigationRevision;
        int opened = 0; board.BoardOpened += _ => opened++;
        Require(board.ActivateButton("paint-drawer-open", Time(100)) && board.PaintDrawerOpen &&
            board.PaintDrawerOpenedAt == Time(100), "The Paint drawer did not open at the supplied clock.");
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(["paint-drawer-close", "menu", "paint-reset", "paint-save"]) &&
            board.Buttons.Select(button => button.Label).SequenceEqual(["v", "EXIT", "RESET", "SAVE"]) &&
            board.Buttons.Select(button => button.Bounds).SequenceEqual(
                [Handle, new(.20, .87, .155, .12), new(.365, .87, .155, .12), new(.53, .87, .155, .12)]) &&
            board.Buttons.All(button => button.Hold == BoardButtonHold.Once) &&
            board.Buttons.Skip(1).All(button => !button.Enabled), "Paint's open drawer differs from the shared bottom row.");
        long revision = board.Revision;
        Require(board.GetPaintDrawerProgress(Time(250)) == .5 && board.Revision == revision &&
            !board.TickPaint(Time(399)) && board.TickPaint(Time(400)) && !board.TickPaint(Time(401)) &&
            board.Buttons.Single(button => button.Id == "menu").Enabled &&
            board.Buttons.Single(button => button.Id == "paint-reset").Enabled &&
            !board.Buttons.Single(button => button.Id == "paint-save").Enabled,
            "Paint actions did not settle once after 300ms, or enabled SAVE without a painting.");
        board.PaintSaveEnabled = true;
        int gestureAt = 500;
        foreach (var button in board.Buttons)
        {
            var aim = new BoardAim(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
            var hand = new BoardHandSample(aim.U, aim.V, Time(gestureAt + 1000), gestureAt)
                { TrackingId = 7, FingerAim = aim, FourFingersExtended = true, FingersTogether = true };
            Require(board.Update([hand], Time(gestureAt), Time(gestureAt)) is null &&
                board.Update([hand], Time(gestureAt + 100), Time(gestureAt + 100)) is null &&
                board.Update([hand with { FingersTogether = false, IndexFingerSeparated = true }], Time(gestureAt + 200), Time(gestureAt + 200)) is null &&
                board.Update([hand with { FingersTogether = false, IndexFingerSeparated = true }], Time(gestureAt + 280), Time(gestureAt + 280)) is null &&
                board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
                "Paint's caption-hold control accepted a pinch or sideways-index gesture.");
            gestureAt += 300;
        }
        Require(board.ActivateButton("paint-save", Time(gestureAt)) && board.NavigationRevision == navigation && opened == 0 &&
            board.PaintDrawerOpen && board.PaintSaveEnabled, "Pointer SAVE restarted the painting or closed the drawer.");
        Require(board.ActivateButton("paint-drawer-close", Time(gestureAt + 100)) && !board.PaintDrawerOpen &&
            board.NavigationRevision == navigation && opened == 0 && board.PaintSaveEnabled,
            "Closing Paint's controls restarted the existing painting.");
    }

    private static void CheckHandleRelease()
    {
        var board = Ready();
        for (int at = 100; at <= 1300; at += 100)
            Require(Held(board, "paint-drawer-open", at).Count == 0 && !board.PaintDrawerOpen,
                "Paint's arrow opened without an initial clear caption.");
        Press(board, "paint-drawer-open", 1400);
        for (int at = 2600; at <= 3900; at += 100)
            Require(Held(board, "paint-drawer-close", at).Count == 0 && board.PaintDrawerOpen,
                "Resting fingers closed the drawer through the replacement arrow.");
        for (int at = 4000; at <= 4400; at += 100) Clear(board, "paint-drawer-close", at);
        Press(board, "paint-drawer-close", 4500);
        Require(!board.PaintDrawerOpen && board.PaintSaveEnabled, "A fresh down-arrow hold failed or discarded the painting.");
    }

    private static void CheckSettledCaptionHolds()
    {
        var board = Ready();
        long navigation = board.NavigationRevision;
        Require(board.ActivateButton("paint-drawer-open", Time(100)), "Paint's hold fixture could not open.");
        Require(!board.ActivateButton("menu", Time(399)) && !board.ActivateButton("paint-save", Time(399)),
            "A moving Paint action accepted pointer input.");
        Clear(board, "paint-save", 200);
        Require(Held(board, "paint-save", 300).Count == 0 && board.HoldProgress(Time(300)).Count == 0,
            "Moving SAVE lettering started a hold.");
        board.ObserveHeldButtons([], Time(399), Time(400), ["paint-save"]);
        for (int at = 500; at <= 1700; at += 100)
            Require(Held(board, "paint-save", at).Count == 0 && board.HoldProgress(Time(at)).Count == 0,
                "Old or moving-caption clearance armed a settled SAVE.");
        Clear(board, "paint-save", 1800);
        for (int at = 1900; at <= 2300; at += 100) Held(board, "paint-save", at);
        Require(board.HoldProgress(Time(2300)).Count == 1, "Fresh SAVE evidence did not begin a hold.");
        Clear(board, "paint-save", 2400);
        Require(board.HoldProgress(Time(2400)).Count == 0, "Intact SAVE lettering failed to cancel partial progress.");
        Press(board, "paint-save", 2500);
        for (int at = 3700; at <= 4900; at += 100)
            Require(Held(board, "paint-save", at).Count == 0, "SAVE repeated without lifting the hand.");
        for (int at = 5000; at <= 5400; at += 100) Clear(board, "paint-save", at);
        Press(board, "paint-save", 5500);
        Require(board.NavigationRevision == navigation && board.Screen == BoardScreen.Paint && board.PaintSaveEnabled,
            "Saving twice restarted the Paint session or discarded export readiness.");
    }

    private static void CheckSaveReadiness()
    {
        var board = Ready();
        Open(board);
        Clear(board, "paint-save", 500);
        for (int at = 600; at <= 1000; at += 100) Held(board, "paint-save", at);
        board.PaintSaveEnabled = false;
        Require(board.HoldProgress(Time(1000)).Count == 0 && !board.ActivateButton("paint-save", Time(1050)),
            "Disabling SAVE retained a partial hold or accepted a pointer.");
        board.PaintSaveEnabled = true;
        for (int at = 1100; at <= 2300; at += 100)
            Require(Held(board, "paint-save", at).Count == 0, "Re-enabled SAVE borrowed its old clear-caption readiness.");
        Press(board, "paint-save", 2400);

        board = Ready(); Open(board);
        Clear(board, "menu", 500);
        for (int at = 600; at <= 1000; at += 100) Held(board, "menu", at);
        board.PaintSaveEnabled = false;
        for (int at = 1100; at < 1600; at += 100) Held(board, "menu", at);
        Require(Held(board, "menu", 1600).SequenceEqual(["menu"]) && board.Screen == BoardScreen.Menu,
            "Changing SAVE readiness interrupted the independent EXIT hold.");
    }

    private static void CheckResetAction()
    {
        var board = Ready();
        board.PaintSaveEnabled = false;
        long navigation = board.NavigationRevision;
        int opened = 0; board.BoardOpened += _ => opened++;
        Require(board.PaintResetRevision == 0 && !board.ActivateButton("paint-reset", Time(50)) &&
            Held(board, "paint-reset", 50).Count == 0, "A closed Paint drawer accepted RESET.");
        Require(board.ActivateButton("paint-drawer-open", Time(100)), "The RESET fixture could not open its drawer.");
        Clear(board, "paint-reset", 200);
        Require(Held(board, "paint-reset", 300).Count == 0 && board.HoldProgress(Time(300)).Count == 0 &&
            !board.ActivateButton("paint-reset", Time(399)), "Moving RESET lettering accepted input.");
        board.ObserveHeldButtons([], Time(399), Time(400), ["paint-reset"]);
        Require(board.Buttons.Single(button => button.Id == "paint-reset").Enabled &&
            !board.Buttons.Single(button => button.Id == "paint-save").Enabled,
            "A blank canvas disabled RESET or enabled SAVE.");
        for (int at = 500; at <= 1700; at += 100)
            Require(Held(board, "paint-reset", at).Count == 0 && board.PaintResetRevision == 0,
                "RESET borrowed clear-caption evidence from its opening animation.");
        Press(board, "paint-reset", 1800);
        Require(board.PaintResetRevision == 1 && board.NavigationRevision == navigation && opened == 0 &&
            board.Screen == BoardScreen.Paint && board.PaintDrawerOpen && !board.PaintSaveEnabled,
            "RESET did not advance its action revision once while preserving the board and open drawer.");
        for (int at = 3000; at <= 4100; at += 100)
            Require(Held(board, "paint-reset", at).Count == 0 && board.PaintResetRevision == 1,
                "RESET repeated while the same hand still covered its caption.");
        for (int at = 4200; at <= 4600; at += 100) Clear(board, "paint-reset", at);
        Press(board, "paint-reset", 4700);
        Require(board.PaintResetRevision == 2 && board.ActivateButton("paint-reset", Time(5900)) &&
            board.PaintResetRevision == 3 && board.NavigationRevision == navigation && opened == 0 && board.PaintDrawerOpen,
            "A fresh RESET hold or pointer action failed to advance the monotonic revision without navigating.");
        board.ResetInput(Time(6000));
        board.ShowMenu(Time(6100)); board.ShowPaint(Time(6200));
        Require(board.PaintResetRevision == 3 && !board.PaintDrawerOpen &&
            !board.ActivateButton("paint-reset", Time(6250)), "Input reset or navigation reused a Paint reset revision.");
    }

    private static void CheckSwipesAndReset()
    {
        var board = Ready(); Open(board);
        long navigation = board.NavigationRevision;
        Require(board.ControlSwipesEnabled && board.SetButtonsVisible(false, Time(500)) &&
            board.Buttons.Count == 0 && board.GetVisibleButtons(Time(650)).Count == 4 &&
            Math.Abs(board.GetButtonsOpacity(Time(650)) - .5) < 1e-9 &&
            !board.ActivateButton("menu", Time(650)) && Held(board, "paint-save", 650).Count == 0 &&
            !board.ActivateButton("paint-reset", Time(650)) && Held(board, "paint-reset", 650).Count == 0 &&
            board.TickControlVisibility(Time(800)) && board.GetVisibleButtons(Time(800)).Count == 0 &&
            board.PaintDrawerOpen && board.PaintSaveEnabled && board.NavigationRevision == navigation,
            "The hide swipe did not fade all Paint controls together or changed the canvas/drawer state.");
        Require(board.SetButtonsVisible(true, Time(900)) && board.Buttons.Count == 0 &&
            board.GetVisibleButtons(Time(1050)).Count == 4 && Math.Abs(board.GetButtonsOpacity(Time(1050)) - .5) < 1e-9 &&
            !board.ActivateButton("paint-reset", Time(1050)) && Held(board, "paint-reset", 1050).Count == 0 &&
            board.PaintResetRevision == 0 && board.TickControlVisibility(Time(1200)) && board.Buttons.Count == 4 && board.PaintDrawerOpen,
            "The reveal swipe did not restore the same Paint drawer with a shared fade.");
        for (int at = 1300; at <= 2400; at += 100)
            Require(Held(board, "paint-save", at).Count == 0, "Restored SAVE skipped fresh clear-caption evidence.");
        Press(board, "paint-save", 2500);
        Require(board.ActivateButton("paint-drawer-close", Time(3700)) && board.SetButtonsVisible(false, Time(3800)) &&
            board.TickControlVisibility(Time(4100)) && board.SetButtonsVisible(true, Time(4200)) &&
            board.TickControlVisibility(Time(4500)) && board.Buttons.Single().Id == "paint-drawer-open",
            "Swiping a closed Paint drawer exposed an action.");
        Require(board.ActivateButton("paint-drawer-open", Time(4600)), "The reset fixture could not reopen its drawer.");
        board.ResetInput(Time(4700));
        Require(!board.PaintDrawerOpen && board.PaintSaveEnabled && board.NavigationRevision == navigation,
            "Resetting Paint input retained an open drawer or restarted the painting.");
        board.ActivateButton("paint-drawer-open", Time(4800));
        board.ShowMenu(Time(4900)); board.ShowPaint(Time(5000));
        Require(!board.PaintDrawerOpen && board.Buttons.Single().Id == "paint-drawer-open" && !board.TickPaint(Time(5400)),
            "Returning to Paint retained an open or in-flight drawer.");
    }

    private static BoardSession Ready()
    {
        var board = new BoardSession(); board.ShowPaint(Time(0)); board.PaintSaveEnabled = true; return board;
    }
    private static void Open(BoardSession board) =>
        Require(board.ActivateButton("paint-drawer-open", Time(100)) && board.TickPaint(Time(400)), "Paint drawer fixture did not settle.");
    private static IReadOnlyList<string> Held(BoardSession board, string id, int at) =>
        board.ObserveHeldButtons([id], Time(at), Time(at), []);
    private static void Clear(BoardSession board, string id, int at) =>
        Require(board.ObserveHeldButtons([], Time(at), Time(at), [id]).Count == 0, "Clear lettering selected a Paint control.");
    private static void Press(BoardSession board, string id, int clearAt)
    {
        Clear(board, id, clearAt);
        for (int at = clearAt + 100; at < clearAt + 1100; at += 100)
            Require(Held(board, id, at).Count == 0, "Paint's caption hold acted before one second: " + id);
        Require(Held(board, id, clearAt + 1100).SequenceEqual([id]), "A full Paint caption hold did not execute: " + id);
    }
    private static DateTimeOffset Time(int milliseconds) =>
        new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
