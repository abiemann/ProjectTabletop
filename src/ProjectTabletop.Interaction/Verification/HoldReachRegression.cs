using ProjectTabletop.Interaction;

internal static class HoldReachRegression
{
    public static void Run()
    {
        CheckExitAcrossLowerCaptions();
        foreach (bool strict in new[] { false, true })
        {
            CheckWithdrawalRequiresRelease(strict, spentFirst: false);
            CheckWithdrawalRequiresRelease(strict, spentFirst: true);
        }
        CheckIneligibleUpperCannotRedirect();
        CheckStaleReachDoesNotCancelCurrentHold();
        CheckCloseRowsAndExplicitChildVeto();
    }

    private static void CheckExitAcrossLowerCaptions()
    {
        foreach (var phase in new[] { BlackjackPhase.Betting, BlackjackPhase.PlayerTurn, BlackjackPhase.RoundOver })
        {
            var board = Blackjack(phase);
            var before = board.BlackjackState;
            var exit = board.Buttons.Single(button => button.Id == "menu");
            string[] lower = phase == BlackjackPhase.PlayerTurn ? ["bj-hit", "bj-stand"] : ["bj-bet-50", "bj-bet-100"];
            Observe(board, [], 2000, ["menu", .. lower]);
            for (int time = 2100; time <= 3100; time += 100)
            {
                // The captured arm crossed 50/100 below EXIT. Order of camera
                // results must not choose the wager instead of the reached row.
                var result = Observe(board, [lower[1], "menu", lower[0]], time);
                Require(time < 3100 ? result.Count == 0 : result.SequenceEqual(["menu"]),
                    "Reaching over lower captions did not complete exactly one full EXIT hold in " + phase + ".");
                Require(board.BlackjackState.Revision == before.Revision,
                    "A reached-over caption changed the wager or cards before navigation.");
                if (time < 3100)
                    Require(board.Buttons.Single(button => button.Id == "menu") == exit &&
                            board.HoldProgress(Time(time)).All(progress => progress.ButtonId == "menu"),
                        "Reach priority changed EXIT geometry or kept progress on the arm-covered row.");
            }
            Require(board.Screen == BoardScreen.Menu && board.MenuScrolled && !board.MenuScrolling,
                "The upper-row EXIT did not return to Blackjack's settled second menu page.");
        }
    }

    private static void CheckWithdrawalRequiresRelease(bool strict, bool spentFirst)
    {
        var board = Blackjack(BlackjackPhase.Betting);
        string[] lower = ["bj-bet-50"];
        Feed([], 2000, ["menu", .. lower]);
        // Cover the lower control first; an arriving upper caption must cancel
        // nearly complete progress, and must not release an already spent place.
        for (int time = 2100; time <= (spentFirst ? 3100 : 3000); time += 100)
        {
            var result = Feed(lower, time);
            Require(spentFirst && time == 3100 ? result.SequenceEqual(lower) : result.Count == 0,
                "The lower-caption preparation activated at the wrong time.");
        }
        long revision = board.BlackjackState.Revision;
        for (int time = 3200; time <= 3500; time += 100)
            Require(Feed(["menu", .. lower], time).Count == 0 &&
                    board.HoldProgress(Time(time)).All(progress => progress.ButtonId == "menu"),
                "A brief reach completed the lower hold or activated EXIT early.");
        for (int time = 3600; time <= 5100; time += 100)
            Require(Feed(lower, time, ["menu"]).Count == 0 &&
                    board.HoldProgress(Time(time)).All(progress => progress.ButtonId == "menu" && time < 3900) &&
                    board.BlackjackState.Revision == revision,
                "Withdrawing from EXIT redirected the press to a still-covered lower coin.");
        for (int time = 5200; time <= 5600; time += 100) Feed([], time, lower);
        for (int time = 5700; time <= 6700; time += 100)
            Require(time < 6700 ? Feed(lower, time).Count == 0 : Feed(lower, time).SequenceEqual(lower),
                "The lower coin did not require and accept a fresh full hold after uncovering.");

        IReadOnlyList<string> Feed(string[] held, int time, string[]? clear = null) =>
            board.ObserveHeldButtons(held, Time(time), Time(time), strict ? clear ?? [] : null);
    }

