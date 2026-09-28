using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static void DrawPhotoCopyButton(CanvasDrawingSession ds, BoardButton button, bool hovered,
        CanvasTextFormat small, IReadOnlyList<BoardFingerSelectionFeedback> feedback)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        DrawButtonSurface(ds, rect, hovered && button.Enabled);
        using var text = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = 29,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        var color = button.Enabled ? AppPalette.ButtonText : AppPalette.MutedText;
        ds.DrawText(button.Label, new Rect(rect.X + 8, rect.Y + 8,
            rect.Width - 16, 63), color, text);
        DrawButtonFingerSelectionFeedback(ds, button, feedback, AppPalette.IndicatorOn, showCaption: true);
    }
}
