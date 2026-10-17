namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    public static readonly TimeSpan PaintDrawerOpeningDuration = BottomDrawerOpeningDuration;
    private static readonly IReadOnlyList<BoardButton> PaintDrawerButtons = Array.AsReadOnly(new[]
    {
        new BoardButton("menu", "EXIT", new(.20, .87, .155, .12), BoardScreen.Menu,
            Hold: BoardButtonHold.Once),
        new BoardButton("paint-reset", "RESET", new(.365, .87, .155, .12), BoardScreen.Paint,
            Hold: BoardButtonHold.Once),
        new BoardButton("paint-save", "SAVE", new(.53, .87, .155, .12), BoardScreen.Paint,
            Hold: BoardButtonHold.Once)
    });
    private bool _paintSaveEnabled;

    public bool PaintDrawerOpen => Screen == BoardScreen.Paint && _paintDrawer.Open;
    public DateTimeOffset? PaintDrawerOpenedAt => PaintDrawerOpen ? _paintDrawer.OpenedAt : null;

    /// <summary>Counts accepted RESET actions. The renderer clears its canvas
    /// when this revision advances, without navigation or closing the drawer.</summary>
    public long PaintResetRevision { get; private set; }

    public double GetPaintDrawerProgress(DateTimeOffset now) =>
        BottomDrawerProgress(_paintDrawer, BoardScreen.Paint, now);

    public bool TickPaint(DateTimeOffset now) => AdvanceBottomDrawer(_paintDrawer, BoardScreen.Paint, now);

    /// <summary>
    /// The renderer enables Save when an in-memory painting is ready to export.
    /// Changes require fresh Save caption evidence without interrupting Exit.
    /// </summary>
    public bool PaintSaveEnabled
    {
        get => _paintSaveEnabled;
        set
        {
            if (_paintSaveEnabled == value) return;
            _paintSaveEnabled = value;
            HoveredButtonIds = HoveredButtonIds.Where(id => id != "paint-save").ToArray();
            FingerSelectionFeedback = FingerSelectionFeedback.Where(item => item.ButtonId != "paint-save").ToArray();
            foreach (var track in _fingerTracks.Values)
                if (track.TargetId == "paint-save") track.Clear();
            ResetHoldCaptionEvidence(["paint-save"], _holdCaptionObservedAt);
        }
    }

    private IReadOnlyList<BoardButton> CurrentPaintButtons()
    {
        if (!PaintDrawerOpen) return Array.AsReadOnly(new[]
        {
            new BoardButton("paint-drawer-open", "^", BottomDrawerHandleBounds, BoardScreen.Paint,
                Hold: BoardButtonHold.Once)
        });
        var buttons = new List<BoardButton>
        {
            new("paint-drawer-close", "v", BottomDrawerHandleBounds, BoardScreen.Paint,
                Hold: BoardButtonHold.Once)
        };
        buttons.AddRange(PaintDrawerButtons.Select(button => button with
        {
            Enabled = _paintDrawer.OpeningReady && (button.Id != "paint-save" || PaintSaveEnabled)
        }));
        return buttons.AsReadOnly();
    }
}
