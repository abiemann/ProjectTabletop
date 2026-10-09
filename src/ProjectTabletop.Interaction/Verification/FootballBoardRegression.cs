using System.Numerics;
using ProjectTabletop.Interaction;

internal static class FootballBoardRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int ms) => Epoch.AddMilliseconds(ms);

    public static void Run()
    {
        CheckNavigationModesAndAppearance();
        CheckCalibrationBarrier();
        CheckCaptionControls();
        CheckInputRevision();
        Console.WriteLine("Football board verification passed: first-page launch, modes, appearances, " +
            "match reset, fresh-input calibration barrier, caption holds, gesture isolation and exit.");
    }

    private static void CheckNavigationModesAndAppearance()
    {
        var board = new BoardSession();
        var opened = new List<BoardScreen>();
        board.BoardOpened += opened.Add;
        Require(board.Buttons.Any(button => button.Id == "football") && !board.MenuScrolled,
            "Football must be available on the first menu page.");
        var card = board.Buttons.Single(button => button.Id == "football");
        Require(card.Label == "Football" && card.Destination == BoardScreen.Football &&
            Math.Abs(card.Bounds.Y - .25) < .00001 && card.Bounds.X == .52 &&
            board.ActivateButton("football", At(700)) && board.Screen == BoardScreen.Football && board.Title == "Football",
            "Football first-page card did not open its board.");
        Require(opened.SequenceEqual([BoardScreen.Football]) &&
            board.Buttons.Select(button => button.Id).SequenceEqual(["football-exit", "football-reset", "football-mode"]) &&
            board.Buttons.All(button => button.Hold == BoardButtonHold.Once),
            "Football should launch once and expose three once-per-uncover controls.");
        board.SetFootballStyle(0, FootballKickerStyle.Pan);
        board.SetFootballStyle(1, FootballKickerStyle.Boot);
        long navigation = board.NavigationRevision;
        Require(board.ActivateButton("football-mode", At(800)) && board.FootballState.Mode == FootballMode.TwoHumans &&
            board.FootballState.Kickers[0].Style == FootballKickerStyle.Pan &&
            board.FootballState.Kickers[1].Style == FootballKickerStyle.Boot &&
            board.Buttons.Single(button => button.Id == "football-mode").Label == "VS AI" &&
            board.NavigationRevision == navigation && opened.Count == 1,
            "Changing mode must reset the match, preserve appearance and stay on the board.");
        board.SetFootballInput(0, new(-.5f, .3f), At(810));
        board.TickFootball(At(810));
        Require(board.FootballState.Phase == FootballPhase.WaitingForPlayers,
            "A two-player board must wait for its second human.");
        board.SetFootballInput(1, new(.5f, -.3f), At(820));
        board.TickFootball(At(820));
        Require(board.FootballState.Phase == FootballPhase.Countdown,
            "Two fresh humans should start the board countdown.");
        Require(board.ActivateButton("football-reset", At(830)) &&
            board.FootballState.Phase == FootballPhase.WaitingForPlayers &&
            board.FootballState.Kickers.All(kicker => !kicker.Present) && board.NavigationRevision == navigation,
            "Match reset must require fresh players without navigating.");
        Require(board.ActivateButton("football-exit", At(840)) && board.Screen == BoardScreen.Menu &&
            !board.MenuScrolled && opened.SequenceEqual([BoardScreen.Football, BoardScreen.Menu]),
            "Football EXIT must return to the first menu page exactly once.");
        board.ShowFootball(At(900));
        Require(board.FootballState.Mode == FootballMode.TwoHumans && board.FootballState.Score1 == 0 &&
            board.FootballState.Phase == FootballPhase.WaitingForPlayers &&
            board.FootballState.Kickers[0].Style == FootballKickerStyle.Pan,
            "Reopening Football must start a new match with the selected mode and appearance.");
    }

    private static void CheckCalibrationBarrier()
    {
        var board = new BoardSession();
        board.ShowFootball(At(0));
        for (int ms = 10; ms <= 1800; ms += 10)
        {
            board.SetFootballInput(0, new(-.5f, .35f), At(ms));
            board.TickFootball(At(ms));
        }
        var live = board.FootballState;
        Require(live.Phase == FootballPhase.Playing && live.BallVelocity.Length() > 0,
            "The calibration fixture did not start playing.");
        board.ResetInput(At(1800));
        var paused = board.FootballState;
        Require(paused.Phase == FootballPhase.WaitingForPlayers && !paused.Kickers[0].Present &&
            paused.BallPosition == live.BallPosition && paused.BallVelocity == live.BallVelocity,
            "A calibration reset, including the current frame timestamp, must pause but preserve the match.");
        board.SetFootballInput(0, Vector2.Zero, At(1790));
        board.TickFootball(At(1810));
        Require(!board.FootballState.Kickers[0].Present && board.FootballState.Phase == FootballPhase.WaitingForPlayers,
            "A delayed observation from before calibration reset must not reactivate a kicker.");
        board.SetFootballInput(0, new(-.5f, .35f), At(1820));
        board.TickFootball(At(1820));
        Require(board.FootballState.Phase == FootballPhase.Countdown &&
            board.FootballState.BallPosition == live.BallPosition,
            "A post-calibration observation must resume via countdown without moving the ball immediately.");
        var sameTime = board.FootballState;
        board.TickFootball(At(1820));
        Require(ReferenceEquals(sameTime, board.FootballState), "Repeated scene samples must not advance football twice.");
    }

    private static void CheckCaptionControls()
    {
        var board = new BoardSession();
        board.ShowFootball(At(0));
        long inputRevision = board.FootballInputRevision;
        var reset = board.Buttons.Single(button => button.Id == "football-reset");
        var hand = new BoardHandSample(reset.Bounds.X + reset.Bounds.Width / 2,
            reset.Bounds.Y + reset.Bounds.Height / 2, At(1010), 1);
        Require(board.Update([hand], At(10), At(10)) is null,
            "A pinch must not select a caption-hold football control.");
        for (int ms = 100; ms <= 1100; ms += 100)
            Require(Hold(board, "football-reset", ms).Count == 0,
                "Football RESET must require a clear-caption observation before holding.");
        Clear(board, "football-reset", 1120);
        for (int ms = 1200; ms < 2200; ms += 100)
            Require(Hold(board, "football-reset", ms).Count == 0,
                "Football RESET must require one second of broken lettering.");
        Require(Hold(board, "football-reset", 2200).SequenceEqual(["football-reset"]) &&
            board.FootballInputRevision == inputRevision + 1,
            "A full caption hold must activate Football RESET and invalidate tracked observations once.");
        long afterReset = board.Revision;
        for (int ms = 2300; ms <= 3400; ms += 100)
            Require(Hold(board, "football-reset", ms).Count == 0 && board.Revision == afterReset &&
                board.FootballInputRevision == inputRevision + 1,
                "A covered RESET must not keep resetting Football.");
        Clear(board, "football-mode", 3500);
        for (int ms = 3600; ms < 4600; ms += 100) Hold(board, "football-mode", ms);
        Require(Hold(board, "football-mode", 4600).SequenceEqual(["football-mode"]) &&
            board.FootballState.Mode == FootballMode.TwoHumans &&
            board.FootballInputRevision == inputRevision + 2,
            "The mode caption must switch to two humans and invalidate tracked observations after a deliberate hold.");
        for (int ms = 4700; ms <= 5900; ms += 100)
            Require(Hold(board, "football-mode", ms).Count == 0 && board.FootballState.Mode == FootballMode.TwoHumans &&
                board.FootballInputRevision == inputRevision + 2,
                "The replacement VS AI caption must not act under the same unreleased hand.");
    }

    private static void CheckInputRevision()
    {
        var board = new BoardSession();
        long revision = board.FootballInputRevision;
        board.ShowFootball(At(0));
        Require(board.FootballInputRevision == ++revision,
            "Opening Football must invalidate observations from the previous match.");
        board.ClearFootballInput(At(10));
        Require(board.FootballInputRevision == revision,
            "Clearing scene observations must not create an input-revision feedback loop.");
        board.SetFootballMode(FootballMode.HumanVsAi, At(20));
        Require(board.FootballInputRevision == revision,
            "Selecting the existing football mode must not invalidate observations.");
        board.SetFootballMode(FootballMode.TwoHumans, At(30));
        Require(board.FootballInputRevision == ++revision,
            "Changing football mode through the API must invalidate observations once.");
        board.ResetFootball(At(40));
        Require(board.FootballInputRevision == ++revision,
            "Resetting football through the API must invalidate observations once.");
        board.SetFootballStyle(0, FootballKickerStyle.Pan);
        Require(board.FootballInputRevision == revision,
            "Changing appearance must not invalidate tracked input.");
    }

    private static IReadOnlyList<string> Hold(BoardSession board, string id, int ms) =>
        board.ObserveHeldButtons([id], At(ms), At(ms), []);
    private static void Clear(BoardSession board, string id, int ms) =>
        board.ObserveHeldButtons([], At(ms), At(ms), [id]);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
