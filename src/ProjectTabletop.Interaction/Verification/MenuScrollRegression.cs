using ProjectTabletop.Interaction;

internal static class MenuScrollRegression
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-03T12:00:00Z");
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    public static void Run()
    {
        CheckReachPastArrow();
        CheckScrollAndInputBarriers();
        CheckArrowReleaseAndReverse();
        CheckReset();
    }

    private static void CheckReachPastArrow()
    {
        var board = new BoardSession();
        var arrow = Button(board, "menu-scroll-down");
        Require(board.Buttons.All(button => !button.IsHold) && arrow is { Label: "v", Enabled: true } &&
            arrow.Bounds == BoardSession.MenuScrollButtonBounds, "Menu navigation must use precision gestures.");
        Clear(board, arrow.Id, 10);
        var hand = Together(arrow);
        for (int time = 100; time <= 2100; time += 100)
        {
            Require(Hold(board, arrow.Id, time).Count == 0 && board.HoldProgress(At(time)).Count == 0,
                "An arm covering the menu arrow warmed or completed a long press.");
            Require(board.Update([hand], At(time), At(time)) is null && !board.MenuScrolling && !board.MenuScrolled,
                "A stationary hand over the menu arrow paged without a selection gesture.");
        }
        var settings = Button(board, "settings");
        Require(SelectFinger(board, Together(settings), 2200) is
            { ButtonId: "settings", Current: BoardScreen.Settings, Gesture: BoardSelectionGesture.IndexSeparation } &&
            !board.MenuScrolled, "Reaching past the arrow to deliberately select Settings scrolled the menu.");
    }

    private static void CheckScrollAndInputBarriers()
    {
        var board = new BoardSession();
        var firstPage = board.Buttons.Select(button => (button.Id, button.Bounds)).ToArray();
        Require(board.GetMenuCards(At(0)).Count == 7 && board.Buttons.Select(button => button.Id).SequenceEqual(
            ["slots", "photo-copy", "blackjack", "crown-deed", "globe", "settings", "menu-scroll-down"]) &&
            board.Buttons.All(button => !button.Bounds.Contains(.72, .53)),
            "The first page must retain five cards with Paint's previous position empty.");
        var arrow = Button(board, "menu-scroll-down");
        Require(board.Update([Pinch(arrow, 1, 1300)], At(1300), At(1300)) is
            { ButtonId: "menu-scroll-down", Gesture: BoardSelectionGesture.Pinch } && board.MenuScrolling && board.MenuScrolled,
            "A deliberate pinch did not start the downward menu scroll.");
        long revision = board.Revision;
        Require(Near(board.GetMenuScrollOffset(At(1300)), 0) &&
            Near(board.GetMenuScrollOffset(At(1625)), .3) && Near(board.GetMenuScrollOffset(At(1950)), .6) &&
            board.Revision == revision && board.MenuScrolling,
            "Presentation samples must be clock-deterministic and cannot settle interaction state.");
        Require(board.Buttons.All(button => !button.Enabled) &&
            !board.ActivateButton("roulette", At(1450)) && !board.ActivateButton("paint", At(1450)) &&
            !board.ActivateButton("settings", At(1450)) &&
            !board.ActivateButton("menu-scroll-up", At(1450)), "A moving menu accepted pointer input.");
        Require(board.Update([Pinch(Button(board, "roulette"), 2, 1500)], At(1500), At(1500)) is null &&
            board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
            "A moving card accepted a gesture or retained selection feedback.");
        Clear(board, "menu-scroll-up", 1600);
        Require(Hold(board, "menu-scroll-up", 1700).Count == 0 && board.HoldProgress(At(1700)).Count == 0,
            "Caption obstruction activated the replacement arrow while the menu was moving.");
        Require(board.TickMenu(At(1950)) && !board.MenuScrolling && !board.TickMenu(At(1950)) &&
            !board.TickMenu(At(1949)), "Menu settlement repeated or moved backward in time.");
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(["roulette", "paint", "settings", "menu-scroll-up"]),
            "The second page did not expose Roulette, Paint and the fixed navigation controls.");
        Require(Near(Button(board, "roulette").Bounds.Y, .25) && Near(Button(board, "paint").Bounds.Y, .25) &&
            Near(Button(board, "roulette").Bounds.X, .08) && Near(Button(board, "paint").Bounds.X, .52) &&
            board.GetMenuCards(At(1950)).Where(button => button.Id is not ("roulette" or "paint"))
                .All(button => button.Bounds.Y + button.Bounds.Height <= BoardSession.MenuCardViewport.Y),
            "A full-page scroll left an original card in the viewport or misplaced Roulette and Paint.");
        var roulette = Button(board, "roulette");
        Require(board.Update([Pinch(roulette, 3, 1949)], At(1949), At(2000)) is null,
            "A delayed camera observation from the animation selected a settled card.");
        Require(board.Update([Pinch(roulette, 4, 1949)], At(2001), At(2001)) is null,
            "A pinch begun during motion selected a settled card.");
        Require(board.Update([Pinch(roulette, 5, 2002) with { SelectionFrameTime = At(1949) }], At(2002), At(2002)) is null,
            "A pre-settlement pointing anchor selected the new card.");
        Require(board.Update([Pinch(roulette, 6, 2100)], At(2100), At(2100)) is
            { ButtonId: "roulette", Current: BoardScreen.Roulette }, "A fresh Roulette card selection failed.");
        board.ShowMenu(At(2200));
        Require(!board.MenuScrolled && !board.MenuScrolling &&
            board.Buttons.Select(button => (button.Id, button.Bounds)).SequenceEqual(firstPage),
            "Returning to the menu did not restore the five first-page cards and their positions.");
    }

    private static void CheckArrowReleaseAndReverse()
    {
        var board = new BoardSession();
        var firstPage = board.Buttons.Select(button => (button.Id, button.Bounds)).ToArray();
        var hand = Together(Button(board, "menu-scroll-down"));
        Require(SelectFinger(board, hand, 100) is
            { ButtonId: "menu-scroll-down", Gesture: BoardSelectionGesture.IndexSeparation },
            "A grouped-to-sideways-index gesture did not page down.");
        Require(board.TickMenu(At(1030)), "The downward page did not settle.");
        for (int time = 1100; time <= 2400; time += 100)
            Require(board.Update([Apart(hand)], At(time), At(time)) is null &&
                Hold(board, "menu-scroll-up", time).Count == 0 && !board.MenuScrolling && board.MenuScrolled,
                "A held or separated hand automatically reversed the menu without a fresh gesture.");
        Require(SelectFinger(board, Together(Button(board, "menu-scroll-up")), 2500) is
            { ButtonId: "menu-scroll-up", Gesture: BoardSelectionGesture.IndexSeparation } && board.MenuScrolling &&
            !board.MenuScrolled && Near(board.GetMenuScrollOffset(At(3105)), .3),
            "Regrouping and deliberately separating the index did not scroll smoothly back.");
        Require(board.TickMenu(At(3430)) && Near(board.GetMenuScrollOffset(At(3430)), 0) &&
            Button(board, "menu-scroll-down").Enabled &&
            board.Buttons.Select(button => (button.Id, button.Bounds)).SequenceEqual(firstPage),
            "The reverse scroll did not restore the entire first page and its original positions.");

        // A pinch pulse lasts longer than the slide. Reusing it at the same
        // physical place must not activate the replacement direction.
        board = new BoardSession();
        var pinch = Pinch(Button(board, "menu-scroll-down"), 1, 100);
        Require(board.Update([pinch], At(100), At(100))?.ButtonId == "menu-scroll-down" && board.TickMenu(At(750)),
            "The pinch paging fixture did not settle.");
        Require(board.Update([pinch], At(800), At(800)) is null && board.MenuScrolled && !board.MenuScrolling,
            "A pinch carried across paging automatically reversed the menu.");
        Require(board.Update([Pinch(Button(board, "menu-scroll-up"), 2, 900)], At(900), At(900))?.ButtonId == "menu-scroll-up",
            "A fresh pinch could not select the return arrow.");
    }

    private static void CheckReset()
    {
        var board = new BoardSession();
        Require(board.ActivateButton("menu-scroll-down", At(100)), "Pointer preview could not scroll the menu.");
        board.ResetInput(At(200));
        Require(!board.MenuScrolling && !board.MenuScrolled && board.HoldProgress(At(200)).Count == 0,
            "Calibration/input reset left a moving menu or a stale hold.");
        Require(board.Update([Pinch(Button(board, "slots"), 1, 150)], At(250), At(250)) is null,
            "An execution from before reset crossed the restored menu.");
    }

    private static BoardButton Button(BoardSession board, string id) => board.Buttons.Single(button => button.Id == id);
    private static BoardHandSample Pinch(BoardButton button, long id, int origin) =>
        new(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2,
            At(origin + 1000), id);
    private static BoardHandSample Together(BoardButton button) => new(double.NaN, double.NaN, DateTimeOffset.MinValue, 0)
    {
        TrackingId = 1, FourFingersExtended = true, FingersTogether = true,
        FingerAim = new(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2)
    };
    private static BoardHandSample Apart(BoardHandSample hand) => hand with { FingersTogether = false, IndexFingerSeparated = true };
    private static BoardNavigation? SelectFinger(BoardSession board, BoardHandSample hand, int start)
    {
        Require(board.Update([hand], At(start), At(start)) is null &&
            board.Update([hand], At(start + 100), At(start + 100)) is null &&
            board.Update([Apart(hand)], At(start + 200), At(start + 200)) is null,
            "A menu gesture activated before deliberate index-separation confirmation.");
        return board.Update([Apart(hand)], At(start + 280), At(start + 280));
    }
    private static IReadOnlyList<string> Hold(BoardSession board, string id, int time) =>
        board.ObserveHeldButtons([id], At(time), At(time), []);
    private static void Clear(BoardSession board, string id, int time) =>
        board.ObserveHeldButtons([], At(time), At(time), [id]);
    private static bool Near(double left, double right) => Math.Abs(left - right) < 1e-10;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
