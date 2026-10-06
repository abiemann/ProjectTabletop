using ProjectTabletop.Interaction;

internal static class WaterGardenBoardRegression
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckNavigationAndReset();
        CheckCaptionHolds();
        CheckGestureIsolation();
        Console.WriteLine("Water Garden interaction verification passed: first-page placement, calm reset revision, " +
            "opening and paging, clear-caption cancellation, single-action holds, stale evidence and gesture isolation.");
    }

    private static void CheckNavigationAndReset()
    {
        var board = new BoardSession();
        var opened = new List<BoardScreen>();
        board.BoardOpened += opened.Add;
        var tile = board.Buttons.Single(button => button.Id == "water-garden");
        Require(tile is { Label: "Water Garden", Destination: BoardScreen.WaterGarden, Hold: BoardButtonHold.None } &&
            tile.Bounds == new BoardRect(.52, .45, .40, .16) && board.WaterGardenResetRevision == 0,
            "Water Garden did not fill the right-middle first-page slot with a normal menu gesture target.");
        Require(board.ActivateButton(tile.Id, At(10)) && board.Screen == BoardScreen.WaterGarden &&
            board.Title == "Water Garden" && board.WaterGardenResetRevision == 1 &&
            opened.SequenceEqual([BoardScreen.WaterGarden]), "Opening Water Garden did not request one calm initial field.");
        long navigation = board.NavigationRevision, revision = board.Revision;
        Require(board.ActivateButton("water-garden-calm", At(20)) && board.WaterGardenResetRevision == 2 &&
            board.NavigationRevision == navigation && board.Revision == revision && opened.Count == 1,
            "Calm Water failed to reset only the simulation, or restarted the board.");
        board.ShowWaterGarden(At(30));
        Require(board.WaterGardenResetRevision == 3 && board.NavigationRevision == navigation + 1 && opened.Count == 2,
            "Explicitly reopening Water Garden did not request a new calm field.");
        board.ResetInput(At(40));
        Require(board.WaterGardenResetRevision == 3, "Resetting camera input also reset the water simulation.");
        Require(board.ActivateButton("menu", At(50)) && board.Screen == BoardScreen.Menu &&
            !board.MenuScrolled && board.WaterGardenResetRevision == 3,
            "Water Garden Exit failed to restore the first menu page.");
        Require(board.ActivateButton("menu-scroll-down", At(60)) && board.TickMenu(At(710)) &&
            board.Buttons.Select(button => button.Id).SequenceEqual(["roulette", "paint", "settings", "menu-scroll-up"]) &&
            !board.ActivateButton("water-garden", At(720)), "Water Garden remained selectable from page two.");
        Require(board.ActivateButton("menu-scroll-up", At(730)) && board.TickMenu(At(1380)) &&
            board.ActivateButton("water-garden", At(1390)) && board.WaterGardenResetRevision == 4,
            "Paging back could not reopen Water Garden.");
    }

    private static void CheckCaptionHolds()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(["menu", "water-garden-calm"]) &&
            board.Buttons.All(button => button.Hold == BoardButtonHold.Once && button.Bounds.Y == .85 &&
                button.Bounds.Height >= .105 && button.Bounds.X + button.Bounds.Width < 1),
            "Water Garden needs two stationary, single-action caption holds at the viewer's edge.");
        long navigation = board.NavigationRevision;
        for (int time = 100; time <= 1100; time += 100)
            Require(Hold(board, "water-garden-calm", time).Count == 0,
                "A cold covered caption acted without a fresh clear reference.");
        Clear(board, "water-garden-calm", 1120);
        for (int time = 1200; time <= 1600; time += 100)
            Require(Hold(board, "water-garden-calm", time).Count == 0, "Calm Water acted before a full second.");
        Require(board.HoldProgress(At(1600)).Single().Progress > 0, "The partial caption hold did not begin.");
        Clear(board, "water-garden-calm", 1650);
        Require(board.HoldProgress(At(1650)).Count == 0,
            "Readable lettering did not immediately cancel Calm Water's partial hold.");
        for (int time = 1700; time < 2700; time += 100)
            Require(Hold(board, "water-garden-calm", time).Count == 0, "Calm Water borrowed cancelled hold time.");
        Require(Hold(board, "water-garden-calm", 2700).SequenceEqual(["water-garden-calm"]) &&
            board.WaterGardenResetRevision == 2 && board.NavigationRevision == navigation,
            "A complete caption hold did not calm the water exactly once without navigation.");
        for (int time = 2800; time <= 3900; time += 100)
            Require(Hold(board, "water-garden-calm", time).Count == 0 && board.WaterGardenResetRevision == 2,
                "Leaving the caption covered repeatedly calmed the water.");
        Clear(board, "water-garden-calm", 4000);
        Clear(board, "water-garden-calm", 4400);
        for (int time = 4500; time < 5500; time += 100)
            Require(Hold(board, "water-garden-calm", time).Count == 0, "A released caption reactivated too early.");
        Require(Hold(board, "water-garden-calm", 5500).SequenceEqual(["water-garden-calm"]) &&
            board.WaterGardenResetRevision == 3, "A fresh clear-and-hold could not calm the water again.");
        Require(board.ObserveHeldButtons(["water-garden-calm"], At(100), At(5600), []).Count == 0 &&
            board.ObserveHeldButtons(["water-garden-calm"], At(5500), At(5600), []).Count == 0 &&
            board.ObserveHeldButtons(["water-garden-calm"], At(6000), At(5900), []).Count == 0 &&
            board.WaterGardenResetRevision == 3, "Stale, duplicate or future caption evidence changed the water.");
        Clear(board, "menu", 5700);
        for (int time = 5800; time < 6800; time += 100)
            Require(Hold(board, "menu", time).Count == 0, "Exit acted before one full second of caption evidence.");
        Require(Hold(board, "menu", 6800).SequenceEqual(["menu"]) && board.Screen == BoardScreen.Menu &&
            board.Buttons.Any(button => button.Id == "water-garden"), "The caption-held Exit did not return to the menu.");
    }

    private static void CheckGestureIsolation()
    {
        var board = new BoardSession();
        board.ShowWaterGarden(At(0));
        var calm = board.Buttons.Single(button => button.Id == "water-garden-calm");
        var exit = board.Buttons.Single(button => button.Id == "menu");
        BoardAim Center(BoardButton button) => new(button.Bounds.X + button.Bounds.Width / 2,
            button.Bounds.Y + button.Bounds.Height / 2);
        var aim = Center(calm);
        Require(board.Update([new(aim.U, aim.V, At(1010), 1)], At(10), At(10)) is null &&
            board.WaterGardenResetRevision == 1 && board.HoveredButtonIds.Count == 0,
            "A pinch over Calm Water activated or highlighted its hold-only caption.");
        aim = Center(exit);
        var together = new BoardHandSample(aim.U, aim.V, DateTimeOffset.MinValue, 0)
        {
            TrackingId = 7, FingerAim = aim, FourFingersExtended = true, FingersTogether = true
        };
        var apart = together with { FingersTogether = false, IndexFingerSeparated = true };
        foreach (int time in new[] { 100, 200, 300, 400 })
            Require(board.Update([time < 300 ? together : apart], At(time), At(time)) is null &&
                board.FingerSelectionFeedback.Count == 0 && board.Screen == BoardScreen.WaterGarden,
                "Landmark gestures selected the caption-only Exit.");
        Clear(board, "water-garden-calm", 450);
        Require(board.ActivateButton("water-garden-calm", At(500)) && board.WaterGardenResetRevision == 2,
            "The local pointer could not calm the water.");
        for (int time = 600; time <= 1700; time += 100)
            Require(Hold(board, "water-garden-calm", time).Count == 0 && board.WaterGardenResetRevision == 2,
                "The pointer action left old caption readiness available for another reset.");
        board.ShowMenu(At(1800));
        Require(!board.ActivateButton("water-garden-calm", At(1810)) &&
            Hold(board, "water-garden-calm", 1820).Count == 0 && board.WaterGardenResetRevision == 2,
            "A water action escaped into another board.");
    }

    private static DateTimeOffset At(int milliseconds) => Origin.AddMilliseconds(milliseconds);
    private static IReadOnlyList<string> Hold(BoardSession board, string id, int milliseconds) =>
        board.ObserveHeldButtons([id], At(milliseconds), At(milliseconds), []);
    private static void Clear(BoardSession board, string id, int milliseconds) =>
        board.ObserveHeldButtons([], At(milliseconds), At(milliseconds), [id]);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
