namespace ProjectTabletop.Interaction;

/// <summary>The requested output action; the application owns capture, saving and countdown timing.</summary>
public enum PhotoCopyAction { Swirl, Copy, TimedCopy, Save }

public sealed partial class BoardSession
{
    /// <summary>The normalized object area between the Photo Copy title and controls.</summary>
    public static BoardRect PhotoCopyShutterBounds => new(.01, .06, .98, .66);

    private static readonly BoardButton PhotoCopyShutter =
        new("photo-shutter", "Capture object", PhotoCopyShutterBounds, BoardScreen.PhotoCopy);
    private bool _photoCopyShutterEnabled;
    private bool _photoCopyHasSwirl;

    /// <summary>
    /// Whether a captured swirl is on the board. Its Swirl control becomes Clear;
    /// Copy becomes Save to save the existing swirl pattern from memory.
    /// </summary>
    public bool PhotoCopyHasSwirl
    {
        get => _photoCopyHasSwirl;
        set
        {
            if (_photoCopyHasSwirl == value) return;
            _photoCopyHasSwirl = value;
            ClearPhotoCopyGestureEvidence(includeResultControls: true);
            // Both identities at a changed place need a fresh clear caption,
            // even when a result appears and disappears between camera frames.
            // The shared reset preserves a place already spent by a held press.
            ResetHoldCaptionEvidence(["photo-swirl", "photo-copy-once", "capture-again", "photo-save"]);
        }
    }

    /// <summary>Maps explicit capture controls and the legacy field shutter to their output action.</summary>
    public static bool TryGetPhotoCopyAction(string buttonId, out PhotoCopyAction action)
    {
        action = buttonId switch
        {
            "photo-copy-once" => PhotoCopyAction.Copy,
            "photo-save" => PhotoCopyAction.Save,
            _ => PhotoCopyAction.Swirl
        };
        return buttonId is "photo-swirl" or "photo-copy-once" or "photo-shutter" or "photo-save";
    }

    /// <summary>
    /// Enables the explicit capture buttons and index-separation shutter in the
    /// object area only when capture is ready. The field shutter is not a rendered
    /// button or a pinch target. Changing readiness requires fresh gesture and
    /// clear-caption hold evidence for capture actions, without interrupting Exit,
    /// Clear or Save. Bottom controls use single-action one-second caption holds.
    /// </summary>
    public bool PhotoCopyShutterEnabled
    {
        get => _photoCopyShutterEnabled;
        set
        {
            if (_photoCopyShutterEnabled == value) return;
            _photoCopyShutterEnabled = value;
            ClearPhotoCopyGestureEvidence(includeResultControls: false);
            ResetHoldCaptionEvidence(["photo-swirl", "photo-copy-once"]);
        }
    }

    private void ClearPhotoCopyGestureEvidence(bool includeResultControls)
    {
        bool Affected(string id) => TryGetPhotoCopyAction(id, out var action) &&
            (includeResultControls || action != PhotoCopyAction.Save) || includeResultControls && id == "capture-again";
        HoveredButtonIds = HoveredButtonIds.Where(id => !Affected(id)).ToArray();
        FingerSelectionFeedback = FingerSelectionFeedback.Where(item => !Affected(item.ButtonId)).ToArray();
        foreach (var track in _fingerTracks.Values)
            if (track.TargetId is { } id && Affected(id)) track.Clear();
    }

    private IReadOnlyList<BoardButton> CurrentPhotoCopyButtons() =>
        Array.AsReadOnly(PhotoCopyButtons.Select(button =>
            PhotoCopyHasSwirl && button.Id == "photo-swirl"
                ? button with { Id = "capture-again", Label = "Clear" }
                : PhotoCopyHasSwirl && button.Id == "photo-copy-once"
                    ? button with { Id = "photo-save", Label = "Save", Enabled = true }
                : TryGetPhotoCopyAction(button.Id, out _)
                    ? button with { Enabled = PhotoCopyShutterEnabled && !PhotoCopyHasSwirl } : button).ToArray());

    private IReadOnlyList<BoardButton> FingerTargets(IReadOnlyList<BoardButton> buttons) =>
        Screen == BoardScreen.PhotoCopy && PhotoCopyShutterEnabled && !PhotoCopyHasSwirl
            ? Array.AsReadOnly(buttons.Concat([PhotoCopyShutter]).ToArray()) : buttons;
}
