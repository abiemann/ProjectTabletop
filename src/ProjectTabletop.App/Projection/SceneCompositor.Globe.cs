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
        string? Error, int SkyTextureWidth, int SkyTextureHeight, int SkyTextureLoadCount,
        int SkySourceWidth, int SkySourceHeight, GlobeModel SelectedModel, GlobeModel? LoadedModel,
        bool IsLoading, int ModelTextureLoadCount, int ResidentModelTextureCount, int ResidentModelCount);

    internal GlobeRenderDiagnostics GetGlobeRenderDiagnostics(double boardAspect, GlobeSnapshot state)
    {
        lock (_gate)
        {
            boardAspect = double.IsFinite(boardAspect) ? Math.Clamp(boardAspect, .2, 5) : 1;
            double radius = .335 * Math.Clamp(state.Zoom, .01, 10);
            return new(_globeRenderer?.IsReady == true, _globeRenderer?.SurfaceWidth ?? 0,
                _globeRenderer?.SurfaceHeight ?? 0, _globeRenderer?.CloudWidth ?? 0, _globeRenderer?.CloudHeight ?? 0,
                .5, .5, boardAspect >= 1 ? radius / boardAspect : radius,
                boardAspect >= 1 ? radius : radius * boardAspect, _globeRenderer?.Error,
                _globeRenderer?.SkyTextureWidth ?? 0, _globeRenderer?.SkyTextureHeight ?? 0,
                _globeRenderer?.SkyTextureLoadCount ?? 0,
                _globeRenderer?.SkySourceWidth ?? 0, _globeRenderer?.SkySourceHeight ?? 0,
                state.Model, _globeRenderer?.LoadedModel, _globeRenderer?.IsLoading == true,
                _globeRenderer?.ModelTextureLoadCount ?? 0, _globeRenderer?.ResidentModelTextureCount ?? 0,
                _globeRenderer?.ResidentModelCount ?? 0);
        }
    }

    internal Task EnsureGlobeResourcesAsync(CanvasDevice device)
    {
        lock (_gate) return GetGlobeRenderer(device).EnsureReadyAsync(_boardSession.GetGlobeSnapshot(_globeClock()).Model);
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
        if (!renderer.Draw(ds, (float)state.Zoom, (float)state.RotationDegrees, boardAspect,
            (float)state.ViewLatitudeDegrees, state.ElapsedSeconds, state.Model))
        {
            if (!renderer.SkyReady) ds.Clear(ThemeColor(2, 5, 11));
            GlobeText(ds, renderer.Error is null ? "Loading…" : "Earth imagery could not load",
                new Rect(140, 460, 720, 80), 24, AppPalette.MutedText);
        }

        DrawGlobeControls(ds, buttons, hovered, selectionFeedback, drawerOpen, drawerProgress, boardAspect,
            _holdControlBrightness, state.ViewLatitudeDegrees);

        const string credit = "NASA EARTH & STAR MAP";
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

    private void RetryGlobeModelAfterAction(string id)
    {
        if (_boardSession.Screen == BoardScreen.Globe &&
            id is "globe-model-clouds" or "globe-model-no-clouds" or "globe-model-night")
            _globeRenderer?.RetryFailedModel(_boardSession.GetGlobeSnapshot(_globeClock()).Model);
    }

    private void DrawGlobeControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback,
        bool drawerOpen = false, float drawerProgress = 1, double boardAspect = 1, double holdExposure = 1,
        double viewLatitudeDegrees = 0)
    {
        DrawBoardTitle(ds, "GLOBE");
        if (buttons.Count == 0) return;
        using var fade = CreateBoardControlsFadeLayer(ds);
        DrawBoardDrawerControls(ds, buttons, hovered, selectionFeedback, drawerOpen, drawerProgress, boardAspect,
            holdExposure);
        if (drawerOpen)
        {
            var state = _boardSession.GetGlobeSnapshot(_globeClock());
            DrawGlobeRail(ds, drawerProgress, false,
                (viewLatitudeDegrees / ProjectTabletop.Interaction.GlobeState.MaximumViewLatitude + 1) / 2);
            DrawGlobeRail(ds, drawerProgress, true,
                (state.Zoom - ProjectTabletop.Interaction.GlobeState.MinimumZoom) /
                (ProjectTabletop.Interaction.GlobeState.MaximumZoom - ProjectTabletop.Interaction.GlobeState.MinimumZoom));
            var model = buttons.FirstOrDefault(button => button.Id == "globe-model");
            if (model is not null && _boardSession.GlobeModelExpanded)
            {
                // The caret is decorative, outside MODEL's stationary caption.
                // Follow the same slide as the expanded rows.
                float rowTop = (float)buttons.Where(button => !IsBoardDrawerHandle(button))
                    .Min(button => button.Bounds.Y) * BoardSurfaceSize;
                double slide = BoardDrawerSlide(drawerProgress, rowTop);
                GlobeText(ds, "^", new Rect(model.Bounds.X * BoardSurfaceSize,
                    model.Bounds.Y * BoardSurfaceSize + slide + 9,
                    model.Bounds.Width * BoardSurfaceSize, 22), 20, ThemeColor(0, 0, 0));
            }
        }
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
