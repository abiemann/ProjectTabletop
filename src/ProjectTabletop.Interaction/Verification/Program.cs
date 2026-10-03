using ProjectTabletop.Interaction;

SlotRegression.Run();
SlotBoardRegression.Run();
BoardOpenedRegression.Run();
MonopolyRegression.Run();
MonopolyBoardRegression.Run();
MonopolyDrawerRegression.Run();
MonopolyRollEventRegression.Run();
MonopolyPresentationRegression.Run();
GlobeBoardRegression.Run();
MenuScrollRegression.Run();
RouletteRegression.Run();
RouletteBoardRegression.Run();
CheckMenuAndNavigation();
CheckOffTargetAndBounds();
CheckHeldPinchAndDropout();
CheckIndependentHands();
CheckFreshness();
CheckResetAndExternalNavigation();
CheckPhotoCopyNavigation();
PaintBoardRegression.Run();
CheckAnchoredSelection();
CheckAnchorFreshnessAndConsumption();
CheckAnchorNavigationAndReset();
CheckPhotoCopySpiral();
BlackjackRegression.Run();
BlackjackBoardRegression.Run();
BoardFingerSelectionRegression.Run();
PhotoCopyShutterRegression.Run();
Console.WriteLine("Board interaction verification passed: menu/navigation, shared hit targets, off-target consumption, " +
    "held-pinch suppression, dropouts, independent hands, freshness, anchored selection and navigation barriers, " +
    "reset, Photo Copy restarts and dense full-board inward spiral placement.");

