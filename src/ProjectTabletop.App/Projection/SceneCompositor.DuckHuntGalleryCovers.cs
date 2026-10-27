using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static CanvasRenderTarget CreateGalleryCover(CanvasDevice device, CanvasBitmap scene,
        BoardRect panel, CanvasBitmap decoration, Rect source, double centerV = .5)
    {
        // The lower compartment is taller than its source photograph. Compose
        // at its displayed aspect so the engraving keeps its natural proportions.
        double heightInScene = DuckHuntGalleryLayout.SourceYToLayoutY(panel.Y + panel.Height) -
            DuckHuntGalleryLayout.SourceYToLayoutY(panel.Y);
        double panelAspect = panel.Width * scene.Size.Width / (heightInScene * scene.Size.Height);
        // Keep at least the engraving's native detail in this one-time cache.
        int width = (int)Math.Ceiling(Math.Max(panel.Width * scene.Size.Width,
            Math.Max(source.Width / .90, source.Height * panelAspect / .88)));
        int height = (int)Math.Ceiling(width / panelAspect);
        var result = new CanvasRenderTarget(device, width, height, 96);
        using var drawing = result.CreateDrawingSession();
        drawing.DrawImage(scene, new Rect(0, 0, width, height),
            new Rect(panel.X * scene.Size.Width, panel.Y * scene.Size.Height,
                panel.Width * scene.Size.Width, panel.Height * scene.Size.Height),
            1, CanvasImageInterpolation.HighQualityCubic);
        double scale = Math.Min(width * .90 / source.Width, height * .88 / source.Height);
        double artWidth = source.Width * scale, artHeight = source.Height * scale;
        drawing.DrawImage(decoration,
            new Rect((width - artWidth) / 2, height * centerV - artHeight / 2, artWidth, artHeight),
            source, 1, CanvasImageInterpolation.HighQualityCubic);
        return result;
    }
}
