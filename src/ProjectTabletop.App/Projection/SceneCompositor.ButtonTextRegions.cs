using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private HandTrackingBounds BoardButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        if (_boardSession.Screen == BoardScreen.Paint) return PaintButtonTextRegion(device, button);
        if (_boardSession.Screen == BoardScreen.Monopoly)
        {
            var rectangle = MonopolyButtonTextRectangle(button);
            using var format = MonopolyButtonTextFormat(button);
            using var layout = new CanvasTextLayout(device, button.Label, format,
                (float)rectangle.Width, (float)rectangle.Height);
            return ButtonInkRegion(button, layout.DrawBounds, rectangle.X, rectangle.Y);
        }
        if (_boardSession.Screen == BoardScreen.Blackjack)
        {
            var spec = CasinoButtonTextSpec(button);
            using var format = CasinoTextFormat(spec.Size, "Bahnschrift", true);
            using var layout = new CanvasTextLayout(device, spec.Caption, format,
                (float)spec.Bounds.Width, (float)spec.Bounds.Height);
            return ButtonInkRegion(button, layout.DrawBounds, spec.Bounds.X, spec.Bounds.Y);
        }
        if (_boardSession.Screen == BoardScreen.PhotoCopy)
        {
            var rectangle = PhotoCopyButtonTextRectangle(button);
            using var format = PhotoCopyButtonTextFormat();
            using var layout = new CanvasTextLayout(device, button.Label, format,
                (float)rectangle.Width, (float)rectangle.Height);
            return ButtonInkRegion(button, layout.DrawBounds, rectangle.X, rectangle.Y);
        }
        using var label = BoardButtonTextFormat();
        bool menu = _boardSession.Screen == BoardScreen.Menu;
        double x = button.Bounds.X * BoardSurfaceSize + (menu ? 32 : 33);
        double y = button.Bounds.Y * BoardSurfaceSize + (menu ? 52 : 27);
        using var textLayout = new CanvasTextLayout(device, button.Label, label,
            (float)(button.Bounds.Width * BoardSurfaceSize), (float)(button.Bounds.Height * BoardSurfaceSize));
        return ButtonInkRegion(button, textLayout.DrawBounds, x, y);
    }

    private static HandTrackingBounds ButtonInkRegion(BoardButton button, Rect ink, double x, double y)
    {
        var b = button.Bounds;
        // Four logical pixels absorb bounded camera registration/raster blur.
        // Keep the actual label inside the same opaque control interior.
        double left = Math.Max(b.X + .012, (x + ink.X - 4) / BoardSurfaceSize);
        double top = Math.Max(b.Y + .012, (y + ink.Y - 4) / BoardSurfaceSize);
        double right = Math.Min(b.X + b.Width - .012, (x + ink.Right + 4) / BoardSurfaceSize);
        double bottom = Math.Min(b.Y + b.Height - .012, (y + ink.Bottom + 4) / BoardSurfaceSize);
        return new(left, top, right - left, bottom - top);
    }
}
