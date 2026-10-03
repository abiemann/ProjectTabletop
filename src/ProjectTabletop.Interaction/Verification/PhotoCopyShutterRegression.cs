using ProjectTabletop.Interaction;

internal static class PhotoCopyShutterRegression
{
    public static void Run()
    {
        CheckShutterAndRearm();
        CheckReadiness();
        CheckAreaAndControls();
        CheckCaptureActions();
        CheckSwirlControls();
        CheckSaveFromSwirl();
        CheckFreshnessAndIdentity();
        Console.WriteLine("Photo Copy shutter verification passed: shared index-separation timing, capture-only event, " +
            "hidden capture target, one-second Exit/Swirl/Copy and Clear/Save holds, immediate pointer actions, readiness barriers, " +
            "held gestures, freshness and independent hands.");
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

        // Camera readiness only affects capture actions. Existing Exit/Clear/Save
        // holds survive real readiness transitions as well as repeated assignments.
        foreach (string id in new[] { "menu", "capture-again", "photo-save" })
        {
            board = PhotoCopy();
            board.PhotoCopyHasSwirl = id != "menu";
            Clear(board, id, 100);
            for (int time = 200; time < 1200; time += 100)
            {
                board.PhotoCopyShutterEnabled = time % 200 == 0;
                Require(Hold(board, id, time).Count == 0,
                    "An unrelated bottom control acted before its full hold.");
            }
            Require(Hold(board, id, 1200).SequenceEqual([id]),
                "Object readiness interrupted an unrelated Exit/Clear/Save hold.");
        }
    }

