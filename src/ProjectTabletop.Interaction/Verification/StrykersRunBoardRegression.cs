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
        CheckFooterLayout();
        CheckCaptionHolds();
        CheckInputResetAndExit();
        CheckPausedExit();
        CheckReturningControls(victory: false);
        CheckReturningControls(victory: true);
        Console.WriteLine("Stryker's Run board passed: first-page launch and second-page Blackjack, lone-hero ready state, " +
            "EXIT/PLAY caption holds, paired paused EXIT controls and fresh resume barriers, stationary control fades, delayed rail entry, " +
            "rail reset barriers and fresh holds after defeat or campaign victory.");
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
                Math.Abs(button.Bounds.Width - .98 * .32) < .000001 &&
                Math.Abs(button.Bounds.Height - .98 * .085) < .000001 &&
                Math.Abs(button.Bounds.Y + button.Bounds.Height / 2 - (.01 + .98 * .95)) < .000001 &&
                button.Bounds.X >= 0 && button.Bounds.Y >= 0 &&
                button.Bounds.X + button.Bounds.Width <= 1 && button.Bounds.Y + button.Bounds.Height <= 1) &&
            opened.SequenceEqual([BoardScreen.StrykersRun]),
            "The ready board must keep the two large caption controls inside the game footer, centred on the same row as paused EXIT.");
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

    private static void CheckFooterLayout()
    {
        BoardRect[] fields = [new(.30, .02, .40, .96), new(.02, .12, .96, .76)];
        var board = new BoardSession();
        board.ShowStrykersRun(At(0));
        for (int index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            board.SetStrykersRunFieldBounds(field, At(10 + index));
            var buttons = board.Buttons;
            Require(buttons.Count == 2 && buttons.All(button =>
                button.Bounds.X >= field.X && button.Bounds.X + button.Bounds.Width <= field.X + field.Width &&
                button.Bounds.Y >= field.Y + field.Height * .90 &&
                button.Bounds.Y + button.Bounds.Height <= field.Y + field.Height &&
                Math.Abs(button.Bounds.Y + button.Bounds.Height / 2 - (field.Y + field.Height * .95)) < .000001 &&
                Math.Abs(button.Bounds.Width - field.Width * .32) < .000001 &&
                Math.Abs(button.Bounds.Height - field.Height * .085) < .000001) &&
                Math.Abs((buttons[0].Bounds.X + buttons[1].Bounds.X + buttons[1].Bounds.Width) / 2 -
                    (field.X + field.Width / 2)) < .000001,
                "Narrow and wide game fields must contain a centred, equally sized EXIT/PLAY pair inside their black footer.");

            var paused = Running();
            paused.ClearStrykersRunInput(At(501));
            var before = paused.StrykersRunState;
            long inputRevision = paused.StrykersRunInputRevision, navigation = paused.NavigationRevision;
            paused.SetStrykersRunFieldBounds(field, At(510));
            Require(ReferenceEquals(before, paused.StrykersRunState) &&
                paused.StrykersRunInputRevision == inputRevision && paused.NavigationRevision == navigation,
                "Changing footer layout must not reset the paused run, navigation, or rail input epoch.");
            Require(paused.Buttons.All(button => button.Bounds.Y == buttons[0].Bounds.Y &&
                button.Bounds.Height == buttons[0].Bounds.Height && button.Bounds.Width == .12) &&
                paused.Buttons[0].Bounds.X == .015 && paused.Buttons[1].Bounds.X == .865 &&
                paused.Buttons[0].Bounds.X + paused.Buttons[0].Bounds.Width < field.X + field.Width * .18 &&
                paused.Buttons[1].Bounds.X > field.X + field.Width * .82,
                "Paused EXIT controls must share the ready controls' row and thickness while remaining beside the dot rail.");
        }

        const string play = "strykers-run-play";
        Clear(board, play, 100);
        for (int ms = 200; ms <= 600; ms += 100) Hold(board, play, ms);
        Require(board.HoldProgress(At(600)).Count == 1, "The layout fixture did not establish a partial caption hold.");
        board.SetStrykersRunFieldBounds(fields[0], At(650));
        Require(board.HoldProgress(At(650)).Count == 0, "A changed footer retained its previous caption's partial hold.");
        board.ObserveHeldButtons([], At(649), At(651), [play]);
        for (int ms = 700; ms <= 1800; ms += 100)
            Require(Hold(board, play, ms).Count == 0 && board.StrykersRunPhase == StrykersRunPhase.Ready,
                "A layout change reused old clear evidence or accepted a delayed pre-layout clear frame.");
        Clear(board, play, 1810);
        for (int ms = 1900; ms < 2900; ms += 100)
            Require(Hold(board, play, ms).Count == 0, "Repositioned PLAY accepted less than a fresh full caption hold.");
        Require(Hold(board, play, 2900).SequenceEqual([play]) &&
            board.StrykersRunPhase == StrykersRunPhase.Starting,
            "A clear repositioned caption followed by a full hold failed to start the run.");
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
        var game = (StrykersRunGame)typeof(BoardSession).GetField("_strykersRun", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
        var craters = (IList)typeof(StrykersRunGame).GetField("_stormCraters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var craterType = typeof(StrykersRunGame).GetNestedType("StormCrater", BindingFlags.NonPublic)!;
        craters.Add(Activator.CreateInstance(craterType, 900, .5f, .5f, before.RunSeconds, before.Distance)!);
        board.ShowMenu(At(530));
        float stopped = board.StrykersRunState.RunSeconds;
        board.SetStrykersRunInput(.1f, At(540));
        board.TickStrykersRun(At(550));
        Require(board.StrykersRunState.Phase == StrykersRunPhase.WaitingForInput &&
            board.StrykersRunState.RunSeconds == stopped, "Navigation retained active off-screen rail input.");
        Require(board.StrykersRunState.Craters.Count == 0 && craters.Count == 0,
            "Leaving Stryker's Run must discard persistent crater state from memory.");
    }

    private static void CheckPausedExit()
    {
        foreach (string source in new[] { "missing", "cleared", "stale" })
        foreach (string exitId in new[] { "strykers-run-exit", "strykers-run-exit-right" })
        {
            var board = Running();
            var before = board.StrykersRunState;
            long inputRevision = board.StrykersRunInputRevision, navigation = board.NavigationRevision;
            int pausedAt = Pause(500);
            Require(board.StrykersRunState is { Phase: StrykersRunPhase.WaitingForInput, RecoveringInput: true } &&
                board.StrykersRunControlsProgress == 1 && !board.StrykersRunControlsAnimating &&
                board.StrykersRunRailProgress == 1 && !board.StrykersRunRailAnimating,
                $"A {source} rail observation must reveal paired paused EXIT controls while keeping the settled rail available.");
            Require(board.Buttons.Select(button => button.Id).SequenceEqual(["strykers-run-exit", "strykers-run-exit-right"]) &&
                board.Buttons.All(button => button is { Label: "EXIT", Enabled: true, Hold: BoardButtonHold.Once }) &&
                board.Buttons[0].Bounds == new BoardRect(.015, .01 + .98 * .9075, .12, .98 * .085) &&
                board.Buttons[1].Bounds == new BoardRect(.865, .01 + .98 * .9075, .12, .98 * .085) &&
                !board.ActivateButton("strykers-run-play", At(pausedAt + 1)),
                "The paused run must expose matching EXIT holds beside both rail ends, without a restart button.");
            var exit = board.Buttons.Single(button => button.Id == exitId);
            var hand = new BoardHandSample(exit.Bounds.X + exit.Bounds.Width / 2,
                exit.Bounds.Y + exit.Bounds.Height / 2, At(pausedAt + 1010), 1);
            Require(board.Update([hand], At(pausedAt + 10), At(pausedAt + 10)) is null &&
                board.Screen == BoardScreen.StrykersRun,
                "A landmark or selection gesture activated the paused EXIT without corrupted lettering.");
            for (int ms = pausedAt + 100; ms <= pausedAt + 1200; ms += 100)
                Require(Hold(board, exit.Id, ms).Count == 0,
                    "The newly visible paused caption armed before being observed clear.");
            Clear(board, exit.Id, pausedAt + 1210);
            for (int ms = pausedAt + 1300; ms <= pausedAt + 1700; ms += 100) Hold(board, exit.Id, ms);
            Require(board.HoldProgress(At(pausedAt + 1700)).Count == 1,
                "A clear-then-corrupted paused EXIT did not accumulate hold progress.");
            Clear(board, exit.Id, pausedAt + 1710);
            Require(board.HoldProgress(At(pausedAt + 1710)).Count == 0,
                "Intact EXIT lettering must cancel the paused hold immediately.");
            for (int ms = pausedAt + 1800; ms <= pausedAt + 2200; ms += 100) Hold(board, exit.Id, ms);
            Require(board.HoldProgress(At(pausedAt + 2200)).Count == 1,
                "The fixture did not establish a partial EXIT hold before rail recovery.");

            int resumedAt = pausedAt + 2210;
            board.SetStrykersRunInput(.7f, At(resumedAt));
            board.TickStrykersRun(At(resumedAt));
            var resumed = board.StrykersRunState;
            Require(resumed.Phase == StrykersRunPhase.Playing && !resumed.RecoveringInput &&
                board.Buttons.Count == 0 && !board.StrykersRunControlsVisible &&
                board.HoldProgress(At(resumedAt)).Count == 0 &&
                !board.ActivateButton(exit.Id, At(resumedAt)),
                "Returning to the rail must immediately hide EXIT and discard its partial hold.");
            Require(resumed.RunSeconds == before.RunSeconds && resumed.SquadX == before.SquadX &&
                resumed.Distance == before.Distance && resumed.Score == before.Score &&
                resumed.SoldierCount == before.SoldierCount && resumed.Level == before.Level &&
                board.StrykersRunInputRevision == inputRevision && board.NavigationRevision == navigation,
                "Pause and resume must preserve the run and rail registration without replaying paused time.");

            int pausedAgainAt = Pause(resumedAt);
            board.ObserveHeldButtons([], At(pausedAgainAt - 1), At(pausedAgainAt + 1), [exit.Id]);
            for (int ms = pausedAgainAt + 100; ms <= pausedAgainAt + 1200; ms += 100)
                Require(Hold(board, exit.Id, ms).Count == 0 && board.Screen == BoardScreen.StrykersRun,
                    $"The {source} pause reused old clear evidence or a delayed pre-pause frame to exit.");
            Clear(board, exit.Id, pausedAgainAt + 1210);
            for (int ms = pausedAgainAt + 1300; ms < pausedAgainAt + 2300; ms += 100)
                Require(Hold(board, exit.Id, ms).Count == 0, "Paused EXIT accepted less than a fresh full hold.");
            Require(Hold(board, exit.Id, pausedAgainAt + 2300).SequenceEqual([exit.Id]) &&
                board.Screen == BoardScreen.Menu,
                $"A fresh clear caption followed by a full {exitId} hold did not return to the menu after {source} input.");

            int Pause(int lastInputAt)
            {
                int at = lastInputAt + (source == "stale" ? 260 : 10);
                if (source == "missing") board.SetStrykersRunInput(null, At(at));
                else if (source == "cleared") board.ClearStrykersRunInput(At(at));
                else board.TickStrykersRun(At(at));
                return at;
            }
        }
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
