using ProjectTabletop.Interaction;

internal static class WaterGardenBoardRegression
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int ms) => Origin.AddMilliseconds(ms);
    private static IReadOnlyList<string> Hold(BoardSession board, string id, int ms) =>
        board.ObserveHeldButtons([id], At(ms), At(ms), []);
    private static void Clear(BoardSession board, string id, int ms) =>
        board.ObserveHeldButtons([], At(ms), At(ms), [id]);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        CheckNavigationAndDrawer();
        CheckCaptionHolds();
        CheckDuckAddHold();
        CheckGestureIsolation();
        Console.WriteLine("Water Garden interaction verification passed: hidden action row, drawer timing, " +
            "RESET and DUCK+ revisions, long-press safety, gesture isolation and menu navigation.");
    }

    private static void CheckNavigationAndDrawer()
    {
        var board = new BoardSession();
        var opened = new List<BoardScreen>();
        board.BoardOpened += opened.Add;
        var tile = board.Buttons.Single(button => button.Id == "water-garden");
        Require(tile is { Label: "Water Garden", Destination: BoardScreen.WaterGarden, Hold: BoardButtonHold.None } &&
            tile.Bounds == new BoardRect(.52, .45, .40, .16), "Water Garden lost its first-page tile.");
        Require(board.ActivateButton(tile.Id, At(10)) && board.WaterGardenResetRevision == 1 &&
            board.Buttons.Single() is { Id: "water-drawer-open", Label: "^", Hold: BoardButtonHold.Once },
            "Opening Water Garden did not show only its bottom-left up-arrow.");
        long navigation = board.NavigationRevision;
        Require(board.ActivateButton("water-drawer-open", At(20)) && board.WaterGardenDrawerOpen &&
            board.Buttons.Select(button => button.Id).SequenceEqual(
                ["water-drawer-close", "water-garden-exit", "water-garden-reset", "water-garden-duck-add"]) &&
            board.Buttons[0].Enabled && board.Buttons.Skip(1).All(button => !button.Enabled) &&
            board.NavigationRevision == navigation && board.WaterGardenResetRevision == 1,
            "Opening the drawer failed to reveal three temporarily disabled actions.");
        Require(!board.ActivateButton("water-garden-reset", At(100)) &&
            !board.ActivateButton("water-garden-duck-add", At(100)) &&
            !board.ActivateButton("water-garden-exit", At(100)) &&
            !board.TickWaterGarden(At(319)) && board.TickWaterGarden(At(320)),
            "The moving drawer accepted an action or missed its settling deadline.");
        Require(board.Buttons.All(button => button.Enabled && button.Hold == BoardButtonHold.Once) &&
            board.Buttons[0].Bounds == new BoardRect(.01, .87, .18, .12) &&
            board.Buttons.Skip(1).All(button => button.Bounds.Y == .87 && button.Bounds.Height == .12),
            "The settled row did not expose four separate hold controls at the viewer's edge.");
        long duckAdds = board.WaterGardenDuckAddRevision;
        Require(board.ActivateButton("water-garden-duck-add", At(330)) &&
            board.WaterGardenDuckAddRevision == duckAdds + 1 && board.WaterGardenResetRevision == 1 &&
            board.NavigationRevision == navigation && board.WaterGardenDrawerOpen && opened.Count == 1,
            "DUCK+ navigated, reset water or closed the drawer.");
        Require(board.ActivateButton("water-garden-reset", At(340)) &&
            board.WaterGardenResetRevision == 2 && board.WaterGardenDuckAddRevision == duckAdds + 1 &&
            board.NavigationRevision == navigation && board.WaterGardenDrawerOpen && opened.Count == 1,
            "RESET failed to clear only the simulation.");
        Require(board.ActivateButton("water-drawer-close", At(350)) && !board.WaterGardenDrawerOpen &&
            board.Buttons.Single().Id == "water-drawer-open" && board.WaterGardenResetRevision == 2,
            "The down-arrow did not hide the actions while preserving the water.");
        board.ResetInput(At(400));
        Require(!board.WaterGardenDrawerOpen && board.WaterGardenResetRevision == 2,
            "Resetting camera input also reset water or reopened the drawer.");
        board.ShowWaterGarden(At(500));
        Require(board.WaterGardenResetRevision == 3 && board.NavigationRevision == navigation + 1 &&
            board.Buttons.Single().Id == "water-drawer-open" && opened.Count == 2,
            "Reopening Water Garden failed to restore its initial closed state.");
        Require(board.ActivateButton("water-drawer-open", At(510)) && board.TickWaterGarden(At(810)) &&
            board.ActivateButton("water-garden-exit", At(820)) && board.Screen == BoardScreen.Menu &&
            !board.MenuScrolled && board.Buttons.Any(button => button.Id == "water-garden"),
            "Revealed EXIT did not restore the first menu page.");
        Require(board.ActivateButton("menu-scroll-down", At(830)) && board.TickMenu(At(1480)) &&
            board.Buttons.Select(button => button.Id).SequenceEqual(["roulette", "paint", "settings", "menu-scroll-up"]) &&
            !board.ActivateButton("water-garden", At(1490)), "Water Garden remained on menu page two.");
        Require(board.ActivateButton("menu-scroll-up", At(1500)) && board.TickMenu(At(2150)) &&
            board.ActivateButton("water-garden", At(2160)) && board.WaterGardenResetRevision == 4,
            "Paging back could not reopen Water Garden.");
    }

    private static void CheckCaptionHolds()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        for (int ms = 100; ms <= 1100; ms += 100)
            Require(Hold(board, "water-drawer-open", ms).Count == 0,
                "The up-arrow acted without a fresh clear caption.");
        Clear(board, "water-drawer-open", 1120);
        for (int ms = 1200; ms < 2200; ms += 100)
            Require(Hold(board, "water-drawer-open", ms).Count == 0,
                "The up-arrow acted before a full second.");
        Require(Hold(board, "water-drawer-open", 2200).SequenceEqual(["water-drawer-open"]) &&
            board.WaterGardenDrawerOpen, "The held up-arrow did not open the drawer once.");
        Require(board.ObserveHeldButtons(["water-drawer-close"], At(2300), At(2300), []).Count == 0 &&
            board.TickWaterGarden(At(2500)) &&
            board.ObserveHeldButtons(["water-drawer-close"], At(2600), At(2600), []).Count == 0,
            "An unreleased hand immediately closed the replacement arrow.");
        Clear(board, "water-drawer-close", 2700);
        Clear(board, "water-drawer-close", 3100);
        for (int ms = 3200; ms < 4200; ms += 100)
            Require(Hold(board, "water-drawer-close", ms).Count == 0,
                "The down-arrow borrowed time from a spent up-arrow.");
        Require(Hold(board, "water-drawer-close", 4200).SequenceEqual(["water-drawer-close"]) &&
            !board.WaterGardenDrawerOpen, "A fresh down-arrow hold did not close the drawer.");
        Require(board.ActivateButton("water-drawer-open", At(4500)) && board.TickWaterGarden(At(4800)),
            "The drawer could not reopen for caption tests.");
        Clear(board, "water-garden-reset", 4810);
        for (int ms = 4900; ms <= 5300; ms += 100)
            Require(Hold(board, "water-garden-reset", ms).Count == 0, "RESET acted too soon.");
        Require(board.HoldProgress(At(5300)).Single().Progress > 0,
            "RESET did not show partial hold progress.");
        Clear(board, "water-garden-reset", 5350);
        Require(board.HoldProgress(At(5350)).Count == 0,
            "Intact RESET lettering did not cancel the partial hold.");
        for (int ms = 5400; ms < 6400; ms += 100)
            Require(Hold(board, "water-garden-reset", ms).Count == 0,
                "RESET borrowed cancelled hold time.");
        Require(Hold(board, "water-garden-reset", 6400).SequenceEqual(["water-garden-reset"]) &&
            board.WaterGardenResetRevision == 2 && board.WaterGardenDrawerOpen,
            "A complete RESET hold did not clear once and retain the drawer.");
        for (int ms = 6500; ms <= 7600; ms += 100)
            Require(Hold(board, "water-garden-reset", ms).Count == 0 && board.WaterGardenResetRevision == 2,
                "A covered RESET repeated without release.");
        Require(board.ObserveHeldButtons(["water-garden-reset"], At(6300), At(7700), []).Count == 0 &&
            board.ObserveHeldButtons(["water-garden-reset"], At(8000), At(7900), []).Count == 0,
            "Stale or future caption evidence reset water.");
        Clear(board, "water-garden-exit", 8000);
        for (int ms = 8100; ms < 9100; ms += 100)
            Require(Hold(board, "water-garden-exit", ms).Count == 0, "EXIT acted too soon.");
        Require(Hold(board, "water-garden-exit", 9100).SequenceEqual(["water-garden-exit"]) &&
            board.Screen == BoardScreen.Menu, "A held EXIT did not return to the menu.");
    }

    private static void CheckGestureIsolation()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        var arrow = board.Buttons.Single();
        var aim = new BoardAim(arrow.Bounds.X + arrow.Bounds.Width / 2,
            arrow.Bounds.Y + arrow.Bounds.Height / 2);
        Require(board.Update([new(aim.U, aim.V, At(1010), 1)], At(10), At(10)) is null &&
            !board.WaterGardenDrawerOpen && board.HoveredButtonIds.Count == 0,
            "A pinch selected the hold-only arrow.");
        Require(board.ActivateButton("water-drawer-open", At(100)) && board.TickWaterGarden(At(400)),
            "The gesture test could not reveal the controls.");
        var reset = board.Buttons.Single(button => button.Id == "water-garden-reset");
        aim = new(reset.Bounds.X + reset.Bounds.Width / 2, reset.Bounds.Y + reset.Bounds.Height / 2);
        var together = new BoardHandSample(aim.U, aim.V, DateTimeOffset.MinValue, 0)
        { TrackingId = 7, FingerAim = aim, FourFingersExtended = true, FingersTogether = true };
        var apart = together with { FingersTogether = false, IndexFingerSeparated = true };
        foreach (int ms in new[] { 500, 600, 700, 800 })
            Require(board.Update([ms < 700 ? together : apart], At(ms), At(ms)) is null &&
                board.FingerSelectionFeedback.Count == 0 && board.WaterGardenResetRevision == 1,
                "A gesture selected a hold-only drawer action.");
        Require(board.ActivateButton("water-garden-reset", At(900)) && board.WaterGardenResetRevision == 2,
            "A local pointer could not reset water.");
        for (int ms = 1000; ms <= 2100; ms += 100)
            Require(Hold(board, "water-garden-reset", ms).Count == 0 && board.WaterGardenResetRevision == 2,
                "The pointer action left caption readiness for another reset.");
        board.ShowMenu(At(2200));
        Require(!board.ActivateButton("water-garden-reset", At(2210)) &&
            Hold(board, "water-garden-reset", 2220).Count == 0,
            "A water action escaped into the menu.");
    }

    private static void CheckDuckAddHold()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        Require(board.ActivateButton("water-drawer-open", At(100)) && board.TickWaterGarden(At(400)),
            "The DUCK+ hold fixture could not settle its drawer.");
        Clear(board, "water-garden-duck-add", 420);
        for (int ms = 500; ms < 1500; ms += 100)
            Require(Hold(board, "water-garden-duck-add", ms).Count == 0,
                "DUCK+ acted before one full second.");
        Require(Hold(board, "water-garden-duck-add", 1500).SequenceEqual(["water-garden-duck-add"]) &&
            board.WaterGardenDuckAddRevision == 1 && board.WaterGardenResetRevision == 1,
            "One complete DUCK+ hold did not add exactly one duck.");
        for (int ms = 1600; ms <= 2700; ms += 100)
            Require(Hold(board, "water-garden-duck-add", ms).Count == 0 &&
                board.WaterGardenDuckAddRevision == 1,
                "A covered DUCK+ acted again without release.");
        Clear(board, "water-garden-duck-add", 2800);
        Clear(board, "water-garden-duck-add", 3200);
        for (int ms = 3300; ms < 4300; ms += 100)
            Require(Hold(board, "water-garden-duck-add", ms).Count == 0,
                "Re-armed DUCK+ borrowed time from its first hold.");
        Require(Hold(board, "water-garden-duck-add", 4300).SequenceEqual(["water-garden-duck-add"]) &&
            board.WaterGardenDuckAddRevision == 2,
            "A second clear-and-hold could not add another duck.");
    }
}
