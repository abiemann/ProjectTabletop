using ProjectTabletop.Interaction;

internal static class MenuScrollRegression
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-03T12:00:00Z");
    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);

    public static void Run()
    {
        CheckCaptionOnlyArrow();
        CheckScrollAndInputBarriers();
        CheckArrowReleaseAndReverse();
        CheckReturnPage();
        CheckReset();
    }

    private static void CheckCaptionOnlyArrow()
    {
        var board = new BoardSession();
        var arrow = Button(board, "menu-scroll-down");
        Require(arrow is { Label: "v", Enabled: true, Hold: BoardButtonHold.Once } &&
            arrow.Bounds == BoardSession.MenuScrollButtonBounds &&
            board.Buttons.Where(button => button.Id != arrow.Id).All(button => !button.IsHold),
            "Only the paging arrow should use a one-shot caption hold.");
        var hand = Together(arrow);
        for (int time = 100; time <= 1300; time += 100)
        {
            Require(Hold(board, arrow.Id, time).Count == 0 && board.HoldProgress(At(time)).Count == 0,
                "A covered arrow armed without a fresh clear-caption observation.");
            Require(board.Update([hand], At(time), At(time)) is null && !board.MenuScrolling && !board.MenuScrolled,
                "Hand landmarks alone paged the menu.");
        }
        Require(board.Update([Pinch(arrow, 1, 1400)], At(1400), At(1400)) is null &&
            SelectFinger(board, hand, 1500) is null && !board.MenuScrolled,
            "A pinch or sideways-index gesture activated the caption-only arrow.");

        Clear(board, arrow.Id, 1800);
        for (int time = 1900; time <= 2400; time += 100)
            Require(Hold(board, arrow.Id, time).Count == 0, "The arrow paged before a full second of caption damage.");
        Require(board.HoldProgress(At(2400)).Single().Progress == .5,
            "The menu arrow did not show shared long-press progress.");
        Clear(board, arrow.Id, 2450);
        Require(board.HoldProgress(At(2450)).Count == 0,
            "Intact arrow lettering did not cancel partial hold progress.");
        for (int time = 2500; time < 3500; time += 100)
            Require(Hold(board, arrow.Id, time).Count == 0,
                "A new hold inherited progress from an earlier interrupted obstruction.");
        Require(Hold(board, arrow.Id, 3500).SequenceEqual([arrow.Id]) && board.MenuScrolling,
            "A full second of fresh arrow obstruction did not page once.");
        Require(board.TickMenu(At(4150)), "The held-arrow paging fixture did not settle.");
        var up = Button(board, "menu-scroll-up");
        Require(up is { Label: "^", Hold: BoardButtonHold.Once } &&
            board.Update([Pinch(up, 2, 4200)], At(4200), At(4200)) is null &&
            SelectFinger(board, Together(up), 4300) is null && board.MenuScrolled,
            "The return arrow accepted a gesture instead of caption evidence.");
        var settings = Button(board, "settings");
        Require(SelectFinger(board, Together(settings), 4700) is
            { ButtonId: "settings", Current: BoardScreen.Settings, Gesture: BoardSelectionGesture.IndexSeparation },
            "Making the arrow a hold button disabled the existing Settings selection gesture.");
    }

    private static void CheckScrollAndInputBarriers()
    {
        var board = new BoardSession();
        var firstPage = board.Buttons.Select(button => (button.Id, button.Bounds)).ToArray();
        Require(board.GetMenuCards(At(0)).Count == 10 && board.Buttons.Select(button => button.Id).SequenceEqual(
            ["slots", "football", "strykers-run", "water-garden", "crown-deed", "globe", "settings", "menu-scroll-down"]) &&
            Button(board, "football").Bounds == new BoardRect(.52, .25, .40, .16) &&
            Button(board, "water-garden").Bounds == new BoardRect(.52, .45, .40, .16),
            "The first page must contain six cards with Football beside Slots and Water Garden beside Stryker's Run.");
        var arrow = Button(board, "menu-scroll-down");
        Clear(board, arrow.Id, 200);
        for (int time = 300; time < 1300; time += 100)
            Require(Hold(board, arrow.Id, time).Count == 0, "Paging started before the hold was complete.");
        Require(Hold(board, arrow.Id, 1300).SequenceEqual([arrow.Id]) && board.MenuScrolling && board.MenuScrolled,
            "The completed arrow hold did not start the downward menu scroll.");
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
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(["roulette", "paint", "photo-copy", "blackjack", "settings", "menu-scroll-up"]),
            "The second page did not expose Roulette, Paint, Photo Copy, Blackjack and the fixed navigation controls.");
        var secondPage = board.Buttons.Select(button => (button.Id, button.Bounds)).ToArray();
        Require(Near(Button(board, "roulette").Bounds.Y, .25) && Near(Button(board, "paint").Bounds.Y, .25) &&
            Near(Button(board, "roulette").Bounds.X, .08) && Near(Button(board, "paint").Bounds.X, .52) &&
            Near(Button(board, "photo-copy").Bounds.X, .08) && Near(Button(board, "photo-copy").Bounds.Y, .45) &&
            Near(Button(board, "blackjack").Bounds.X, .52) && Near(Button(board, "blackjack").Bounds.Y, .45) &&
            board.GetMenuCards(At(1950)).Where(button => button.Id is not ("roulette" or "paint" or "photo-copy" or "blackjack"))
                .All(button => button.Bounds.Y + button.Bounds.Height <= BoardSession.MenuCardViewport.Y),
            "A full-page scroll left an original card in the viewport or misplaced Roulette, Paint and Photo Copy.");
        var roulette = Button(board, "roulette");
        Require(board.Update([Pinch(roulette, 3, 1949)], At(1949), At(2000)) is null,
            "A delayed camera observation from the animation selected a settled card.");
        Require(board.Update([Pinch(roulette, 4, 1949)], At(2001), At(2001)) is null,
            "A pinch begun during motion selected a settled card.");
        Require(board.Update([Pinch(roulette, 5, 2002) with { SelectionFrameTime = At(1949) }], At(2002), At(2002)) is null,
            "A pre-settlement pointing anchor selected the new card.");
        Require(board.Update([Pinch(roulette, 6, 2100)], At(2100), At(2100)) is
            { ButtonId: "roulette", Current: BoardScreen.Roulette }, "A fresh Roulette card selection failed.");
        Require(Hold(board, "roulette-exit", 2150).Count == 0,
            "Roulette EXIT armed without a fresh clear-caption observation.");
        Clear(board, "roulette-exit", 2200);
        for (int time = 2300; time < 3300; time += 100)
            Require(Hold(board, "roulette-exit", time).Count == 0, "Roulette EXIT completed early.");
        Require(Hold(board, "roulette-exit", 3300).SequenceEqual(["roulette-exit"]) &&
            board.Screen == BoardScreen.Menu && board.MenuScrolled && !board.MenuScrolling &&
            Near(board.GetMenuScrollOffset(At(3300)), .6) &&
            board.Buttons.Select(button => (button.Id, button.Bounds)).SequenceEqual(secondPage),
            "Holding Roulette EXIT did not restore the settled second menu page and its hit targets.");
        Require(Hold(board, "menu-scroll-up", 3400).Count == 0 && board.HoldProgress(At(3400)).Count == 0,
            "Returning to the menu carried caption readiness onto the paging arrow.");
        Clear(board, "menu-scroll-up", 3500);
        for (int time = 3600; time < 4600; time += 100)
            Require(Hold(board, "menu-scroll-up", time).Count == 0, "The returned paging arrow completed early.");
        Require(Hold(board, "menu-scroll-up", 4600).SequenceEqual(["menu-scroll-up"]) &&
            board.TickMenu(At(5250)) && !board.MenuScrolled &&
            board.Buttons.Select(button => (button.Id, button.Bounds)).SequenceEqual(firstPage),
            "Returning from Roulette prevented a fresh paging hold from reaching the first page.");
    }

    private static void CheckArrowReleaseAndReverse()
    {
        var board = new BoardSession();
        var firstPage = board.Buttons.Select(button => (button.Id, button.Bounds)).ToArray();
        Clear(board, "menu-scroll-down", 10);
        for (int time = 100; time < 1100; time += 100)
            Require(Hold(board, "menu-scroll-down", time).Count == 0, "The first paging hold completed early.");
        Require(Hold(board, "menu-scroll-down", 1100).SequenceEqual(["menu-scroll-down"]) &&
            board.TickMenu(At(1750)), "The downward one-shot paging hold did not settle.");
        for (int time = 1800; time <= 3000; time += 100)
            Require(Hold(board, "menu-scroll-up", time).Count == 0 &&
                board.HoldProgress(At(time)).Count == 0 && !board.MenuScrolling && board.MenuScrolled,
                "Keeping the caption covered automatically reversed the one-shot arrow.");

        // A brief clear frame is not a release of the spent physical place.
        Clear(board, "menu-scroll-up", 3100);
        Require(Hold(board, "menu-scroll-up", 3200).Count == 0 &&
            board.HoldProgress(At(3200)).Count == 0, "A brief optical dropout rearmed the replacement arrow.");
        Clear(board, "menu-scroll-up", 3300);
        Clear(board, "menu-scroll-up", 3650);
        for (int time = 3800; time < 4800; time += 100)
            Require(Hold(board, "menu-scroll-up", time).Count == 0, "The reverse hold completed early.");
        Require(Hold(board, "menu-scroll-up", 4800).SequenceEqual(["menu-scroll-up"]) && board.MenuScrolling &&
            !board.MenuScrolled && Near(board.GetMenuScrollOffset(At(5125)), .3),
            "Uncovering and holding the replacement arrow did not scroll smoothly back.");
        Require(board.TickMenu(At(5450)) && Near(board.GetMenuScrollOffset(At(5450)), 0) &&
            Button(board, "menu-scroll-down").Enabled &&
            board.Buttons.Select(button => (button.Id, button.Bounds)).SequenceEqual(firstPage),
            "The reverse scroll did not restore the entire first page and its original positions.");
        for (int time = 5500; time <= 6800; time += 100)
            Require(Hold(board, "menu-scroll-down", time).Count == 0 && !board.MenuScrolled && !board.MenuScrolling,
                "The restored down arrow repeated under the same covered caption.");
    }

    private static void CheckReturnPage()
    {
        foreach (bool secondPage in new[] { false, true })
        {
            var board = new BoardSession();
            if (secondPage)
                Require(board.ActivateButton("menu-scroll-down", At(100)) && board.TickMenu(At(750)),
                    "The round-trip fixture could not settle the second menu page.");
            var original = board.Buttons.Select(button => (button.Id, button.Bounds)).ToArray();
            var launchers = board.Buttons.Where(button => button.Destination != BoardScreen.Menu).ToArray();
            int time = 1000;
            foreach (var launcher in launchers)
            {
                Require(board.ActivateButton(launcher.Id, At(time)) && board.Screen == launcher.Destination,
                    $"The round-trip fixture could not open {launcher.Id}.");
                board.ShowMenu(At(time + 100));
                Require(board.MenuScrolled == secondPage && !board.MenuScrolling &&
                    board.Buttons.Select(button => (button.Id, button.Bounds)).SequenceEqual(original),
                    $"Returning from {launcher.Id} lost its launcher page or changed the hit targets.");
                time += 1000;
            }
        }
    }

    private static void CheckReset()
    {
        var board = new BoardSession();
        Require(board.ActivateButton("menu-scroll-down", At(100)), "Pointer preview could not scroll the menu.");
        board.ResetInput(At(200));
        Require(!board.MenuScrolling && board.MenuScrolled && Near(board.GetMenuScrollOffset(At(200)), .6) &&
            board.HoldProgress(At(200)).Count == 0,
            "Calibration/input reset lost the selected page or left a moving menu or stale hold.");
        Require(board.Update([Pinch(Button(board, "roulette"), 1, 150)], At(250), At(250)) is null,
            "An execution from before reset crossed the restored menu.");
        Require(board.ActivateButton("roulette", At(300)), "The reset fixture could not open Roulette.");
        board.ResetInput(At(400));
        board.ShowMenu(At(500));
        Require(board.MenuScrolled && !board.MenuScrolling && Button(board, "roulette").Enabled,
            "A camera/input reset while away discarded the remembered launcher page.");

        board = new BoardSession();
        Clear(board, "menu-scroll-down", 10);
        Hold(board, "menu-scroll-down", 100);
        Hold(board, "menu-scroll-down", 200);
        board.ResetInput(At(250));
        for (int time = 300; time <= 1500; time += 100)
            Require(Hold(board, "menu-scroll-down", time).Count == 0 &&
                board.HoldProgress(At(time)).Count == 0 && !board.MenuScrolling,
                "Input reset retained the old clear-caption readiness or partial hold.");
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
