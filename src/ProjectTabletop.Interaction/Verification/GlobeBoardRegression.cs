using ProjectTabletop.Interaction;

internal static class GlobeBoardRegression
{
    public static void Run()
    {
        CheckEntranceAndRotation();
        CheckSmoothControlsAndBounds();
        CheckNavigationAndPinches();
        CheckFourFingerSelection();
        Console.WriteLine("Globe verification passed: pure entrance/spin frames, bounded smooth zoom, clockwise/counterclockwise " +
            "rotation, exact shared controls, pinch and four-finger selection, held-action suppression and restart barriers.");
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
        Require(board.GetGlobeSnapshot(Time(362000)) is { TargetZoom: 1.5, IntroProgress: 0, RotationDegrees: 0 },
            "Returning to Globe did not restart the approach and orientation.");
    }

    private static void CheckSmoothControlsAndBounds()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        string[] ids = ["globe-exit", "globe-zoom-out", "globe-zoom-in", "globe-rotate-left", "globe-rotate-right"];
        string[] labels = ["Exit", "Zoom -", "Zoom +", "< Rotate", "Rotate >"];
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(ids) &&
            board.Buttons.Select(button => button.Label).SequenceEqual(labels), "Globe controls do not match the requested captions.");
        foreach (BoardButton button in board.Buttons)
            Require(button.Bounds.Y > .7 && button.Bounds.Width >= .26 && button.Bounds.Height >= .105 &&
                button.Bounds.X > 0 && button.Bounds.X + button.Bounds.Width < 1 &&
                button.Bounds.Y + button.Bounds.Height < 1 &&
                !board.Buttons.Any(other => other.Id != button.Id &&
                    other.Bounds.X < button.Bounds.X + button.Bounds.Width &&
                    other.Bounds.X + other.Bounds.Width > button.Bounds.X &&
                    other.Bounds.Y < button.Bounds.Y + button.Bounds.Height &&
                    other.Bounds.Y + other.Bounds.Height > button.Bounds.Y),
                "A Globe control lacks four-finger room, is outside the board or overlaps a neighboring hit target.");

        GlobeSnapshot before = board.GetGlobeSnapshot(Time(4000));
        Require(board.ActivateButton("globe-zoom-in", Time(4000)), "Zoom + did not accept a laptop selection.");
        GlobeSnapshot instant = board.GetGlobeSnapshot(Time(4000));
        GlobeSnapshot halfway = board.GetGlobeSnapshot(Time(4175));
        GlobeSnapshot final = board.GetGlobeSnapshot(Time(4350));
        Require(Near(instant.Zoom, before.Zoom) && Near(instant.TargetZoom, 1.875) && halfway.Zoom > instant.Zoom &&
            halfway.Zoom < final.Zoom && Near(final.Zoom, 1.875), "Zooming jumped immediately or failed to settle smoothly.");
        Require(board.ActivateButton("globe-zoom-in", Time(4350)) && board.ActivateButton("globe-zoom-in", Time(4350)),
            "Consecutive zoom selections were lost.");
        Require(Near(board.GetGlobeSnapshot(Time(4700)).TargetZoom, 1.5 * Math.Pow(1.25, 3)), "Quick selections failed to accumulate zoom targets.");
        for (int time = 5000; time < 15000; time += 400) board.ActivateButton("globe-zoom-in", Time(time));
        Require(Near(board.GetGlobeSnapshot(Time(15000)).Zoom, GlobeState.MaximumZoom) &&
            !Button(board, "globe-zoom-in").Enabled && !board.ActivateButton("globe-zoom-in", Time(15000)),
            "Zoom exceeded its maximum or left the capped control enabled.");
        for (int time = 16000; time < 36000; time += 400) board.ActivateButton("globe-zoom-out", Time(time));
        Require(Near(board.GetGlobeSnapshot(Time(36000)).Zoom, GlobeState.MinimumZoom) &&
            !Button(board, "globe-zoom-out").Enabled && !board.ActivateButton("globe-zoom-out", Time(36000)),
            "Zoom exceeded its minimum or left the capped control enabled.");

        Require(board.ActivateButton("globe-rotate-right", Time(37000)), "Rotate > did not execute.");
        Require(Near(board.GetGlobeSnapshot(Time(37000)).RotationDegrees, 37) &&
            Near(board.GetGlobeSnapshot(Time(37350)).RotationDegrees, 57.35), "Manual right rotation did not blend into the continuing spin.");
        Require(board.ActivateButton("globe-rotate-left", Time(38000)) &&
            Near(board.GetGlobeSnapshot(Time(38350)).RotationDegrees, 38.35), "Manual left rotation did not reverse the added rotation.");
        long revision = board.Revision;
        Require(!board.ActivateButton("globe-rotate-right", Time(37999)) && !board.ActivateButton("unknown", Time(39000)) &&
            board.Revision == revision, "Rejected actions changed Globe state.");
        board.ShowGlobe(Time(40000));
        Require(board.GetGlobeSnapshot(Time(43000)) is { TargetZoom: 1.5 } && Near(board.GetGlobeSnapshot(Time(43000)).Zoom, 1.5),
            "Restarting Globe retained the previous zoom.");
    }

    private static void CheckNavigationAndPinches()
    {
        var board = new BoardSession(); BoardButton globe = Button(board, "globe");
        Require(At(board, 100, Pinch(globe, 1, 100)) is { Previous: BoardScreen.Menu, Current: BoardScreen.Globe,
            ButtonId: "globe", Gesture: BoardSelectionGesture.Pinch } && board.Title == "Globe", "A menu pinch did not launch Globe.");
        BoardButton zoom = Button(board, "globe-zoom-in");
        Require(At(board, 140, Pinch(zoom, 1, 100)) is null && Near(board.GetGlobeSnapshot(Time(140)).TargetZoom, 1.5),
            "A held launch pinch also zoomed Earth.");
        Require(At(board, 180, Pinch(zoom, 2, 180)) is { Previous: BoardScreen.Globe, Current: BoardScreen.Globe,
            ButtonId: "globe-zoom-in" }, "A fresh pinch failed to zoom Globe.");
        Require(At(board, 220, Pinch(zoom, 2, 180)) is null && At(board, 260) is null &&
            At(board, 300, Pinch(zoom, 2, 180)) is null && Near(board.GetGlobeSnapshot(Time(300)).TargetZoom, 1.875),
            "A held or briefly missing pinch replayed a Globe action.");
        Require(At(board, 400, Pinch(Button(board, "globe-exit"), 3, 400)) is
            { Previous: BoardScreen.Globe, Current: BoardScreen.Menu, ButtonId: "globe-exit" },
            "Globe Exit did not report the actual launcher navigation.");
        Require(At(board, 440, Pinch(globe, 3, 400)) is null, "A held Exit pinch reentered Globe.");
        board.ShowGlobe(Time(500));
        Require(At(board, 540, Pinch(zoom, 4, 460)) is null &&
            At(board, 580, Pinch(zoom, 5, 580)) is not null, "Direct navigation retained an old execution or blocked a fresh one.");
        board.ResetInput(Time(620));
        Require(At(board, 660, Pinch(zoom, 6, 600)) is null &&
            At(board, 700, Pinch(zoom, 7, 700)) is not null, "Input reset failed to protect Globe controls from stale executions.");
    }

    private static void CheckFourFingerSelection()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        BoardHandSample together = Together(Button(board, "globe-rotate-right"));
        long revision = board.Revision;
        for (int time = 100; time <= 1100; time += 100)
            Require(At(board, time, together) is null, "Holding four fingers together automatically selected Globe rotation.");
        Require(board.FingerSelectionFeedback.Single() is { Stage: BoardFingerSelectionStage.Armed },
            "Grouped four fingers did not arm the middle-fingertip target.");
        _ = board.GetGlobeSnapshot(Time(1150));
        Require(board.Revision == revision, "The spinning Earth invalidated an armed selection.");
        BoardHandSample apart = Apart(together);
        Require(At(board, 1200, apart) is null && At(board, 1280, apart) is
            { ButtonId: "globe-rotate-right", Current: BoardScreen.Globe, TrackingId: 11,
                Gesture: BoardSelectionGesture.IndexSeparation }, "Sideways index movement did not select Globe rotation.");
        Require(At(board, 1380, apart) is null && At(board, 1460, apart) is null,
            "Remaining separated repeatedly rotated Globe.");
        Require(Select(board, together, 1600)?.ButtonId == "globe-rotate-right", "Rejoining fingers failed to rearm rotation.");
        Require(Select(board, Together(Button(board, "globe-exit")), 2100) is
            { Current: BoardScreen.Menu, ButtonId: "globe-exit", Gesture: BoardSelectionGesture.IndexSeparation },
            "The shared four-finger gesture failed to leave Globe.");
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