    private static void CheckIneligibleUpperCannotRedirect()
    {
        foreach (bool strict in new[] { false, true })
        foreach (string scenario in new[] { "ordinary", "disabled", "blocked", "blocked-row" })
        {
            bool player = scenario == "disabled";
            var board = Blackjack(player ? BlackjackPhase.PlayerTurn : BlackjackPhase.Betting);
            string lower = player ? "bj-hit" : "bj-bet-50";
            string[] upper = scenario switch
            {
                "blocked" => ["menu"], "blocked-row" => ["menu", "bj-reset"], _ => ["bj-reset"]
            };
            string[] blocked = scenario.StartsWith("blocked", StringComparison.Ordinal) ? upper : [];
            long revision = board.BlackjackState.Revision;
            Feed([], 2000, ["menu", lower]);
            for (int time = 2100; time <= 3600; time += 100)
                Require(Feed([lower, .. upper], time, blocked: blocked).Count == 0 &&
                        board.HoldProgress(Time(time)).Count == 0 && board.BlackjackState.Revision == revision,
                    "An " + scenario + " upper target redirected activation to the lower arm caption.");
            for (int time = 3700; time <= 5000; time += 100)
                Require(Feed([lower], time).Count == 0 && board.BlackjackState.Revision == revision,
                    "An ineligible upper target left the covered lower caption armed.");

            IReadOnlyList<string> Feed(string[] held, int time, string[]? clear = null, string[]? blocked = null) =>
                board.ObserveHeldButtons(held, Time(time), Time(time), strict ? clear ?? [] : null, blocked);
        }
    }

    private static void CheckStaleReachDoesNotCancelCurrentHold()
    {
        var board = Blackjack(BlackjackPhase.Betting);
        Observe(board, [], 2000, ["menu", "bj-bet-50"]);
        for (int time = 2100; time <= 2900; time += 100) Observe(board, ["bj-bet-50"], time);
        foreach (var (frame, now) in new[] { (2400, 3000), (3100, 3000), (2900, 3000) })
            Require(board.ObserveHeldButtons(["menu", "bj-bet-50"], Time(frame), Time(now), []).Count == 0,
                "Stale, future or duplicate reach evidence activated a control.");
        Require(Observe(board, ["bj-bet-50"], 3000).Count == 0 &&
                Observe(board, ["bj-bet-50"], 3100).SequenceEqual(["bj-bet-50"]),
            "Rejected reach evidence canceled a current legitimate lower hold.");
    }

    private static void CheckCloseRowsAndExplicitChildVeto()
    {
        var board = new BoardSession();
        board.ShowGlobe(Time(0));
        Require(board.ActivateButton("globe-drawer-open", Time(100)) && board.TickGlobe(Time(400)) &&
                board.ActivateButton("globe-model", Time(500)), "Could not prepare close model rows.");
        string[] captions = ["globe-model-night", "globe-save"];
        Observe(board, [], 1000, captions);
        for (int time = 1100; time <= 2600; time += 100)
            Require(Observe(board, captions, time).Count == 0 && board.HoldProgress(Time(time)).Count == 0 &&
                    board.SelectedGlobeModel == GlobeModel.Clouds,
                "Closely stacked controls bypassed ambiguity without the explicit model-choice guard.");
        // The board-specific parent/child veto still resolves these close rows.
        for (int time = 2700; time <= 3700; time += 100)
        {
            var result = Observe(board, captions, time, blocked: ["globe-save"]);
            Require(time < 3700 ? result.Count == 0 : result.SequenceEqual(["globe-model-night"]),
                "The shared reach rule overrode Globe's explicit lower-parent veto.");
        }
        Require(board.SelectedGlobeModel == GlobeModel.Night && board.GlobeModelExpanded,
            "Resolving a child model hold closed the model options.");
    }

    private static BoardSession Blackjack(BlackjackPhase phase)
    {
        var game = new BlackjackGame(initialShoe: new[] { 10, 10, 8, 7 }
            .Select(rank => new BlackjackCard(rank, BlackjackSuit.Clubs)));
        var board = new BoardSession(game);
        Require(board.ActivateButton("menu-scroll-down", Time(-2000)) && board.TickMenu(Time(-1000)),
            "Could not prepare Blackjack's second launcher page.");
        board.ShowBlackjack(Time(0));
        if (phase != BlackjackPhase.Betting)
            Require(board.ActivateButton("bj-deal", Time(100)), "Could not prepare an active Blackjack round.");
        if (phase == BlackjackPhase.RoundOver)
        {
            Require(board.ActivateButton("bj-stand", Time(200)), "Could not settle the Blackjack round.");
            board.TickBlackjack(Time(900));
            board.TickBlackjack(Time(1600));
        }
        Require(board.BlackjackState.Phase == phase, "The reach fixture has the wrong Blackjack phase.");
        return board;
    }

    private static IReadOnlyList<string> Observe(BoardSession board, string[] held, int time,
        string[]? clear = null, string[]? blocked = null) =>
        board.ObserveHeldButtons(held, Time(time), Time(time), clear ?? [], blocked);

    private static DateTimeOffset Time(int ms) =>
        new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
