using ProjectTabletop.Interaction;

internal static class PhotoCopyDrawerRegression
{
    private static readonly BoardRect Handle = new(.01, .87, .18, .12);
    private static readonly BoardRect[] Row = [Handle, new(.20, .87, .155, .12),
        new(.365, .87, .155, .12), new(.53, .87, .155, .12)];

    public static void Run()
    {
        CheckLayoutAndCaptureIdentity();
        CheckHandleHoldAndRelease();
        CheckSettlementAndFreshEvidence();
        CheckHiddenActionsAndReset();
        Console.WriteLine("Photo Copy drawer passed: shared Globe row bounds, hidden actions, full caption holds, " +
            "300ms settlement and fresh-reference barriers, release/cancellation, capture identity and Clear restart.");
    }

    private static void CheckLayoutAndCaptureIdentity()
    {
        foreach (bool result in new[] { false, true })
        {
            var board = Ready();
            board.PhotoCopyHasSwirl = result;
            board.PhotoCopyShutterEnabled = !result;
            int opened = 0;
            board.BoardOpened += _ => opened++;
            long navigation = board.NavigationRevision;
            Require(board.Buttons.Single() is { Id: "photo-drawer-open", Label: "^", Hold: BoardButtonHold.Once, Enabled: true } up &&
                up.Bounds == Handle && !board.PhotoCopyDrawerOpen && board.GetPhotoCopyDrawerProgress(Time(50)) == 0,
                "A new Photo Copy capture exposed actions before opening its drawer.");
            Require(BoardSession.PhotoCopyShutterBounds == new BoardRect(.01, .06, .98, .66),
                "The drawer changed the hidden object capture field.");
            Require(board.ActivateButton("photo-drawer-open", Time(100)) && board.PhotoCopyDrawerOpen &&
                board.PhotoCopyDrawerOpenedAt == Time(100), "The Photo Copy drawer did not open at the supplied clock.");
            string[] ids = result ? ["photo-drawer-close", "menu", "capture-again", "photo-save"] :
                ["photo-drawer-close", "menu", "photo-swirl", "photo-copy-once"];
            Require(board.Buttons.Select(button => button.Id).SequenceEqual(ids) &&
                board.Buttons.Select(button => button.Bounds).SequenceEqual(Row) &&
                board.Buttons.All(button => button.Hold == BoardButtonHold.Once) &&
                board.Buttons.Skip(1).All(button => !button.Enabled),
                "Photo Copy's drawer identities, physical row or moving-action suppression differs from Globe.");
            long revision = board.Revision;
            Require(Near(board.GetPhotoCopyDrawerProgress(Time(99)), 0) &&
                Near(board.GetPhotoCopyDrawerProgress(Time(250)), .5) &&
                Near(board.GetPhotoCopyDrawerProgress(Time(400)), 1) && board.Revision == revision &&
                !board.TickPhotoCopy(Time(399)) && board.TickPhotoCopy(Time(400)) && !board.TickPhotoCopy(Time(401)),
                "Drawer clock sampling mutated state or settlement did not occur exactly once after 300 ms.");
            Require(board.Buttons.All(button => button.Enabled) && board.NavigationRevision == navigation && opened == 0 &&
                board.PhotoCopyHasSwirl == result && board.PhotoCopyShutterEnabled == !result,
                "Opening the drawer restarted capture, changed its readiness/result, or disabled settled actions.");
            Require(board.ActivateButton("photo-drawer-close", Time(500)) && !board.PhotoCopyDrawerOpen &&
                board.Buttons.Single().Id == "photo-drawer-open" && !board.TickPhotoCopy(Time(900)) &&
                board.NavigationRevision == navigation && opened == 0 &&
                board.PhotoCopyHasSwirl == result && board.PhotoCopyShutterEnabled == !result,
                "Closing the drawer changed capture identity or resurrected its hidden actions.");
            Require(board.ActivateButton("photo-drawer-open", Time(1000)) && board.TickPhotoCopy(Time(1300)),
                "The preserved capture could not reopen its drawer.");
            if (!result) continue;
            Require(board.ActivateButton("capture-again", Time(1400)) && board.NavigationRevision == navigation + 1 &&
                opened == 1 && board.PhotoCopyDrawerOpen && Near(board.GetPhotoCopyDrawerProgress(Time(1400)), 1),
                "Clear failed to restart capture once while preserving its settled action drawer.");
            board.PhotoCopyHasSwirl = false;
            board.PhotoCopyShutterEnabled = true;
            Require(board.Buttons.Select(button => button.Id).SequenceEqual(
                ["photo-drawer-close", "menu", "photo-swirl", "photo-copy-once"]) && board.Buttons.All(button => button.Enabled),
                "The cleared capture did not restore Swirl/Copy in the same open row.");
        }
    }

    private static void CheckHandleHoldAndRelease()
    {
        var board = Ready();
        long navigation = board.NavigationRevision;
        for (int time = 100; time <= 1300; time += 100)
            Require(Held(board, "photo-drawer-open", time).Count == 0 && !board.PhotoCopyDrawerOpen,
                "The drawer handle opened without an initial clear caption.");
        Press(board, "photo-drawer-open", 1400);
        Require(board.PhotoCopyDrawerOpen && board.NavigationRevision == navigation && board.PhotoCopyShutterEnabled,
            "The complete up-arrow hold failed or restarted the capture.");
        // The replacement down arrow occupies the same spent physical place.
        for (int time = 2600; time <= 3900; time += 100)
            Require(Held(board, "photo-drawer-close", time).Count == 0 && board.PhotoCopyDrawerOpen,
                "Resting fingers toggled the drawer shut through the replacement arrow.");
        for (int time = 4000; time <= 4400; time += 100) Clear(board, "photo-drawer-close", time);
        Press(board, "photo-drawer-close", 4500);
        Require(!board.PhotoCopyDrawerOpen && board.NavigationRevision == navigation && board.PhotoCopyShutterEnabled,
            "A released, fresh down-arrow hold changed capture state or failed to close.");
    }

