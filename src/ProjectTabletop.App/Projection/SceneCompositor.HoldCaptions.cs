using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // All long-press words share Water Garden's type and physical size.
    // The same format and uppercase caption drive drawing and camera ink bounds.
    private static CanvasTextFormat HoldButtonCaptionFormat() => new()
    {
        FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 28,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static void DrawHoldButtonCaption(CanvasDrawingSession ds, string caption, Rect bounds,
        Color color, double aspect)
    {
        using var format = HoldButtonCaptionFormat();
        DrawBoardAspectText(ds, caption.ToUpperInvariant(), bounds, color, format, aspect);
    }

    private HandTrackingBounds HoldButtonCaptionRegion(CanvasDevice device, BoardButton button, Rect bounds,
        string? caption = null)
    {
        using var format = HoldButtonCaptionFormat();
        return BoardAspectButtonTextRegion(device, button with { Label = (caption ?? button.Label).ToUpperInvariant() },
            format, bounds);
    }
}