static void CheckMenuAndNavigation()
{
    var session = new BoardSession();
    Require(session.Screen == BoardScreen.Menu, "The board did not start at the menu.");
    string[] names = ["Dragon Slots", "Photo Copy", "Blackjack", "Paint", "Monopoly", "Globe", "Settings"];
    Require(session.Buttons.Where(button => !button.IsHold).Select(button => button.Label).SequenceEqual(names), "Menu order or labels differ from the requested menu.");
    Require(session.Buttons.Single(button => button.Id == "settings") is { Destination: BoardScreen.Settings } &&
        session.Buttons.All(button => button.Destination != BoardScreen.HandTracking),
        "Hand-Tracking is still a menu tile, or the Settings cog is missing.");
    BoardButton[] buttons = session.Buttons.Where(button => !button.IsHold).ToArray();
    long eventId = 0;
    for (int index = 0; index < buttons.Length; index++)
    {
        var button = buttons[index];
        var bounds = button.Bounds;
        bool cog = button.Id == "settings";
        Require(cog ? bounds.Width >= .18 && bounds.Height >= .12 : bounds.Width >= .35 && bounds.Height >= .15,
            "A menu target is too small for the board.");
        Require(bounds.X > 0 && bounds.Y > 0 && bounds.X + bounds.Width < 1 && bounds.Y + bounds.Height < 1,
            "A menu target reaches outside the board.");
        Require(buttons.Count(other => other.Bounds.Contains(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)) == 1,
            "Menu targets overlap at a button's center.");

        int time = 100 + index * 4000;
        Require(Update(session, time, Over(button)) is null, "Hover alone selected an application.");
        Require(session.HoveredButtonIds.SequenceEqual([button.Id]), "Hover did not use the shared target rectangle.");
        BoardNavigation? navigation = Update(session, time + 10, Over(button, ++eventId, time + 10));
        Require(navigation is { Previous: BoardScreen.Menu } && navigation.Current == button.Destination &&
            navigation.ButtonId == button.Id && session.Screen == button.Destination, "Menu pinch opened the wrong application.");
        Require(session.Title == names[index], "The application title is incorrect.");
        Require(session.Buttons.Count >= 1 && (session.Buttons[0].Destination == BoardScreen.Menu ||
            button.Destination == BoardScreen.Monopoly && session.Buttons[0].Id == "mp-exit" ||
            button.Destination == BoardScreen.Globe && session.Buttons[0].Id == "globe-drawer-open"),
            "An application lacks a back-to-menu target.");
        if (button.Destination == BoardScreen.Monopoly)
        {
            Require(Update(session, time + 20, Over(session.Buttons[0], ++eventId, time + 20)) is
                { Current: BoardScreen.Monopoly, ButtonId: "mp-exit" } && session.MonopolyDrawerOpen,
                "The Monopoly caret did not open its drawer.");
            var exit = session.Buttons.Single(item => item.Id == "mp-exit-game");
            Require(Update(session, time + 321, Over(exit, ++eventId, time + 321))?.Current == BoardScreen.Menu,
                "The drawer's Exit Game target did not return to the launcher.");
        }
        else if (button.Destination == BoardScreen.Globe)
        {
            // The drawer handle is a long-press: a pinch leaves it closed, a one-second hold opens it.
            Require(Update(session, time + 20, Over(session.Buttons[0], ++eventId, time + 20)) is null &&
                !session.GlobeDrawerOpen, "A pinch toggled the Globe's long-press drawer handle.");
            for (int held = 30; held < 1030; held += 250)
                Require(session.ObserveHeldButtons(["globe-drawer-open"], Time(time + held), Time(time + held)).Count == 0,
                    "The Globe handle opened before a full second of hold evidence.");
            Require(session.ObserveHeldButtons(["globe-drawer-open"], Time(time + 1030), Time(time + 1030))
                    .SequenceEqual(["globe-drawer-open"]) && session.GlobeDrawerOpen,
                "A one-second hold did not open the Globe drawer.");
            // Exit is a long-press too, once the drawer has settled.
            var exit = session.Buttons.Single(item => item.Id == "globe-exit");
            Require(Update(session, time + 1400, Over(exit, ++eventId, time + 1400)) is null &&
                session.Screen == BoardScreen.Globe, "A pinch selected the Globe's long-press Exit.");
            for (int held = 1400; held < 2400; held += 250)
                session.ObserveHeldButtons(["globe-exit"], Time(time + held), Time(time + held));
            Require(session.ObserveHeldButtons(["globe-exit"], Time(time + 2400), Time(time + 2400))
                    .SequenceEqual(["globe-exit"]) && session.Screen == BoardScreen.Menu,
                "The Globe drawer's long-press Exit did not return to the launcher.");
        }
        else if (button.Destination is BoardScreen.Slots or BoardScreen.PhotoCopy)
        {
            // These boards use single-action holds on the viewer's edge row.
            string exitId = button.Destination == BoardScreen.Slots ? "slot-exit" : "menu";
            Require(session.Buttons.All(item => item.Hold == BoardButtonHold.Once) && session.Buttons[0].Id == exitId,
                "The board's bottom controls are not long-press buttons led by Exit.");
            Require(Update(session, time + 20, Over(session.Buttons[0], ++eventId, time + 20)) is null &&
                session.Screen == button.Destination, "A pinch selected the board's long-press Exit.");
            for (int held = 30; held < 1030; held += 250)
                Require(session.ObserveHeldButtons([exitId], Time(time + held), Time(time + held)).Count == 0,
                    "The board's Exit acted before a full second.");
            Require(session.ObserveHeldButtons([exitId], Time(time + 1030), Time(time + 1030))
                    .SequenceEqual([exitId]) && session.Screen == BoardScreen.Menu,
                "A one-second hold on the board's Exit did not return to the launcher.");
        }
        else if (button.Destination == BoardScreen.Settings)
        {
            var tester = session.Buttons.Single(item => item.Id == "hand-tracking");
            Require(tester is { Label: "Hand-Tracking", Destination: BoardScreen.HandTracking, Hold: BoardButtonHold.None } &&
                tester.Bounds.Width >= .35 && tester.Bounds.Height >= .15, "Settings lacks the Hand-Tracking tester tile.");
            Require(Update(session, time + 20, Over(tester, ++eventId, time + 20))?.Current == BoardScreen.HandTracking &&
                session.Title == "Hand-Tracking", "The Settings tile did not open the Hand-Tracking tester.");
            Require(session.Buttons.Single() is { Id: "menu", Destination: BoardScreen.Settings } &&
                Update(session, time + 40, Over(session.Buttons[0], ++eventId, time + 40))?.Current == BoardScreen.Settings,
                "The tester's back button did not return to Settings.");
            Require(Update(session, time + 60, Over(session.Buttons[0], ++eventId, time + 60))?.Current == BoardScreen.Menu,
                "Settings' back button did not return to the launcher.");
        }
        else
            Require(Update(session, time + 20, Over(session.Buttons[0], ++eventId, time + 20))?.Current == BoardScreen.Menu,
                "The back-to-menu target did not return to the launcher.");
    }
}

