namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public static readonly TimeSpan GlobeDrawerOpeningDuration = BottomDrawerOpeningDuration;
    private readonly GlobeState _globe;
    // The handle and its three actions share one bottom row, left of the
    // bottom-right credit. The wide, tall handle keeps opposite glass margins
    // for the camera's colour fit and clears Earth's settled 1.5× limb on
    // rectangular boards; a square board's limb reaches its upper-right corner.
    // Fingers on this edge row leave the wrist outside the camera view, so hand
    // tracking kept dropping mid-gesture. Every control here is a hold button:
    // the handle and Exit act once per long press, the zooms repeat.
    private static readonly IReadOnlyList<BoardButton> GlobeDrawerButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("globe-exit", "Exit", new(.20, .87, .155, .12), BoardScreen.Menu, Hold: BoardButtonHold.Once),
        new BoardButton("globe-zoom-out", "Zoom -", new(.365, .87, .155, .12), BoardScreen.Globe, Hold: BoardButtonHold.Repeat),
        new BoardButton("globe-zoom-in", "Zoom +", new(.53, .87, .155, .12), BoardScreen.Globe, Hold: BoardButtonHold.Repeat)
    });
    public bool GlobeDrawerOpen => Screen == BoardScreen.Globe && _globeDrawer.Open;
    public DateTimeOffset? GlobeDrawerOpenedAt => GlobeDrawerOpen ? _globeDrawer.OpenedAt : null;
    public double GlobeHomeRotationDegrees => _globe.HomeRotationDegrees;
    public double GlobeHomeLatitudeDegrees => _globe.HomeLatitudeDegrees;

    public void ShowGlobe(DateTimeOffset? now = null) => Show(BoardScreen.Globe, now ?? MonotonicClock.UtcNow);

    /// <summary>Returns a presentation frame without mutating input barriers or interaction revisions.</summary>
    public GlobeSnapshot GetGlobeSnapshot(DateTimeOffset now) => _globe.GetSnapshot(now);

    /// <summary>Samples the drawer's entrance using the same supplied clock as the Earth presentation.</summary>
    public double GetGlobeDrawerProgress(DateTimeOffset now) => BottomDrawerProgress(_globeDrawer, BoardScreen.Globe, now);

    /// <summary>Enables settled drawer controls once, rejecting observations made during its entrance.</summary>
    public bool TickGlobe(DateTimeOffset now) => AdvanceBottomDrawer(_globeDrawer, BoardScreen.Globe, now);

    private IReadOnlyList<BoardButton> CurrentGlobeButtons()
    {
        if (!GlobeDrawerOpen) return Array.AsReadOnly(new[]
        {
            new BoardButton("globe-drawer-open", "^", BottomDrawerHandleBounds, BoardScreen.Globe, Hold: BoardButtonHold.Once)
        });
        var buttons = new List<BoardButton>
        {
            new("globe-drawer-close", "v", BottomDrawerHandleBounds, BoardScreen.Globe, Hold: BoardButtonHold.Once)
        };
        buttons.AddRange(GlobeDrawerButtons.Select(button => button with
        {
            Enabled = _globeDrawer.OpeningReady && (button.Id == "globe-exit" || _globe.CanHandleAction(button.Id))
        }));
        return buttons.AsReadOnly();
    }

    private bool SelectGlobeButton(BoardButton button, DateTimeOffset now)
    {
        if (now < _globeDrawer.ObservedAt) return false;
        if (button.Id == "globe-drawer-open")
            return SelectBottomDrawer(_globeDrawer, BoardScreen.Globe, open: true, now);
        if (button.Id == "globe-drawer-close")
            return SelectBottomDrawer(_globeDrawer, BoardScreen.Globe, open: false, now);
        if (!GlobeDrawerOpen || !_globeDrawer.OpeningReady) return false;
        if (button.Id == "globe-exit")
        {
            Show(BoardScreen.Menu, now);
            return true;
        }
        if (button.Id is not ("globe-zoom-in" or "globe-zoom-out") || !_globe.HandleAction(button.Id, now)) return false;
        // Zoom repeats retain their hold; only a changed drawer resets caption
        // readiness and partial progress through BottomDrawerInputBarrier.
        BottomDrawerGestureBarrier(now);
        return true;
    }
}
