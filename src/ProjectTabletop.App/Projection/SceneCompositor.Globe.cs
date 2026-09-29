using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.App.Projection.GlobeRendering;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private GlobeSurfaceRenderer? _globeRenderer;

    internal sealed record GlobeRenderDiagnostics(bool Ready, int SurfaceWidth, int SurfaceHeight,
        int CloudWidth, int CloudHeight, double CenterU, double CenterV, double RadiusU, double RadiusV,
        string? Error);

    internal GlobeRenderDiagnostics GetGlobeRenderDiagnostics(double boardAspect, GlobeSnapshot state)
    {
        lock (_gate)
        {
            boardAspect = double.IsFinite(boardAspect) ? Math.Clamp(boardAspect, .2, 5) : 1;
            double radius = .335 * Math.Clamp(state.Zoom, .01, 10);
            return new(_globeRenderer?.IsReady == true, _globeRenderer?.SurfaceWidth ?? 0,
                _globeRenderer?.SurfaceHeight ?? 0, _globeRenderer?.CloudWidth ?? 0, _globeRenderer?.CloudHeight ?? 0,
                .5, .5, boardAspect >= 1 ? radius / boardAspect : radius,
                boardAspect >= 1 ? radius : radius * boardAspect, _globeRenderer?.Error);
        }
    }

    internal Task EnsureGlobeResourcesAsync(CanvasDevice device)
    {
        lock (_gate) return GetGlobeRenderer(device).EnsureReadyAsync();
    }

    private GlobeSurfaceRenderer GetGlobeRenderer(CanvasDevice device)
    {
        if (_globeRenderer is null || _globeRenderer.Device != device)
        {
            _globeRenderer?.Dispose();
            _globeRenderer = new(device);
        }
        return _globeRenderer;
    }

    private void DisposeGlobeRenderer()
    {
        _globeRenderer?.Dispose();
        _globeRenderer = null;
    }

    private void DrawGlobeBoard(CanvasDrawingSession ds, GlobeSnapshot state,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback, double boardAspect = 1,
        bool drawerOpen = false, float drawerProgress = 1)
    {
        var renderer = GetGlobeRenderer(ds.Device);
        if (!renderer.Draw(ds, (float)state.Zoom, (float)state.RotationDegrees, boardAspect))
        {
            ds.Clear(ThemeColor(2, 5, 11));
            GlobeText(ds, renderer.Error is null ? "Loading Earth imagery…" : "Earth imagery could not load",
                new Rect(140, 395, 720, 80), 24, AppPalette.MutedText);
        }

        DrawGlobeControls(ds, buttons, hovered, selectionFeedback, drawerOpen, drawerProgress, boardAspect);

        const string credit = "NASA EARTH OBSERVATIONS";
        var zoomLabel = state.TargetZoom.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×";
        var zoomCaption = zoomLabel + "  ·  SLOW EASTWARD ROTATION";
        // Anchor the actual two-line group to the bottom-right, independently
        // of the drawer. A measured shared width preserves centered captions.
        double captionWidth = Math.Ceiling(Math.Max(CaptionWidth(credit, 13), CaptionWidth(zoomCaption, 15)));
        double captionLeft = BoardSurfaceSize - 40 - captionWidth;
        GlobeText(ds, credit, new Rect(captionLeft, 910, captionWidth, 22), 13,
            ThemeColor(116, 148, 169), true);
        GlobeText(ds, zoomCaption, new Rect(captionLeft, 935, captionWidth, 25), 15,
            ThemeColor(150, 190, 216), true);

        double CaptionWidth(string text, float size)
        {
            using var format = new CanvasTextFormat
            {
                FontFamily = "Segoe UI", FontSize = size, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap
            };
            using var layout = new CanvasTextLayout(ds.Device, text, format, BoardSurfaceSize, 25);
            return layout.DrawBounds.Width;
        }
    }

    private static void DrawGlobeControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback,
        bool drawerOpen = false, float drawerProgress = 1, double boardAspect = 1)
    {
        DrawBoardTitle(ds, "GLOBE");

        var drawerButtons = buttons.Where(button => !IsGlobeDrawerHandle(button)).ToArray();
        if (drawerOpen && drawerButtons.Length > 0)
        {
            // Clip the viewport rather than the final row footprint so the
            // actions visibly rise from below the board's lower edge.
            using var clip = CanvasGeometry.CreateRectangle(ds.Device,
                new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
            using var layer = ds.CreateLayer(1, clip);
            var previous = ds.Transform;
            float rowTop = (float)drawerButtons.Min(button => button.Bounds.Y) * BoardSurfaceSize;
            ds.Transform = Matrix3x2.CreateTranslation(0, GlobeDrawerSlide(drawerProgress, rowTop)) * previous;
            try
            {
                foreach (var button in drawerButtons)
                {
                    DrawGlobeButton(ds, button, button.Enabled && hovered.Contains(button.Id));
                    DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, ThemeColor(24, 104, 124));
                }
            }
            finally { ds.Transform = previous; }
        }

        // The handle remains fixed while its row's actions rise beside it.
        foreach (var button in buttons.Where(IsGlobeDrawerHandle))
        {
            DrawGlobeDrawerHandle(ds, button, button.Enabled && hovered.Contains(button.Id), boardAspect);
            DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, ThemeColor(24, 104, 124),
                showCaption: false);
        }
    }

    // Start below the edge by more than the hover outline's half-width.
    private static float GlobeDrawerSlide(float progress, float rowTop) =>
        (BoardSurfaceSize - rowTop + 4) * MathF.Pow(1 - Math.Clamp(progress, 0, 1), 3);

    private static bool IsGlobeDrawerHandle(BoardButton button) =>
        button.Id is "globe-drawer-open" or "globe-drawer-close";

    // The camera trigger measures the same vector silhouette that is rendered.
    // Its physical proportions remain stable on portrait and landscape boards.
    private static Vector2[] GlobeDrawerArrowVertices(BoardButton button, double aspect)
    {
        aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .2, 5) : 1;
        float x = aspect >= 1 ? (float)(1 / aspect) : 1;
        float y = (aspect >= 1 ? 1 : (float)aspect) * (button.Id == "globe-drawer-open" ? -1 : 1);
        var center = new Vector2((float)(button.Bounds.X + button.Bounds.Width / 2) * BoardSurfaceSize,
            (float)(button.Bounds.Y + button.Bounds.Height / 2) * BoardSurfaceSize);
        Vector2[] profile = [new(-27, -8), new(0, 12), new(27, -8),
            new(22.5f, -12), new(0, 4.5f), new(-22.5f, -12)];
        return profile.Select(point => center + new Vector2(point.X * x, point.Y * y)).ToArray();
    }

    private static Rect GlobeDrawerArrowInk(BoardButton button, double aspect)
    {
        var points = GlobeDrawerArrowVertices(button, aspect);
        float left = points.Min(point => point.X), top = points.Min(point => point.Y);
        return new(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    private static void DrawGlobeDrawerHandle(CanvasDrawingSession ds, BoardButton button, bool hovered, double aspect)
    {
        var b = button.Bounds;
        var rectangle = new Rect(b.X * BoardSurfaceSize, b.Y * BoardSurfaceSize,
            b.Width * BoardSurfaceSize, b.Height * BoardSurfaceSize);
        using var glass = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(231, 239, 244) },
            new() { Position = .48f, Color = ThemeColor(201, 218, 229) },
            new() { Position = 1, Color = ThemeColor(176, 199, 214) }
        ]) { StartPoint = new((float)rectangle.X, (float)rectangle.Y),
            EndPoint = new((float)rectangle.X, (float)rectangle.Bottom) };
        ds.FillRoundedRectangle(rectangle, 25, 25, glass);
        ds.DrawRoundedRectangle(rectangle, 25, 25,
            hovered ? ThemeColor(124, 238, 255) : ThemeColor(96, 155, 185), hovered ? 2.5f : 1.25f);
        ds.DrawLine((float)rectangle.X + 25, (float)rectangle.Y + 2,
            (float)rectangle.Right - 25, (float)rectangle.Y + 2, ThemeColor(247, 252, 255, 185), 1);
        var vertices = GlobeDrawerArrowVertices(button, aspect);
        using var geometry = CanvasGeometry.CreatePolygon(ds.Device, vertices);
        var ink = GlobeDrawerArrowInk(button, aspect);
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

    private static Rect GlobeButtonTextRectangle(BoardButton button)
    {
        var bounds = button.Bounds;
        return new Rect(bounds.X * BoardSurfaceSize + 14, bounds.Y * BoardSurfaceSize + 3,
            bounds.Width * BoardSurfaceSize - 28, bounds.Height * BoardSurfaceSize - 10);
    }

    private static CanvasTextFormat GlobeButtonTextFormat(BoardButton button) => new()
    {
        FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 32,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static void DrawGlobeButton(CanvasDrawingSession ds, BoardButton button, bool hovered)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        // Pale opaque glass gives the camera a bright, quiet caption background
        // without pure-white glare. Earth details cannot enter the label masks.
        using var glass = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(234, 239, 242) },
            new() { Position = .49f, Color = ThemeColor(226, 232, 236) },
            new() { Position = 1, Color = ThemeColor(218, 225, 230) }
        ]) { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, 15, 15, glass);
        ds.DrawRoundedRectangle(rect, 15, 15,
            hovered ? ThemeColor(103, 224, 255) : ThemeColor(117, 151, 173), hovered ? 2.5f : 1.2f);
        ds.DrawLine((float)rect.X + 17, (float)rect.Y + 2, (float)rect.Right - 17, (float)rect.Y + 2,
            ThemeColor(255, 255, 255, 160), 1);
        using var format = GlobeButtonTextFormat(button);
        ds.DrawText(button.Label, GlobeButtonTextRectangle(button), button.Enabled
            ? ThemeColor(50, 55, 59) : ThemeColor(111, 119, 125), format);
    }

    private static void GlobeText(CanvasDrawingSession ds, string text, Rect rect, float size,
        Windows.UI.Color color, bool centered = true)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = size, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = centered ? CanvasHorizontalAlignment.Center : CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap
        };
        ds.DrawText(text, rect, color, format);
    }
}
