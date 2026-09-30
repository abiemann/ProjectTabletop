namespace ProjectTabletop.Interaction;

public enum BoardScreen { Menu, HandTracking, PhotoCopy, Blackjack, Paint, Monopoly, Globe, Media, Slots, Settings }

/// <summary>A rectangle in the board's normalized, perspective-corrected coordinate system.</summary>
public readonly record struct BoardRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double u, double v) =>
        double.IsFinite(u) && double.IsFinite(v) && u is >= 0 and <= 1 && v is >= 0 and <= 1 &&
        u >= X && u <= X + Width && v >= Y && v <= Y + Height;
}

/// <summary>How a hold button answers fingers resting on its caption.</summary>
public enum BoardButtonHold
{
    /// <summary>An ordinary button, selected by a gesture.</summary>
    None,
    /// <summary>Once after a second of camera evidence, then again each further second.</summary>
    Repeat,
    /// <summary>Once after a second; the fingers must lift before it can act again.</summary>
    Once
}

/// <param name="Hold">Activated by holding fingers over its caption rather than by a gesture,
/// from camera evidence alone. Hold buttons are never lit and ignore selection gestures.</param>
public sealed record BoardButton(string Id, string Label, BoardRect Bounds, BoardScreen Destination, bool Enabled = true,
    BoardButtonHold Hold = BoardButtonHold.None)
{
    public bool IsHold => Hold != BoardButtonHold.None;
}

/// <summary>
/// A hand selection position mapped to board coordinates. SelectionFrameTime
/// identifies an optional open-hand anchor retained while the fingers close;
/// otherwise U/V are the current fingertip. Preserve samples that cannot be
/// mapped, using NaN coordinates, so their execution events are consumed.
/// ExecuteEventId identifies one pinch and remains unchanged throughout its pulse.
/// ExecuteUntil is exactly one second after that event, as produced by HandGestureTracker.
/// </summary>
public readonly record struct BoardHandSample(double U, double V, DateTimeOffset ExecuteUntil,
    long ExecuteEventId, DateTimeOffset? SelectionFrameTime = null)
{
    /// <summary>Stable identity of this observed hand; zero disables finger selection.</summary>
    public long TrackingId { get; init; }
    /// <summary>The current middle fingertip, independent of the index pinch anchor.</summary>
    public BoardAim? FingerAim { get; init; }
    public bool FourFingersExtended { get; init; }
    public bool FingersTogether { get; init; }
    public bool IndexFingerSeparated { get; init; }
}

public readonly record struct BoardAim(double U, double V);
public enum BoardFingerSelectionStage { Arming, Armed, Separating, Selected }
public sealed record BoardFingerSelectionFeedback(string ButtonId, BoardFingerSelectionStage Stage, double Progress);

public enum BoardSelectionGesture { Pinch, IndexSeparation }

public sealed record BoardNavigation(BoardScreen Previous, BoardScreen Current, string ButtonId)
{
    /// <summary>Identity of the hand whose gesture successfully selected this target.</summary>
    public long TrackingId { get; init; }
    public BoardSelectionGesture Gesture { get; init; } = BoardSelectionGesture.Pinch;
}

