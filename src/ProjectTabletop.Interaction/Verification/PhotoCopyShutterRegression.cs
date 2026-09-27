using ProjectTabletop.Interaction;

internal static class PhotoCopyShutterRegression
{
    public static void Run()
    {
        CheckShutterAndRearm();
        CheckReadiness();
        CheckAreaAndControls();
        CheckCaptureActions();
        CheckFreshnessAndIdentity();
        Console.WriteLine("Photo Copy shutter verification passed: shared index-separation timing, capture-only event, " +
            "hidden capture target, Swirl/Copy/timed Copy controls, pointer/pinch/index actions, readiness barriers, " +
            "top controls, held gestures, freshness and independent hands.");
    }

    private static void CheckShutterAndRearm()
    {
        var board = PhotoCopy();
        var hand = Together(.5, .6);
        long revision = board.Revision;
        Require(At(board, 100, hand) is null && Feedback(board).Stage == BoardFingerSelectionStage.Arming,
            "The object area did not start the common finger-selection state machine.");
        At(board, 150, hand);
        Require(Feedback(board) is { Stage: BoardFingerSelectionStage.Arming, Progress: .5 },
            "The shutter did not require the same 100 ms together confirmation as a button.");
        At(board, 200, hand);
        Require(Feedback(board).Stage == BoardFingerSelectionStage.Armed &&
            board.HoveredButtonIds.SequenceEqual(["photo-shutter"]), "The shutter did not report ready/hover feedback.");
        for (int at = 300; at <= 1500; at += 100)
            Require(At(board, at, hand) is null, "Holding grouped fingers captured without separating the index.");
        At(board, 1600, Apart(hand));
        Require(At(board, 1679, Apart(hand)) is null &&
            Feedback(board).Stage == BoardFingerSelectionStage.Separating,
            "The shutter executed before 80 ms of separated evidence.");
        Require(At(board, 1680, Apart(hand)) is
            { Previous: BoardScreen.PhotoCopy, Current: BoardScreen.PhotoCopy, ButtonId: "photo-shutter",
                TrackingId: 1, Gesture: BoardSelectionGesture.IndexSeparation } && board.Revision == revision,
            "The shutter event lost its hand/gesture identity or navigated/restarted Photo Copy.");
        for (int at = 1780; at <= 2880; at += 100)
            Require(At(board, at, Apart(hand)) is null, "Holding the index apart repeated the shutter.");
        Require(Select(board, hand, 3000)?.ButtonId == "photo-shutter" && board.Revision == revision,
            "A fresh grouped-to-separated gesture could not retake a ready capture.");
    }