// Settings' simple back-button screen stands in for any launched board.
static BoardButton Cog(BoardSession session) => session.Buttons.Single(button => button.Id == "settings");

static void CheckOffTargetAndBounds()
{
    foreach ((double u, double v) in new[] { (-.01, .30), (1.01, .30), (.20, -.01), (.20, 1.01),
        (.5, .5), (double.NaN, .3), (.2, double.PositiveInfinity) })
    {
        var session = new BoardSession();
        var button = Cog(session);
        Require(Update(session, 100, new BoardHandSample(u, v, Time(1100), 1)) is null, "An off-target pinch navigated.");
        Require(session.HoveredButtonIds.Count == 0, "An invalid/off-target position hovered a button.");
        Require(Update(session, 140, Over(button, 1, 100)) is null,
            "Moving an existing off-target pinch onto a button activated it.");
        Require(Update(session, 180, Over(button, 2, 180))?.Current == BoardScreen.Settings,
            "A new pinch after an off-target pinch could not activate a button.");
    }
    var duplicate = new BoardSession();
    Require(Update(duplicate, 100, new(double.NaN, 0, Time(1100), 1), Over(Cog(duplicate), 1, 100)) is null,
        "Duplicating an off-target event at a button activated it.");
}

static void CheckHeldPinchAndDropout()
{
    var session = new BoardSession();
    Update(session, 100, Over(Cog(session), 1, 100));
    BoardButton back = session.Buttons[0];
    Require(Update(session, 140, Over(back, 1, 100)) is null, "A held pinch crossed from launch to back.");
    Require(Update(session, 180) is null && session.HoveredButtonIds.Count == 0, "A missing hand retained hover.");
    Require(Update(session, 220, Over(back, 1, 100)) is null, "A brief disappearance replayed a held pinch.");
    Require(Update(session, 1200, Over(back, 1, 100)) is null, "An expired pinch was reactivated.");
    Require(Update(session, 1240, Over(back, 2, 1240))?.Current == BoardScreen.Menu,
        "A fresh pinch did not navigate after a held pinch.");
}

static void CheckIndependentHands()
{
    var session = new BoardSession();
    BoardButton first = Cog(session);
    // Both events share a pulse deadline, but only the first hand is off-target.
    Require(Update(session, 100, new BoardHandSample(double.NaN, double.NaN, Time(1100), 1) { TrackingId = 11 },
        Over(first, 2, 100) with { TrackingId = 22 }) is
        { Current: BoardScreen.Settings, TrackingId: 22, Gesture: BoardSelectionGesture.Pinch },
        "One hand's off-target pinch consumed or inherited the identity of the other hand's simultaneous pinch.");
    Require(Update(session, 140, Over(session.Buttons[0], 2, 100), Over(session.Buttons[0], 1, 100)) is null,
        "Reordering two hands replayed one of their pinches.");
    Require(Update(session, 180, Over(session.Buttons[0], 2, 100) with { TrackingId = 22 },
        Over(session.Buttons[0], 3, 180) with { TrackingId = 11 }) is
        { Current: BoardScreen.Menu, TrackingId: 11, Gesture: BoardSelectionGesture.Pinch },
        "A held pinch prevented the other hand from executing independently or reported the wrong hand.");

    session = new BoardSession();
    Require(Update(session, 100, Over(Cog(session), 1, 100), Over(session.Buttons[1], 2, 100))?.Current == BoardScreen.Settings,
        "Simultaneous targets did not deterministically select one screen.");
    Require(Update(session, 140, Over(session.Buttons[0], 2, 100)) is null,
        "The unused simultaneous event activated a button on the new screen.");
}