/// <summary>
/// Board application navigation shared by rendering and gesture hit testing.
/// Call from one thread. Each pinch or together-to-separated finger gesture can activate
/// one target only, including when hands disappear, change order or switch screens.
/// </summary>
public sealed partial class BoardSession
{
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan SelectionLifetime = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ExecuteDuration = TimeSpan.FromSeconds(1);
    /// <summary>The cog in the menu's upper-right corner opens Settings.</summary>
    public static readonly BoardRect SettingsCogBounds = new(.70, .045, .24, .13);
    private static readonly IReadOnlyList<BoardButton> MenuButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("slots", "Dragon Slots", new(.08, .25, .40, .16), BoardScreen.Slots),
        new BoardButton("photo-copy", "Photo Copy", new(.52, .25, .40, .16), BoardScreen.PhotoCopy),
        new BoardButton("blackjack", "Blackjack", new(.08, .45, .40, .16), BoardScreen.Blackjack),
        new BoardButton("paint", "Paint", new(.52, .45, .40, .16), BoardScreen.Paint),
        new BoardButton("monopoly", "Monopoly", new(.08, .65, .40, .16), BoardScreen.Monopoly),
        new BoardButton("globe", "Globe", new(.52, .65, .40, .16), BoardScreen.Globe),
        new BoardButton("settings", "Settings", SettingsCogBounds, BoardScreen.Settings)
    });
    private static readonly IReadOnlyList<BoardButton> AppButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("menu", "Back to menu", new(.06, .055, .30, .105), BoardScreen.Menu)
    });
    // A short caption: resting fingers cover more of it for the camera.
    private static readonly IReadOnlyList<BoardButton> SettingsButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("menu", "Back", new(.06, .055, .30, .105), BoardScreen.Menu),
        new BoardButton("hand-tracking", "Hand-Tracking", new(.08, .25, .40, .16), BoardScreen.HandTracking)
    });
    // The tester is reached from Settings, so its back button returns there.
    private static readonly IReadOnlyList<BoardButton> HandTrackingButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("menu", "Back to settings", new(.06, .055, .30, .105), BoardScreen.Settings)
    });
    private static readonly IReadOnlyList<BoardButton> PhotoCopyButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("menu", "Exit", new(.08, .835, .26, .105), BoardScreen.Menu),
        new BoardButton("photo-swirl", "Swirl", new(.37, .835, .26, .105), BoardScreen.PhotoCopy),
        new BoardButton("photo-copy-once", "Copy", new(.66, .835, .26, .105), BoardScreen.PhotoCopy)
    });
    private static readonly IReadOnlyList<BoardButton> PaintButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("menu", "Exit", new(.06, .85, .26, .105), BoardScreen.Menu),
        new BoardButton("paint-save", "Save", new(.68, .85, .26, .105), BoardScreen.Paint, Enabled: false)
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
    /// <summary>Raised once after a board opens, including menu gestures and reopening the same board.</summary>
    public event Action<BoardScreen>? BoardOpened;
    public string Title => Screen switch
    {
        BoardScreen.Menu => "Project Tabletop",
        BoardScreen.HandTracking => "Hand-Tracking",
        BoardScreen.PhotoCopy => "Photo Copy",
        BoardScreen.Blackjack => "Blackjack",
        BoardScreen.Paint => "Paint",
        BoardScreen.Monopoly => "Monopoly",
        BoardScreen.Globe => "Globe",
        BoardScreen.Media => "Media",
        BoardScreen.Slots => "Dragon Slots",
        BoardScreen.Settings => "Settings",
        _ => throw new InvalidOperationException("Unknown board screen.")
    };
    public IReadOnlyList<BoardButton> Buttons => Screen switch
    {
        BoardScreen.Menu => MenuButtons,
        BoardScreen.PhotoCopy => CurrentPhotoCopyButtons(),
        BoardScreen.Paint => CurrentPaintButtons(),
        BoardScreen.Blackjack => BlackjackButtons(),
        BoardScreen.Monopoly => MonopolyButtons(),
        BoardScreen.Globe => CurrentGlobeButtons(),
        BoardScreen.Media => Array.Empty<BoardButton>(),
        BoardScreen.Slots => SlotsButtons(),
        BoardScreen.Settings => SettingsButtons,
        BoardScreen.HandTracking => HandTrackingButtons,
        _ => AppButtons
    };
    public IReadOnlyList<string> HoveredButtonIds { get; private set; } = Array.Empty<string>();

    public BoardNavigation? Update(IReadOnlyList<BoardHandSample> hands,
        DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hands);
        HoveredButtonIds = Array.Empty<string>();
        FingerSelectionFeedback = Array.Empty<BoardFingerSelectionFeedback>();
        if (_lastNow is { } previousNow && now < previousNow)
        {
            PauseFingerSelection();
            return null;
        }
        _lastNow = now;
        AdvanceBlackjackPresentation(now);
        AdvanceMonopolyPresentation(now);
        AdvanceGlobeDrawer(now);
        AdvanceSlots(now);
        if (frameTime > now || now - frameTime > ObservationLifetime ||
            frameTime <= _ignoreFramesThrough ||
            (_lastFrameTime is { } previousFrame && frameTime <= previousFrame))
        {
            PauseFingerSelection();
            return null;
        }
        _lastFrameTime = frameTime;

        // Hold-to-repeat buttons respond only to held caption evidence.
        var buttons = Buttons.Where(button => !button.IsHold).ToArray();
        var fingerTargets = FingerTargets(buttons);
        FingerSelectionCandidate? fingerSelection = UpdateFingerSelection(hands, fingerTargets, frameTime);
        HoveredButtonIds = fingerTargets.Where(button => button.Enabled && hands.Any(hand =>
                (hand.TrackingId > 0 && hand.FourFingersExtended
                    ? FingerHoverTarget(hand, fingerTargets)?.Id == button.Id
                    : button.Id != PhotoCopyShutter.Id && SelectionIsCurrent(hand, frameTime) && button.Bounds.Contains(hand.U, hand.V))))
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
            var result = new BoardNavigation(Screen, selected.Destination, selected.Id)
                { TrackingId = hand.TrackingId, Gesture = BoardSelectionGesture.Pinch };
            if (!SelectButton(selected, now)) continue;
            return result with { Current = Screen };
        }
        if (fingerSelection is not null)
        {
            var selected = fingerSelection.Button;
            var result = new BoardNavigation(Screen, selected.Destination, selected.Id)
                { TrackingId = fingerSelection.TrackingId, Gesture = BoardSelectionGesture.IndexSeparation };
            if (SelectButton(selected, now))
            {
                MarkFingerSelection(fingerSelection, frameTime);
                return result with { Current = Screen };
            }
        }
        return null;
    }

    public void ShowMenu(DateTimeOffset? now = null) => Show(BoardScreen.Menu, now ?? DateTimeOffset.UtcNow);
    public void ShowHandTrackingTest(DateTimeOffset? now = null) => Show(BoardScreen.HandTracking, now ?? DateTimeOffset.UtcNow);
    public void ShowPhotoCopy(DateTimeOffset? now = null) => Show(BoardScreen.PhotoCopy, now ?? DateTimeOffset.UtcNow);
    public void ShowPaint(DateTimeOffset? now = null) => Show(BoardScreen.Paint, now ?? DateTimeOffset.UtcNow);
    public void ShowBlackjack(DateTimeOffset? now = null) => Show(BoardScreen.Blackjack, now ?? DateTimeOffset.UtcNow);
    public void ShowMedia(DateTimeOffset? now = null) => Show(BoardScreen.Media, now ?? DateTimeOffset.UtcNow);
    public void ShowSettings(DateTimeOffset? now = null) => Show(BoardScreen.Settings, now ?? DateTimeOffset.UtcNow);

    /// <summary>Clear hover and reject observations/pulses that predate a camera or calibration reset.</summary>
    public void ResetInput(DateTimeOffset now)
    {
        ClearBlackjackPresentationHold();
        AdvanceBlackjackPresentation(now);
        ClearMonopolyPresentationHold();
        ClearMonopolyDrawerUi();
        AdvanceMonopolyPresentation(now);
        ClearGlobeDrawerUi();
        AdvanceGlobeDrawer(now);
        ClearHolds();
        HoveredButtonIds = Array.Empty<string>();
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        _lastFrameTime = null;
        _lastNow = now;
        InvalidateFingerSelection(now);
        // Keep the high-water mark: clearing visual state must never replay a pinch.
    }

    private void Show(BoardScreen screen, DateTimeOffset now)
    {
        ClearBlackjackPresentationHold();
        AdvanceBlackjackPresentation(now);
        ClearMonopolyPresentationHold();
        ClearMonopolyDrawerUi();
        AdvanceMonopolyPresentation(now);
        ClearGlobeDrawerUi();
        AdvanceGlobeDrawer(now);
        if (screen == BoardScreen.Globe) _globe.Start(now);
        ClearHolds();
        Screen = screen;
        Revision++;
        HoveredButtonIds = Array.Empty<string>();
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        InvalidateFingerSelection(now);
        BoardOpened?.Invoke(screen);
    }

    private bool SelectionIsCurrent(BoardHandSample hand, DateTimeOffset frameTime) =>
        hand.SelectionFrameTime is not { } selectionTime ||
        selectionTime > _ignoreSelectionsThrough && selectionTime <= frameTime &&
        frameTime - selectionTime <= SelectionLifetime;

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;
}