    private static void CheckReadiness()
    {
        var board = new BoardSession();
        board.ShowPhotoCopy(Time(0));
        var hand = Together(.5, .6);
        Require(!board.PhotoCopyShutterEnabled && Select(board, hand, 100) is null &&
            board.FingerSelectionFeedback.Count == 0 && board.HoveredButtonIds.Count == 0,
            "The shutter was available before the app marked capture ready.");
        board.PhotoCopyShutterEnabled = true;
        Require(At(board, 400, Apart(hand)) is null && At(board, 480, Apart(hand)) is null,
            "An index already separated while disabled captured upon readiness.");
        Require(Select(board, hand, 600)?.ButtonId == "photo-shutter", "Readiness blocked a fresh shutter gesture.");
        board.PhotoCopyShutterEnabled = false;
        board.PhotoCopyShutterEnabled = true;
        Require(At(board, 1000, Apart(hand)) is null && At(board, 1080, Apart(hand)) is null,
            "Toggling readiness replayed a previously selected gesture.");

        foreach (int stageTime in new[] { 100, 200, 300 })
        {
            board = PhotoCopy();
            At(board, 100, hand);
            if (stageTime >= 200) At(board, 200, hand);
            if (stageTime >= 300) At(board, 300, Apart(hand));
            board.PhotoCopyShutterEnabled = false;
            Require(board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
                "Disabling capture left active shutter feedback.");
            At(board, 400, hand);
            board.PhotoCopyShutterEnabled = true;
            Require(At(board, 500, Apart(hand)) is null && At(board, 580, Apart(hand)) is null,
                "Pre-readiness arming/separation evidence survived a capture readiness transition.");
            Require(Select(board, hand, 700)?.ButtonId == "photo-shutter",
                "A fresh gesture could not arm after capture became ready again.");
        }

        board = PhotoCopy();
        At(board, 100, hand); board.PhotoCopyShutterEnabled = true;
        At(board, 200, hand); board.PhotoCopyShutterEnabled = true;
        At(board, 300, Apart(hand)); board.PhotoCopyShutterEnabled = true;
        Require(At(board, 380, Apart(hand))?.ButtonId == "photo-shutter",
            "Reporting unchanged readiness every frame prevented capture.");

        foreach (string id in new[] { "menu", "capture-again" })
        {
            board = PhotoCopy();
            var button = board.Buttons.Single(item => item.Id == id);
            var control = Together(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
            At(board, 100, control); board.PhotoCopyShutterEnabled = false;
            Require(board.HoveredButtonIds.SequenceEqual([id]) && Feedback(board).ButtonId == id,
                "Object readiness removed an unrelated top control's hover/arming feedback.");
            At(board, 200, control); board.PhotoCopyShutterEnabled = true;
            Require(Feedback(board).Stage == BoardFingerSelectionStage.Armed,
                "Object readiness interrupted top-control grouping confirmation.");
            At(board, 300, Apart(control)); board.PhotoCopyShutterEnabled = false;
            Require(At(board, 380, Apart(control))?.ButtonId == id,
                "Object readiness interrupted the top control's index-separation confirmation.");
        }
    }

    private static void CheckAreaAndControls()
    {
        var board = PhotoCopy();
        Require(board.Buttons.Select(item => item.Id).SequenceEqual(
                ["menu", "photo-swirl", "photo-copy-once", "photo-copy-timer", "capture-again"]) &&
            !board.ActivateButton("photo-shutter", Time(10)),
            "The hidden shutter appeared as a rendered or pointer-accessible button.");
        Require(BoardSession.PhotoCopyShutterBounds == new BoardRect(.01, .23, .98, .76),
            "The shutter no longer matches the normalized object capture area.");
        foreach (var aim in new[] { new BoardAim(.01, .23), new(.99, .99), new(.5, .6) })
            Require(Select(PhotoCopy(), Together(aim.U, aim.V), 100)?.ButtonId == "photo-shutter",
                "A valid capture-area position could not execute the shutter.");
        foreach (var aim in new[] { new BoardAim(.005, .6), new(.995, .6), new(.5, .22), new(.5, .995),
                     new(.445, .1), new(double.NaN, .6), new(.5, double.PositiveInfinity) })
            Require(Select(PhotoCopy(), Together(aim.U, aim.V), 100) is null,
                "The shutter executed outside its capture area or from an invalid aim.");

        foreach (string id in new[] { "menu", "capture-again" })
        {
            board = PhotoCopy();
            var button = board.Buttons.Single(item => item.Id == id);
            var hand = Together(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
            long revision = board.Revision;
            Require(Select(board, hand, 100)?.ButtonId == id && board.Revision == revision + 1,
                "The shutter intercepted a top control's finger-selection gesture.");
        }

        board = PhotoCopy();
        var pinch = new BoardHandSample(.5, .6, Time(1100), 7) { TrackingId = 2 };
        Require(At(board, 100, pinch) is null && board.HoveredButtonIds.Count == 0,
            "A field pinch activated or hovered the index-only shutter.");
        Require(At(board, 180, pinch with { U = .2, V = .1 }) is null,
            "A pinch consumed outside the buttons replayed on Back to menu.");
        Require(At(board, 260, pinch with { U = .2, V = .1, ExecuteEventId = 8, ExecuteUntil = Time(1260) })
            is { ButtonId: "menu", Gesture: BoardSelectionGesture.Pinch },
            "The shutter removed legacy pinch support from real top controls.");
        board = PhotoCopy();
        var capturedHand = Together(.5, .6);
        At(board, 100, capturedHand); At(board, 200, capturedHand); At(board, 300, Apart(capturedHand));
        Require(At(board, 380, Apart(capturedHand), new BoardHandSample(.2, .1, Time(1380), 9))?.ButtonId == "menu",
            "A pending field shutter stole precedence from a top-button pinch.");
    }

    private static void CheckFreshnessAndIdentity()
    {
        var hand = Together(.5, .6);
        foreach ((int source, int now) in new[] { (400, 751), (500, 490), (300, 380), (250, 380), (250, 250) })
        {
            var board = PhotoCopy();
            At(board, 100, hand); At(board, 200, hand); At(board, 300, Apart(hand));
            Require(board.Update([Apart(hand)], Time(source), Time(now)) is null &&
                board.FingerSelectionFeedback.Count == 0, "An invalid frame completed the Photo Copy shutter.");
        }
        foreach (Action<BoardSession> invalidate in new Action<BoardSession>[]
        {
            board => board.ResetInput(Time(250)),
            board => board.ShowPhotoCopy(Time(250)),
            board => { board.ShowMenu(Time(250)); board.ShowPhotoCopy(Time(260)); }
        })
        {
            var board = PhotoCopy(); At(board, 100, hand); At(board, 200, hand); invalidate(board);
            Require(At(board, 300, Apart(hand)) is null && At(board, 380, Apart(hand)) is null,
                "A pre-reset or pre-navigation pose triggered the shutter afterward.");
            Require(Select(board, hand, 500)?.ButtonId == "photo-shutter", "Fresh shutter input was blocked after reset.");
        }
        var identity = PhotoCopy();
        At(identity, 100, hand); At(identity, 200, hand);
        Require(At(identity, 300, Apart(hand) with { TrackingId = 2 }) is null &&
            At(identity, 380, Apart(hand) with { TrackingId = 2 }) is null,
            "A different hand borrowed the shutter's armed evidence.");
        var simultaneous = PhotoCopy(); var other = hand with { TrackingId = 2 };
        At(simultaneous, 100, hand, other); At(simultaneous, 200, other, hand);
        At(simultaneous, 300, Apart(other), Apart(hand));
        Require(At(simultaneous, 380, Apart(other), Apart(hand)) is { ButtonId: "photo-shutter", TrackingId: 2 },
            "Simultaneous shutters did not retain the executing hand's identity.");
        Require(At(simultaneous, 460, Apart(hand), Apart(other)) is null,
            "The other hand's old shutter confirmation also executed after capture.");
        var missing = PhotoCopy(); At(missing, 100, hand); At(missing, 200, hand); At(missing, 300);
        Require(At(missing, 600, Apart(hand)) is null && At(missing, 680, Apart(hand)) is null,
            "A stale shutter arm survived a long hand dropout.");
        var zero = PhotoCopy();
        Require(Select(zero, hand with { TrackingId = 0 }, 100) is null,
            "A hand without a stable identity triggered the shutter.");
    }

    private static void CheckCaptureActions()
    {
        (string Id, PhotoCopyAction Action, BoardRect Bounds)[] actions =
        [
            ("photo-swirl", PhotoCopyAction.Swirl, new(.31, .055, .13, .105)),
            ("photo-copy-once", PhotoCopyAction.Copy, new(.45, .055, .13, .105)),
            ("photo-copy-timer", PhotoCopyAction.TimedCopy, new(.59, .055, .15, .105))
        ];
        Require(BoardSession.TryGetPhotoCopyAction("photo-shutter", out var fieldAction) && fieldAction == PhotoCopyAction.Swirl &&
            !BoardSession.TryGetPhotoCopyAction("menu", out _) && !BoardSession.TryGetPhotoCopyAction("capture-again", out _) &&
            !BoardSession.TryGetPhotoCopyAction("unknown", out _), "Photo Copy action IDs were mapped incorrectly.");
        var layout = PhotoCopy().Buttons;
        Require(layout.Single(button => button.Id == "menu").Bounds == new BoardRect(.06, .055, .24, .105) &&
            layout.Single(button => button.Id == "capture-again").Bounds == new BoardRect(.75, .055, .19, .105),
            "Photo Copy did not retain its navigation/reset controls in their intended slots.");
        foreach (var button in layout)
            Require(layout.Count(other => other.Bounds.Contains(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2)) == 1, "Photo Copy top controls overlap.");
        var otherBoard = new BoardSession(); otherBoard.ShowHandTrackingTest(Time(0));
        Require(otherBoard.Buttons.Single().Bounds == new BoardRect(.06, .055, .30, .105),
            "The compact Photo Copy menu button changed other boards' navigation targets.");

        foreach (var action in actions)
        {
            var board = new BoardSession(); board.ShowPhotoCopy(Time(0));
            var button = board.Buttons.Single(item => item.Id == action.Id);
            var hand = Together(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
            var pinch = new BoardHandSample(hand.FingerAim!.Value.U, hand.FingerAim.Value.V, Time(1400), 1)
                { TrackingId = 2 };
            long revision = board.Revision;
            Require(button.Bounds == action.Bounds && !button.Enabled &&
                BoardSession.TryGetPhotoCopyAction(action.Id, out var mapped) && mapped == action.Action &&
                !board.ActivateButton(action.Id, Time(20)) && Select(board, hand, 100) is null &&
                board.FingerSelectionFeedback.Count == 0 && board.HoveredButtonIds.Count == 0,
                action.Id + " was enabled or armed without capture readiness.");
            Require(At(board, 400, pinch) is null, "A disabled Photo Copy control accepted a pinch.");
            board.PhotoCopyShutterEnabled = true;
            Require(board.Buttons.Single(item => item.Id == action.Id).Enabled && At(board, 420, pinch) is null &&
                At(board, 430, Apart(hand)) is null && At(board, 510, Apart(hand)) is null,
                "Enabling capture replayed a disabled-period pinch or separated fingers.");
            Require(Select(board, hand, 600) is { Previous: BoardScreen.PhotoCopy, Current: BoardScreen.PhotoCopy,
                TrackingId: 1, Gesture: BoardSelectionGesture.IndexSeparation } selected && selected.ButtonId == action.Id &&
                board.Revision == revision, action.Id + " did not select once without restarting capture.");
            Require(At(board, 980, Apart(hand)) is null && At(board, 1080, Apart(hand)) is null,
                "A held index repeated an explicit capture action.");
            board.PhotoCopyShutterEnabled = false;
            Require(!board.ActivateButton(action.Id, Time(1100)), "Pointer capture ignored readiness after a previous selection.");

            // Every explicit button shares normal pinch consumption and action barriers.
            board = PhotoCopy(); revision = board.Revision;
            pinch = pinch with { ExecuteUntil = Time(1100) };
            Require(At(board, 100, pinch) is { TrackingId: 2, Gesture: BoardSelectionGesture.Pinch } pinched &&
                pinched.ButtonId == action.Id && At(board, 140, pinch) is null && board.Revision == revision,
                action.Id + " pinch did not remain a single same-session action.");
            Require(At(board, 200, pinch with { ExecuteUntil = Time(1200), ExecuteEventId = 2 })?.ButtonId == action.Id &&
                board.Screen == BoardScreen.PhotoCopy && board.Revision == revision,
                "A fresh Photo Copy pinch was blocked after the previous pulse.");

            board = PhotoCopy(); revision = board.Revision;
            Require(board.ActivateButton(action.Id, Time(100)) && board.Screen == BoardScreen.PhotoCopy &&
                board.Revision == revision && board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
                action.Id + " pointer action navigated, reset capture, or left input feedback.");
            Require(At(board, 200, pinch with { ExecuteUntil = Time(1090) }) is null,
                "A pre-pointer camera pulse replayed after explicit capture.");

            foreach (int stageTime in new[] { 100, 200, 300 })
            {
                board = PhotoCopy();
                At(board, 100, hand);
                if (stageTime >= 200) At(board, 200, hand);
                if (stageTime >= 300) At(board, 300, Apart(hand));
                board.PhotoCopyShutterEnabled = false;
                Require(board.HoveredButtonIds.Count == 0 && board.FingerSelectionFeedback.Count == 0,
                    "Capture becoming busy retained an action's hover/arming feedback.");
                board.PhotoCopyShutterEnabled = true;
                Require(At(board, 400, Apart(hand)) is null && At(board, 480, Apart(hand)) is null,
                    "An explicit capture action retained gesture evidence through disabled readiness.");
                Require(Select(board, hand, 600)?.ButtonId == action.Id,
                    "Fresh fingers could not rearm after the capture action became ready.");
            }
        }
    }

    private static BoardSession PhotoCopy()
    {
        var board = new BoardSession(); board.ShowPhotoCopy(Time(0)); board.PhotoCopyShutterEnabled = true; return board;
    }
    private static BoardNavigation? Select(BoardSession board, BoardHandSample hand, int start)
    {
        Require(At(board, start, hand) is null && At(board, start + 100, hand) is null &&
            At(board, start + 200, Apart(hand)) is null, "The shutter executed before the complete gesture.");
        return At(board, start + 280, Apart(hand));
    }
    private static BoardHandSample Together(double u, double v) => new(double.NaN, double.NaN,
        DateTimeOffset.MinValue, 0) { TrackingId = 1, FourFingersExtended = true, FingersTogether = true, FingerAim = new(u, v) };
    private static BoardHandSample Apart(BoardHandSample hand) => hand with { FingersTogether = false, IndexFingerSeparated = true };
    private static BoardFingerSelectionFeedback Feedback(BoardSession board) => board.FingerSelectionFeedback.Single();
    private static BoardNavigation? At(BoardSession board, int at, params BoardHandSample[] hands) => board.Update(hands, Time(at), Time(at));
    private static DateTimeOffset Time(int milliseconds) =>
        new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