static void CheckFreshness()
{
    var session = new BoardSession();
    BoardHandSample pinch = Over(Cog(session), 1, 0);
    Require(session.Update([pinch], Time(0), Time(351)) is null, "A stale frame navigated.");
    Require(session.Update([pinch], Time(500), Time(400)) is null, "A future frame navigated.");
    Require(session.Update([pinch], Time(410), Time(410))?.Current == BoardScreen.Settings,
        "Rejected frames prevented a subsequent valid observation.");
    BoardHandSample back = Over(session.Buttons[0], 2, 420);
    Require(session.Update([back], Time(410), Time(420)) is null, "A duplicate frame navigated.");
    Require(session.Update([back], Time(400), Time(430)) is null, "An out-of-order frame navigated.");
    Require(session.Update([back], Time(405), Time(405)) is null, "A reversed clock navigated.");
    Require(session.Update([back], Time(440), Time(440))?.Current == BoardScreen.Menu,
        "Fresh input was rejected after timestamp failures.");

    session = new BoardSession();
    Require(Update(session, 100, Over(session.Buttons[0], 1, -900)) is null, "An expired event navigated at its exact deadline.");
    Require(Update(session, 140, Over(session.Buttons[0], 2, 200)) is null, "A pulse from a future execution navigated.");
    Require(Update(session, 180, Over(session.Buttons[0])) is null, "A zero event ID navigated.");
}

static void CheckResetAndExternalNavigation()
{
    var session = new BoardSession();
    Update(session, 100, Over(Cog(session), 1, 100));
    session.ResetInput(Time(120));
    Require(session.HoveredButtonIds.Count == 0, "Reset retained hover.");
    Require(session.Update([Over(session.Buttons[0], 2, 100)], Time(110), Time(130)) is null,
        "A frame from before reset activated a button.");
    Update(session, 140); // An empty first frame must not end protection against old pulses.
    Require(Update(session, 180, Over(session.Buttons[0], 2, 100)) is null,
        "An unseen but already-active pulse survived reset.");
    Require(Update(session, 220, Over(session.Buttons[0], 1, 100)) is null, "Reset replayed a previously consumed event.");
    Require(Update(session, 260, Over(session.Buttons[0], 3, 260))?.Current == BoardScreen.Menu,
        "Reset blocked a fresh execution.");

    session.ShowHandTrackingTest(Time(300));
    Require(session.Screen == BoardScreen.HandTracking, "Direct test navigation failed.");
    Require(Update(session, 340, Over(session.Buttons[0], 4, 280)) is null,
        "External navigation replayed a previously active pulse.");
    session.ShowMedia(Time(400));
    Require(session.Screen == BoardScreen.Media && session.Buttons.Count == 0, "Media did not leave the launcher overlay.");
    Require(Update(session, 440, new BoardHandSample(.2, .3, Time(1440), 5)) is null, "Media unexpectedly had an interactive target.");
    session.ShowMenu(Time(480));
    Require(Update(session, 520, Over(Cog(session), 5, 440)) is null, "Returning from media replayed a pulse.");
    Require(Update(session, 560, Over(Cog(session), 6, 560))?.Current == BoardScreen.Settings,
        "Returning from media prevented a new pinch.");
}

static void CheckPhotoCopyNavigation()
{
    var session = new BoardSession();
    Require(session.Revision == 0, "An unused session already has a navigation revision.");
    Require(Update(session, 100, Over(session.Buttons[1], 1, 100)) is { Current: BoardScreen.PhotoCopy, ButtonId: "photo-copy" },
        "The renamed Photo Copy target did not launch Photo Copy.");
    Require(session.Revision == 1, "Launching Photo Copy did not advance the navigation revision.");
    session.PhotoCopyHasSwirl = true;
    BoardButton captureAgain = session.Buttons.Single(button => button.Id == "capture-again");
    Require(!captureAgain.Bounds.Contains(session.Buttons[0].Bounds.X, session.Buttons[0].Bounds.Y),
        "Capture again overlaps Back to menu.");
    Require(Update(session, 140, Over(captureAgain, 1, 100)) is null && session.Revision == 1,
        "A held launch pinch restarted Photo Copy.");
    Require(Update(session, 180, Over(captureAgain, 2, 180)) is null && session.Revision == 1,
        "A new pinch selected Photo Copy's long-press Clear control.");
    Require(session.ActivateButton("capture-again", Time(200)) && session.Revision == 2,
        "Pointer Clear did not restart the current Photo Copy session.");
    Require(Update(session, 220, Over(captureAgain, 2, 180)) is null && session.Revision == 2,
        "A held Capture again pinch repeatedly restarted Photo Copy.");
    session.ShowPhotoCopy(Time(260));
    Require(session.Screen == BoardScreen.PhotoCopy && session.Revision == 3,
        "External navigation to the current Photo Copy screen did not restart it.");
    session.ResetInput(Time(300));
    Require(session.Revision == 3, "Resetting camera input restarted the application.");
    session.ShowMenu(Time(340));
    Require(session.Screen == BoardScreen.Menu && session.Revision == 4, "External menu navigation did not advance revision.");
}