    private static void CheckSettlementAndFreshEvidence()
    {
        foreach (bool result in new[] { false, true })
        {
            var board = Ready();
            board.PhotoCopyHasSwirl = result;
            board.PhotoCopyShutterEnabled = !result;
            string action = result ? "photo-save" : "photo-swirl";
            Require(board.ActivateButton("photo-drawer-open", Time(100)), "The freshness fixture could not open.");
            Require(!board.ActivateButton(action, Time(200)) && !board.ActivateButton("menu", Time(399)),
                "An action became executable during drawer motion.");
            Clear(board, action, 200);
            Require(Held(board, action, 300).Count == 0 && board.HoldProgress(Time(300)).Count == 0,
                "Moving caption evidence started a hold.");
            // Settlement triggered by the observation itself must reject both
            // its old source time and the clear reference from moving controls.
            Require(board.ObserveHeldButtons([], Time(399), Time(400), [action]).Count == 0 &&
                board.Buttons.Single(button => button.Id == action).Enabled,
                "A delayed camera observation did not settle the drawer safely.");
            Require(board.ObserveHeldButtons([], Time(400), Time(450), [action]).Count == 0,
                "A frame at the settlement boundary activated a caption.");
            for (int time = 500; time <= 1700; time += 100)
                Require(Held(board, action, time).Count == 0 && board.HoldProgress(Time(time)).Count == 0,
                    "Pre-settlement clearance armed a newly visible action.");
            Clear(board, action, 1800);
            for (int time = 1900; time <= 2300; time += 100)
                Require(Held(board, action, time).Count == 0, "A fresh action hold completed early.");
            Require(board.HoldProgress(Time(2300)).Count == 1, "Fresh settled caption evidence never armed.");
            Clear(board, action, 2400);
            Require(board.HoldProgress(Time(2400)).Count == 0, "Intact lettering did not cancel the partial action hold.");
            Press(board, action, 2500);
            long navigation = board.NavigationRevision;
            for (int time = 3700; time <= 4900; time += 100)
                Require(Held(board, action, time).Count == 0 && board.NavigationRevision == navigation,
                    "A single-action hold repeated without a physical release.");
        }
    }

    private static void CheckHiddenActionsAndReset()
    {
        foreach (bool result in new[] { false, true })
        {
            var board = Ready(); board.PhotoCopyHasSwirl = result;
            string[] hidden = ["menu", "photo-swirl", "photo-copy-once", "capture-again", "photo-save"];
            foreach (string id in hidden)
                Require(!board.ActivateButton(id, Time(50)), "A hidden Photo Copy action accepted a pointer: " + id);
            for (int time = 100; time <= 1300; time += 100)
                Require(board.ObserveHeldButtons(hidden, Time(time), Time(time), hidden).Count == 0 &&
                    board.HoldProgress(Time(time)).Count == 0, "Hidden captions accumulated or executed a hold.");
            var pinch = new BoardHandSample(.4425, .93, Time(2600), 7) { TrackingId = 3 };
            Require(board.Update([pinch], Time(1400), Time(1400)) is null &&
                board.ActivateButton("photo-drawer-open", Time(1500)) && board.TickPhotoCopy(Time(1800)) &&
                board.Update([pinch], Time(1900), Time(1900)) is null,
                "A hidden/stale gesture executed when its action appeared.");
            long navigation = board.NavigationRevision;
            board.ResetInput(Time(2000));
            Require(!board.PhotoCopyDrawerOpen && board.NavigationRevision == navigation &&
                board.PhotoCopyHasSwirl == result && !board.TickPhotoCopy(Time(2300)),
                "Input reset restarted capture or left its action drawer active.");
            Require(board.ActivateButton("photo-drawer-open", Time(2400)), "The reset drawer could not reopen.");
            board.ShowMenu(Time(2500)); board.ShowPhotoCopy(Time(2600));
            Require(!board.PhotoCopyDrawerOpen && board.Buttons.Single().Id == "photo-drawer-open" &&
                !board.TickPhotoCopy(Time(3000)), "Navigation retained or resurrected an in-flight drawer.");
        }
    }

    private static BoardSession Ready()
    {
        var board = new BoardSession(); board.ShowPhotoCopy(Time(0)); board.PhotoCopyShutterEnabled = true; return board;
    }
    private static IReadOnlyList<string> Held(BoardSession board, string id, int at) =>
        board.ObserveHeldButtons([id], Time(at), Time(at), []);
    private static void Clear(BoardSession board, string id, int at) =>
        Require(board.ObserveHeldButtons([], Time(at), Time(at), [id]).Count == 0, "Clear lettering selected a drawer action.");
    private static void Press(BoardSession board, string id, int clearAt)
    {
        Clear(board, id, clearAt);
        for (int time = clearAt + 100; time < clearAt + 1100; time += 100)
            Require(Held(board, id, time).Count == 0, "Photo Copy drawer action fired before one second: " + id);
        Require(Held(board, id, clearAt + 1100).SequenceEqual([id]), "A full drawer caption hold did not execute: " + id);
    }
    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-9;
    private static DateTimeOffset Time(int milliseconds) =>
        new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
