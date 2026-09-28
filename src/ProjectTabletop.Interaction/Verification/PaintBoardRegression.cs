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
        var exit = board.Buttons.Single();
        Require(exit is { Id: "menu", Label: "Exit", Destination: BoardScreen.Menu } &&
            exit.Bounds.X < .1 && exit.Bounds.Y < .1, "Paint must have one Exit target in the top-left.");

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
        Console.WriteLine("Paint interaction verification passed: menu replacement, top-left Exit, shared pinch/four-finger exit, " +
            "canvas execution isolation and navigation barriers.");
    }

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
