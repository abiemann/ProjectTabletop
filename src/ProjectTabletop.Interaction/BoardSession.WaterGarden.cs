namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    // The water simulation starts with ten ducks and holds at most twenty.
    private const int WaterGardenAdditionalDuckCapacity = 10;
    private int _waterGardenAddedDucks;
    public static readonly TimeSpan WaterGardenDrawerOpeningDuration = BottomDrawerOpeningDuration;
    private static readonly IReadOnlyList<BoardButton> WaterGardenDrawerButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("water-garden-exit", "EXIT", new(.20, .87, .155, .12), BoardScreen.Menu,
            Hold: BoardButtonHold.Once),
        new BoardButton("water-garden-reset", "RESET", new(.365, .87, .155, .12), BoardScreen.WaterGarden,
            Hold: BoardButtonHold.Once),
        new BoardButton("water-garden-duck-add", "DUCK+", new(.53, .87, .155, .12), BoardScreen.WaterGarden,
            Hold: BoardButtonHold.Repeat)
    });

    public bool WaterGardenDrawerOpen => Screen == BoardScreen.WaterGarden && _waterGardenDrawer.Open;
    public DateTimeOffset? WaterGardenDrawerOpenedAt => WaterGardenDrawerOpen ? _waterGardenDrawer.OpenedAt : null;
    public bool WaterGardenStickPresent { get; private set; }

    /// <summary>Disarms the bottom controls while a fresh, confirmed stick tip is
    /// over visible water. A transition cancels holds and requires new caption
    /// evidence without moving the drawer or resetting the pond.</summary>
    public bool SetWaterGardenStickPresent(bool present, DateTimeOffset now)
    {
        present &= Screen == BoardScreen.WaterGarden;
        if (WaterGardenStickPresent == present) return false;
        WaterGardenStickPresent = present;
        if (Screen == BoardScreen.WaterGarden)
            BottomDrawerInputBarrier(_waterGardenDrawer, now);
        return true;
    }

    /// <summary>Increments on opening Water Garden and each accepted Reset action.
    /// The renderer consumes this revision to clear its simulation without navigating.</summary>
    public long WaterGardenResetRevision { get; private set; }

    /// <summary>Counts accepted Duck+ actions without restarting the pond.</summary>
    public long WaterGardenDuckAddRevision { get; private set; }

    /// <summary>Additional ducks requested since the most recent Water Garden reset.</summary>
    public int WaterGardenAddedDuckCount => _waterGardenAddedDucks;

    public double GetWaterGardenDrawerProgress(DateTimeOffset now) =>
        BottomDrawerProgress(_waterGardenDrawer, BoardScreen.WaterGarden, now);

    public bool TickWaterGarden(DateTimeOffset now) =>
        AdvanceBottomDrawer(_waterGardenDrawer, BoardScreen.WaterGarden, now);

    private IReadOnlyList<BoardButton> CurrentWaterGardenButtons()
    {
        if (!WaterGardenDrawerOpen) return Array.AsReadOnly(new[]
        {
            new BoardButton("water-drawer-open", "^", BottomDrawerHandleBounds, BoardScreen.WaterGarden,
                Enabled: !WaterGardenStickPresent, Hold: BoardButtonHold.Once)
        });
        var buttons = new List<BoardButton>
        {
            new("water-drawer-close", "v", BottomDrawerHandleBounds, BoardScreen.WaterGarden,
                Enabled: !WaterGardenStickPresent, Hold: BoardButtonHold.Once)
        };
        buttons.AddRange(WaterGardenDrawerButtons.Select(button => button with
        {
            Enabled = !WaterGardenStickPresent && _waterGardenDrawer.OpeningReady &&
                (button.Id != "water-garden-duck-add" || _waterGardenAddedDucks < WaterGardenAdditionalDuckCapacity)
        }));
        return buttons.AsReadOnly();
    }

    private bool SelectWaterGardenButton(BoardButton button, DateTimeOffset now)
    {
        if (WaterGardenStickPresent || now < _waterGardenDrawer.ObservedAt) return false;
        if (button.Id == "water-drawer-open")
            return SelectBottomDrawer(_waterGardenDrawer, BoardScreen.WaterGarden, open: true, now);
        if (button.Id == "water-drawer-close")
            return SelectBottomDrawer(_waterGardenDrawer, BoardScreen.WaterGarden, open: false, now);
        if (!WaterGardenDrawerOpen || !_waterGardenDrawer.OpeningReady) return false;
        switch (button.Id)
        {
            case "water-garden-exit":
                Show(BoardScreen.Menu, now);
                return true;
            case "water-garden-reset":
                RequestWaterGardenReset();
                break;
            case "water-garden-duck-add":
                if (_waterGardenAddedDucks >= WaterGardenAdditionalDuckCapacity) return false;
                _waterGardenAddedDucks++;
                WaterGardenDuckAddRevision++;
                break;
            default:
                return false;
        }
        BottomDrawerGestureBarrier(now);
        return true;
    }

    private void RequestWaterGardenReset()
    {
        WaterGardenResetRevision++;
        _waterGardenAddedDucks = 0;
        WaterGardenStickPresent = false;
    }

    public void ShowWaterGarden(DateTimeOffset? now = null) =>
        Show(BoardScreen.WaterGarden, now ?? MonotonicClock.UtcNow);
}
