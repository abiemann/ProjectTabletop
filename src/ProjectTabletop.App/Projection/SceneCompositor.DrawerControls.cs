using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static void DrawBoardDrawerControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback,
        bool drawerOpen = false, float drawerProgress = 1, double boardAspect = 1, double holdExposure = 1)
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
                    DrawDrawerButton(ds, button, button.Enabled && hovered.Contains(button.Id), boardAspect, holdExposure);
                    DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, ThemeColor(24, 104, 124));
                }
            }
            finally { ds.Transform = previous; }
        }

        // The handle remains fixed while its row's actions rise beside it.
        foreach (var button in buttons.Where(IsBoardDrawerHandle))
        {
            DrawBoardDrawerHandle(ds, button, button.Enabled && hovered.Contains(button.Id), boardAspect, holdExposure);
            DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, ThemeColor(24, 104, 124),
                showCaption: false);
        }
    }

    // Start below the edge by more than the hover outline's half-width.
    private static float BoardDrawerSlide(float progress, float rowTop) =>
        (BoardSurfaceSize - rowTop + 4) * MathF.Pow(1 - Math.Clamp(progress, 0, 1), 3);

    private static bool IsBoardDrawerHandle(BoardButton button) =>
        button.Id is "globe-drawer-open" or "globe-drawer-close" or "photo-drawer-open" or "photo-drawer-close" or
            "water-drawer-open" or "water-drawer-close" or "paint-drawer-open" or "paint-drawer-close";

    // The camera trigger measures the same vector silhouette that is rendered.
    // Its physical proportions remain stable on portrait and landscape boards.
    // Segoe UI Semibold's I stem at the Water Garden EXIT caption's 28-unit
    // size measures 3.3496 units. Both black bands and their clear band use
    // that perpendicular thickness, rather than the old broad solid triangle.
    private const float DrawerArrowStroke = 3.35f;
    private static readonly float DrawerArrowBandHeight = DrawerArrowStroke * MathF.Sqrt(1 + 22f * 22 / (27 * 27));
    private static readonly Vector2[] DrawerArrowOuterProfile = DrawerArrowBandProfile(0);
    private static readonly Vector2[] DrawerArrowInnerProfile = DrawerArrowBandProfile(2 * DrawerArrowBandHeight);

    private static Vector2[] DrawerArrowBandProfile(float offset)
    {
        float tip = (22 + 3 * DrawerArrowBandHeight) / 2 - offset;
        return [new(-27, tip - 22), new(0, tip), new(27, tip - 22),
            new(27, tip - 22 - DrawerArrowBandHeight), new(0, tip - DrawerArrowBandHeight),
            new(-27, tip - 22 - DrawerArrowBandHeight)];
    }

    private static Vector2[] DrawerArrowVertices(BoardButton button, double aspect) =>
        DrawerArrowProfile(button, aspect, DrawerArrowOuterProfile);

    private static Vector2[] DrawerArrowProfile(BoardButton button, double aspect, Vector2[] profile)
    {
        aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .2, 5) : 1;
        float size = IsCrownDeedDrawerHandle(button) ? 22f / 27 : 1;
        float x = size * (aspect >= 1 ? (float)(1 / aspect) : 1);
        float y = size * (aspect >= 1 ? 1 : (float)aspect) *
            (button.Id is "globe-drawer-open" or "photo-drawer-open" or "water-drawer-open" or "paint-drawer-open" or "mp-exit" ? -1 : 1);
        var center = new Vector2((float)(button.Bounds.X + button.Bounds.Width / 2) * BoardSurfaceSize,
            (float)(button.Bounds.Y + button.Bounds.Height / 2) * BoardSurfaceSize);
        return profile.Select(point => center + new Vector2(point.X * x, point.Y * y)).ToArray();
    }

    private static Rect DrawerArrowInk(BoardButton button, double aspect) =>
        DrawerArrowGlyphInk(button, DrawerArrowVertices(button, aspect), aspect);

    private static Rect DrawerArrowGlyphInk(BoardButton button, Vector2[] points, double aspect)
    {
        points = [.. points, .. DrawerArrowProfile(button, aspect, DrawerArrowInnerProfile)];
        float left = points.Min(point => point.X), top = points.Min(point => point.Y);
        return new(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    // Black / button-colour / black stacked arrows. Leaving the middle band
    // unpainted preserves the exact button surface, including its gradient.
    // Closing mirrors the complete glyph; drawing and sensing share its bounds.
    private static CanvasGeometry DrawerArrowGeometry(CanvasDevice device, BoardButton button,
        Vector2[] points, double aspect)
    {
        using var arrow = CanvasGeometry.CreatePolygon(device, points);
        using var inner = CanvasGeometry.CreatePolygon(device, DrawerArrowProfile(button, aspect, DrawerArrowInnerProfile));
        return CanvasGeometry.CreateGroup(device, [arrow, inner]);
    }

    private static void DrawBoardDrawerHandle(CanvasDrawingSession ds, BoardButton button, bool hovered, double aspect,
        double holdExposure = 1)
    {
        double plate = button.IsHold ? holdExposure : 1;
        var b = button.Bounds;
        float radius = BoardButtonCornerRadius(button);
        var rectangle = new Rect(b.X * BoardSurfaceSize, b.Y * BoardSurfaceSize,
            b.Width * BoardSurfaceSize, b.Height * BoardSurfaceSize);
        using var glass = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = HoldExposure(ThemeColor(231, 239, 244), plate) },
            new() { Position = .48f, Color = HoldExposure(ThemeColor(201, 218, 229), plate) },
            new() { Position = 1, Color = HoldExposure(ThemeColor(176, 199, 214), plate) }
        ]) { StartPoint = new((float)rectangle.X, (float)rectangle.Y),
            EndPoint = new((float)rectangle.X, (float)rectangle.Bottom) };
        ds.FillRoundedRectangle(rectangle, radius, radius, glass);
        ds.DrawRoundedRectangle(rectangle, radius, radius,
            hovered ? ThemeColor(124, 238, 255) : ThemeColor(96, 155, 185), hovered ? 2.5f : 1.25f);
        ds.DrawLine((float)rectangle.X + 25, (float)rectangle.Y + 2,
            (float)rectangle.Right - 25, (float)rectangle.Y + 2, ThemeColor(247, 252, 255, 185), 1);
        var vertices = DrawerArrowVertices(button, aspect);
        using var geometry = DrawerArrowGeometry(ds.Device, button, vertices, aspect);
        ds.FillGeometry(geometry, button.Enabled ? ThemeColor(0, 0, 0) : ThemeColor(91, 100, 107));
    }

    private static Rect DrawerButtonTextRectangle(BoardButton button)
    {
        var bounds = button.Bounds;
        return new Rect(bounds.X * BoardSurfaceSize + 14, bounds.Y * BoardSurfaceSize + 3,
            bounds.Width * BoardSurfaceSize - 28, bounds.Height * BoardSurfaceSize - 10);
    }

    private static void DrawDrawerButton(CanvasDrawingSession ds, BoardButton button, bool hovered,
        double boardAspect, double holdExposure = 1)
    {
        double plate = button.IsHold ? holdExposure : 1;
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
            new() { Position = 0, Color = HoldExposure(ThemeColor(234, 239, 242), plate) },
            new() { Position = .49f, Color = HoldExposure(ThemeColor(226, 232, 236), plate) },
            new() { Position = 1, Color = HoldExposure(ThemeColor(218, 225, 230), plate) }
        ]) { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, radius, radius, glass);
        ds.DrawRoundedRectangle(rect, radius, radius,
            hovered ? ThemeColor(103, 224, 255) : ThemeColor(117, 151, 173), hovered ? 2.5f : 1.2f);
        ds.DrawLine((float)rect.X + 17, (float)rect.Y + 2, (float)rect.Right - 17, (float)rect.Y + 2,
            ThemeColor(255, 255, 255, 160), 1);
        DrawHoldButtonCaption(ds, button.Label, DrawerButtonTextRectangle(button), button.Enabled
            ? ThemeColor(50, 55, 59) : ThemeColor(111, 119, 125), boardAspect);
    }
}
