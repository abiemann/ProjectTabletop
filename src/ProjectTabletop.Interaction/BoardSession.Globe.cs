namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public static readonly TimeSpan GlobeDrawerOpeningDuration = TimeSpan.FromMilliseconds(300);
    private readonly GlobeState _globe;
    private bool _globeDrawerOpen;
    private DateTimeOffset? _globeDrawerOpenedAt;
    private bool _globeDrawerOpeningReady;
    private DateTimeOffset _globeDrawerObservedAt = DateTimeOffset.MinValue;
    // The handle and its three actions share one bottom row, left of the
    // bottom-right credit. The wide, tall handle keeps opposite glass margins
    // for the camera's colour fit and clears Earth's settled 1.5× limb on
    // rectangular boards; a square board's limb reaches its upper-right corner.
    private static readonly BoardRect GlobeDrawerHandleBounds = new(.01, .87, .18, .12);
    private static readonly IReadOnlyList<BoardButton> GlobeDrawerButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("globe-exit", "Exit", new(.20, .87, .155, .12), BoardScreen.Menu),
        new BoardButton("globe-zoom-out", "Zoom -", new(.365, .87, .155, .12), BoardScreen.Globe, HoldToRepeat: true),
        new BoardButton("globe-zoom-in", "Zoom +", new(.53, .87, .155, .12), BoardScreen.Globe, HoldToRepeat: true)
    });
    public bool GlobeDrawerOpen => Screen == BoardScreen.Globe && _globeDrawerOpen;
    public DateTimeOffset? GlobeDrawerOpenedAt => GlobeDrawerOpen ? _globeDrawerOpenedAt : null;
    public double GlobeHomeRotationDegrees => _globe.HomeRotationDegrees;

    public void ShowGlobe(DateTimeOffset? now = null) => Show(BoardScreen.Globe, now ?? DateTimeOffset.UtcNow);

    /// <summary>Returns a presentation frame without mutating input barriers or interaction revisions.</summary>
    public GlobeSnapshot GetGlobeSnapshot(DateTimeOffset now) => _globe.GetSnapshot(now);

    /// <summary>Samples the drawer's entrance using the same supplied clock as the Earth presentation.</summary>
    public double GetGlobeDrawerProgress(DateTimeOffset now) => GlobeDrawerOpenedAt is { } opened
        ? _globeDrawerOpeningReady ? 1 : Math.Clamp((now - opened).TotalMilliseconds /
            GlobeDrawerOpeningDuration.TotalMilliseconds, 0, 1) : 0;

    /// <summary>Enables settled drawer controls once, rejecting observations made during its entrance.</summary>
    public bool TickGlobe(DateTimeOffset now) => AdvanceGlobeDrawer(now);

    private IReadOnlyList<BoardButton> CurrentGlobeButtons()
    {
        if (!GlobeDrawerOpen) return Array.AsReadOnly(new[]
        {
            new BoardButton("globe-drawer-open", "^", GlobeDrawerHandleBounds, BoardScreen.Globe)
        });
        var buttons = new List<BoardButton>
        {
            new("globe-drawer-close", "v", GlobeDrawerHandleBounds, BoardScreen.Globe)
        };
        buttons.AddRange(GlobeDrawerButtons.Select(button => button with
        {
            Enabled = _globeDrawerOpeningReady && (button.Id == "globe-exit" || _globe.CanHandleAction(button.Id))
        }));
        return buttons.AsReadOnly();
    }

    private bool SelectGlobeButton(BoardButton button, DateTimeOffset now)
    {
        if (now < _globeDrawerObservedAt) return false;
        if (button.Id == "globe-drawer-open")
        {
            if (GlobeDrawerOpen) return false;
            _globeDrawerOpen = true;
            _globeDrawerOpenedAt = now;
            _globeDrawerOpeningReady = false;
            GlobeInputBarrier(now);
            return true;
        }
        if (button.Id == "globe-drawer-close")
        {
            if (!GlobeDrawerOpen) return false;
            ClearGlobeDrawerUi();
            GlobeInputBarrier(now);
            return true;
        }
        if (!GlobeDrawerOpen || !_globeDrawerOpeningReady) return false;
        if (button.Id == "globe-exit")
        {
            Show(BoardScreen.Menu, now);
            return true;
        }
        if (button.Id is not ("globe-zoom-in" or "globe-zoom-out") || !_globe.HandleAction(button.Id, now)) return false;
        GlobeInputBarrier(now);
        return true;
    }

    private bool AdvanceGlobeDrawer(DateTimeOffset now)
    {
        _globeDrawerObservedAt = Later(_globeDrawerObservedAt, now);
        if (!GlobeDrawerOpen || _globeDrawerOpeningReady || _globeDrawerOpenedAt is not { } opened ||
            now < opened + GlobeDrawerOpeningDuration) return false;
        _globeDrawerOpeningReady = true;
        // A moving control cannot acquire a selection that executes as it settles.
        // Reject delayed camera frames, old pulse origins and retained pointing anchors.
        GlobeInputBarrier(opened + GlobeDrawerOpeningDuration);
        return true;
    }

    private void ClearGlobeDrawerUi()
    {
        if (_globeDrawerOpen) Revision++;
        _globeDrawerOpen = false;
        _globeDrawerOpenedAt = null;
        _globeDrawerOpeningReady = false;
    }

    private void GlobeInputBarrier(DateTimeOffset now)
    {
        Revision++;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
    }
}
