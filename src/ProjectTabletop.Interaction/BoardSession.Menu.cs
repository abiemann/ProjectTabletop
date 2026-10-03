namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public static readonly TimeSpan MenuScrollDuration = TimeSpan.FromMilliseconds(650);
    public static readonly BoardRect MenuCardViewport = new(.06, .238, .88, .587);
    public static readonly BoardRect MenuScrollButtonBounds = new(.76, .85, .18, .105);
    private const double MenuRowStep = .20;
    private static readonly IReadOnlyList<BoardButton> MenuCards = Array.AsReadOnly(new[]
    {
        new BoardButton("slots", "Dragon Slots", new(.08, .25, .40, .16), BoardScreen.Slots),
        new BoardButton("photo-copy", "Photo Copy", new(.52, .25, .40, .16), BoardScreen.PhotoCopy),
        new BoardButton("blackjack", "Blackjack", new(.08, .45, .40, .16), BoardScreen.Blackjack),
        new BoardButton("paint", "Paint", new(.52, .45, .40, .16), BoardScreen.Paint),
        new BoardButton("monopoly", "Monopoly", new(.08, .65, .40, .16), BoardScreen.Monopoly),
        new BoardButton("globe", "Globe", new(.52, .65, .40, .16), BoardScreen.Globe),
        new BoardButton("roulette", "Roulette", new(.08, .85, .40, .16), BoardScreen.Roulette)
    });
    private bool _menuScrolled;
    private DateTimeOffset? _menuScrollStartedAt;
    private DateTimeOffset _menuScrollObservedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _menuInputReadyAfter = DateTimeOffset.MinValue;

    public bool MenuScrolled => Screen == BoardScreen.Menu && _menuScrolled;
    public bool MenuScrolling => Screen == BoardScreen.Menu && _menuScrollStartedAt is not null;

    /// <summary>Pure presentation sample: one row of motion, using the caller's clock.</summary>
    public double GetMenuScrollOffset(DateTimeOffset now)
    {
        if (_menuScrollStartedAt is not { } started) return _menuScrolled ? MenuRowStep : 0;
        double progress = Math.Clamp((now - started) / MenuScrollDuration, 0, 1);
        double eased = progress * progress * (3 - 2 * progress);
        return MenuRowStep * (_menuScrolled ? eased : 1 - eased);
    }

    /// <summary>All seven cards for drawing inside MenuCardViewport, including moving, clipped cards.
    /// Use Buttons for input; partially visible or moving cards are never targets.</summary>
    public IReadOnlyList<BoardButton> GetMenuCards(DateTimeOffset now) =>
        MenuCards.Select(button => MoveMenuCard(button, GetMenuScrollOffset(now), !MenuScrolling)).ToArray();

    public bool TickMenu(DateTimeOffset now) => AdvanceMenuScroll(now);

    private IReadOnlyList<BoardButton> CurrentMenuButtons()
    {
        double offset = _menuScrolled ? MenuRowStep : 0;
        var buttons = MenuCards.Select(button => MoveMenuCard(button, offset, !MenuScrolling))
            .Where(button => button.Bounds.Y >= MenuCardViewport.Y &&
                button.Bounds.Y + button.Bounds.Height <= MenuCardViewport.Y + MenuCardViewport.Height).ToList();
        buttons.Add(new("settings", "Settings", SettingsCogBounds, BoardScreen.Settings, Enabled: !MenuScrolling));
        buttons.Add(new(_menuScrolled ? "menu-scroll-up" : "menu-scroll-down", _menuScrolled ? "^" : "v",
            MenuScrollButtonBounds, BoardScreen.Menu, Enabled: !MenuScrolling, Hold: BoardButtonHold.Once));
        return buttons.AsReadOnly();
    }

    private static BoardButton MoveMenuCard(BoardButton button, double offset, bool enabled) =>
        button with { Bounds = button.Bounds with { Y = button.Bounds.Y - offset }, Enabled = enabled };

    private bool SelectMenuScroll(BoardButton button, DateTimeOffset now)
    {
        if (MenuScrolling || now < _menuScrollObservedAt ||
            button.Id != (_menuScrolled ? "menu-scroll-up" : "menu-scroll-down")) return false;
        _menuScrolled = !_menuScrolled;
        _menuScrollStartedAt = now;
        MenuInputBarrier(now);
        return true;
    }

    private bool AdvanceMenuScroll(DateTimeOffset now)
    {
        if (Screen != BoardScreen.Menu || now < _menuScrollObservedAt) return false;
        _menuScrollObservedAt = now;
        if (_menuScrollStartedAt is not { } started || now < started + MenuScrollDuration) return false;
        _menuScrollStartedAt = null;
        // Gestures seen during the slide cannot select a card when it settles.
        MenuInputBarrier(started + MenuScrollDuration);
        return true;
    }

    private void MenuInputBarrier(DateTimeOffset now)
    {
        Revision++;
        _menuInputReadyAfter = Later(_menuInputReadyAfter, now);
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
        // Keep the arrow's spent place so holding down cannot immediately act
        // on the up arrow that replaces it. Its caption must be clear again.
        ResetHoldCaptionEvidence();
    }

    private void ClearMenuScroll(DateTimeOffset now)
    {
        _menuScrolled = false;
        _menuScrollStartedAt = null;
        _menuScrollObservedAt = now;
        _menuInputReadyAfter = now;
    }
}
