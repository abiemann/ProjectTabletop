using ProjectTabletop.Interaction;

CheckMenuAndNavigation();
CheckOffTargetAndBounds();
CheckHeldPinchAndDropout();
CheckIndependentHands();
CheckFreshness();
CheckResetAndExternalNavigation();
Console.WriteLine("Board interaction verification passed: menu/navigation, shared hit targets, off-target consumption, " +
    "held-pinch suppression, dropouts, independent hands, freshness and reset.");

static void CheckMenuAndNavigation()
{
    var session = new BoardSession();
    Require(session.Screen == BoardScreen.Menu, "The board did not start at the menu.");
    string[] names = ["Hand-Tracking", "Copy-Machine", "Blackjack", "Monopoly", "GTA", "Diablo"];
    Require(session.Buttons.Select(button => button.Label).SequenceEqual(names), "Menu order or labels differ from the requested menu.");
    BoardButton[] buttons = session.Buttons.ToArray();
    for (int index = 0; index < buttons.Length; index++)
    {
        var button = buttons[index];
        var bounds = button.Bounds;
        Require(bounds.Width >= .35 && bounds.Height >= .15, "A menu target is too small for the board.");
        Require(bounds.X > 0 && bounds.Y > 0 && bounds.X + bounds.Width < 1 && bounds.Y + bounds.Height < 1,
            "A menu target reaches outside the board.");
        Require(buttons.Count(other => other.Bounds.Contains(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)) == 1,
            "Menu targets overlap at a button's center.");

        int time = 100 + index * 100;
        Require(Update(session, time, Over(button)) is null, "Hover alone selected an application.");
        Require(session.HoveredButtonIds.SequenceEqual([button.Id]), "Hover did not use the shared target rectangle.");
        BoardNavigation? navigation = Update(session, time + 10, Over(button, 2 * index + 1, time + 10));
        Require(navigation is { Previous: BoardScreen.Menu } && navigation.Current == button.Destination &&
            navigation.ButtonId == button.Id && session.Screen == button.Destination, "Menu pinch opened the wrong application.");
        Require(session.Title == names[index], "The application title is incorrect.");
        Require(session.Buttons.Count == 1 && session.Buttons[0].Destination == BoardScreen.Menu,
            "An application lacks a back-to-menu target.");
        Require(Update(session, time + 20, Over(session.Buttons[0], 2 * index + 2, time + 20))?.Current == BoardScreen.Menu,
            "The back-to-menu target did not return to the launcher.");
    }
}

static void CheckOffTargetAndBounds()
{
    foreach ((double u, double v) in new[] { (-.01, .30), (1.01, .30), (.20, -.01), (.20, 1.01),
        (.5, .5), (double.NaN, .3), (.2, double.PositiveInfinity) })
    {
        var session = new BoardSession();
        var button = session.Buttons[0];
        Require(Update(session, 100, new BoardHandSample(u, v, Time(1100), 1)) is null, "An off-target pinch navigated.");
        Require(session.HoveredButtonIds.Count == 0, "An invalid/off-target position hovered a button.");
        Require(Update(session, 140, Over(button, 1, 100)) is null,
            "Moving an existing off-target pinch onto a button activated it.");
        Require(Update(session, 180, Over(button, 2, 180))?.Current == BoardScreen.HandTracking,
            "A new pinch after an off-target pinch could not activate a button.");
    }
    var duplicate = new BoardSession();
    Require(Update(duplicate, 100, new(double.NaN, 0, Time(1100), 1), Over(duplicate.Buttons[0], 1, 100)) is null,
        "Duplicating an off-target event at a button activated it.");
}

static void CheckHeldPinchAndDropout()
{
    var session = new BoardSession();
    Update(session, 100, Over(session.Buttons[0], 1, 100));
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
    BoardButton first = session.Buttons[0];
    // Both events share a pulse deadline, but only the first hand is off-target.
    Require(Update(session, 100, new(double.NaN, double.NaN, Time(1100), 1), Over(first, 2, 100))?.Current == BoardScreen.HandTracking,
        "One hand's off-target pinch consumed the other hand's simultaneous pinch.");
    Require(Update(session, 140, Over(session.Buttons[0], 2, 100), Over(session.Buttons[0], 1, 100)) is null,
        "Reordering two hands replayed one of their pinches.");
    Require(Update(session, 180, Over(session.Buttons[0], 2, 100), Over(session.Buttons[0], 3, 180))?.Current == BoardScreen.Menu,
        "A held pinch prevented the other hand from executing independently.");

    session = new BoardSession();
    Require(Update(session, 100, Over(session.Buttons[0], 1, 100), Over(session.Buttons[1], 2, 100))?.Current == BoardScreen.HandTracking,
        "Simultaneous targets did not deterministically select one screen.");
    Require(Update(session, 140, Over(session.Buttons[0], 2, 100)) is null,
        "The unused simultaneous event activated a button on the new screen.");
}

static void CheckFreshness()
{
    var session = new BoardSession();
    BoardHandSample pinch = Over(session.Buttons[0], 1, 0);
    Require(session.Update([pinch], Time(0), Time(351)) is null, "A stale frame navigated.");
    Require(session.Update([pinch], Time(500), Time(400)) is null, "A future frame navigated.");
    Require(session.Update([pinch], Time(410), Time(410))?.Current == BoardScreen.HandTracking,
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
    Update(session, 100, Over(session.Buttons[0], 1, 100));
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
    Require(Update(session, 520, Over(session.Buttons[0], 5, 440)) is null, "Returning from media replayed a pulse.");
    Require(Update(session, 560, Over(session.Buttons[0], 6, 560))?.Current == BoardScreen.HandTracking,
        "Returning from media prevented a new pinch.");
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
