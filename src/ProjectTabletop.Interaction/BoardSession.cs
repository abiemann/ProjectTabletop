namespace ProjectTabletop.Interaction;

public enum BoardScreen { Menu, HandTracking, PhotoCopy, Blackjack, Monopoly, Gta, Diablo, Media }

/// <summary>A rectangle in the board's normalized, perspective-corrected coordinate system.</summary>
public readonly record struct BoardRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double u, double v) =>
        double.IsFinite(u) && double.IsFinite(v) && u is >= 0 and <= 1 && v is >= 0 and <= 1 &&
        u >= X && u <= X + Width && v >= Y && v <= Y + Height;
}

public sealed record BoardButton(string Id, string Label, BoardRect Bounds, BoardScreen Destination, bool Enabled = true);

/// <summary>
/// A hand selection position mapped to board coordinates. SelectionFrameTime
/// identifies an optional open-hand anchor retained while the fingers close;
/// otherwise U/V are the current fingertip. Preserve samples that cannot be
/// mapped, using NaN coordinates, so their execution events are consumed.
/// ExecuteEventId identifies one pinch and remains unchanged throughout its pulse.
/// ExecuteUntil is exactly one second after that event, as produced by HandGestureTracker.
/// </summary>
public readonly record struct BoardHandSample(double U, double V, DateTimeOffset ExecuteUntil,
    long ExecuteEventId, DateTimeOffset? SelectionFrameTime = null);

public sealed record BoardNavigation(BoardScreen Previous, BoardScreen Current, string ButtonId);

