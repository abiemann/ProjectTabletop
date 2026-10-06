namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private static readonly IReadOnlyList<BoardButton> WaterGardenButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("menu", "Exit", new(.06, .85, .26, .105), BoardScreen.Menu,
            Hold: BoardButtonHold.Once),
        new BoardButton("water-garden-calm", "Calm Water", new(.58, .85, .36, .105), BoardScreen.WaterGarden,
            Hold: BoardButtonHold.Once)
    });

    /// <summary>Increments on opening Water Garden and each accepted Calm Water action.
    /// The renderer consumes this revision to clear its simulation without navigating.</summary>
    public long WaterGardenResetRevision { get; private set; }

    public void ShowWaterGarden(DateTimeOffset? now = null) =>
        Show(BoardScreen.WaterGarden, now ?? MonotonicClock.UtcNow);
}
