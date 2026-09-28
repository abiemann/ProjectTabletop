using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
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
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback, double boardAspect = 1)
    {
        var renderer = GetGlobeRenderer(ds.Device);
        if (!renderer.Draw(ds, (float)state.Zoom, (float)state.RotationDegrees, boardAspect))
        {
            ds.Clear(ThemeColor(2, 5, 11));
            GlobeText(ds, renderer.Error is null ? "Loading Earth imagery…" : "Earth imagery could not load",
                new Rect(140, 395, 720, 80), 24, AppPalette.MutedText);
        }

        DrawGlobeControls(ds, buttons, hovered, selectionFeedback);

        GlobeText(ds, "NASA EARTH OBSERVATIONS", new Rect(130, 680, 740, 22), 13,
            ThemeColor(116, 148, 169), true);
        var zoomLabel = state.TargetZoom.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×";
        GlobeText(ds, zoomLabel + "  ·  SLOW EASTWARD ROTATION", new Rect(130, 705, 740, 25), 15,
            ThemeColor(150, 190, 216), true);
    }

    private static void DrawGlobeControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback)
    {
        // The control interiors stay opaque as Earth rotates behind them.
        DrawBoardTitle(ds, "GLOBE");
        GlobeText(ds, "EARTH  /  BLUE MARBLE", new Rect(658, 41, 300, 27), 15,
            ThemeColor(137, 174, 195), true);

        foreach (var button in buttons)
        {
            DrawGlobeButton(ds, button, button.Enabled && hovered.Contains(button.Id));
            DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, ThemeColor(24, 104, 124));
        }
        GlobeText(ds, FingerSelectionCaption(selectionFeedback, "Four fingers together. Aim, then separate index."),
            new Rect(100, 968, 800, 23), 13, ThemeColor(134, 169, 191), true);
    }

    private static Rect GlobeButtonTextRectangle(BoardButton button)
    {
        var bounds = button.Bounds;
        return new Rect(bounds.X * BoardSurfaceSize + 14, bounds.Y * BoardSurfaceSize + 3,
            bounds.Width * BoardSurfaceSize - 28, bounds.Height * BoardSurfaceSize - 10);
    }

    private static CanvasTextFormat GlobeButtonTextFormat(BoardButton button) => new()
    {
        FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 36,
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