/// <summary>
/// Board application navigation shared by rendering and gesture hit testing.
/// Call from one thread. Each pinch can activate one target only, including when
/// hands disappear briefly, change order, move to another target, or switch screens.
/// </summary>
public sealed partial class BoardSession
{
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan SelectionLifetime = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ExecuteDuration = TimeSpan.FromSeconds(1);
    private static readonly IReadOnlyList<BoardButton> MenuButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("hand-tracking", "Hand-Tracking", new(.08, .25, .40, .16), BoardScreen.HandTracking),
        new BoardButton("photo-copy", "Photo Copy", new(.52, .25, .40, .16), BoardScreen.PhotoCopy),
        new BoardButton("blackjack", "Blackjack", new(.08, .45, .40, .16), BoardScreen.Blackjack),
        new BoardButton("monopoly", "Monopoly", new(.52, .45, .40, .16), BoardScreen.Monopoly),
        new BoardButton("gta", "GTA", new(.08, .65, .40, .16), BoardScreen.Gta),
        new BoardButton("diablo", "Diablo", new(.52, .65, .40, .16), BoardScreen.Diablo)
    });
    private static readonly IReadOnlyList<BoardButton> AppButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("menu", "Back to menu", new(.06, .055, .30, .105), BoardScreen.Menu)
    });
    private static readonly IReadOnlyList<BoardButton> PhotoCopyButtons = Array.AsReadOnly(new[]
    {
        AppButtons[0],
        new BoardButton("capture-again", "Capture again", new(.64, .055, .30, .105), BoardScreen.PhotoCopy)
    });
    private DateTimeOffset? _lastFrameTime;
    private DateTimeOffset? _lastNow;
    private DateTimeOffset _ignoreExecutionsThrough = DateTimeOffset.MinValue;
    private DateTimeOffset _ignoreFramesThrough = DateTimeOffset.MinValue;
    private DateTimeOffset _ignoreSelectionsThrough = DateTimeOffset.MinValue;
    private long _consumedEventId;

    public BoardScreen Screen { get; private set; } = BoardScreen.Menu;
    /// <summary>Changes on every navigation, including restarting the current application.</summary>
    public long Revision { get; private set; }
    public string Title => Screen switch
    {
        BoardScreen.Menu => "Project Tabletop",
        BoardScreen.HandTracking => "Hand-Tracking",
        BoardScreen.PhotoCopy => "Photo Copy",
        BoardScreen.Blackjack => "Blackjack",
        BoardScreen.Monopoly => "Monopoly",
        BoardScreen.Gta => "GTA",
        BoardScreen.Diablo => "Diablo",
        BoardScreen.Media => "Media",
        _ => throw new InvalidOperationException("Unknown board screen.")
    };
    public IReadOnlyList<BoardButton> Buttons => Screen switch
    {
        BoardScreen.Menu => MenuButtons,
        BoardScreen.PhotoCopy => PhotoCopyButtons,
        BoardScreen.Blackjack => BlackjackButtons(),
        BoardScreen.Media => Array.Empty<BoardButton>(),
        _ => AppButtons
    };
    public IReadOnlyList<string> HoveredButtonIds { get; private set; } = Array.Empty<string>();

    public BoardNavigation? Update(IReadOnlyList<BoardHandSample> hands,
        DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hands);
        HoveredButtonIds = Array.Empty<string>();
        if (_lastNow is { } previousNow && now < previousNow) return null;
        _lastNow = now;
        if (frameTime > now || now - frameTime > ObservationLifetime ||
            frameTime <= _ignoreFramesThrough ||
            (_lastFrameTime is { } previousFrame && frameTime <= previousFrame)) return null;
        _lastFrameTime = frameTime;

        var buttons = Buttons;
        HoveredButtonIds = buttons.Where(button => button.Enabled && hands.Any(hand =>
                SelectionIsCurrent(hand, frameTime) && button.Bounds.Contains(hand.U, hand.V)))
            .Select(button => button.Id).ToArray();

        // IDs come from a monotonically increasing event sequence, never reset by
        // tracking restarts. Consume the entire frame before a possible navigation,
        // including off-board/expired events, to prevent replay on the next screen.
        long previouslyConsumed = _consumedEventId;
        foreach (var hand in hands)
            _consumedEventId = Math.Max(_consumedEventId, hand.ExecuteEventId);

        var examinedEvents = new HashSet<long>();
        foreach (var hand in hands)
        {
            if (!examinedEvents.Add(hand.ExecuteEventId) || hand.ExecuteEventId <= previouslyConsumed || hand.ExecuteUntil <= now ||
                hand.ExecuteUntil - now > ExecuteDuration ||
                hand.ExecuteUntil - ExecuteDuration <= _ignoreExecutionsThrough ||
                !SelectionIsCurrent(hand, frameTime)) continue;
            BoardButton? selected = buttons.FirstOrDefault(button => button.Enabled && button.Bounds.Contains(hand.U, hand.V));
            if (selected is null) continue;
            var result = new BoardNavigation(Screen, selected.Destination, selected.Id);
            if (!SelectButton(selected, now)) continue;
            return result;
        }
        return null;
    }

    public void ShowMenu(DateTimeOffset? now = null) => Show(BoardScreen.Menu, now ?? DateTimeOffset.UtcNow);
    public void ShowHandTrackingTest(DateTimeOffset? now = null) => Show(BoardScreen.HandTracking, now ?? DateTimeOffset.UtcNow);
    public void ShowPhotoCopy(DateTimeOffset? now = null) => Show(BoardScreen.PhotoCopy, now ?? DateTimeOffset.UtcNow);
    public void ShowBlackjack(DateTimeOffset? now = null) => Show(BoardScreen.Blackjack, now ?? DateTimeOffset.UtcNow);
    public void ShowMedia(DateTimeOffset? now = null) => Show(BoardScreen.Media, now ?? DateTimeOffset.UtcNow);

    /// <summary>Clear hover and reject observations/pulses that predate a camera or calibration reset.</summary>
    public void ResetInput(DateTimeOffset now)
    {
        HoveredButtonIds = Array.Empty<string>();
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        _lastFrameTime = null;
        _lastNow = now;
        // Keep the high-water mark: clearing visual state must never replay a pinch.
    }

    private void Show(BoardScreen screen, DateTimeOffset now)
    {
        Screen = screen;
        Revision++;
        HoveredButtonIds = Array.Empty<string>();
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
    }

    private bool SelectionIsCurrent(BoardHandSample hand, DateTimeOffset frameTime) =>
        hand.SelectionFrameTime is not { } selectionTime ||
        selectionTime > _ignoreSelectionsThrough && selectionTime <= frameTime &&
        frameTime - selectionTime <= SelectionLifetime;

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;
}
