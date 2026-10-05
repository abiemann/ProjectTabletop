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
        if (!renderer.Draw(ds, (float)state.Zoom, (float)state.RotationDegrees, boardAspect, (float)state.ViewLatitudeDegrees))
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
        DrawBoardDrawerControls(ds, buttons, hovered, selectionFeedback, drawerOpen, drawerProgress, boardAspect);
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