static void CheckAnchoredSelection()
{
    var session = new BoardSession();
    BoardButton photo = session.Buttons[1];
    // The compositor supplies the open-hand selection point even when the
    // current fingertip curls outside the target during a pinch.
    BoardHandSample anchored = Over(photo) with { SelectionFrameTime = Time(100) };
    Require(Update(session, 140, anchored) is null && session.HoveredButtonIds.SequenceEqual([photo.Id]),
        "The closing hand lost the highlight at its anchored selection point.");
    Require(Update(session, 200) is null && session.HoveredButtonIds.Count == 0,
        "A missing hand retained an anchored highlight.");
    Require(Update(session, 260, anchored with { ExecuteEventId = 1, ExecuteUntil = Time(1260) })?.Current == BoardScreen.PhotoCopy,
        "A recent selection anchor could not launch Photo Copy after a brief dropout.");

    session = new BoardSession();
    BoardHandSample offTarget = new(.5, .5, Time(1200), 1, Time(100));
    Require(Update(session, 200, offTarget) is null && session.HoveredButtonIds.Count == 0,
        "An off-target anchor navigated or highlighted a target.");
    Require(Update(session, 240, Over(photo, 1, 200) with { SelectionFrameTime = Time(220) }) is null,
        "An off-target anchored pinch drifted onto a button and executed.");

    // Each sample carries its own anchor; neither ordering nor an off-target
    // pinch can transfer another hand's selection or swallow its fresh event.
    session = new BoardSession();
    BoardHandSample other = new(.5, .5, DateTimeOffset.MinValue, 0, Time(100));
    Update(session, 140, anchored, other);
    Require(Update(session, 260,
        other with { ExecuteEventId = 1, ExecuteUntil = Time(1260) },
        anchored with { ExecuteEventId = 2, ExecuteUntil = Time(1260) })?.Current == BoardScreen.PhotoCopy,
        "Reordering two hands lost the pinching hand's independent selection anchor.");
}

static void CheckAnchorFreshnessAndConsumption()
{
    foreach ((int anchorTime, int frameTime, int now) in new[]
        { (0, 751, 751), (201, 200, 200), (225, 200, 250) })
    {
        var session = new BoardSession();
        BoardButton photo = session.Buttons[1];
        BoardHandSample invalid = Over(photo, 1, now) with { SelectionFrameTime = Time(anchorTime) };
        Require(session.Update([invalid], Time(frameTime), Time(now)) is null && session.HoveredButtonIds.Count == 0,
            "An expired or future selection anchor navigated or hovered a target.");
        Require(Update(session, now + 40,
            Over(photo, 1, now) with { SelectionFrameTime = Time(now + 20) }) is null,
            "An event rejected for its anchor replayed after the anchor became valid.");
        Require(Update(session, now + 80,
            Over(photo, 2, now + 80) with { SelectionFrameTime = Time(now + 60) })?.Current == BoardScreen.PhotoCopy,
            "Rejecting an invalid anchor prevented a later fresh pinch.");
    }

    var boundary = new BoardSession();
    // Inference latency does not consume the source-time selection window;
    // frame freshness still independently requires an observation under 350 ms.
    Require(boundary.Update([Over(boundary.Buttons[1], 1, 1000) with { SelectionFrameTime = Time(0) }],
        Time(750), Time(1000))?.Current == BoardScreen.PhotoCopy,
        "A valid 750 ms anchor was expired by inference latency.");
}

