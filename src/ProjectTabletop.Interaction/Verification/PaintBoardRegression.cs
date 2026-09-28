using ProjectTabletop.Interaction;

internal static class PaintBoardRegression
{
    public static void Run()
    {
        var board = new BoardSession();
        var paint = board.Buttons.Single(button => button.Id == "paint");
        Require(paint.Label == "Paint" && paint.Destination == BoardScreen.Paint &&
            board.Buttons.All(button => button.Id != "monopoly"), "Paint did not replace the Monopoly menu target.");
        Require(board.ActivateButton("paint", Time(0)) && board.Screen == BoardScreen.Paint && board.Title == "Paint",
            "The Paint menu target did not launch Paint.");
        var exit = board.Buttons.Single(button => button.Id == "menu");
        Require(exit is { Id: "menu", Label: "Exit", Destination: BoardScreen.Menu } &&
            exit.Bounds == new BoardRect(.06, .88, .14, .075), "Paint must have a compact Exit target in the bottom-left.");
        var save = board.Buttons.Single(button => button.Id == "paint-save");
        Require(board.Buttons.Count == 2 && save is { Label: "Save", Destination: BoardScreen.Paint, Enabled: false } &&
            save.Bounds == new BoardRect(.80, .88, .14, .075) && !board.PaintSaveEnabled,
            "Paint must start with a compact disabled Save target in the bottom-right.");

        // The canvas is driven by observed disturbance, not by a hidden execution
        // target. A pinch over paint must neither navigate nor replay on Exit.
        long revision = board.Revision;
        Require(At(board, 100, new BoardHandSample(.65, .65, Time(1100), 1)) is null && board.Revision == revision &&
            board.HoveredButtonIds.Count == 0, "Executing over the Paint canvas activated a button.");
        var aim = Center(exit);
        Require(At(board, 150, new BoardHandSample(aim.U, aim.V, Time(1100), 1)) is null,
            "A canvas pinch replayed when moved onto Exit.");
        Require(At(board, 200, new BoardHandSample(aim.U, aim.V, Time(1200), 2)) is
            { Previous: BoardScreen.Paint, Current: BoardScreen.Menu, ButtonId: "menu" },
            "A fresh pinch could not exit Paint.");

        board.ShowPaint(Time(300));
        var together = new BoardHandSample(double.NaN, double.NaN, DateTimeOffset.MinValue, 0)
        {
            TrackingId = 7, FingerAim = aim, FourFingersExtended = true, FingersTogether = true
        };
        var apart = together with { FingersTogether = false, IndexFingerSeparated = true };
        Require(At(board, 400, together) is null && At(board, 500, together) is null &&
            board.FingerSelectionFeedback.Single().Stage == BoardFingerSelectionStage.Armed,
            "The shared four-finger gesture could not arm Paint Exit.");
        Require(At(board, 600, apart) is null && At(board, 680, apart) is
            { Previous: BoardScreen.Paint, Current: BoardScreen.Menu, ButtonId: "menu",
                Gesture: BoardSelectionGesture.IndexSeparation, TrackingId: 7 },
            "The confirmed sideways index gesture could not exit Paint.");

        board.ShowPaint(Time(700));
        Require(At(board, 800, apart) is null && At(board, 880, apart) is null && board.Screen == BoardScreen.Paint,
            "Reopening Paint replayed the held exit gesture.");
        CheckSaveActions();
        CheckSaveReadiness();
        Console.WriteLine("Paint interaction verification passed: menu replacement, floating Exit/Save controls, shared pinch/four-finger actions, " +
            "Save without navigation, readiness/held-gesture barriers, canvas execution isolation and navigation barriers.");
    }