    private static void CheckAreaAndControls()
    {
        var board = PhotoCopy();
        Require(board.Buttons.Select(item => item.Id).SequenceEqual(
                ["menu", "photo-swirl", "photo-copy-once"]) &&
            board.Buttons.All(item => item.Hold == BoardButtonHold.Once) &&
            !board.ActivateButton("photo-shutter", Time(10)),
            "Photo Copy did not expose three single-action hold controls while keeping its field shutter hidden.");
        Require(BoardSession.PhotoCopyShutterBounds == new BoardRect(.01, .06, .98, .66),
            "The shutter no longer matches the normalized object capture area.");
        foreach (var aim in new[] { new BoardAim(.01, .06), new(.99, .72), new(.5, .6) })
            Require(Select(PhotoCopy(), Together(aim.U, aim.V), 100)?.ButtonId == "photo-shutter",
                "A valid capture-area position could not execute the shutter.");
        foreach (var aim in new[] { new BoardAim(.005, .6), new(.995, .6), new(.5, .055), new(.5, .725),
                     new(.35, .82), new(double.NaN, .6), new(.5, double.PositiveInfinity) })
            Require(Select(PhotoCopy(), Together(aim.U, aim.V), 100) is null,
                "The shutter executed outside its capture area or from an invalid aim.");

        foreach (string id in new[] { "menu", "photo-swirl", "photo-copy-once", "capture-again", "photo-save" })
        {
            board = PhotoCopy();
            board.PhotoCopyHasSwirl = id is "capture-again" or "photo-save";
            var button = board.Buttons.Single(item => item.Id == id);
            var hand = Together(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
            Require(Select(board, hand, 100) is null && board.HoveredButtonIds.Count == 0 &&
                board.FingerSelectionFeedback.Count == 0, id + " still accepted a bottom-control finger gesture.");
            var pinch = new BoardHandSample(hand.FingerAim!.Value.U, hand.FingerAim.Value.V, Time(1400), 7);
            Require(At(board, 400, pinch) is null &&
                At(board, 500, pinch with { ExecuteUntil = Time(1500), ExecuteEventId = 8 }) is null,
                id + " still accepted a bottom-control pinch.");
        }

        board = PhotoCopy();
        var capturedHand = Together(.5, .6);
        At(board, 100, capturedHand); At(board, 200, capturedHand); At(board, 300, Apart(capturedHand));
        Require(At(board, 380, Apart(capturedHand), new BoardHandSample(.2, .88, Time(1380), 9))?.ButtonId == "photo-shutter",
            "An ignored pinch over a hold control stole the hidden object's valid shutter gesture.");
        board = PhotoCopy();
        Require(At(board, 100, new BoardHandSample(.5, .6, Time(1100), 7)) is null &&
            board.HoveredButtonIds.Count == 0, "A field pinch activated or hovered the index-only shutter.");
        long revision = board.Revision;
        Press(board, "menu", 200);
        Require(board.Screen == BoardScreen.Menu && board.Revision == revision + 1,
            "Photo Copy's full Exit hold did not navigate exactly once.");
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
            ("photo-swirl", PhotoCopyAction.Swirl, new(.37, .835, .26, .105)),
            ("photo-copy-once", PhotoCopyAction.Copy, new(.66, .835, .26, .105))
        ];
        Require(BoardSession.TryGetPhotoCopyAction("photo-shutter", out var fieldAction) && fieldAction == PhotoCopyAction.Swirl &&
            !BoardSession.TryGetPhotoCopyAction("menu", out _) && !BoardSession.TryGetPhotoCopyAction("capture-again", out _) &&
            !BoardSession.TryGetPhotoCopyAction("photo-copy-timer", out _) &&
            !BoardSession.TryGetPhotoCopyAction("unknown", out _), "Photo Copy action IDs were mapped incorrectly.");
        var layout = PhotoCopy().Buttons;
        Require(layout.Single(button => button.Id == "menu") is { Label: "Exit", Hold: BoardButtonHold.Once } exit &&
            exit.Bounds == new BoardRect(.08, .835, .26, .105),
            "Photo Copy did not preserve its bottom Exit geometry.");
        foreach (var button in layout)
            Require(layout.Count(other => other.Bounds.Contains(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2)) == 1, "Photo Copy bottom controls overlap.");
        var otherBoard = new BoardSession(); otherBoard.ShowHandTrackingTest(Time(0));
        Require(otherBoard.Buttons.Single() is { Label: "Back to settings", Hold: BoardButtonHold.None } otherExit &&
            otherExit.Bounds == new BoardRect(.06, .055, .30, .105),
            "The Photo Copy Exit change altered other boards' navigation.");

        foreach (var action in actions)
        {
            var board = new BoardSession(); board.ShowPhotoCopy(Time(0));
            var button = board.Buttons.Single(item => item.Id == action.Id);
            long revision = board.Revision;
            Require(button.Bounds == action.Bounds && !button.Enabled && button.Hold == BoardButtonHold.Once &&
                BoardSession.TryGetPhotoCopyAction(action.Id, out var mapped) && mapped == action.Action &&
                !board.ActivateButton(action.Id, Time(20)), action.Id + " bypassed capture readiness.");
            Clear(board, action.Id, 100);
            for (int time = 200; time <= 1300; time += 100)
                Require(Hold(board, action.Id, time).Count == 0 && board.HoldProgress(Time(time)).Count == 0,
                    "A disabled capture control accumulated hold evidence.");
            board.PhotoCopyShutterEnabled = true;
            for (int time = 1400; time <= 2500; time += 100)
                Require(Hold(board, action.Id, time).Count == 0 && board.HoldProgress(Time(time)).Count == 0,
                    "A disabled-period clear caption armed a newly enabled capture action.");
            Press(board, action.Id, 2600);
            Require(board.Screen == BoardScreen.PhotoCopy && board.Revision == revision,
                "A capture hold navigated or restarted the Photo Copy session.");
            for (int time = 3800; time <= 4900; time += 100)
                Require(Hold(board, action.Id, time).Count == 0, "Resting fingers repeated a one-shot capture.");
            for (int time = 5000; time <= 5400; time += 100) Clear(board, action.Id, time);
            Press(board, action.Id, 5500);

            // A busy->ready round trip without any intervening camera frame
            // must erase both partial progress and previous clear-caption readiness.
            board = PhotoCopy();
            Clear(board, action.Id, 100);
            for (int time = 200; time <= 600; time += 100) Hold(board, action.Id, time);
            Require(board.HoldProgress(Time(600)).Count == 1, "The readiness fixture did not create partial progress.");
            board.PhotoCopyShutterEnabled = false;
            board.PhotoCopyShutterEnabled = true;
            Require(board.HoldProgress(Time(600)).Count == 0, "A readiness transition retained partial capture progress.");
            for (int time = 700; time <= 2000; time += 100)
                Require(Hold(board, action.Id, time).Count == 0 && board.HoldProgress(Time(time)).Count == 0,
                    "A readiness round trip reused previous clear-caption evidence.");
            Clear(board, action.Id, 2100);
            for (int time = 2200; time < 3200; time += 100)
            {
                board.PhotoCopyShutterEnabled = true;
                Require(Hold(board, action.Id, time).Count == 0, "A restarted capture hold acted early.");
            }
            Require(Hold(board, action.Id, 3200).SequenceEqual([action.Id]),
                "Unchanged readiness interrupted a newly cleared full capture hold.");

            board = PhotoCopy(); revision = board.Revision;
            Require(board.ActivateButton(action.Id, Time(100)) && board.Screen == BoardScreen.PhotoCopy &&
                board.Revision == revision, "Pointer capture lost its immediate same-session action.");
            board.PhotoCopyShutterEnabled = false;
            Require(!board.ActivateButton(action.Id, Time(200)), "Pointer capture ignored disabled readiness.");
        }

        var ambiguous = PhotoCopy();
        ambiguous.ObserveHeldButtons([], Time(100), Time(100), ["photo-swirl", "photo-copy-once"]);
        for (int time = 200; time <= 1600; time += 100)
            Require(ambiguous.ObserveHeldButtons(["photo-swirl", "photo-copy-once"], Time(time), Time(time), []).Count == 0 &&
                ambiguous.HoldProgress(Time(time)).Count == 0, "Two covered Photo Copy captions activated a control.");
    }

