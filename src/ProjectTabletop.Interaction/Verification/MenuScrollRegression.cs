using ProjectTabletop.Interaction;

internal static class MenuScrollRegression
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-03T12:00:00Z");
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    public static void Run()
    {
        CheckScrollAndInputBarriers();
        CheckArrowReleaseAndReverse();
        CheckReset();
    }

    private static void CheckScrollAndInputBarriers()
    {
        var board = new BoardSession();
        var firstPage = board.Buttons.Select(button => (button.Id, button.Bounds)).ToArray();
        Require(board.GetMenuCards(At(0)).Count == 7 && board.Buttons.All(button => button.Id != "roulette"),
            "Roulette must begin below the six visible menu cards.");
        var arrow = Button(board, "menu-scroll-down");
        Require(arrow is { Hold: BoardButtonHold.Once, Label: "v", Enabled: true } &&
            arrow.Bounds == BoardSession.MenuScrollButtonBounds, "The menu arrow lacks a shared one-shot caption hold.");
        Require(board.Update([Pinch(arrow, 1, 100)], At(100), At(100)) is null && !board.MenuScrolling,
            "A pinch bypassed the menu arrow's long press.");
        Clear(board, "menu-scroll-down", 200);
        for (int time = 300; time < 1300; time += 100)
            Require(Hold(board, "menu-scroll-down", time).Count == 0, "The menu scrolled before a full second.");
        Require(Hold(board, "menu-scroll-down", 1300).SequenceEqual(["menu-scroll-down"]) &&
            board.MenuScrolling && board.MenuScrolled, "A full caption hold did not start the downward menu scroll.");
        long revision = board.Revision;
        Require(Near(board.GetMenuScrollOffset(At(1300)), 0) &&
            Near(board.GetMenuScrollOffset(At(1625)), .3) && Near(board.GetMenuScrollOffset(At(1950)), .6) &&
            board.Revision == revision && board.MenuScrolling,
            "Presentation samples must be clock-deterministic and cannot settle interaction state.");
        Require(board.Buttons.All(button => !button.Enabled) &&
            !board.ActivateButton("roulette", At(1450)) && !board.ActivateButton("settings", At(1450)) &&
            !board.ActivateButton("menu-scroll-up", At(1450)), "A moving menu accepted pointer input.");
        Require(board.Update([Pinch(Button(board, "roulette"), 2, 1500)], At(1500), At(1500)) is null &&
            board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
            "A moving card accepted a gesture or retained selection feedback.");
        Clear(board, "menu-scroll-up", 1600);
        Require(Hold(board, "menu-scroll-up", 1700).Count == 0 && board.HoldProgress(At(1700)).Count == 0,
            "The replacement caption warmed while its menu was moving.");
        Require(board.TickMenu(At(1950)) && !board.MenuScrolling && !board.TickMenu(At(1950)) &&
            !board.TickMenu(At(1949)), "Menu settlement repeated or moved backward in time.");
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(["roulette", "settings", "menu-scroll-up"]),
            "The second page did not expose exactly Roulette and the fixed navigation controls.");
        Require(Near(Button(board, "roulette").Bounds.Y, .25) &&
            board.GetMenuCards(At(1950)).Where(button => button.Id != "roulette")
                .All(button => button.Bounds.Y + button.Bounds.Height <= BoardSession.MenuCardViewport.Y),
            "A full-page scroll left an original card in the viewport or misplaced Roulette.");
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
            "Returning to the menu did not restore all six original cards and their positions.");
    }

    private static void CheckArrowReleaseAndReverse()
    {
        var board = new BoardSession();
        var firstPage = board.Buttons.Select(button => (button.Id, button.Bounds)).ToArray();
        Clear(board, "menu-scroll-down", 10);
        for (int time = 100; time <= 1100; time += 100) Hold(board, "menu-scroll-down", time);
        board.TickMenu(At(1750));
        for (int time = 1800; time <= 3100; time += 100)
            Require(Hold(board, "menu-scroll-up", time).Count == 0 && !board.MenuScrolling,
                "Fingers left on the arrow automatically reversed the menu.");
        Clear(board, "menu-scroll-up", 3200);
        Clear(board, "menu-scroll-up", 3550);
        for (int time = 3600; time < 4600; time += 100)
            Require(Hold(board, "menu-scroll-up", time).Count == 0, "The reverse arrow reused earlier hold time.");
        Require(Hold(board, "menu-scroll-up", 4600).SequenceEqual(["menu-scroll-up"]) && board.MenuScrolling &&
            !board.MenuScrolled && Near(board.GetMenuScrollOffset(At(4925)), .3),
            "Released, freshly covered up-arrow did not scroll smoothly back.");
        Require(board.TickMenu(At(5250)) && Near(board.GetMenuScrollOffset(At(5250)), 0) &&
            Button(board, "menu-scroll-down").Enabled &&
            board.Buttons.Select(button => (button.Id, button.Bounds)).SequenceEqual(firstPage),
            "The reverse scroll did not restore the entire first page and its original positions.");
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
