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
                .5, .438, boardAspect >= 1 ? radius / boardAspect : radius,
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

        GlobeText(ds, "NASA EARTH OBSERVATIONS", new Rect(130, 806, 740, 22), 13,
            ThemeColor(116, 148, 169), true);
        var zoomLabel = state.TargetZoom.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×";
        GlobeText(ds, zoomLabel + "  ·  SLOW EASTWARD ROTATION", new Rect(130, 829, 740, 25), 15,
            ThemeColor(150, 190, 216), true);
    }

    private static void DrawGlobeControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback)
    {
        // This quiet border and instrument typography remain still while the
        // globe rotates. The control interiors are fully opaque, including at
        // maximum zoom when Earth extends behind them.
        ds.DrawRoundedRectangle(new Rect(18, 18, 964, 964), 24, 24, ThemeColor(83, 132, 164, 75), 1);
        ds.DrawLine(39, 41, 39, 67, ThemeColor(116, 220, 252), 3);
        GlobeText(ds, "GLOBE", new Rect(58, 34, 300, 41), 26, ThemeColor(205, 237, 255), false);
        GlobeText(ds, "EARTH  /  BLUE MARBLE", new Rect(658, 41, 300, 27), 15,
            ThemeColor(137, 174, 195), true);

        foreach (var button in buttons)
        {
            DrawGlobeButton(ds, button, button.Enabled && hovered.Contains(button.Id));
            DrawButtonFingerSelectionFeedback(ds, button, selectionFeedback, ThemeColor(104, 222, 254));
        }
        GlobeText(ds, FingerSelectionCaption(selectionFeedback, "Four fingers together. Aim, then separate index."),
            new Rect(100, 963, 800, 23), 13, ThemeColor(134, 169, 191), true);
    }

    private static Rect GlobeButtonTextRectangle(BoardButton button)
    {
        var bounds = button.Bounds;
        return new Rect(bounds.X * BoardSurfaceSize + 14, bounds.Y * BoardSurfaceSize + 3,
            bounds.Width * BoardSurfaceSize - 28, bounds.Height * BoardSurfaceSize - 10);
    }

    private static CanvasTextFormat GlobeButtonTextFormat(BoardButton button) => new()
    {
        FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 22,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static void DrawGlobeButton(CanvasDrawingSession ds, BoardButton button, bool hovered)
    {
        var bounds = button.Bounds;
        var rect = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
            bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
        // All gradient stops are opaque. Live surface details must never appear
        // inside the caption/body masks used for hand acquisition.
        using var glass = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(46, 72, 93) },
            new() { Position = .49f, Color = ThemeColor(18, 37, 55) },
            new() { Position = 1, Color = ThemeColor(8, 22, 36) }
        ]) { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, 15, 15, glass);
        ds.DrawRoundedRectangle(rect, 15, 15,
            hovered ? ThemeColor(103, 224, 255) : ThemeColor(100, 149, 180), hovered ? 2.5f : 1.2f);
        ds.DrawLine((float)rect.X + 17, (float)rect.Y + 2, (float)rect.Right - 17, (float)rect.Y + 2,
            ThemeColor(185, 224, 244, 70), 1);
        using var format = GlobeButtonTextFormat(button);
        ds.DrawText(button.Label, GlobeButtonTextRectangle(button), button.Enabled
            ? ThemeColor(232, 247, 255) : ThemeColor(109, 135, 153), format);
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
