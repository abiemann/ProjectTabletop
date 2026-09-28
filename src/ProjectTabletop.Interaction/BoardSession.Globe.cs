namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private readonly GlobeState _globe;
    private static readonly IReadOnlyList<BoardButton> GlobeButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("globe-exit", "Exit", new(.025, .885, .12, .072), BoardScreen.Menu),
        new BoardButton("globe-zoom-out", "Zoom -", new(.16, .885, .18, .072), BoardScreen.Globe),
        new BoardButton("globe-zoom-in", "Zoom +", new(.355, .885, .18, .072), BoardScreen.Globe),
        new BoardButton("globe-rotate-left", "< Rotate", new(.55, .885, .205, .072), BoardScreen.Globe),
        new BoardButton("globe-rotate-right", "Rotate >", new(.77, .885, .205, .072), BoardScreen.Globe)
    });

    public void ShowGlobe(DateTimeOffset? now = null) => Show(BoardScreen.Globe, now ?? DateTimeOffset.UtcNow);

    /// <summary>Returns a presentation frame without mutating input barriers or interaction revisions.</summary>
    public GlobeSnapshot GetGlobeSnapshot(DateTimeOffset now) => _globe.GetSnapshot(now);

    private IReadOnlyList<BoardButton> CurrentGlobeButtons() => Array.AsReadOnly(GlobeButtons.Select(button =>
        button.Id == "globe-exit" ? button : button with { Enabled = _globe.CanHandleAction(button.Id) }).ToArray());

    private bool SelectGlobeButton(BoardButton button, DateTimeOffset now)
    {
        if (button.Id == "globe-exit")
        {
            Show(BoardScreen.Menu, now);
            return true;
        }
        if (!_globe.HandleAction(button.Id, now)) return false;
        Revision++;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
        return true;
    }
}
