using System.Numerics;
using System.Reflection;
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
        CheckMatchControlVisibility();
        CheckReturningCaptionReadiness();
        CheckControlLayoutReferences();
        CheckInputRevision();
        Console.WriteLine("Football board verification passed: first-page launch, modes, appearances, " +
            "match reset, fresh-input calibration barrier, inactive-only controls, settled-confetti control entrance, " +
            "fresh returning caption holds, gesture isolation and exit.");
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
        Require(!board.ActivateButton("football-reset", At(830)),
            "The hidden projected RESET must not interrupt a countdown.");
        board.ResetFootball(At(830));
        Require(board.FootballState.Phase == FootballPhase.WaitingForPlayers &&
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

    private static void CheckMatchControlVisibility()
    {
        var board = new BoardSession();
        board.ShowFootball(At(0));
        board.SetFootballMode(FootballMode.TwoHumans, At(1));
        Require(board.FootballControlsVisible && board.Buttons.Count == 3,
            "Waiting for players must expose the field overlay controls.");
        int ms = 10;
        Step();
        Require(board.FootballState.Phase == FootballPhase.Countdown, "Visibility fixture did not start its countdown.");
        Hidden();
        while (ms < 1800) Step();
        Require(board.FootballState.Phase == FootballPhase.Playing, "Visibility fixture did not start playing.");
        Hidden();
        for (int i = 0; i < 30; i++)
        {
            ms += 10;
            board.TickFootball(At(ms));
        }
        Require(board.FootballState.RecoveringInput && board.FootballState.Phase == FootballPhase.Playing,
            "Visibility fixture did not enter brief tracking recovery.");
        Hidden();
        Step();
        Require(!board.FootballState.RecoveringInput, "Fresh players must end tracking recovery.");
        Hidden();

        var game = (FootballGame)typeof(BoardSession).GetField("_football", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(board)!;
        for (int score = 1; score <= FootballGame.WinningScore; score++)
        {
            // Seed a complete, low goal crossing; the board still transitions via
            // its production input and tick paths, including the final win.
            Set("_ballPosition", new Vector2(.76f, 0));
            Set("_ballVelocity", new Vector2(1.4f, 0));
            Set("_ballHeight", FootballGame.BallRadius);
            Set("_ballVerticalVelocity", 0f);
            Set("_snapshot", null);
            for (int i = 0; i < 9; i++) Step();
            Require(board.FootballState.Score1 == score, "Visibility fixture did not score once.");
            if (score == FootballGame.WinningScore) break;
            Require(board.FootballState.Phase == FootballPhase.Goal, "An ordinary goal must retain its celebration.");
            Hidden();
            for (int i = 0; i < 320; i++)
            {
                Step();
                Require(!board.FootballControlsVisible && board.Buttons.Count == 0,
                    "Controls must stay hidden between goals, throughout the following countdown.");
            }
        }
        Require(board.FootballState.Phase == FootballPhase.Finished,
            "Visibility fixture did not finish the match.");
        Hidden();
        int finishedAt = ms;
        for (int elapsed = 0; elapsed <= 10_000; elapsed += 100)
        {
            ms = finishedAt + elapsed;
            board.SetFootballVictoryPresentation(elapsed / 1000d, At(ms));
            Hidden();
        }
        double previousTop = 1.02;
        for (int elapsed = 10_100; elapsed < 11_500; elapsed += 100)
        {
            ms = finishedAt + elapsed;
            board.SetFootballVictoryPresentation(elapsed / 1000d, At(ms));
            Require(board.FootballControlsVisible && board.FootballControlsAnimating && board.Buttons.Count == 3 &&
                board.Buttons.All(button => !button.Enabled && button.Bounds.Y < previousTop && button.Bounds.Y > .885),
                "After confetti settles, controls must slide continuously upward while disabled.");
            previousTop = board.Buttons[0].Bounds.Y;
            foreach (string id in new[] { "football-exit", "football-reset", "football-mode" })
            {
                Clear(board, id, ms);
                Require(!board.ActivateButton(id, At(ms)) && Hold(board, id, ms + 1).Count == 0,
                    "Moving captions must not accept pointer actions or caption holds.");
            }
            Require(board.HoldProgress(At(ms + 1)).Count == 0,
                "Moving captions must not accumulate hold progress.");
        }
        ms = finishedAt + 11_500;
        board.SetFootballVictoryPresentation(11.5, At(ms));
        Require(board.FootballControlsVisible && !board.FootballControlsAnimating &&
            board.Buttons.All(button => button.Enabled && button.Bounds.Y == .885),
            "The controls must finish their slow entrance at their resting overlay positions.");
        board.ObserveHeldButtons([], At(ms - 10), At(ms + 10), ["football-reset"]);
        long revision = board.FootballInputRevision;
        for (int elapsed = 20; elapsed <= 1120; elapsed += 100)
            Require(Hold(board, "football-reset", ms + elapsed).Count == 0 &&
                board.FootballInputRevision == revision,
                "A delayed clear frame from the slide must not arm the stationary controls.");
        Clear(board, "football-reset", ms + 1200);
        for (int elapsed = 1300; elapsed < 2300; elapsed += 100)
            Require(Hold(board, "football-reset", ms + elapsed).Count == 0,
                "The stationary control must require a complete fresh caption hold.");
        Require(Hold(board, "football-reset", ms + 2300).SequenceEqual(["football-reset"]) &&
            board.FootballState.Phase == FootballPhase.WaitingForPlayers && board.Buttons.Count == 3,
            "A new clear caption followed by a full hold must reset the match with immediate waiting controls.");
        Set("_phase", FootballPhase.Finished);
        Set("_snapshot", null);
        Require(!board.FootballControlsVisible && board.Buttons.Count == 0,
            "Resetting the match must discard the previous victory's entrance progress.");

        void Set(string name, object? value) => typeof(FootballGame)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
        void Step()
        {
            ms += 10;
            board.SetFootballInput(0, new(-.52f, .35f), At(ms));
            board.SetFootballInput(1, new(.52f, -.35f), At(ms));
            board.TickFootball(At(ms));
        }
        void Hidden()
        {
            Require(!board.FootballControlsVisible && board.Buttons.Count == 0,
                "An active match must expose no projected controls or hit targets.");
            var mode = board.FootballState.Mode;
            long revision = board.FootballInputRevision;
            foreach (string id in new[] { "football-exit", "football-reset", "football-mode" })
                Require(!board.ActivateButton(id, At(ms)) && Hold(board, id, ms).Count == 0,
                    "Hidden controls must reject both pointer actions and caption holds.");
            Require(board.Screen == BoardScreen.Football && board.FootballInputRevision == revision &&
                board.FootballState.Mode == mode, "Hidden actions must not navigate, reset or change the mode.");
        }
    }

    private static void CheckReturningCaptionReadiness()
    {
        var board = new BoardSession();
        board.ShowFootball(At(0));
        Clear(board, "football-reset", 10);
        Hold(board, "football-reset", 20);
        Hold(board, "football-reset", 120);
        Require(board.HoldProgress(At(120)).Count == 1, "The readiness fixture must begin a partial hold.");
        board.SetFootballInput(0, new(-.5f, .35f), At(130));
        board.TickFootball(At(130));
        Require(!board.FootballControlsVisible && board.HoldProgress(At(130)).Count == 0,
            "Hiding controls must immediately cancel partial caption holds.");
        board.ClearFootballInput(At(140));
        Require(board.FootballControlsVisible, "A sustained input loss must restore waiting controls.");
        Require(board.ObserveHeldButtons([], At(135), At(150), ["football-reset"]).Count == 0,
            "A delayed clear-caption frame must not arm returning controls.");
        long revision = board.FootballInputRevision;
        for (int ms = 160; ms <= 1260; ms += 100)
            Require(Hold(board, "football-reset", ms).Count == 0 && board.FootballInputRevision == revision,
                "A returning control must remain disarmed under a hand until its new caption is seen intact.");
        Clear(board, "football-reset", 1300);
        for (int ms = 1400; ms < 2400; ms += 100)
            Require(Hold(board, "football-reset", ms).Count == 0,
                "A rearmed returning control must still require a complete fresh hold.");
        Require(Hold(board, "football-reset", 2400).SequenceEqual(["football-reset"]) &&
            board.FootballInputRevision == revision + 1,
            "A fresh clear caption followed by one second of broken lettering must rearm the returning control.");
    }

    private static void CheckControlLayoutReferences()
    {
        var board = new BoardSession();
        board.ShowFootball(At(0));
        Clear(board, "football-mode", 10);
        Hold(board, "football-mode", 20);
        Hold(board, "football-mode", 120);
        long revision = board.Revision;
        board.SetFootballControlTop(.82, At(130));
        Require(board.Revision > revision && board.HoldProgress(At(130)).Count == 0 &&
            board.Buttons.All(button => button.Bounds.Y == .82 && button.Bounds.Height == .095),
            "Moving field overlay controls must move their hit targets and invalidate old caption references.");
        revision = board.Revision;
        board.SetFootballControlTop(.82, At(140));
        Require(board.Revision == revision, "An unchanged field layout must not invalidate input on every frame.");
        board.ObserveHeldButtons([], At(125), At(150), ["football-mode"]);
        for (int ms = 160; ms <= 1260; ms += 100)
            Require(Hold(board, "football-mode", ms).Count == 0 && board.FootballState.Mode == FootballMode.HumanVsAi,
                "Delayed clear evidence for an old caption position must not arm the moved control.");
        Clear(board, "football-mode", 1300);
        for (int ms = 1400; ms < 2400; ms += 100) Hold(board, "football-mode", ms);
        Require(Hold(board, "football-mode", 2400).SequenceEqual(["football-mode"]) &&
            board.FootballState.Mode == FootballMode.TwoHumans,
            "Moved controls must rearm from clear lettering and accept a fresh full hold.");
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