static void CheckAnchorNavigationAndReset()
{
    var session = new BoardSession();
    Update(session, 200, Over(Cog(session), 1, 200) with { SelectionFrameTime = Time(100) });
    BoardButton back = session.Buttons[0];
    Require(Update(session, 300, Over(back, 2, 300) with { SelectionFrameTime = Time(150) }) is null &&
        session.HoveredButtonIds.Count == 0,
        "Another hand's pre-navigation anchor activated or highlighted the new Back button.");
    Require(Update(session, 340, Over(back, 2, 300) with { SelectionFrameTime = Time(320) }) is null,
        "A consumed pre-navigation anchored event replayed with a new anchor.");
    Require(Update(session, 380, Over(back, 3, 380) with { SelectionFrameTime = Time(350) })?.Current == BoardScreen.Menu,
        "A fresh anchor could not return to the menu.");
    Require(Update(session, 420, Over(session.Buttons[1], 4, 420) with { SelectionFrameTime = Time(370) }) is null,
        "A pre-Back anchor survived the return to menu.");

    // Every explicit screen change invalidates existing anchors, including
    // showing the same application and returning from media.
    Action<BoardSession, DateTimeOffset>[] show =
    [
        (board, time) => board.ShowMenu(time),
        (board, time) => board.ShowHandTrackingTest(time),
        (board, time) => board.ShowSettings(time),
        (board, time) => board.ShowPaint(time),
        (board, time) => { board.ShowMedia(time); board.ShowMenu(time); }
    ];
    foreach (var navigate in show)
    {
        session = new BoardSession();
        navigate(session, Time(200));
        BoardButton target = session.Buttons[0];
        Require(Update(session, 300, Over(target, 1, 300) with { SelectionFrameTime = Time(200) }) is null &&
            session.HoveredButtonIds.Count == 0,
            "Explicit navigation retained an old selection anchor.");
        Require(Update(session, 400, Over(target, 2, 400) with { SelectionFrameTime = Time(350) }) is not null,
            "Explicit navigation blocked a new selection anchor.");
    }

    session = new BoardSession();
    session.ShowPaint(Time(100));
    session.PaintSaveEnabled = true;
    BoardButton save = session.Buttons.Single(button => button.Id == "paint-save");
    Require(Update(session, 200, Over(save, 1, 200) with { SelectionFrameTime = Time(150) }) is
        { Previous: BoardScreen.Paint, Current: BoardScreen.Paint, ButtonId: "paint-save" }, "An anchored Save failed.");
    Require(Update(session, 260, Over(save, 2, 260) with { SelectionFrameTime = Time(180) }) is null,
        "Save retained another hand's pre-action selection anchor.");
    session.ResetInput(Time(300));
    Require(Update(session, 350, Over(save, 3, 350) with { SelectionFrameTime = Time(280) }) is null &&
        session.HoveredButtonIds.Count == 0, "Camera/calibration reset retained an old selection anchor.");
    Require(Update(session, 420, Over(save, 4, 420) with { SelectionFrameTime = Time(380) }) is not null,
        "Camera/calibration reset blocked a fresh selection anchor.");
}

