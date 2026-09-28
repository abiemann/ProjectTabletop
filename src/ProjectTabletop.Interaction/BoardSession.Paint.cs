namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private bool _paintSaveEnabled;

    /// <summary>
    /// The renderer enables Save when an in-memory painting is ready to export.
    /// Changes require fresh Save gesture evidence without interrupting Exit.
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
        }
    }

    private IReadOnlyList<BoardButton> CurrentPaintButtons() => Array.AsReadOnly(PaintButtons.Select(button =>
        button.Id == "paint-save" ? button with { Enabled = PaintSaveEnabled } : button).ToArray());
}
