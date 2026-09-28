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
        using var text = PhotoCopyButtonTextFormat();
        var color = button.Enabled ? AppPalette.ButtonText : AppPalette.MutedText;
        ds.DrawText(button.Label, PhotoCopyButtonTextRectangle(button), color, text);
        DrawButtonFingerSelectionFeedback(ds, button, feedback, AppPalette.IndicatorOn, showCaption: true);
    }

    private static CanvasTextFormat PhotoCopyButtonTextFormat() => new()
        {
            FontFamily = "Segoe UI", FontSize = 29,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
    private static Rect PhotoCopyButtonTextRectangle(BoardButton button)
    {
        var b = button.Bounds;
        return new(b.X * BoardSurfaceSize + 8, b.Y * BoardSurfaceSize + 8,
            b.Width * BoardSurfaceSize - 16, 63);
    }
}
