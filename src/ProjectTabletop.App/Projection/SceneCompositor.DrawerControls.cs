using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static void DrawBoardDrawerControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback,
        bool drawerOpen = false, float drawerProgress = 1, double boardAspect = 1)
    {
        var drawerButtons = buttons.Where(button => !IsBoardDrawerHandle(button)).ToArray();
        if (drawerOpen && drawerButtons.Length > 0)
        {
            // Clip the viewport rather than the final row footprint so the
            // actions visibly rise from below the board's lower edge.
            using var clip = CanvasGeometry.CreateRectangle(ds.Device,
                new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
            using var layer = ds.CreateLayer(1, clip);
            var previous = ds.Transform;
            float rowTop = (float)drawerButtons.Min(button => button.Bounds.Y) * BoardSurfaceSize;
            ds.Transform = Matrix3x2.CreateTranslation(0, BoardDrawerSlide(drawerProgress, rowTop)) * previous;
            try
            {
                foreach (var button in drawerButtons)
                {
                    DrawDrawerButton(ds, button, button.Enabled && hovered.Contains(button.Id));
                    DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, ThemeColor(24, 104, 124));
                }
            }
            finally { ds.Transform = previous; }
        }

        // The handle remains fixed while its row's actions rise beside it.
        foreach (var button in buttons.Where(IsBoardDrawerHandle))
        {
            DrawBoardDrawerHandle(ds, button, button.Enabled && hovered.Contains(button.Id), boardAspect);
            DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, ThemeColor(24, 104, 124),
                showCaption: false);
        }
    }

    // Start below the edge by more than the hover outline's half-width.
    private static float BoardDrawerSlide(float progress, float rowTop) =>
        (BoardSurfaceSize - rowTop + 4) * MathF.Pow(1 - Math.Clamp(progress, 0, 1), 3);

    private static bool IsBoardDrawerHandle(BoardButton button) =>
        button.Id is "globe-drawer-open" or "globe-drawer-close" or "photo-drawer-open" or "photo-drawer-close" or
            "water-drawer-open" or "water-drawer-close";

    // The camera trigger measures the same vector silhouette that is rendered.
    // Its physical proportions remain stable on portrait and landscape boards.
    private static Vector2[] DrawerArrowVertices(BoardButton button, double aspect)
    {
        aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .2, 5) : 1;
        float x = aspect >= 1 ? (float)(1 / aspect) : 1;
        float y = (aspect >= 1 ? 1 : (float)aspect) *
            (button.Id is "globe-drawer-open" or "photo-drawer-open" or "water-drawer-open" ? -1 : 1);
        var center = new Vector2((float)(button.Bounds.X + button.Bounds.Width / 2) * BoardSurfaceSize,
            (float)(button.Bounds.Y + button.Bounds.Height / 2) * BoardSurfaceSize);
        Vector2[] profile = [new(-27, -8), new(0, 12), new(27, -8),
            new(22.5f, -12), new(0, 4.5f), new(-22.5f, -12)];
        return profile.Select(point => center + new Vector2(point.X * x, point.Y * y)).ToArray();
    }

    private static Rect DrawerArrowInk(BoardButton button, double aspect)
    {
        var points = DrawerArrowVertices(button, aspect);
        float left = points.Min(point => point.X), top = points.Min(point => point.Y);
        return new(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    private static void DrawBoardDrawerHandle(CanvasDrawingSession ds, BoardButton button, bool hovered, double aspect)
    {
        var b = button.Bounds;
        float radius = BoardButtonCornerRadius(button);
        var rectangle = new Rect(b.X * BoardSurfaceSize, b.Y * BoardSurfaceSize,
            b.Width * BoardSurfaceSize, b.Height * BoardSurfaceSize);
        using var glass = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(231, 239, 244) },
            new() { Position = .48f, Color = ThemeColor(201, 218, 229) },
            new() { Position = 1, Color = ThemeColor(176, 199, 214) }
        ]) { StartPoint = new((float)rectangle.X, (float)rectangle.Y),
            EndPoint = new((float)rectangle.X, (float)rectangle.Bottom) };
        ds.FillRoundedRectangle(rectangle, radius, radius, glass);
        ds.DrawRoundedRectangle(rectangle, radius, radius,
            hovered ? ThemeColor(124, 238, 255) : ThemeColor(96, 155, 185), hovered ? 2.5f : 1.25f);
        ds.DrawLine((float)rectangle.X + 25, (float)rectangle.Y + 2,
            (float)rectangle.Right - 25, (float)rectangle.Y + 2, ThemeColor(247, 252, 255, 185), 1);
        var vertices = DrawerArrowVertices(button, aspect);
        using var geometry = CanvasGeometry.CreatePolygon(ds.Device, vertices);
        var ink = DrawerArrowInk(button, aspect);
        using var titanium = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(22, 83, 112) },
            new() { Position = .5f, Color = ThemeColor(36, 65, 89) },
            new() { Position = 1, Color = ThemeColor(13, 37, 59) }
        ]) { StartPoint = new((float)ink.X, (float)ink.Y), EndPoint = new((float)ink.X, (float)ink.Bottom) };
        ds.FillGeometry(geometry, titanium);
        using var bevel = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
        ds.DrawGeometry(geometry, ThemeColor(63, 116, 143), .65f, bevel);
    }

    private static Rect DrawerButtonTextRectangle(BoardButton button)
    {
        var bounds = button.Bounds;
        return new Rect(bounds.X * BoardSurfaceSize + 14, bounds.Y * BoardSurfaceSize + 3,
            bounds.Width * BoardSurfaceSize - 28, bounds.Height * BoardSurfaceSize - 10);
    }

    private static CanvasTextFormat DrawerButtonTextFormat(BoardButton button) => new()
    {
        FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 32,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static void DrawDrawerButton(CanvasDrawingSession ds, BoardButton button, bool hovered)
    {
        var bounds = button.Bounds;
        float radius = BoardButtonCornerRadius(button);
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        // Pale opaque glass gives the camera a bright, quiet caption background
        // without pure-white glare. Board artwork cannot enter the label masks.
        // Palm-down fingers over dark glass needed a search light on every live
        // attempt; over this glass they are found from the unlit camera image.
        using var glass = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(234, 239, 242) },
            new() { Position = .49f, Color = ThemeColor(226, 232, 236) },
            new() { Position = 1, Color = ThemeColor(218, 225, 230) }
        ]) { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, radius, radius, glass);
        ds.DrawRoundedRectangle(rect, radius, radius,
            hovered ? ThemeColor(103, 224, 255) : ThemeColor(117, 151, 173), hovered ? 2.5f : 1.2f);
        ds.DrawLine((float)rect.X + 17, (float)rect.Y + 2, (float)rect.Right - 17, (float)rect.Y + 2,
            ThemeColor(255, 255, 255, 160), 1);
        using var format = DrawerButtonTextFormat(button);
        ds.DrawText(DrawerButtonCaption(button), DrawerButtonTextRectangle(button), button.Enabled
            ? ThemeColor(50, 55, 59) : ThemeColor(111, 119, 125), format);
    }

    private static string DrawerButtonCaption(BoardButton button) => button.Id is
        "menu" or "photo-swirl" or "photo-copy-once" or "capture-again" or "photo-save"
            ? button.Label.ToUpperInvariant() : button.Label;
}
