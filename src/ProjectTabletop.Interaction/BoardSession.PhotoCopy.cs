namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    /// <summary>The normalized object area below the Photo Copy controls.</summary>
    public static BoardRect PhotoCopyShutterBounds => new(.01, .23, .98, .76);

    private static readonly BoardButton PhotoCopyShutter =
        new("photo-shutter", "Capture object", PhotoCopyShutterBounds, BoardScreen.PhotoCopy);
    private static readonly Lazy<IReadOnlyList<BoardButton>> PhotoCopyFingerTargets =
        new(() => Array.AsReadOnly(PhotoCopyButtons.Concat([PhotoCopyShutter]).ToArray()));
    private bool _photoCopyShutterEnabled;

    /// <summary>
    /// Allow an index-separation shutter gesture in the object area only when
    /// capture is ready. The shutter is not a rendered button or a pinch target.
    /// Changing readiness requires new together-to-separated evidence.
    /// </summary>
    public bool PhotoCopyShutterEnabled
    {
        get => _photoCopyShutterEnabled;
        set
        {
            if (_photoCopyShutterEnabled == value) return;
            _photoCopyShutterEnabled = value;
            HoveredButtonIds = HoveredButtonIds.Where(id => id != PhotoCopyShutter.Id).ToArray();
            FingerSelectionFeedback = FingerSelectionFeedback.Where(item => item.ButtonId != PhotoCopyShutter.Id).ToArray();
            foreach (var track in _fingerTracks.Values)
                if (track.TargetId == PhotoCopyShutter.Id) track.Clear();
        }
    }

    private IReadOnlyList<BoardButton> FingerTargets(IReadOnlyList<BoardButton> buttons) =>
        Screen == BoardScreen.PhotoCopy && PhotoCopyShutterEnabled ? PhotoCopyFingerTargets.Value : buttons;
}
