using ProjectTabletop.Interaction;

internal static class GlobeBoardRegression
{
    public static void Run()
    {
        CheckEntranceAndRotation();
        CheckDrawerAndSmoothControls();
        CheckNavigationAndPinches();
        CheckFourFingerSelection();
        CheckHoldToRepeat();
        CheckInterruptedRepeatHold();
        CheckStrictCaptionRepeat();
        CheckLongPressHandle();
        CheckSimultaneousHolds();
        CheckHoldProgress();
        Console.WriteLine("Globe verification passed: pure entrance/spin frames, bounded smooth zoom, hidden rotation " +
            "controls, timed drawer targets, pinch and four-finger gestures ignored by every hold control, reset barriers, " +
            "hold-to-repeat zoom timing, clear-caption readiness and reference resets, gaps and stale frames, " +
            "long-press drawer handle with release, long-press Exit.");
    }

    private static void CheckHoldToRepeat()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        string[] zoomIn = ["globe-zoom-in"];
        Require(board.ObserveHeldButtons(zoomIn, Time(1000), Time(1000)).Count == 0,
            "A hidden hold button accepted evidence while the drawer was closed.");
        Require(board.ActivateButton("globe-drawer-open", Time(3000)) && board.TickGlobe(Time(3300)),
            "The hold fixture could not open the drawer.");
        Require(Button(board, "globe-zoom-in").Hold == BoardButtonHold.Repeat &&
            Button(board, "globe-zoom-out").Hold == BoardButtonHold.Repeat &&
            Button(board, "globe-exit").Hold == BoardButtonHold.Once &&
            Button(board, "globe-drawer-close").Hold == BoardButtonHold.Once,
            "Globe's zooms should repeat, and its handle and Exit act once per long press.");
        IReadOnlyList<string> Hold(string[] ids, int time) => board.ObserveHeldButtons(ids, Time(time), Time(time + 40));
        for (int time = 4000; time < 5000; time += 100)
            Require(Hold(zoomIn, time).Count == 0, "A hold activated before a full second of caption evidence.");
        Require(Hold(zoomIn, 5000).SequenceEqual(zoomIn) && Near(board.GetGlobeSnapshot(Time(5100)).TargetZoom, 1.875),
            "One second of held evidence did not zoom exactly once.");
        Require(Hold(zoomIn, 5000).Count == 0 && Hold(zoomIn, 4900).Count == 0,
            "A repeated or delayed frame advanced the hold.");
        for (int time = 5100; time < 6000; time += 100)
            Require(time is > 5300 and < 5500 || Hold(zoomIn, time).Count == 0,
                "A continued hold repeated before another second; a 200 ms evidence gap released it.");
        Require(Hold(zoomIn, 6000).SequenceEqual(zoomIn) && Near(board.GetGlobeSnapshot(Time(6400)).TargetZoom, 1.5 * 1.25 * 1.25),
            "A continued hold did not repeat after each further second.");
        Require(Hold([], 6400).Count == 0, "An uncovered frame activated a hold.");
        for (int time = 6500; time < 7500; time += 100)
            Require(Hold(zoomIn, time).Count == 0, "A released hold kept its previous progress.");
        Require(Hold(zoomIn, 7500).SequenceEqual(zoomIn),
            "A gap over 350 ms did not restart the one-second hold.");
        Require(Hold(["globe-exit", "globe-drawer-close"], 7600).Count == 0 && board.GlobeDrawerOpen &&
            board.Screen == BoardScreen.Globe, "One frame of evidence activated Exit or toggled the handle.");
        Require(board.ObserveHeldButtons(zoomIn, Time(8500), Time(9000)).Count == 0,
            "Stale hold evidence activated a button.");
        string[] zoomOut = ["globe-zoom-out"];
        for (int time = 10000; time < 11000; time += 100)
            Require(Hold(zoomOut, time).Count == 0, "Zoom - activated before a full second.");
        Require(Hold(zoomOut, 11000).SequenceEqual(zoomOut),
            "Zoom - did not share the hold-to-repeat behaviour.");
        board.ShowMenu(Time(11100)); board.ShowGlobe(Time(11200));
        Require(board.ObserveHeldButtons(zoomIn, Time(12500), Time(12500)).Count == 0,
            "Leaving Globe retained a hold or its drawer.");
    }

    private static void CheckInterruptedRepeatHold()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        Require(board.ActivateButton("globe-drawer-open", Time(100)) && board.TickGlobe(Time(400)),
            "The interrupted repeat fixture could not open the drawer.");
        IReadOnlyList<string> Hold(int time) => board.ObserveHeldButtons(["globe-zoom-in"], Time(time), Time(time));
        for (int time = 1000; time < 2000; time += 100)
            Require(Hold(time).Count == 0, "The repeat fixture acted before a second.");
        Require(Hold(2000).SequenceEqual(["globe-zoom-in"]), "The repeat fixture did not zoom once.");
        Require(Hold(2100).Count == 0 && Hold(2200).Count == 0, "The zoom repeated before its next second.");
        double beforeGap = board.GetGlobeSnapshot(Time(2200)).TargetZoom;
        Require(board.HoldProgress(Time(3500)).Count == 0 && Hold(3500).Count == 0 &&
            Near(board.GetGlobeSnapshot(Time(3500)).TargetZoom, beforeGap),
            "A returning positive frame caught up a zoom repeat across a camera gap.");
        for (int time = 3600; time < 4500; time += 100)
            Require(Hold(time).Count == 0, "A camera gap shortened the next repeating hold.");
        Require(Hold(4500).SequenceEqual(["globe-zoom-in"]), "The restarted zoom did not act after a fresh second.");
        Require(Hold(4500).Count == 0 && Hold(4499).Count == 0,
            "Repeated or out-of-order evidence repeated the zoom action.");
        for (int time = 4600; time < 5500; time += 100)
            Require(Hold(time).Count == 0, "A restarted continuous zoom repeated early.");
        Require(Hold(5500).SequenceEqual(["globe-zoom-in"]) &&
            Near(board.GetGlobeSnapshot(Time(5500)).TargetZoom, beforeGap * 1.25 * 1.25),
            "The restarted continuous hold did not resume one zoom per second.");
    }

    private static void CheckStrictCaptionRepeat()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        Require(board.ActivateButton("globe-drawer-open", Time(100)) && board.TickGlobe(Time(400)),
            "The strict-caption repeat fixture could not open the drawer.");
        IReadOnlyList<string> Hold(int time) =>
            board.ObserveHeldButtons(["globe-zoom-in"], Time(time), Time(time), []);
        IReadOnlyList<string> Clear(int time) =>
            board.ObserveHeldButtons([], Time(time), Time(time), ["globe-zoom-in"]);
        Require(Clear(1000).Count == 0, "A clear Globe caption activated Zoom +.");
        for (int time = 1100; time < 2100; time += 100)
            Require(Hold(time).Count == 0, "A cleared Globe caption shortened the first hold.");
        Require(Hold(2100).SequenceEqual(["globe-zoom-in"]), "A strict hold did not zoom after its first second.");

        // A sibling's enabled state or appearance can change after a zoom. Its
        // replacement reference must not interrupt this unchanged repeat hold.
        board.ResetHoldCaptionEvidence(["globe-zoom-out"]);
        for (int time = 2200; time < 3100; time += 100)
            Require(Hold(time).Count == 0, "A strict zoom repeated before its next second.");
        Require(Hold(3100).SequenceEqual(["globe-zoom-in"]) &&
            Near(board.GetGlobeSnapshot(Time(3100)).TargetZoom, 1.5 * 1.25 * 1.25),
            "Resetting an unrelated caption interrupted a strict repeat or consumed its readiness.");

        board.ResetHoldCaptionEvidence();
        for (int time = 3200; time <= 4500; time += 100)
            Require(Hold(time).Count == 0 && board.HoldProgress(Time(time)).Count == 0,
                "A replaced control reference reused a previous clear caption or partial hold.");
        Require(Clear(4600).Count == 0, "A replacement reference's clear caption activated Zoom +.");
        board.ResetHoldCaptionEvidence(["globe-zoom-in"]);
        Require(board.ObserveHeldButtons([], Time(4600), Time(4650), ["globe-zoom-in"]).Count == 0 &&
            board.ObserveHeldButtons([], Time(4599), Time(4650), ["globe-zoom-in"]).Count == 0,
            "Repeated or out-of-order clearance activated a replacement reference.");
        for (int time = 4700; time <= 6000; time += 100)
            Require(Hold(time).Count == 0 && board.HoldProgress(Time(time)).Count == 0,
                "An obsolete clear frame re-armed the changed control reference.");
        Require(Clear(6100).Count == 0, "Fresh clearance activated a reset Globe caption.");
        for (int time = 6200; time < 7200; time += 100)
            Require(Hold(time).Count == 0, "A reset Globe caption reused previous hold progress.");
        Require(Hold(7200).SequenceEqual(["globe-zoom-in"]) &&
            Near(board.GetGlobeSnapshot(Time(7200)).TargetZoom, 1.5 * Math.Pow(1.25, 3)),
            "A fresh clear reference followed by a full strict hold failed to zoom.");
    }

    // On-board feedback: how far each held button is toward acting.
    private static void CheckHoldProgress()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        Require(board.ActivateButton("globe-drawer-open", Time(100)) && board.TickGlobe(Time(400)),
            "The hold-progress fixture could not open the drawer.");
        IReadOnlyList<string> Hold(string[] ids, int time) => board.ObserveHeldButtons(ids, Time(time), Time(time));
        Require(board.HoldProgress(Time(1000)).Count == 0, "Progress appeared before any hold.");
        for (int time = 1000; time <= 1500; time += 100) Hold(["globe-zoom-in"], time);
        Require(board.HoldProgress(Time(1500)) is [{ ButtonId: "globe-zoom-in", Progress: > .45 and < .55 }],
            "Half a second of holding did not show half progress.");
        for (int time = 1600; time <= 2000; time += 100) Hold(["globe-zoom-in"], time);
        Require(board.HoldProgress(Time(2200)) is [{ ButtonId: "globe-zoom-in", Progress: > .15 and < .25 }],
            "A repeating hold did not restart its progress after acting.");
        Require(board.HoldProgress(Time(2400)).Count == 0, "Progress outlived the hold's camera evidence.");
        Hold(["globe-zoom-in"], 2500); Hold(["globe-zoom-in"], 2800);
        Hold(["globe-zoom-in", "globe-exit"], 2900);
        Require(board.HoldProgress(Time(2900)).Count == 0, "An ambiguous two-caption frame kept hold progress.");
    }

    // Safety: two covered captions at once are ambiguous, so nothing acts.
    private static void CheckSimultaneousHolds()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        Require(board.ActivateButton("globe-drawer-open", Time(100)) && board.TickGlobe(Time(400)),
            "The simultaneous-hold fixture could not open the drawer.");
        IReadOnlyList<string> Hold(string[] ids, int time) => board.ObserveHeldButtons(ids, Time(time), Time(time));
        for (int time = 1000; time <= 3000; time += 100)
            Require(Hold(["globe-zoom-in", "globe-exit"], time).Count == 0 && board.Screen == BoardScreen.Globe &&
                Near(board.GetGlobeSnapshot(Time(time)).TargetZoom, 1.5),
                "Two covered long-press captions activated a button.");
        for (int time = 3100; time < 4100; time += 100)
            Require(Hold(["globe-zoom-in"], time).Count == 0, "An ambiguous hold shortened the next long press.");
        Require(Hold(["globe-zoom-in"], 4100).SequenceEqual(["globe-zoom-in"]),
            "A single covered caption did not act after its own second.");
    }

    private static void CheckLongPressHandle()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        IReadOnlyList<string> Hold(string id, int time) => board.ObserveHeldButtons([id], Time(time), Time(time + 40));
        IReadOnlyList<string> Lift(int time) => board.ObserveHeldButtons([], Time(time), Time(time + 40));
        Require(Button(board, "globe-drawer-open").Hold == BoardButtonHold.Once, "The drawer handle is not a long-press.");
        for (int time = 4000; time < 5000; time += 100)
            Require(Hold("globe-drawer-open", time).Count == 0 && !board.GlobeDrawerOpen,
                "The handle toggled before a full second.");
        Require(Hold("globe-drawer-open", 5000).SequenceEqual(["globe-drawer-open"]) && board.GlobeDrawerOpen,
            "A one-second hold did not open the drawer.");
        // Fingers still resting on the handle, now "v", must not close it again.
        board.TickGlobe(Time(5300));
        for (int time = 5400; time <= 8000; time += 100)
            Require(Hold("globe-drawer-close", time).Count == 0 && board.GlobeDrawerOpen,
                "A continued hold toggled the drawer again.");
        Require(Lift(8100).Count == 0 && Lift(8300).Count == 0 && Hold("globe-drawer-close", 8400).Count == 0 &&
            Hold("globe-drawer-close", 9500).Count == 0 && board.GlobeDrawerOpen,
            "A lift under 350 ms of observed frames re-armed the spent handle.");
        Require(Lift(9600).Count == 0 && Lift(9800).Count == 0 && Lift(9950).Count == 0, "Lifting activated a button.");
        for (int time = 10000; time < 11000; time += 100)
            Require(Hold("globe-drawer-close", time).Count == 0, "The re-armed handle closed before a full second.");
        Require(Hold("globe-drawer-close", 11000).SequenceEqual(["globe-drawer-close"]) && !board.GlobeDrawerOpen,
            "A fresh one-second hold after lifting did not close the drawer.");
        board.ShowMenu(Time(11100)); board.ShowGlobe(Time(11200));
        for (int time = 11300; time < 12300; time += 100)
            Require(Hold("globe-drawer-open", time).Count == 0, "A reopened Globe handle acted before a second.");
        Require(Hold("globe-drawer-open", 12300).SequenceEqual(["globe-drawer-open"]),
            "Leaving Globe kept the handle spent.");
    }

    private static void CheckEntranceAndRotation()
    {
        var board = new BoardSession();
        Require(board.Buttons[5] is { Id: "globe", Label: "Globe", Destination: BoardScreen.Globe },
            "The Globe menu tile did not replace Diablo in its original position.");
        board.ShowGlobe(Time(100));
        long revision = board.Revision;
        GlobeSnapshot start = board.GetGlobeSnapshot(Time(100));
        GlobeSnapshot middle = board.GetGlobeSnapshot(Time(1600));
        GlobeSnapshot finished = board.GetGlobeSnapshot(Time(3100));
        Require(start is { IntroProgress: 0, ElapsedSeconds: 0, TargetZoom: 1.5 } && Near(start.Zoom, .06),
            "Earth did not start far away at its default zoom.");
        Require(Near(middle.IntroProgress, .5) && middle.Zoom > start.Zoom && middle.Zoom < finished.Zoom &&
            Near(finished.IntroProgress, 1) && Near(finished.Zoom, 1.5), "The three-second approach did not fill the default view.");
        Require(Near(finished.RotationDegrees, 3) && Near(board.GetGlobeSnapshot(Time(60100)).RotationDegrees, 60),
            "Earth did not spin slowly at one degree per second.");
        Require(board.GetGlobeSnapshot(Time(1600)) == middle && board.Revision == revision &&
            start.Revision == finished.Revision, "Presentation sampling mutated interaction state or used an inconsistent clock.");
        Require(Near(board.GetGlobeSnapshot(Time(360100)).RotationDegrees, 0) &&
            board.GetGlobeSnapshot(Time(99)) is { IntroProgress: 0, ElapsedSeconds: 0 },
            "Long-running spin wrapping or a pre-launch clock produced invalid globe geometry.");
        board.ShowMenu(Time(361000)); board.ShowGlobe(Time(362000));
        Require(board.GetGlobeSnapshot(Time(362000)) is { TargetZoom: 1.5, IntroProgress: 0, RotationDegrees: 0 } &&
            !board.GlobeDrawerOpen && board.GlobeDrawerOpenedAt is null,
            "Returning to Globe did not restart the approach, orientation and closed drawer.");

        // The shader centres longitude -rotation: a Pacific home (-120 degrees)
        // opens at rotation 120 and keeps spinning eastward from there.
        var home = new BoardSession(globe: new GlobeState(-120));
        home.ShowGlobe(Time(100));
        Require(Near(home.GetGlobeSnapshot(Time(100)).RotationDegrees, 120) &&
            Near(home.GetGlobeSnapshot(Time(3100)).RotationDegrees, 123),
            "Globe did not open on its home longitude.");
        home.ShowMenu(Time(9000)); home.ShowGlobe(Time(10000));
        Require(Near(home.GetGlobeSnapshot(Time(10000)).RotationDegrees, 120) &&
            Near(new GlobeState(-180).GetSnapshot(Time(0)).RotationDegrees, 0) &&
            Near(new GlobeState(150).GetSnapshot(Time(0)).RotationDegrees, 0),
            "Relaunching lost the home longitude, or an unstarted Globe rotated.");
        var east = new GlobeState(150); east.Start(Time(0));
        Require(Near(east.GetSnapshot(Time(0)).RotationDegrees, 210), "An eastern home longitude opened at the wrong rotation.");

        // The home latitude stays centred while Earth spins; polar homes are limited.
        var north = new BoardSession(globe: new GlobeState(-118, 34));
        north.ShowGlobe(Time(0));
        Require(Near(north.GetGlobeSnapshot(Time(0)).ViewLatitudeDegrees, 34) &&
            Near(north.GetGlobeSnapshot(Time(60000)).ViewLatitudeDegrees, 34) && Near(north.GlobeHomeLatitudeDegrees, 34) &&
            Near(new GlobeState(0, 78).HomeLatitudeDegrees, GlobeState.MaximumViewLatitude) &&
            Near(new GlobeState(0, -89).HomeLatitudeDegrees, -GlobeState.MaximumViewLatitude) &&
            Near(new GlobeState().GetSnapshot(Time(0)).ViewLatitudeDegrees, 0),
            "The Globe did not hold its bounded home latitude at the centre.");
        bool rejected = false;
        try { _ = new GlobeState(0, double.NaN); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected, "A non-finite home latitude was accepted.");

        // Time zones resolve to tz reference cities, refined by the Windows region.
        static bool City((double Latitude, double Longitude)? city, double latitude, double longitude) =>
            city is { } value && Math.Abs(value.Latitude - latitude) < .01 && Math.Abs(value.Longitude - longitude) < .01;
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        Require(City(GlobeHome.ReferenceCity(pacific, "US"), 34.0522, -118.2428) &&
            City(GlobeHome.ReferenceCity(pacific, "CA"), 49.2667, -123.1167) &&
            City(GlobeHome.ReferenceCity(pacific), 34.0522, -118.2428) &&
            City(GlobeHome.ReferenceCity(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")), 22.5333, 88.3667) &&
            City(GlobeHome.ReferenceCity(TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time")), -33.8667, 151.2167) &&
            GlobeHome.ReferenceCity(TimeZoneInfo.FindSystemTimeZoneById("UTC-11")) is null,
            "Time zones did not resolve to their tz reference cities.");
    }

    private static void CheckDrawerAndSmoothControls()
    {
        var board = new BoardSession(); int opened = 0;
        board.BoardOpened += screen => { if (screen == BoardScreen.Globe) opened++; };
        board.ShowGlobe(Time(0));
        Require(board.Buttons.Single() is { Id: "globe-drawer-open", Label: "^", Enabled: true } &&
            board.Buttons[0].Bounds == new BoardRect(.01, .87, .18, .12),
            "Globe did not begin with only its left drawer handle.");
        long revision = board.Revision;
        Require(!board.ActivateButton("globe-exit", Time(100)) && !board.ActivateButton("globe-zoom-in", Time(200)) &&
            !board.ActivateButton("globe-zoom-out", Time(300)) && !board.ActivateButton("globe-rotate-right", Time(400)) &&
            !board.ActivateButton("globe-rotate-left", Time(500)) && board.Revision == revision,
            "A hidden Globe control remained executable.");
        Require(board.ActivateButton("globe-drawer-open", Time(4000)) && board.GlobeDrawerOpen &&
            board.GlobeDrawerOpenedAt == Time(4000), "The drawer handle did not open a timed drawer.");
        string[] ids = ["globe-drawer-close", "globe-exit", "globe-zoom-out", "globe-zoom-in"];
        string[] labels = ["v", "Exit", "Zoom -", "Zoom +"];
        Require(board.Buttons.Select(button => button.Id).SequenceEqual(ids) &&
            board.Buttons.Select(button => button.Label).SequenceEqual(labels), "The open drawer contains incorrect controls.");
        BoardRect[] bounds = [new(.01, .87, .18, .12), new(.20, .87, .155, .12),
            new(.365, .87, .155, .12), new(.53, .87, .155, .12)];
        Require(board.Buttons.Select(button => button.Bounds).SequenceEqual(bounds) &&
            Button(board, "globe-drawer-close").Bounds == new BoardRect(.01, .87, .18, .12),
            "Drawer controls did not form one bottom row beside the fixed handle.");
        // The row ends left of the bottom-right credit and zoom readout.
        foreach (BoardButton button in board.Buttons)
            Require(button.Bounds.X > 0 && button.Bounds.X + button.Bounds.Width < .69 &&
                button.Bounds.Width >= (button.Id == "globe-drawer-close" ? .18 : .155) &&
                button.Bounds.Y == .87 && button.Bounds.Height == .12 &&
                button.Bounds.Y + button.Bounds.Height < 1 &&
                !board.Buttons.Any(other => other.Id != button.Id &&
                    other.Bounds.X < button.Bounds.X + button.Bounds.Width &&
                    other.Bounds.X + other.Bounds.Width > button.Bounds.X &&
                    other.Bounds.Y < button.Bounds.Y + button.Bounds.Height &&
                    other.Bounds.Y + other.Bounds.Height > button.Bounds.Y),
                "A drawer control lacks grouped-finger room, is outside the board or overlaps another target.");
        revision = board.Revision;
        Require(Near(board.GetGlobeDrawerProgress(Time(3999)), 0) && Near(board.GetGlobeDrawerProgress(Time(4150)), .5) &&
            Near(board.GetGlobeDrawerProgress(Time(4300)), 1) && board.Revision == revision &&
            board.Buttons.Where(button => button.Id != "globe-drawer-close").All(button => !button.Enabled) &&
            !board.ActivateButton("globe-exit", Time(4150)) && !board.ActivateButton("globe-zoom-in", Time(4299)),
            "Drawer sampling changed state or a moving control became executable.");
        Require(!board.TickGlobe(Time(4299)) && board.TickGlobe(Time(4300)) && !board.TickGlobe(Time(4301)) &&
            board.Buttons.All(button => button.Enabled) && Near(board.GetGlobeDrawerProgress(Time(4100)), 1) && opened == 1,
            "Settled drawer controls did not enable once, or the drawer restarted the Earth.");

        GlobeSnapshot before = board.GetGlobeSnapshot(Time(4400));
        Require(board.ActivateButton("globe-zoom-in", Time(4400)), "Zoom + did not accept a settled laptop selection.");
        GlobeSnapshot instant = board.GetGlobeSnapshot(Time(4400));
        GlobeSnapshot halfway = board.GetGlobeSnapshot(Time(4575));
        GlobeSnapshot final = board.GetGlobeSnapshot(Time(4750));
        Require(Near(instant.Zoom, before.Zoom) && Near(instant.TargetZoom, 1.875) && halfway.Zoom > instant.Zoom &&
            halfway.Zoom < final.Zoom && Near(final.Zoom, 1.875), "Zooming jumped immediately or failed to settle smoothly.");
        Require(board.ActivateButton("globe-zoom-in", Time(4750)) && board.ActivateButton("globe-zoom-in", Time(4750)),
            "Consecutive zoom selections were lost.");
        Require(Near(board.GetGlobeSnapshot(Time(5100)).TargetZoom, 1.5 * Math.Pow(1.25, 3)),
            "Quick selections failed to accumulate zoom targets.");
        for (int time = 5500; time < 15000; time += 400) board.ActivateButton("globe-zoom-in", Time(time));
        Require(Near(board.GetGlobeSnapshot(Time(15000)).Zoom, GlobeState.MaximumZoom) &&
            !Button(board, "globe-zoom-in").Enabled && !board.ActivateButton("globe-zoom-in", Time(15000)),
            "Zoom exceeded its maximum or left the capped control enabled.");
        for (int time = 16000; time < 36000; time += 400) board.ActivateButton("globe-zoom-out", Time(time));
        Require(Near(board.GetGlobeSnapshot(Time(36000)).Zoom, GlobeState.MinimumZoom) &&
            !Button(board, "globe-zoom-out").Enabled && !board.ActivateButton("globe-zoom-out", Time(36000)),
            "Zoom exceeded its minimum or left the capped control enabled.");
        revision = board.Revision;
        Require(!board.ActivateButton("globe-rotate-right", Time(37000)) &&
            !board.ActivateButton("globe-rotate-left", Time(38000)) && !board.ActivateButton("unknown", Time(39000)) &&
            board.Revision == revision && Near(board.GetGlobeSnapshot(Time(39000)).RotationDegrees, 39),
            "A removed action changed state or the drawer interrupted the Earth's automatic spin.");
        Require(board.ActivateButton("globe-drawer-close", Time(40000)) && !board.GlobeDrawerOpen &&
            board.Buttons.Single().Id == "globe-drawer-open" && opened == 1,
            "Closing the drawer did not hide its controls or reopened the board.");
        board.ShowGlobe(Time(41000));
        Require(board.GetGlobeSnapshot(Time(44000)) is { TargetZoom: 1.5 } &&
            Near(board.GetGlobeSnapshot(Time(44000)).Zoom, 1.5) && !board.GlobeDrawerOpen,
            "Restarting Globe retained the previous zoom or drawer.");
        Require(board.ActivateButton("globe-drawer-open", Time(45000)) &&
            board.ActivateButton("globe-drawer-close", Time(45100)) && !board.TickGlobe(Time(45400)) &&
            !board.GlobeDrawerOpen, "An interrupted drawer entrance reappeared after its deadline.");
    }

    private static void CheckNavigationAndPinches()
    {
        var board = new BoardSession(); BoardButton globe = Button(board, "globe");
        Require(At(board, 100, Pinch(globe, 1, 100)) is { Previous: BoardScreen.Menu, Current: BoardScreen.Globe,
            ButtonId: "globe", Gesture: BoardSelectionGesture.Pinch } && board.Title == "Globe", "A menu pinch did not launch Globe.");
        // Every Globe control is a hold button: pinches, held or fresh, never select or hover them.
        BoardButton handle = Button(board, "globe-drawer-open");
        Require(At(board, 140, Pinch(handle, 1, 100)) is null && At(board, 160, Pinch(handle, 2, 160)) is null &&
            !board.GlobeDrawerOpen && board.HoveredButtonIds.Count == 0,
            "A pinch toggled or hovered the long-press drawer handle.");
        Require(board.ActivateButton("globe-drawer-open", Time(180)), "The drawer could not open.");
        // Evidence while the drawer is still moving cannot start Exit's timer.
        Require(board.ObserveHeldButtons(["globe-exit"], Time(200), Time(200)).Count == 0 &&
            board.ObserveHeldButtons(["globe-exit"], Time(400), Time(400)).Count == 0 && board.TickGlobe(Time(480)),
            "Exit accepted hold evidence during the drawer's entrance.");
        long eventId = 3;
        foreach (string id in new[] { "globe-exit", "globe-zoom-in", "globe-zoom-out", "globe-drawer-close" })
        {
            int time = 500 + (int)eventId * 40;
            BoardButton control = Button(board, id);
            Require(At(board, time, Pinch(control, eventId, time)) is null &&
                At(board, time + 20, Pinch(control, eventId, time)) is null && board.HoveredButtonIds.Count == 0,
                $"A pinch selected or hovered the hold button {id}.");
            eventId++;
        }
        Require(board.Screen == BoardScreen.Globe && board.GlobeDrawerOpen &&
            Near(board.GetGlobeSnapshot(Time(800)).TargetZoom, 1.5), "A pinch changed the Globe.");
        // Exit acts after one second of caption evidence, once.
        for (int time = 1000; time < 2000; time += 100)
            Require(board.ObserveHeldButtons(["globe-exit"], Time(time), Time(time)).Count == 0 &&
                board.Screen == BoardScreen.Globe, "Exit left before a full second of hold evidence.");
        Require(board.ObserveHeldButtons(["globe-exit"], Time(2000), Time(2000)).SequenceEqual(["globe-exit"]) &&
            board.Screen == BoardScreen.Menu, "A one-second hold did not leave Globe through Exit.");
        Require(At(board, 2040, Pinch(globe, 1, 100)) is null && board.Screen == BoardScreen.Menu,
            "An old launch pinch reentered Globe after Exit.");
        board.ShowGlobe(Time(2100));
        Require(board.ActivateButton("globe-drawer-open", Time(2120)), "The drawer could not reopen.");
        board.ResetInput(Time(2160));
        Require(!board.GlobeDrawerOpen && board.Buttons.Single().Id == "globe-drawer-open" &&
            board.ObserveHeldButtons(["globe-exit"], Time(2200), Time(2200)).Count == 0,
            "Input reset failed to close the drawer or left Exit active.");
    }

    private static void CheckFourFingerSelection()
    {
        var board = new BoardSession(); board.ShowGlobe(Time(0));
        // No Globe control answers the finger gesture: grouped fingers cannot
        // arm, hover or select the handle, Exit or the zooms.
        BoardHandSample handle = Together(Button(board, "globe-drawer-open"));
        for (int time = 100; time <= 1300; time += 100)
            Require(At(board, time, time < 1200 ? handle : Apart(handle)) is null &&
                board.FingerSelectionFeedback.Count == 0 && board.HoveredButtonIds.Count == 0 && !board.GlobeDrawerOpen,
                "Finger gestures armed, hovered or toggled the long-press drawer handle.");
        Require(board.ActivateButton("globe-drawer-open", Time(1300)) && board.TickGlobe(Time(1600)),
            "The drawer could not open.");
        int start = 1700;
        foreach (string id in new[] { "globe-exit", "globe-zoom-in", "globe-zoom-out", "globe-drawer-close" })
        {
            BoardHandSample fingers = Together(Button(board, id));
            for (int time = start; time <= start + 1300; time += 100)
                Require(At(board, time, time < start + 1200 ? fingers : Apart(fingers)) is null &&
                    board.FingerSelectionFeedback.Count == 0 && board.HoveredButtonIds.Count == 0,
                    $"Finger gestures armed, hovered or selected the hold button {id}.");
            start += 1500;
        }
        Require(board.Screen == BoardScreen.Globe && board.GlobeDrawerOpen &&
            Near(board.GetGlobeSnapshot(Time(start)).TargetZoom, 1.5), "A finger gesture changed the Globe.");
    }

    private static BoardHandSample Together(BoardButton button) => new(double.NaN, double.NaN,
        DateTimeOffset.MinValue, 0) { TrackingId = 11, FourFingersExtended = true, FingersTogether = true, FingerAim = Center(button) };
    private static BoardHandSample Apart(BoardHandSample hand) => hand with { FingersTogether = false, IndexFingerSeparated = true };
    private static BoardHandSample Pinch(BoardButton button, long eventId, int time) => new(Center(button).U,
        Center(button).V, Time(time + 1000), eventId) { TrackingId = 11 };
    private static BoardAim Center(BoardButton button) => new(button.Bounds.X + button.Bounds.Width / 2,
        button.Bounds.Y + button.Bounds.Height / 2);
    private static BoardButton Button(BoardSession board, string id) => board.Buttons.Single(button => button.Id == id);
    private static BoardNavigation? At(BoardSession board, int time, params BoardHandSample[] hands) => board.Update(hands, Time(time), Time(time));
    private static DateTimeOffset Time(int milliseconds) => new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);
    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-9;
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