static void CheckPhotoCopySpiral()
{
    foreach (double aspect in new[] { .4, .72, 1.0, 2.5 })
    {
        IReadOnlyList<PhotoCopyPlacement> placements = PhotoCopyLayout.Create(spriteWidthOverHeight: aspect);
        Require(placements.Count == 576 && placements.Count == PhotoCopyLayout.DefaultCount,
            "Photo Copy should fill the board with 576 stamps.");
        Require(placements.Select(p => (p.CenterU, p.CenterV)).Distinct().Count() == placements.Count,
            "The fill repeats a position instead of reaching the entire board.");
        Require(placements.Any(p => p.CenterU == 0 && p.CenterV == 0) &&
            placements.Any(p => p.CenterU == 1 && p.CenterV == 0) &&
            placements.Any(p => p.CenterU == 1 && p.CenterV == 1) &&
            placements.Any(p => p.CenterU == 0 && p.CenterV == 1),
            "The fill does not reach all four board corners.");
        Require(placements.Count(p => p.CenterU == 0) == 24 && placements.Count(p => p.CenterU == 1) == 24 &&
            placements.Count(p => p.CenterV == 0) == 24 && placements.Count(p => p.CenterV == 1) == 24,
            "The fill leaves an outside edge uncovered.");

        int previousLayer = 0;
        PhotoCopyPlacement? previous = null;
        foreach (PhotoCopyPlacement placement in placements)
        {
            Require(placement.CenterU is >= 0 and <= 1 && placement.CenterV is >= 0 and <= 1,
                "A stamp anchor is outside the board.");
            double dx = .5 - placement.CenterU, dy = .5 - placement.CenterV;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            Require(radius > 0 && double.IsFinite(placement.RotationRadians),
                "A center stamp has no defined middle-finger direction.");
            double directionU = Math.Sin(placement.RotationRadians);
            double directionV = -Math.Cos(placement.RotationRadians);
            Require(Math.Abs(directionU - dx / radius) < 1e-10 && Math.Abs(directionV - dy / radius) < 1e-10,
                "A copy's middle finger does not face the board center.");
            Require(Math.Abs(placement.Width / placement.Height - aspect) < 1e-10,
                "The hand image's aspect ratio was distorted.");
            Require(Math.Abs(Math.Max(placement.Width, placement.Height) - .18) < 1e-10,
                "Hand copies should have a consistent, bounded size.");

            int column = (int)Math.Round(placement.CenterU * 23), row = (int)Math.Round(placement.CenterV * 23);
            int layer = Math.Min(Math.Min(column, row), Math.Min(23 - column, 23 - row));
            Require(layer >= previousLayer && layer <= previousLayer + 1,
                "The square spiral skipped an outside layer or moved back outward.");
            if (previous is { } prior)
            {
                double step = Math.Abs(placement.CenterU - prior.CenterU) + Math.Abs(placement.CenterV - prior.CenterV);
                Require(Math.Abs(step - 1.0 / 23) < 1e-10,
                    "The fill jumped between distant parts of the board.");
                if (layer == previousLayer)
                {
                    double clockwise = (prior.CenterU - .5) * (placement.CenterV - .5) -
                        (prior.CenterV - .5) * (placement.CenterU - .5);
                    Require(clockwise > 0, "The fill changed its clockwise direction around a layer.");
                }
            }
            previous = placement;
            previousLayer = layer;
        }
        Require(placements.Take(92).All(p => p.CenterU == 0 || p.CenterU == 1 || p.CenterV == 0 || p.CenterV == 1),
            "The outside perimeter should finish before the fill moves inward.");
        Require(placements.Skip(92).All(p => p.CenterU > 0 && p.CenterU < 1 && p.CenterV > 0 && p.CenterV < 1),
            "A perimeter stamp appeared after filling began inside the board.");
        Require(placements.TakeLast(4).All(p => Math.Abs(p.CenterU - .5) < .022 && Math.Abs(p.CenterV - .5) < .022),
            "The fill stopped short of the board center.");

        // Sample the whole square, including edges, corners, and the center.
        // An inscribed-circle spiral leaves those samples far from any stamp.
        for (int x = 0; x <= 40; x++)
        for (int y = 0; y <= 40; y++)
        {
            double nearestSquared = placements.Min(p => Math.Pow(p.CenterU - x / 40.0, 2) +
                Math.Pow(p.CenterV - y / 40.0, 2));
            Require(nearestSquared < .032 * .032,
                "The hand stamps leave a sparse region in the board fill.");
        }

        // The renderer rotates arbitrary captured finger directions around
        // the palm. Layout rotations keep that finger pointing inward.
        foreach (double sourceDirection in new[] { -Math.PI / 2, 0, Math.PI / 4, 2.5 })
        foreach (PhotoCopyPlacement placement in placements)
        {
            double rotation = placement.RotationRadians - sourceDirection - Math.PI / 2;
            double rotatedU = Math.Cos(sourceDirection) * Math.Cos(rotation) - Math.Sin(sourceDirection) * Math.Sin(rotation);
            double rotatedV = Math.Cos(sourceDirection) * Math.Sin(rotation) + Math.Sin(sourceDirection) * Math.Cos(rotation);
            Require(Math.Abs(rotatedU - Math.Sin(placement.RotationRadians)) < 1e-10 &&
                Math.Abs(rotatedV + Math.Cos(placement.RotationRadians)) < 1e-10,
                "A rotated captured hand lost its inward middle-finger direction.");
        }
    }

    foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1 })
        RequireThrows(() => PhotoCopyLayout.Create(spriteWidthOverHeight: invalid),
            "An invalid image aspect ratio was accepted.");
}
static void RequireThrows(Action action, string message)
{
    try { action(); }
    catch (ArgumentOutOfRangeException) { return; }
    throw new InvalidOperationException(message);
}

static BoardHandSample Over(BoardButton button, long eventId = 0, int executeAt = 0) =>
    new(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2,
        eventId > 0 ? Time(executeAt + 1000) : DateTimeOffset.MinValue, eventId);
static BoardNavigation? Update(BoardSession session, int milliseconds, params BoardHandSample[] hands) =>
    session.Update(hands, Time(milliseconds), Time(milliseconds));
static DateTimeOffset Time(int milliseconds) => new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
