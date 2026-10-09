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
        CheckStickControlInterlock();
        Console.WriteLine("Water Garden interaction verification passed: hidden action row, drawer timing, " +
            "RESET and repeating DUCK+ revisions, stick control interlock, long-press safety, gesture isolation and menu navigation.");
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
        Require(board.Buttons.All(button => button.Enabled && button.Hold ==
                (button.Id == "water-garden-duck-add" ? BoardButtonHold.Repeat : BoardButtonHold.Once)) &&
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
            board.Buttons.Select(button => button.Id).SequenceEqual(["roulette", "paint", "photo-copy", "settings", "menu-scroll-up"]) &&
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
        const string add = "water-garden-duck-add";
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        Require(board.ActivateButton("water-drawer-open", At(100)) && board.TickWaterGarden(At(400)),
            "The DUCK+ hold fixture could not settle its drawer.");
        long navigation = board.NavigationRevision;
        Clear(board, add, 420);
        for (int ms = 500; ms <= 3500; ms += 100)
        {
            bool expected = ms > 500 && (ms - 500) % 1000 == 0;
            var activated = Hold(board, add, ms);
            Require((expected ? activated.SequenceEqual([add]) : activated.Count == 0) &&
                board.WaterGardenDuckAddRevision == (ms - 500) / 1000,
                "Continuously covered DUCK+ did not add exactly once at each full second.");
            Require(board.ObserveHeldButtons([add], At(ms), At(ms + 1), []).Count == 0 &&
                board.ObserveHeldButtons([add], At(ms - 400), At(ms + 1), []).Count == 0 &&
                board.ObserveHeldButtons([add], At(ms + 20), At(ms + 1), []).Count == 0,
                "Repeated, stale or future camera evidence added a duck.");
        }
        Clear(board, add, 3550);
        Require(board.HoldProgress(At(3550)).Count == 0,
            "Intact DUCK+ lettering did not immediately cancel repeating hold progress.");
        for (int ms = 3600; ms < 4600; ms += 100)
            Require(Hold(board, add, ms).Count == 0 && board.WaterGardenDuckAddRevision == 3,
                "DUCK+ reused elapsed time after the caption became clear.");
        Require(Hold(board, add, 4600).SequenceEqual([add]) && board.WaterGardenDuckAddRevision == 4,
            "A fresh full-second DUCK+ hold did not restart after release.");
        Require(Hold(board, add, 4700).Count == 0, "DUCK+ repeated before its next second.");
        // Losing evidence longer than the shared gap starts a new hold, even if
        // the first returning frame still shows broken lettering.
        for (int ms = 5100; ms < 6100; ms += 100)
            Require(Hold(board, add, ms).Count == 0 && board.WaterGardenDuckAddRevision == 4,
                "DUCK+ continued its previous timer across a camera-evidence gap.");
        Require(Hold(board, add, 6100).SequenceEqual([add]) && board.WaterGardenDuckAddRevision == 5,
            "DUCK+ failed to restart after a full second of reacquired evidence.");

        for (int ms = 6200; ms <= 6800; ms += 100)
            Require(Hold(board, add, ms).Count == 0, "DUCK+ repeated before its stick-interruption fixture.");
        Require(board.HoldProgress(At(6800)).Single().Progress > 0 &&
            board.SetWaterGardenStickPresent(true, At(6850)) && board.HoldProgress(At(6850)).Count == 0,
            "The stick did not cancel an active repeating DUCK+ hold.");
        for (int ms = 6900; ms <= 8000; ms += 100)
            Require(Hold(board, add, ms).Count == 0 && board.WaterGardenDuckAddRevision == 5,
                "DUCK+ continued repeating while the stick was in the water.");
        Require(board.SetWaterGardenStickPresent(false, At(8050)), "The repeat fixture did not release its stick.");
        board.ObserveHeldButtons([], At(8040), At(8060), [add]);
        for (int ms = 8100; ms <= 9200; ms += 100)
            Require(Hold(board, add, ms).Count == 0 && board.WaterGardenDuckAddRevision == 5 &&
                board.HoldProgress(At(ms)).Count == 0,
                "DUCK+ resumed after stick loss without a fresh clear caption.");
        Clear(board, add, 9250);
        for (int ms = 9300; ms <= 15500; ms += 100)
        {
            bool expected = ms > 9300 && ms <= 14300 && (ms - 9300) % 1000 == 0;
            var activated = Hold(board, add, ms);
            Require((expected ? activated.SequenceEqual([add]) : activated.Count == 0) &&
                board.WaterGardenDuckAddRevision == Math.Min(10, 5 + (ms - 9300) / 1000),
                "Re-armed DUCK+ missed its one-second cadence or exceeded the ten-added-duck limit.");
        }
        Require(board.WaterGardenAddedDuckCount == 10 && !board.Buttons.Single(button => button.Id == add).Enabled &&
            board.HoldProgress(At(15500)).Count == 0 && board.WaterGardenResetRevision == 1 &&
            board.NavigationRevision == navigation && board.WaterGardenDrawerOpen,
            "Repeating DUCK+ failed to stop at capacity, reset the pond or changed navigation.");
    }

    private static void CheckStickControlInterlock()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        Require(board.SetWaterGardenStickPresent(true, At(10)) && board.WaterGardenStickPresent &&
            !board.Buttons.Single().Enabled && !board.ActivateButton("water-drawer-open", At(20)),
            "A stick in the water left the bottom-left arrow armed.");
        Clear(board, "water-drawer-open", 30);
        for (int ms = 100; ms <= 1200; ms += 100)
            Require(Hold(board, "water-drawer-open", ms).Count == 0 && !board.WaterGardenDrawerOpen,
                "Covered arrow lettering opened the drawer while the stick was present.");
        Require(board.SetWaterGardenStickPresent(false, At(1250)) && !board.WaterGardenStickPresent &&
            board.Buttons.Single().Enabled && board.ActivateButton("water-drawer-open", At(1300)) &&
            board.TickWaterGarden(At(1600)),
            "Removing the stick did not restore usable drawer controls.");

        long navigation = board.NavigationRevision;
        long reset = board.WaterGardenResetRevision;
        long duckAdds = board.WaterGardenDuckAddRevision;
        Clear(board, "water-garden-reset", 1610);
        for (int ms = 1700; ms <= 2300; ms += 100)
            Require(Hold(board, "water-garden-reset", ms).Count == 0,
                "The interlock fixture completed its RESET hold prematurely.");
        Require(board.HoldProgress(At(2300)).Single().Progress > 0 &&
            board.SetWaterGardenStickPresent(true, At(2350)) && board.HoldProgress(At(2350)).Count == 0 &&
            board.WaterGardenDrawerOpen && board.Buttons.All(button => !button.Enabled),
            "Detecting the stick failed to cancel an in-progress hold or changed the drawer position.");
        foreach (string id in board.Buttons.Select(button => button.Id).ToArray())
            Require(!board.ActivateButton(id, At(2400)), "A disarmed pointer control still acted: " + id);
        for (int ms = 2400; ms <= 3500; ms += 100)
            Require(board.ObserveHeldButtons(["water-garden-reset"], At(ms), At(ms),
                ["water-garden-duck-add", "water-garden-exit", "water-drawer-close"]).Count == 0,
                "Covered RESET lettering completed a hold during stick interaction.");
        Require(board.WaterGardenResetRevision == reset && board.WaterGardenDuckAddRevision == duckAdds &&
            board.NavigationRevision == navigation && board.WaterGardenDrawerOpen,
            "Disarming controls reset the simulation, added a duck or navigated.");

        Require(board.SetWaterGardenStickPresent(false, At(3550)) && board.Buttons.All(button => button.Enabled),
            "Stick loss did not re-enable the settled action row.");
        // A frame captured while disarmed can finish processing after release.
        Require(board.ObserveHeldButtons([], At(3540), At(3560), ["water-garden-reset"]).Count == 0,
            "A delayed disarmed-frame observation activated a control.");
        for (int ms = 3600; ms <= 4700; ms += 100)
            Require(Hold(board, "water-garden-reset", ms).Count == 0 &&
                board.WaterGardenResetRevision == reset && board.HoldProgress(At(ms)).Count == 0,
                "Stick loss reused old clear lettering or pre-stick hold time.");
        Clear(board, "water-garden-reset", 4750);
        for (int ms = 4800; ms < 5800; ms += 100)
        {
            Require(!board.SetWaterGardenStickPresent(false, At(ms)),
                "Repeated absent-stick observations reported a state change.");
            Require(Hold(board, "water-garden-reset", ms).Count == 0,
                "The re-armed RESET acted before a new full second.");
        }
        Require(Hold(board, "water-garden-reset", 5800).SequenceEqual(["water-garden-reset"]) &&
            board.WaterGardenResetRevision == reset + 1 && board.WaterGardenDrawerOpen,
            "Fresh clear lettering and a full hold could not RESET after stick removal.");

        for (int i = 0; i < 10; i++)
            Require(board.ActivateButton("water-garden-duck-add", At(5900 + i * 10)),
                "The capacity fixture could not add its remaining ducks.");
        Require(!board.Buttons.Single(button => button.Id == "water-garden-duck-add").Enabled &&
            board.SetWaterGardenStickPresent(true, At(6050)) &&
            board.SetWaterGardenStickPresent(false, At(6100)) &&
            !board.Buttons.Single(button => button.Id == "water-garden-duck-add").Enabled &&
            !board.ActivateButton("water-garden-duck-add", At(6150)) && board.WaterGardenAddedDuckCount == 10 &&
            board.Buttons.Where(button => button.Id != "water-garden-duck-add").All(button => button.Enabled),
            "Stick removal bypassed the duck limit or left unrelated controls disarmed.");

        board.SetWaterGardenStickPresent(true, At(6200));
        board.ShowMenu(At(6300));
        long menuRevision = board.Revision;
        var menuButtons = board.Buttons.ToArray();
        board.SetWaterGardenStickPresent(false, At(6310));
        Require(!board.WaterGardenStickPresent && !board.SetWaterGardenStickPresent(true, At(6350)) &&
            board.Revision == menuRevision && board.Buttons.SequenceEqual(menuButtons),
            "Water Garden's stick interlock escaped into the main menu.");
        board.ShowWaterGarden(At(6400));
        Require(!board.WaterGardenStickPresent && board.Buttons.Single().Enabled,
            "Reopening Water Garden retained a stale stick interlock.");
    }
}