    private static void CheckSwirlControls()
    {
        var board = PhotoCopy();
        Require(!board.PhotoCopyHasSwirl && board.Buttons.All(button => button.Id != "capture-again") &&
            !board.ActivateButton("capture-again", Time(10)) && !board.ActivateButton("photo-copy-timer", Time(20)) &&
            !board.ActivateButton("photo-save", Time(30)), "Result controls appeared without a captured swirl.");
        var swirl = board.Buttons.Single(button => button.Id == "photo-swirl");
        Press(board, "photo-swirl", 100);
        board.PhotoCopyHasSwirl = true;
        board.PhotoCopyShutterEnabled = false;
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(["menu", "capture-again", "photo-save"]) &&
            board.Buttons.Single(button => button.Id == "capture-again") is { Label: "Clear", Enabled: true, Hold: BoardButtonHold.Once } clear &&
            clear.Bounds == swirl.Bounds &&
            board.Buttons.Single(button => button.Id == "photo-save") is { Label: "Save", Enabled: true, Hold: BoardButtonHold.Once },
            "A displayed swirl failed to preserve the same single-action hold controls.");
        Require(!board.ActivateButton("photo-copy-once", Time(1250)) && !board.ActivateButton("photo-swirl", Time(1260)) &&
            Select(board, Together(.5, .4), 1300) is null, "The displayed result still permitted fresh capture.");

