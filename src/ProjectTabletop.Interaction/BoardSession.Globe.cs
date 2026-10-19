namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public static readonly TimeSpan GlobeDrawerOpeningDuration = BottomDrawerOpeningDuration;
    private readonly GlobeState _globe;
    private bool _globeModelExpanded;
    // The model choices share MODEL's size and begin directly above it. Every
    // button uses caption holds; zoom and pitch are independent dotted rails.
    private static readonly IReadOnlyList<BoardButton> GlobeDrawerButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("globe-exit", "Exit", new(.20, .87, .155, .12), BoardScreen.Menu, Hold: BoardButtonHold.Once),
        new BoardButton("globe-model", "MODEL", new(.365, .87, .155, .12), BoardScreen.Globe, Hold: BoardButtonHold.Once),
        new BoardButton("globe-save", "SAVE", new(.53, .87, .155, .12), BoardScreen.Globe, Hold: BoardButtonHold.Once)
    });
    private static readonly IReadOnlyList<BoardButton> GlobeModelButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("globe-model-clouds", "CLOUDS", new(.365, .735, .155, .12), BoardScreen.Globe, Hold: BoardButtonHold.Once),
        new BoardButton("globe-model-no-clouds", "NO CLOUDS", new(.53, .735, .155, .12), BoardScreen.Globe, Hold: BoardButtonHold.Once),
        new BoardButton("globe-model-night", "NIGHT", new(.695, .735, .155, .12), BoardScreen.Globe, Hold: BoardButtonHold.Once)
    });
    public bool GlobeDrawerOpen => Screen == BoardScreen.Globe && _globeDrawer.Open;
    public bool GlobeModelExpanded => Screen == BoardScreen.Globe && _globeModelExpanded;
    public bool GlobeSaveEnabled { get; set; } = true;
    public GlobeModel SelectedGlobeModel => _globe.Model;
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

    /// <summary>Stationary rail projection boundary; changes when its drawer or visibility changes, not on zoom.</summary>
    public DateTimeOffset GlobePitchReferenceNotBefore => Later(_globeDrawer.InputReadyAfter, _controlsInputReadyAfter);

    /// <summary>Whether the stationary rail is fully visible, independently of action-frame barriers.</summary>
    public bool GlobePitchAvailable(DateTimeOffset now) =>
        GlobeDrawerOpen && _globeDrawer.OpeningReady && ButtonsVisible && _controlFadeStartedAt is null &&
        _globe.HasFinishedEntrance(now);

    /// <summary>Only fresh observations of the fully displayed pitch rail can steer Earth.</summary>
    public bool GlobePitchInputReady(DateTimeOffset frameTime) =>
        GlobePitchAvailable(frameTime) && frameTime > _globeDrawer.InputReadyAfter &&
        frameTime > _controlsInputReadyAfter && frameTime > _ignoreFramesThrough;

    /// <summary>Retains the chosen pitch on release, closing the drawer, or hiding controls.</summary>
    public bool SetGlobePitchPosition(double position, DateTimeOffset frameTime) =>
        GlobePitchInputReady(frameTime) && _globe.SetPitchPosition(position, frameTime);

    /// <summary>The zoom rail shares the pitch rail's settled, fresh-frame input policy.</summary>
    public bool SetGlobeZoomPosition(double position, DateTimeOffset frameTime) =>
        GlobePitchInputReady(frameTime) && _globe.SetZoomPosition(position, frameTime);

    private void InitializeGlobe(DateTimeOffset now)
    {
        _globeModelExpanded = false;
        _globe.Start(now);
    }

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
            Enabled = _globeDrawer.OpeningReady && (button.Id != "globe-save" || GlobeSaveEnabled)
        }));
        if (_globeModelExpanded)
            buttons.AddRange(GlobeModelButtons.Select(button => button with { Enabled = _globeDrawer.OpeningReady }));
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
        if (button.Id == "globe-model")
        {
            _globeModelExpanded = !_globeModelExpanded;
            // New captions require a clear observation after they appear.
            BottomDrawerInputBarrier(_globeDrawer, now);
            return true;
        }
        if (button.Id == "globe-save")
        {
            BottomDrawerGestureBarrier(now);
            return true;
        }
        GlobeModel? model = button.Id switch
        {
            "globe-model-clouds" => GlobeModel.Clouds,
            "globe-model-no-clouds" => GlobeModel.NoClouds,
            "globe-model-night" => GlobeModel.Night,
            _ => null
        };
        if (!_globeModelExpanded || model is null || !_globe.SelectModel(model.Value, now)) return false;
        BottomDrawerGestureBarrier(now);
        return true;
    }
}
