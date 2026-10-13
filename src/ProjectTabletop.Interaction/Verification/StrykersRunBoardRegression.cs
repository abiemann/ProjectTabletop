using System.Collections;
using System.Reflection;
using ProjectTabletop.Interaction;

internal static class StrykersRunBoardRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);

    public static void Run()
    {
        CheckNavigationAndExplicitPlay();
        CheckRailPresentation();
        CheckCaptionHolds();
        CheckInputResetAndExit();
        CheckReturningControls(victory: false);
        CheckReturningControls(victory: true);
        Console.WriteLine("Stryker's Run board passed: first-page launch and second-page Blackjack, lone-hero ready state, " +
            "EXIT/PLAY caption holds, stationary control fades, delayed rail entry, rail reset barriers and fresh holds after defeat or campaign victory.");
    }

    private static void CheckRailPresentation()
    {
        var board = new BoardSession();
        board.ShowStrykersRun(At(0));
        var bounds = board.Buttons.Select(button => button.Bounds).ToArray();
        Require(board.StrykersRunRailProgress == 0 && !board.StrykersRunRailVisible &&
            !board.StrykersRunRailAnimating, "The ready screen must keep its input dots hidden.");
        Require(board.ActivateButton("strykers-run-play", At(10)) && board.StrykersRunRailAnimating &&
            board.StrykersRunRailProgress == 0, "PLAY must begin the rail below the board.");
        double previousRail = 0, previousControls = 1;
        for (int ms = 20; ms <= 450; ms += 10)
        {
            board.TickStrykersRun(At(ms));
            Require(board.StrykersRunRailAnimating && board.StrykersRunRailProgress > previousRail &&
                board.StrykersRunControlsProgress < previousControls &&
                board.Buttons.Select(button => button.Bounds).SequenceEqual(bounds) &&
                board.Buttons.All(button => !button.Enabled),
                "Starting must bring the rail upward while fixed caption controls fade without accepting input.");
            previousRail = board.StrykersRunRailProgress;
            previousControls = board.StrykersRunControlsProgress;
        }
        for (int ms = 460; ms <= 520; ms += 10) board.TickStrykersRun(At(ms));
        Require(board.StrykersRunState.Phase == StrykersRunPhase.WaitingForInput &&
            board.StrykersRunRailProgress == 1 && board.StrykersRunRailVisible &&
            !board.StrykersRunRailAnimating && !board.StrykersRunControlsVisible,
            "The settled rail must remain fully visible while the game waits for its first input.");
        board.ShowMenu(At(530)); board.ShowStrykersRun(At(540));
        Require(!board.StrykersRunRailVisible && board.StrykersRunControlsProgress == 1 &&
            board.Buttons.Select(button => button.Bounds).SequenceEqual(bounds),
            "A new visit must restore stationary controls with the rail hidden.");
    }

    private static void CheckNavigationAndExplicitPlay()
    {
        var board = new BoardSession();
        var opened = new List<BoardScreen>();
        board.BoardOpened += opened.Add;
        Require(board.Buttons.Single(button => button.Id == "strykers-run") is
            { Label: "Stryker's Run", Destination: BoardScreen.StrykersRun, Bounds: { X: .08, Y: .45 } } &&
            board.Buttons.All(button => button.Id != "blackjack") && board.ActivateButton("strykers-run", At(0)),
            "Stryker's Run must occupy the old first-page Blackjack position.");
        Require(board.Title == "Stryker's Run" && board.StrykersRunState is
            { Phase: StrykersRunPhase.Ready, SoldierCount: 1, HeroAlive: true, TroopCount: 0 } &&
            board.Buttons.Select(button => button.Id).SequenceEqual(["strykers-run-exit", "strykers-run-play"]) &&
            board.Buttons.Select(button => button.Label).SequenceEqual(["EXIT", "PLAY"]) &&
            board.Buttons.All(button => button.Hold == BoardButtonHold.Once && button.Enabled &&
                button.Bounds.Y >= .85 && button.Bounds.Y + button.Bounds.Height < .955) &&
            opened.SequenceEqual([BoardScreen.StrykersRun]),
            "The ready board must show exactly two stationary caption controls clear of the bottom input rail.");
        board.SetStrykersRunInput(.8f, At(10));
        board.TickStrykersRun(At(10));
        Require(board.StrykersRunState.Phase == StrykersRunPhase.Ready,
            "Rail input must not bypass PLAY.");
        long inputRevision = board.StrykersRunInputRevision, navigation = board.NavigationRevision;
        Require(board.ActivateButton("strykers-run-play", At(20)) && board.StrykersRunControlsAnimating &&
            board.StrykersRunInputRevision == inputRevision + 1 && board.NavigationRevision == navigation && opened.Count == 1,
            "PLAY must start a run, invalidate old rail input and preserve the board launch count.");
        for (int ms = 30; ms <= 500; ms += 10) board.TickStrykersRun(At(ms));
        Require(board.StrykersRunState.Phase == StrykersRunPhase.WaitingForInput && !board.StrykersRunControlsVisible &&
            board.Buttons.Count == 0 && !board.ActivateButton("strykers-run-play", At(510)) &&
            !board.ActivateButton("strykers-run-exit", At(510)),
            "After withdrawal, hidden controls must not restart or navigate away from the run.");
        board.ShowMenu(At(520));
        Require(board.ActivateButton("menu-scroll-down", At(530)) && board.TickMenu(At(1180)) &&
            board.Buttons.Single(button => button.Id == "blackjack").Bounds.X == .52 &&
            Math.Abs(board.Buttons.Single(button => button.Id == "blackjack").Bounds.Y - .45) < .000001 &&
            board.ActivateButton("blackjack", At(1190)) && board.Screen == BoardScreen.Blackjack,
            "Blackjack must remain accessible beside Photo Copy on page two.");
        board.ShowStrykersRun(At(1200));
        Require(board.StrykersRunState is { Phase: StrykersRunPhase.Ready, SoldierCount: 1, RunSeconds: 0 } &&
            board.StrykersRunControlsProgress == 1 && opened[^1] == BoardScreen.StrykersRun,
            "The direct launch path must return to a clean lone-hero ready state.");
    }

    private static void CheckCaptionHolds()
    {
        var board = new BoardSession();
        board.ShowStrykersRun(At(0));
        var play = board.Buttons.Single(button => button.Id == "strykers-run-play");
        var pinch = new BoardHandSample(play.Bounds.X + play.Bounds.Width / 2,
            play.Bounds.Y + play.Bounds.Height / 2, At(1010), 1);
        Require(board.Update([pinch], At(10), At(10)) is null,
            "A hand landmark or pinch must not activate PLAY without caption corruption.");
        for (int ms = 100; ms <= 1200; ms += 100)
            Require(Hold(board, "strykers-run-play", ms).Count == 0,
                "An unseen-clear caption armed a run.");
        Clear(board, "strykers-run-play", 1210);
        for (int ms = 1300; ms <= 1800; ms += 100) Hold(board, "strykers-run-play", ms);
        Require(board.HoldProgress(At(1800)).Count == 1, "The valid partial caption hold did not begin.");
        Clear(board, "strykers-run-play", 1810);
        Require(board.HoldProgress(At(1810)).Count == 0,
            "Intact lettering must immediately cancel partial PLAY progress.");
        for (int ms = 1900; ms < 2900; ms += 100)
            Require(Hold(board, "strykers-run-play", ms).Count == 0, "PLAY accepted less than a full fresh hold.");
        Require(Hold(board, "strykers-run-play", 2900).SequenceEqual(["strykers-run-play"]) &&
            board.StrykersRunState.Phase == StrykersRunPhase.Starting &&
            board.Buttons.All(button => !button.Enabled), "The full caption hold failed to start control withdrawal.");
        board.TickStrykersRun(At(3000));
        Require(board.StrykersRunControlsProgress is > 0 and < 1 &&
            !board.ActivateButton("strykers-run-exit", At(3000)) && Hold(board, "strykers-run-exit", 3010).Count == 0,
            "Fading captions accepted pointer or camera input.");
    }

    private static void CheckInputResetAndExit()
    {
        var board = Running();
        var before = board.StrykersRunState;
        board.ResetInput(At(501));
        Require(board.StrykersRunState is { Phase: StrykersRunPhase.WaitingForInput, RecoveringInput: true } &&
            board.StrykersRunState.RunSeconds == before.RunSeconds && board.StrykersRunState.SquadX == before.SquadX,
            "A camera or calibration reset must pause the complete run immediately.");
        board.SetStrykersRunInput(.9f, At(500));
        board.TickStrykersRun(At(510));
        Require(board.StrykersRunState.Phase == StrykersRunPhase.WaitingForInput,
            "A pre-reset rail observation restarted the run.");
        board.SetStrykersRunInput(.9f, At(520));
        board.TickStrykersRun(At(520));
        Require(board.StrykersRunState.Phase == StrykersRunPhase.Playing && board.StrykersRunState.RunSeconds == before.RunSeconds,
            "Fresh rail recovery replayed paused time.");
        board.ShowMenu(At(530));
        float stopped = board.StrykersRunState.RunSeconds;
        board.SetStrykersRunInput(.1f, At(540));
        board.TickStrykersRun(At(550));
        Require(board.StrykersRunState.Phase == StrykersRunPhase.WaitingForInput &&
            board.StrykersRunState.RunSeconds == stopped, "Navigation retained active off-screen rail input.");
    }

    private static void CheckReturningControls(bool victory)
    {
        var board = Running();
        var game = (StrykersRunGame)typeof(BoardSession).GetField("_strykersRun", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
        if (victory)
        {
            // The gameplay suite verifies earning victory. This fixture isolates
            // the terminal presentation and shared caption rearming policy.
            typeof(StrykersRunGame).GetField("_phase", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(game, StrykersRunPhase.Victory);
            typeof(StrykersRunGame).GetField("_phaseSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, 0f);
            typeof(StrykersRunGame).GetMethod("Change", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, null);
        }
        else
        {
            var enemyType = typeof(StrykersRunGame).GetNestedType("Enemy", BindingFlags.NonPublic)!;
            var enemies = (IList)typeof(StrykersRunGame).GetField("_enemies", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
            enemies.Add(Activator.CreateInstance(enemyType, 100, .5f, StrykersRunGame.SquadY - .0001f, StrykersRunEnemyKind.Boss, 100)!);
        }
        board.SetStrykersRunInput(.5f, At(510));
        board.TickStrykersRun(At(510));
        Require(board.StrykersRunState.Phase == (victory ? StrykersRunPhase.Victory : StrykersRunPhase.GameOver) &&
            board.StrykersRunState.HeroAlive == victory && !board.StrykersRunControlsVisible,
            "Defeat or victory must begin its presentation with controls hidden.");
        for (int ms = 520; ms <= 3710; ms += 10)
        {
            board.TickStrykersRun(At(ms));
            if (ms % 100 == 0) Require(Hold(board, "strykers-run-play", ms).Count == 0,
                "A caption carried through the ending automatically restarted the campaign.");
            if (ms == 3110) Require(!board.StrykersRunControlsVisible, "Controls appeared before the ending presentation settled.");
            if (ms == 1010) Require(!board.StrykersRunRailVisible && !board.StrykersRunRailAnimating,
                "The input rail remained visible after its ending withdrawal.");
            if (ms == 3310) Require(board.StrykersRunControlsAnimating && board.Buttons.All(button => !button.Enabled),
                "The returning control row must fade in while disabled.");
        }
        Require(board.StrykersRunControlsProgress == 1 && !board.StrykersRunControlsAnimating &&
            board.Buttons.Count == 2 && board.Buttons.All(button => button.Enabled),
            "The settled ending did not return exactly EXIT and PLAY.");
        for (int ms = 3800; ms <= 4900; ms += 100)
            Require(Hold(board, "strykers-run-play", ms).Count == 0,
                "A returning caption must first be observed intact before it can arm.");
        Clear(board, "strykers-run-play", 4910);
        for (int ms = 5000; ms < 6000; ms += 100)
            Require(Hold(board, "strykers-run-play", ms).Count == 0, "The returned PLAY acted before its fresh full hold.");
        Require(Hold(board, "strykers-run-play", 6000).SequenceEqual(["strykers-run-play"]) &&
            board.StrykersRunState is { Phase: StrykersRunPhase.Starting, HeroAlive: true, TroopCount: 0, Level: 1, LevelSeconds: 0 },
            "A fresh returned PLAY did not restart with the lone protagonist.");
    }

    private static BoardSession Running()
    {
        var board = new BoardSession();
        board.ShowStrykersRun(At(0));
        Require(board.ActivateButton("strykers-run-play", At(1)), "The fixture must press PLAY.");
        for (int ms = 10; ms <= 500; ms += 10) { board.SetStrykersRunInput(.5f, At(ms)); board.TickStrykersRun(At(ms)); }
        Require(board.StrykersRunState.Phase == StrykersRunPhase.Playing, "The board fixture did not enter play.");
        return board;
    }
    private static IReadOnlyList<string> Hold(BoardSession board, string id, int ms) => board.ObserveHeldButtons([id], At(ms), At(ms), []);
    private static void Clear(BoardSession board, string id, int ms) => board.ObserveHeldButtons([], At(ms), At(ms), [id]);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
