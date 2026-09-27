namespace ProjectTabletop.Interaction;

/// <summary>The requested output action; the application owns capture and countdown timing.</summary>
public enum PhotoCopyAction { Swirl, Copy, TimedCopy }

public sealed partial class BoardSession
{
    /// <summary>The normalized object area below the Photo Copy controls.</summary>
    public static BoardRect PhotoCopyShutterBounds => new(.01, .23, .98, .76);

    private static readonly BoardButton PhotoCopyShutter =
        new("photo-shutter", "Capture object", PhotoCopyShutterBounds, BoardScreen.PhotoCopy);
    private bool _photoCopyShutterEnabled;

    /// <summary>Maps explicit capture controls and the legacy field shutter to their output action.</summary>
    public static bool TryGetPhotoCopyAction(string buttonId, out PhotoCopyAction action)
    {
        action = buttonId switch
        {
            "photo-copy-once" => PhotoCopyAction.Copy,
            "photo-copy-timer" => PhotoCopyAction.TimedCopy,
            _ => PhotoCopyAction.Swirl
        };
        return buttonId is "photo-swirl" or "photo-copy-once" or "photo-copy-timer" or "photo-shutter";
    }

    /// <summary>
    /// Enables the explicit capture buttons and index-separation shutter in the
    /// object area only when capture is ready. The field shutter is not a rendered
    /// button or a pinch target. Changing readiness requires fresh gesture evidence
    /// for capture actions, without interrupting Back to menu or Capture again.
    /// </summary>
    public bool PhotoCopyShutterEnabled
    {
        get => _photoCopyShutterEnabled;
        set
        {
            if (_photoCopyShutterEnabled == value) return;
            _photoCopyShutterEnabled = value;
            HoveredButtonIds = HoveredButtonIds.Where(id => !TryGetPhotoCopyAction(id, out _)).ToArray();
            FingerSelectionFeedback = FingerSelectionFeedback.Where(item => !TryGetPhotoCopyAction(item.ButtonId, out _)).ToArray();
            foreach (var track in _fingerTracks.Values)
                if (track.TargetId is { } id && TryGetPhotoCopyAction(id, out _)) track.Clear();
        }
    }

    private IReadOnlyList<BoardButton> CurrentPhotoCopyButtons() => PhotoCopyShutterEnabled
        ? PhotoCopyButtons
        : Array.AsReadOnly(PhotoCopyButtons.Select(button => TryGetPhotoCopyAction(button.Id, out _)
            ? button with { Enabled = false } : button).ToArray());

    private IReadOnlyList<BoardButton> FingerTargets(IReadOnlyList<BoardButton> buttons) =>
        Screen == BoardScreen.PhotoCopy && PhotoCopyShutterEnabled
            ? Array.AsReadOnly(buttons.Concat([PhotoCopyShutter]).ToArray()) : buttons;
}