        // Briefly seeing the new caption clear cannot release the spent place:
        // the same fingers must not turn a successful Swirl straight into Clear.
        Clear(board, "capture-again", 1700);
        for (int time = 1800; time <= 3100; time += 100)
            Require(Hold(board, "capture-again", time).Count == 0, "Swirl's spent place immediately cleared its new result.");
        for (int time = 3200; time <= 3600; time += 100) Clear(board, "capture-again", time);
        long revision = board.Revision;
        Clear(board, "capture-again", 3700);
        for (int time = 3800; time < 4800; time += 100)
        {
            board.PhotoCopyHasSwirl = true;
            Require(Hold(board, "capture-again", time).Count == 0, "Clear acted before its complete second.");
        }
        Require(Hold(board, "capture-again", 4800).SequenceEqual(["capture-again"]) && board.Revision == revision + 1,
            "Clear required camera readiness or unchanged result state interrupted its hold.");
        board.PhotoCopyHasSwirl = false; // The compositor removes its result after the restart.
        board.PhotoCopyShutterEnabled = true;
        for (int time = 4900; time <= 6200; time += 100)
            Require(Hold(board, "photo-swirl", time).Count == 0, "Clearing a result repeated through the restored Swirl caption.");
        for (int time = 6300; time <= 6700; time += 100) Clear(board, "photo-swirl", time);
        Press(board, "photo-swirl", 6800);
    }

    private static void CheckSaveFromSwirl()
    {
        Require(BoardSession.TryGetPhotoCopyAction("photo-save", out var action) && action == PhotoCopyAction.Save,
            "Save did not identify the in-memory output action.");
        var board = PhotoCopy(); board.PhotoCopyHasSwirl = true; board.PhotoCopyShutterEnabled = false;
        long revision = board.Revision;
        Press(board, "photo-save", 100);
        Require(board.Revision == revision && board.PhotoCopyHasSwirl && board.Screen == BoardScreen.PhotoCopy,
            "Save required a ready camera or changed the captured image/session.");
        for (int time = 1300; time <= 2600; time += 100)
            Require(Hold(board, "photo-save", time).Count == 0, "Resting fingers repeatedly saved the image.");
        for (int time = 2700; time <= 3100; time += 100) Clear(board, "photo-save", time);
        Press(board, "photo-save", 3200);
        Require(board.Revision == revision && board.PhotoCopyHasSwirl, "Re-armed Save changed the captured result.");
        Require(board.ActivateButton("photo-save", Time(4400)) && board.PhotoCopyHasSwirl &&
            board.Revision == revision, "Pointer Save no longer saved the same result immediately.");

        // Either direction of a result-label replacement must discard partial
        // progress, including replacing it back before any camera observes it.
        foreach (var (before, after, hasSwirl) in new[]
        {
            ("photo-swirl", "capture-again", false), ("photo-copy-once", "photo-save", false),
            ("capture-again", "photo-swirl", true), ("photo-save", "photo-copy-once", true)
        })
        {
            board = PhotoCopy(); board.PhotoCopyHasSwirl = hasSwirl;
            Clear(board, before, 100);
            for (int time = 200; time <= 600; time += 100) Hold(board, before, time);
            board.PhotoCopyHasSwirl = !hasSwirl;
            Require(board.HoldProgress(Time(600)).Count == 0, "A result-label change retained partial hold progress.");
            for (int time = 700; time <= 1900; time += 100)
                Require(Hold(board, after, time).Count == 0 && board.HoldProgress(Time(time)).Count == 0,
                    "A replacement caption inherited the previous action's clearance.");
            Press(board, after, 2000);

            board = PhotoCopy(); board.PhotoCopyHasSwirl = hasSwirl;
            Clear(board, before, 100);
            for (int time = 200; time <= 600; time += 100) Hold(board, before, time);
            board.PhotoCopyHasSwirl = !hasSwirl;
            board.PhotoCopyHasSwirl = hasSwirl;
            for (int time = 700; time <= 1900; time += 100)
                Require(Hold(board, before, time).Count == 0 && board.HoldProgress(Time(time)).Count == 0,
                    "An unobserved result round trip reused old caption evidence.");
            Press(board, before, 2000);
        }
    }

    private static IReadOnlyList<string> Hold(BoardSession board, string id, int at) =>
        board.ObserveHeldButtons([id], Time(at), Time(at), []);

    private static void Clear(BoardSession board, string id, int at) =>
        Require(board.ObserveHeldButtons([], Time(at), Time(at), [id]).Count == 0, "A clear caption activated a control.");

    private static void Press(BoardSession board, string id, int clearAt)
    {
        Clear(board, id, clearAt);
        for (int time = clearAt + 100; time < clearAt + 1100; time += 100)
            Require(Hold(board, id, time).Count == 0, id + " activated before a full second.");
        Require(Hold(board, id, clearAt + 1100).SequenceEqual([id]), id + " did not act after its full one-second hold.");
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
