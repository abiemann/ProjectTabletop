using ProjectTabletop.Interaction;

internal static class GlobeBoardRegression
{
    public static void Run()
    {
        CheckEntranceAndRotation();
        CheckDrawerAndSmoothControls();
        CheckNavigationAndPinches();
        CheckFourFingerSelection();
        CheckHoldToRepeat();
        Console.WriteLine("Globe verification passed: pure entrance/spin frames, bounded smooth zoom, hidden rotation " +
            "controls, timed drawer targets, pinch and four-finger selection, held-action suppression and reset barriers, " +
            "hold-to-repeat zoom timing, gaps, stale frames and gesture exclusion.");
    }

    private static void CheckHoldToRepeat()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        string[] zoomIn = ["globe-zoom-in"];
        Require(board.ObserveHeldButtons(zoomIn, Time(1000), Time(1000)).Count == 0,
            "A hidden hold button accepted evidence while the drawer was closed.");
        Require(board.ActivateButton("globe-drawer-open", Time(3000)) && board.TickGlobe(Time(3300)),
            "The hold fixture could not open the drawer.");
        Require(Button(board, "globe-zoom-in").HoldToRepeat && Button(board, "globe-zoom-out").HoldToRepeat &&
            !Button(board, "globe-exit").HoldToRepeat && !Button(board, "globe-drawer-close").HoldToRepeat,
            "Only Globe's zoom buttons should be hold-to-repeat.");
        IReadOnlyList<string> Hold(string[] ids, int time) => board.ObserveHeldButtons(ids, Time(time), Time(time + 40));
        for (int time = 4000; time < 5000; time += 100)
            Require(Hold(zoomIn, time).Count == 0, "A hold activated before a full second of caption evidence.");
        Require(Hold(zoomIn, 5000).SequenceEqual(zoomIn) && Near(board.GetGlobeSnapshot(Time(5100)).TargetZoom, 1.875),
            "One second of held evidence did not zoom exactly once.");
        Require(Hold(zoomIn, 5000).Count == 0 && Hold(zoomIn, 4900).Count == 0,
            "A repeated or delayed frame advanced the hold.");
        for (int time = 5100; time < 6000; time += 100)
            Require(time is > 5300 and < 5500 || Hold(zoomIn, time).Count == 0,
                "A continued hold repeated before another second; a 200 ms evidence gap released it.");
        Require(Hold(zoomIn, 6000).SequenceEqual(zoomIn) && Near(board.GetGlobeSnapshot(Time(6400)).TargetZoom, 1.5 * 1.25 * 1.25),
            "A continued hold did not repeat after each further second.");
        Require(Hold([], 6400).Count == 0 && Hold(zoomIn, 6500).Count == 0 && Hold(zoomIn, 7400).Count == 0 &&
            Hold(zoomIn, 7500).SequenceEqual(zoomIn),
            "A gap over 350 ms did not restart the one-second hold.");
        Require(Hold(["globe-exit", "globe-drawer-close"], 7600).Count == 0 && board.GlobeDrawerOpen &&
            board.Screen == BoardScreen.Globe, "Evidence over gesture buttons activated them.");
        Require(board.ObserveHeldButtons(zoomIn, Time(8500), Time(9000)).Count == 0,
            "Stale hold evidence activated a button.");
        string[] zoomOut = ["globe-zoom-out"];
        Require(Hold(zoomOut, 10000).Count == 0 && Hold(zoomOut, 11000).SequenceEqual(zoomOut),
            "Zoom - did not share the hold-to-repeat behaviour.");
        board.ShowMenu(Time(11100)); board.ShowGlobe(Time(11200));
        Require(board.ObserveHeldButtons(zoomIn, Time(12500), Time(12500)).Count == 0,
            "Leaving Globe retained a hold or its drawer.");
    }

    private static void CheckEntranceAndRotation()
    {
        var board = new BoardSession();
        Require(board.Buttons[^1] is { Id: "globe", Label: "Globe", Destination: BoardScreen.Globe },
            "The Globe menu tile did not replace Diablo in its original position.");
        board.ShowGlobe(Time(100));
        long revision = board.Revision;
        GlobeSnapshot start = board.GetGlobeSnapshot(Time(100));
        GlobeSnapshot middle = board.GetGlobeSnapshot(Time(1600));
        GlobeSnapshot finished = board.GetGlobeSnapshot(Time(3100));
        Require(start is { IntroProgress: 0, ElapsedSeconds: 0, TargetZoom: 1.5 } && Near(start.Zoom, .06),
            "Earth did not start far away at its default zoom.");
        Require(Near(middle.IntroProgress, .5) && middle.Zoom > start.Zoom && middle.Zoom < finished.Zoom &&
            Near(finished.IntroProgress, 1) && Near(finished.Zoom, 1.5), "The three-second approach did not fill the default view.");
        Require(Near(finished.RotationDegrees, 3) && Near(board.GetGlobeSnapshot(Time(60100)).RotationDegrees, 60),
            "Earth did not spin slowly at one degree per second.");
        Require(board.GetGlobeSnapshot(Time(1600)) == middle && board.Revision == revision &&
            start.Revision == finished.Revision, "Presentation sampling mutated interaction state or used an inconsistent clock.");
        Require(Near(board.GetGlobeSnapshot(Time(360100)).RotationDegrees, 0) &&
            board.GetGlobeSnapshot(Time(99)) is { IntroProgress: 0, ElapsedSeconds: 0 },
            "Long-running spin wrapping or a pre-launch clock produced invalid globe geometry.");
        board.ShowMenu(Time(361000)); board.ShowGlobe(Time(362000));
        Require(board.GetGlobeSnapshot(Time(362000)) is { TargetZoom: 1.5, IntroProgress: 0, RotationDegrees: 0 } &&
            !board.GlobeDrawerOpen && board.GlobeDrawerOpenedAt is null,
            "Returning to Globe did not restart the approach, orientation and closed drawer.");

        // The shader centres longitude -rotation: a Pacific home (-120 degrees)
        // opens at rotation 120 and keeps spinning eastward from there.
        var home = new BoardSession(globe: new GlobeState(-120));
        home.ShowGlobe(Time(100));
        Require(Near(home.GetGlobeSnapshot(Time(100)).RotationDegrees, 120) &&
            Near(home.GetGlobeSnapshot(Time(3100)).RotationDegrees, 123),
            "Globe did not open on its home longitude.");
        home.ShowMenu(Time(9000)); home.ShowGlobe(Time(10000));
        Require(Near(home.GetGlobeSnapshot(Time(10000)).RotationDegrees, 120) &&
            Near(new GlobeState(-180).GetSnapshot(Time(0)).RotationDegrees, 0) &&
            Near(new GlobeState(150).GetSnapshot(Time(0)).RotationDegrees, 0),
            "Relaunching lost the home longitude, or an unstarted Globe rotated.");
        var east = new GlobeState(150); east.Start(Time(0));
        Require(Near(east.GetSnapshot(Time(0)).RotationDegrees, 210), "An eastern home longitude opened at the wrong rotation.");
    }

    private static void CheckDrawerAndSmoothControls()
    {
        var board = new BoardSession(); int opened = 0;
        board.BoardOpened += screen => { if (screen == BoardScreen.Globe) opened++; };
        board.ShowGlobe(Time(0));
        Require(board.Buttons.Single() is { Id: "globe-drawer-open", Label: "^", Enabled: true } &&
            board.Buttons[0].Bounds == new BoardRect(.01, .87, .18, .12),
            "Globe did not begin with only its left drawer handle.");
        long revision = board.Revision;
        Require(!board.ActivateButton("globe-exit", Time(100)) && !board.ActivateButton("globe-zoom-in", Time(200)) &&
            !board.ActivateButton("globe-zoom-out", Time(300)) && !board.ActivateButton("globe-rotate-right", Time(400)) &&
            !board.ActivateButton("globe-rotate-left", Time(500)) && board.Revision == revision,
            "A hidden Globe control remained executable.");
        Require(board.ActivateButton("globe-drawer-open", Time(4000)) && board.GlobeDrawerOpen &&
            board.GlobeDrawerOpenedAt == Time(4000), "The drawer handle did not open a timed drawer.");
        string[] ids = ["globe-drawer-close", "globe-exit", "globe-zoom-out", "globe-zoom-in"];
        string[] labels = ["v", "Exit", "Zoom -", "Zoom +"];
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(ids) &&
            board.Buttons.Select(button => button.Label).SequenceEqual(labels), "The open drawer contains incorrect controls.");
        BoardRect[] bounds = [new(.01, .87, .18, .12), new(.20, .87, .155, .12),
            new(.365, .87, .155, .12), new(.53, .87, .155, .12)];
        Require(board.Buttons.Select(button => button.Bounds).SequenceEqual(bounds) &&
            Button(board, "globe-drawer-close").Bounds == new BoardRect(.01, .87, .18, .12),
            "Drawer controls did not form one bottom row beside the fixed handle.");
        // The row ends left of the bottom-right credit and zoom readout.
        foreach (BoardButton button in board.Buttons)
            Require(button.Bounds.X > 0 && button.Bounds.X + button.Bounds.Width < .69 &&
                button.Bounds.Width >= (button.Id == "globe-drawer-close" ? .18 : .155) &&
                button.Bounds.Y == .87 && button.Bounds.Height == .12 &&
                button.Bounds.Y + button.Bounds.Height < 1 &&
                !board.Buttons.Any(other => other.Id != button.Id &&
                    other.Bounds.X < button.Bounds.X + button.Bounds.Width &&
                    other.Bounds.X + other.Bounds.Width > button.Bounds.X &&
                    other.Bounds.Y < button.Bounds.Y + button.Bounds.Height &&
                    other.Bounds.Y + other.Bounds.Height > button.Bounds.Y),
                "A drawer control lacks grouped-finger room, is outside the board or overlaps another target.");
        revision = board.Revision;
        Require(Near(board.GetGlobeDrawerProgress(Time(3999)), 0) && Near(board.GetGlobeDrawerProgress(Time(4150)), .5) &&
            Near(board.GetGlobeDrawerProgress(Time(4300)), 1) && board.Revision == revision &&
            board.Buttons.Where(button => button.Id != "globe-drawer-close").All(button => !button.Enabled) &&
            !board.ActivateButton("globe-exit", Time(4150)) && !board.ActivateButton("globe-zoom-in", Time(4299)),
            "Drawer sampling changed state or a moving control became executable.");
        Require(!board.TickGlobe(Time(4299)) && board.TickGlobe(Time(4300)) && !board.TickGlobe(Time(4301)) &&
            board.Buttons.All(button => button.Enabled) && Near(board.GetGlobeDrawerProgress(Time(4100)), 1) && opened == 1,
            "Settled drawer controls did not enable once, or the drawer restarted the Earth.");

        GlobeSnapshot before = board.GetGlobeSnapshot(Time(4400));
        Require(board.ActivateButton("globe-zoom-in", Time(4400)), "Zoom + did not accept a settled laptop selection.");
        GlobeSnapshot instant = board.GetGlobeSnapshot(Time(4400));
        GlobeSnapshot halfway = board.GetGlobeSnapshot(Time(4575));
        GlobeSnapshot final = board.GetGlobeSnapshot(Time(4750));
        Require(Near(instant.Zoom, before.Zoom) && Near(instant.TargetZoom, 1.875) && halfway.Zoom > instant.Zoom &&
            halfway.Zoom < final.Zoom && Near(final.Zoom, 1.875), "Zooming jumped immediately or failed to settle smoothly.");
        Require(board.ActivateButton("globe-zoom-in", Time(4750)) && board.ActivateButton("globe-zoom-in", Time(4750)),
            "Consecutive zoom selections were lost.");
        Require(Near(board.GetGlobeSnapshot(Time(5100)).TargetZoom, 1.5 * Math.Pow(1.25, 3)),
            "Quick selections failed to accumulate zoom targets.");
        for (int time = 5500; time < 15000; time += 400) board.ActivateButton("globe-zoom-in", Time(time));
        Require(Near(board.GetGlobeSnapshot(Time(15000)).Zoom, GlobeState.MaximumZoom) &&
            !Button(board, "globe-zoom-in").Enabled && !board.ActivateButton("globe-zoom-in", Time(15000)),
            "Zoom exceeded its maximum or left the capped control enabled.");
        for (int time = 16000; time < 36000; time += 400) board.ActivateButton("globe-zoom-out", Time(time));
        Require(Near(board.GetGlobeSnapshot(Time(36000)).Zoom, GlobeState.MinimumZoom) &&
            !Button(board, "globe-zoom-out").Enabled && !board.ActivateButton("globe-zoom-out", Time(36000)),
            "Zoom exceeded its minimum or left the capped control enabled.");
        revision = board.Revision;
        Require(!board.ActivateButton("globe-rotate-right", Time(37000)) &&
            !board.ActivateButton("globe-rotate-left", Time(38000)) && !board.ActivateButton("unknown", Time(39000)) &&
            board.Revision == revision && Near(board.GetGlobeSnapshot(Time(39000)).RotationDegrees, 39),
            "A removed action changed state or the drawer interrupted the Earth's automatic spin.");
        Require(board.ActivateButton("globe-drawer-close", Time(40000)) && !board.GlobeDrawerOpen &&
            board.Buttons.Single().Id == "globe-drawer-open" && opened == 1,
            "Closing the drawer did not hide its controls or reopened the board.");
        board.ShowGlobe(Time(41000));
        Require(board.GetGlobeSnapshot(Time(44000)) is { TargetZoom: 1.5 } &&
            Near(board.GetGlobeSnapshot(Time(44000)).Zoom, 1.5) && !board.GlobeDrawerOpen,
            "Restarting Globe retained the previous zoom or drawer.");
        Require(board.ActivateButton("globe-drawer-open", Time(45000)) &&
            board.ActivateButton("globe-drawer-close", Time(45100)) && !board.TickGlobe(Time(45400)) &&
            !board.GlobeDrawerOpen, "An interrupted drawer entrance reappeared after its deadline.");
    }

    private static void CheckNavigationAndPinches()
    {
        var board = new BoardSession(); BoardButton globe = Button(board, "globe");
        Require(At(board, 100, Pinch(globe, 1, 100)) is { Previous: BoardScreen.Menu, Current: BoardScreen.Globe,
            ButtonId: "globe", Gesture: BoardSelectionGesture.Pinch } && board.Title == "Globe", "A menu pinch did not launch Globe.");
        BoardButton handle = Button(board, "globe-drawer-open");
        Require(At(board, 140, Pinch(handle, 1, 100)) is null && !board.GlobeDrawerOpen,
            "A held launch pinch also opened the drawer.");
        Require(At(board, 180, Pinch(handle, 2, 180)) is { Previous: BoardScreen.Globe, Current: BoardScreen.Globe,
            ButtonId: "globe-drawer-open" }, "A fresh pinch failed to open the drawer.");
        BoardButton zoom = Button(board, "globe-zoom-in"), exit = Button(board, "globe-exit");
        Require(At(board, 220, Pinch(exit, 3, 220)) is null && board.Screen == BoardScreen.Globe,
            "A pinch selected a moving drawer control.");
        Require(board.Update([Pinch(exit, 4, 300)], Time(450), Time(500)) is null &&
            At(board, 520, Pinch(exit, 5, 300)) is null &&
            At(board, 540, Pinch(exit, 6, 540) with { SelectionFrameTime = Time(300) }) is null &&
            board.Screen == BoardScreen.Globe,
            "A delayed frame, old pinch origin or retained aim crossed the drawer's completion barrier.");
        // Zoom buttons are hold-to-repeat: a pinch, fresh or held, never selects them.
        Require(At(board, 580, Pinch(zoom, 7, 580)) is null && At(board, 620, Pinch(zoom, 7, 580)) is null &&
            board.HoveredButtonIds.Count == 0 && Near(board.GetGlobeSnapshot(Time(700)).TargetZoom, 1.5),
            "A pinch selected or hovered a hold-to-repeat zoom button.");
        Require(At(board, 740, Pinch(Button(board, "globe-drawer-close"), 8, 740)) is { ButtonId: "globe-drawer-close" } &&
            At(board, 780, Pinch(handle, 8, 740)) is null && !board.GlobeDrawerOpen,
            "Closing the drawer left controls exposed or a held pinch reopened it.");
        Require(At(board, 820, Pinch(handle, 9, 820)) is { ButtonId: "globe-drawer-open" },
            "The drawer could not reopen with a new pinch.");
        Require(At(board, 1180, Pinch(Button(board, "globe-exit"), 10, 1180)) is
            { Previous: BoardScreen.Globe, Current: BoardScreen.Menu, ButtonId: "globe-exit" } &&
            At(board, 1220, Pinch(globe, 10, 1180)) is null,
            "Drawer Exit did not leave the board once or a held pulse reentered it.");
        board.ShowGlobe(Time(1260));
        Require(At(board, 1300, Pinch(handle, 11, 1240)) is null &&
            At(board, 1340, Pinch(handle, 12, 1340)) is not null,
            "Direct navigation retained an old execution or blocked a fresh drawer gesture.");
        board.ResetInput(Time(1380));
        Require(!board.GlobeDrawerOpen && board.Buttons.Single().Id == "globe-drawer-open" &&
            At(board, 1420, Pinch(handle, 13, 1360)) is null &&
            At(board, 1460, Pinch(handle, 14, 1460)) is not null &&
            At(board, 1820, Pinch(exit, 15, 1820)) is not null,
            "Input reset failed to close the drawer, reject stale executions or accept fresh controls.");
    }

    private static void CheckFourFingerSelection()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        BoardHandSample together = Together(Button(board, "globe-drawer-open"));
        long revision = board.Revision;
        for (int time = 100; time <= 1100; time += 100)
            Require(At(board, time, together) is null, "Holding grouped fingers automatically opened the drawer.");
        Require(board.FingerSelectionFeedback.Single() is { Stage: BoardFingerSelectionStage.Armed },
            "Grouped fingers did not arm the drawer handle.");
        _ = board.GetGlobeSnapshot(Time(1150));
        Require(board.Revision == revision, "The spinning Earth invalidated an armed selection.");
        BoardHandSample apart = Apart(together);
        Require(At(board, 1200, apart) is null && At(board, 1280, apart) is
            { ButtonId: "globe-drawer-open", Current: BoardScreen.Globe, TrackingId: 11,
                Gesture: BoardSelectionGesture.IndexSeparation }, "Sideways index movement did not open the drawer.");
        Require(At(board, 1380, apart) is null && At(board, 1460, apart) is null && board.GlobeDrawerOpen,
            "Keeping the index separated immediately closed the drawer.");
        // Zoom buttons are hold-to-repeat: grouped fingers cannot arm, hover or select them.
        BoardHandSample zoom = Together(Button(board, "globe-zoom-in"));
        for (int time = 1500; time <= 2400; time += 100)
            Require(At(board, time, time % 200 == 0 ? zoom : Apart(zoom)) is null &&
                board.FingerSelectionFeedback.Count == 0 && board.HoveredButtonIds.Count == 0,
                "Finger gestures armed, hovered or selected a hold-to-repeat zoom button.");
        Require(Near(board.GetGlobeSnapshot(Time(2500)).TargetZoom, 1.5), "A finger gesture zoomed Earth.");
        Require(Select(board, Together(Button(board, "globe-drawer-close")), 2800)?.ButtonId == "globe-drawer-close" &&
            !board.GlobeDrawerOpen && Select(board, Together(Button(board, "globe-drawer-open")), 3200)?.ButtonId == "globe-drawer-open",
            "The shared finger gesture could not close and reopen the drawer.");
        Require(Select(board, Together(Button(board, "globe-exit")), 3900) is
            { Current: BoardScreen.Menu, ButtonId: "globe-exit", Gesture: BoardSelectionGesture.IndexSeparation },
            "The shared four-finger gesture failed to leave Globe through the settled drawer.");
    }

    private static BoardNavigation? Select(BoardSession board, BoardHandSample hand, int time)
    {
        Require(At(board, time, hand) is null && At(board, time + 100, hand) is null &&
            At(board, time + 200, Apart(hand)) is null, "A Globe gesture executed without separate confirmation.");
        return At(board, time + 280, Apart(hand));
    }
    private static BoardHandSample Together(BoardButton button) => new(double.NaN, double.NaN,
        DateTimeOffset.MinValue, 0) { TrackingId = 11, FourFingersExtended = true, FingersTogether = true, FingerAim = Center(button) };
    private static BoardHandSample Apart(BoardHandSample hand) => hand with { FingersTogether = false, IndexFingerSeparated = true };
    private static BoardHandSample Pinch(BoardButton button, long eventId, int time) => new(Center(button).U,
        Center(button).V, Time(time + 1000), eventId) { TrackingId = 11 };
    private static BoardAim Center(BoardButton button) => new(button.Bounds.X + button.Bounds.Width / 2,
        button.Bounds.Y + button.Bounds.Height / 2);
    private static BoardButton Button(BoardSession board, string id) => board.Buttons.Single(button => button.Id == id);
    private static BoardNavigation? At(BoardSession board, int time, params BoardHandSample[] hands) => board.Update(hands, Time(time), Time(time));
    private static DateTimeOffset Time(int milliseconds) => new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-9;
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
