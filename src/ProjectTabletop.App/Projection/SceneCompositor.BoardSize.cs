using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private string? _estimatedBoardSizeCaption;
    private string? _estimatedBoardSizeSource;

    /// <summary>
    /// Sets optional board dimensions displayed only on the Hand-Tracking board. The caller
    /// supplies a validated optical estimate or user-measured dimensions for the current scan.
    /// Clearing the board clip forgets both the dimensions and their source; values supplied
    /// without a current clip are ignored. The annotation never changes projection geometry.
    /// </summary>
    public void SetEstimatedBoardSize(BoardSizeEstimate? size, bool measured = false)
    {
        if (size is not null && (!double.IsFinite(size.ShortSideCentimeters) ||
            !double.IsFinite(size.LongSideCentimeters) || size.ShortSideCentimeters <= 0 ||
            size.LongSideCentimeters < size.ShortSideCentimeters))
            throw new ArgumentOutOfRangeException(nameof(size), "Board sides must be finite, positive and ordered short to long.");
        lock (_gate)
        {
            string? caption = size is not null && _boardMediaClip is not null
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{(measured ? "Measured" : "Estimated")} board: {size.ShortSideCentimeters:F1} × {size.LongSideCentimeters:F1} cm")
                : null;
            string? source = caption is null ? null : measured
                ? "User-supplied board dimensions" : "From lens height / throw ratio";
            if (_estimatedBoardSizeCaption == caption && _estimatedBoardSizeSource == source) return;
            _estimatedBoardSizeCaption = caption;
            _estimatedBoardSizeSource = source;
            if (_boardSession.Screen == BoardScreen.HandTracking) _renderedBoardState = null;
        }
    }

    private void DrawEstimatedBoardSize(CanvasDrawingSession ds, CanvasTextFormat body, CanvasTextFormat small)
    {
        if (_estimatedBoardSizeCaption is not { } caption) return;
        DrawGlassPanel(ds, new Rect(80, 866, 840, 84));
        ds.DrawText(caption, 103, 876, AppPalette.Text, body);
        ds.DrawText(_estimatedBoardSizeSource ?? string.Empty, 104, 912, AppPalette.MutedText, small);
    }
}