    private static void CheckSaveActions()
    {
        var board = new BoardSession();
        board.ShowPaint(Time(0));
        long revision = board.Revision;
        var save = board.Buttons.Single(button => button.Id == "paint-save");
        var aim = Center(save);
        Require(!board.ActivateButton("paint-save", Time(10)) &&
            At(board, 100, new BoardHandSample(aim.U, aim.V, Time(1100), 1)) is null &&
            board.HoveredButtonIds.Count == 0, "Disabled Paint Save accepted pointer or pinch input.");
        board.PaintSaveEnabled = true;
        Require(At(board, 150, new BoardHandSample(aim.U, aim.V, Time(1100), 1)) is null,
            "A pinch observed while Save was disabled replayed when it became enabled.");
        Require(At(board, 200, new BoardHandSample(aim.U, aim.V, Time(1200), 2) { TrackingId = 17 }) is
            { Previous: BoardScreen.Paint, Current: BoardScreen.Paint, ButtonId: "paint-save",
                TrackingId: 17, Gesture: BoardSelectionGesture.Pinch } && board.Revision == revision,
            "Paint Save did not report the pinch action while retaining the same canvas session.");
        Require(At(board, 250, new BoardHandSample(aim.U, aim.V, Time(1200), 2)) is null &&
            board.Screen == BoardScreen.Paint && board.Revision == revision,
            "Holding the Save pinch repeated the action or restarted the canvas.");
        Require(board.ActivateButton("paint-save", Time(300)) && board.Screen == BoardScreen.Paint &&
            board.Revision == revision, "Pointer Save navigated or restarted the Paint board.");
        Require(At(board, 350, new BoardHandSample(aim.U, aim.V, Time(1250), 3)) is null,
            "An in-flight pinch from before pointer Save repeated the action.");

        var together = Together(save);
        var apart = Apart(together);
        Require(Select(board, together, 400) is { ButtonId: "paint-save", Current: BoardScreen.Paint,
                Previous: BoardScreen.Paint, Gesture: BoardSelectionGesture.IndexSeparation } && board.Revision == revision,
            "Four-finger Save failed or restarted the Paint session.");
        Require(At(board, 800, apart) is null && At(board, 880, apart) is null,
            "A held sideways index repeatedly saved the painting.");
        Require(Select(board, together, 1000)?.ButtonId == "paint-save" && board.Revision == revision,
            "Rejoining and separating fingers could not save the same painting again.");
        board.ShowMenu(Time(1400));
        Require(!board.ActivateButton("paint-save", Time(1450)), "Paint Save was callable from another board.");
    }

    private static void CheckSaveReadiness()
    {
        var board = new BoardSession();
        board.ShowPaint(Time(0));
        var save = board.Buttons.Single(button => button.Id == "paint-save");
        var together = Together(save);
        Require(At(board, 100, together) is null && At(board, 200, together) is null &&
            board.FingerSelectionFeedback.Count == 0, "A disabled Save target armed a four-finger selection.");
        board.PaintSaveEnabled = true;
        Require(At(board, 300, Apart(together)) is null && At(board, 380, Apart(together)) is null,
            "Enabling Save inherited a together pose observed while it was disabled.");
        At(board, 500, together); At(board, 600, together);
        Require(board.FingerSelectionFeedback.Single().Stage == BoardFingerSelectionStage.Armed,
            "Enabled Paint Save did not arm a fresh gesture.");
        board.PaintSaveEnabled = false;
        Require(board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
            "Disabling Paint Save retained stale hover or gesture feedback.");
        board.PaintSaveEnabled = true;
        Require(At(board, 700, Apart(together)) is null && At(board, 780, Apart(together)) is null,
            "Toggling Save readiness retained the old armed gesture.");
        Require(Select(board, together, 900)?.ButtonId == "paint-save",
            "Save readiness changes blocked a fresh gesture.");

        var exit = Together(board.Buttons.Single(button => button.Id == "menu"));
        At(board, 1300, exit); At(board, 1400, exit);
        board.PaintSaveEnabled = false;
        Require(At(board, 1500, Apart(exit)) is null && At(board, 1580, Apart(exit)) is
            { ButtonId: "menu", Current: BoardScreen.Menu },
            "Disabling Save interrupted an independent armed Exit gesture.");
    }

    private static BoardNavigation? Select(BoardSession board, BoardHandSample hand, int start)
    {
        Require(At(board, start, hand) is null && At(board, start + 100, hand) is null &&
            At(board, start + 200, Apart(hand)) is null, "Paint action fired before index separation was confirmed.");
        return At(board, start + 280, Apart(hand));
    }
    private static BoardHandSample Together(BoardButton button) => new(double.NaN, double.NaN, DateTimeOffset.MinValue, 0)
        { TrackingId = 77, FingerAim = Center(button), FourFingersExtended = true, FingersTogether = true };
    private static BoardHandSample Apart(BoardHandSample sample) => sample with
        { FingersTogether = false, IndexFingerSeparated = true };

    private static BoardAim Center(BoardButton button) =>
        new(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
    private static BoardNavigation? At(BoardSession board, int milliseconds, params BoardHandSample[] hands) =>
        board.Update(hands, Time(milliseconds), Time(milliseconds));
    private static DateTimeOffset Time(int milliseconds) =>
        new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
