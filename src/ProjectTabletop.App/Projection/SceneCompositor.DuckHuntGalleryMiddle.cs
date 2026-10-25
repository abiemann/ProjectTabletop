using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const double GalleryMiddlePedestalTop = .78;
    private CanvasRenderTarget? _galleryMiddleCurtain, _galleryMiddleEdge;
    private CanvasGeometry? _galleryMiddleOpeningSource, _galleryMiddleOpening;
    private CanvasLinearGradientBrush? _galleryMiddleRecess;
    private double _galleryMiddleOpeningAspect;

    private void PrepareGalleryMiddle(CanvasDevice device)
    {
        if (_galleryMiddleCurtain is not null ||
            _duckHuntGalleryImages?.Image("target-practice-saloon-v1.png") is not { } scene) return;
        var panel = DuckHuntGalleryLayout.MiddlePanelSceneBounds;
        int width = (int)Math.Round(panel.Width * scene.Size.Width);
        int height = (int)Math.Round(panel.Height * scene.Size.Height);
        _galleryMiddleCurtain = new CanvasRenderTarget(device, width, height, 96);
        using (var draw = _galleryMiddleCurtain.CreateDrawingSession())
        {
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            draw.DrawImage(scene, new Rect(0, 0, width, height),
                new Rect(panel.X * scene.Size.Width, panel.Y * scene.Size.Height,
                    panel.Width * scene.Size.Width, panel.Height * scene.Size.Height));
        }
        using (var path = new CanvasPathBuilder(device))
        {
            // Follow the beige opening behind the carved corner brackets. The
            // original goldwork and side columns remain in front of the stage.
            path.BeginFigure(new(250, 350));
            path.AddLine(new(1421, 350));
            path.AddCubicBezier(new(1425, 372), new(1443, 391), new(1464, 405));
            path.AddLine(new(1464, 520));
            path.AddLine(new(208, 520));
            path.AddLine(new(208, 405));
            path.AddCubicBezier(new(231, 396), new(240, 372), new(250, 350));
            path.EndFigure(CanvasFigureLoop.Closed);
            _galleryMiddleOpeningSource = CanvasGeometry.CreatePath(path);
        }
        _galleryMiddleRecess = new CanvasLinearGradientBrush(device,
        [
            new() { Position = 0, Color = Color.FromArgb(255, 22, 19, 16) },
            new() { Position = .80f, Color = Color.FromArgb(255, 48, 37, 27) },
            new() { Position = 1, Color = Color.FromArgb(255, 73, 51, 31) }
        ]);
        _galleryMiddleEdge = new CanvasRenderTarget(device, GalleryTargetWidth, GalleryTargetHeight, 96);
        using (var draw = _galleryMiddleEdge.CreateDrawingSession())
        using (var brass = new CanvasLinearGradientBrush(device,
        [
            new() { Position = 0, Color = Color.FromArgb(255, 53, 39, 24) },
            new() { Position = .38f, Color = Color.FromArgb(255, 160, 122, 67) },
            new() { Position = .62f, Color = Color.FromArgb(255, 203, 169, 104) },
            new() { Position = 1, Color = Color.FromArgb(255, 73, 51, 28) }
        ]))
        {
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            float x = (float)DuckHuntGalleryLayout.TargetBaseU * GalleryTargetWidth;
            brass.StartPoint = new(x - 8, 0); brass.EndPoint = new(x + 8, 0);
            // A round frame seen edge-on has a narrow, rounded metal profile.
            // Its thickness remains visible when the printed face reaches 90°.
            draw.FillRoundedRectangle(new Rect(x - 5.5, 11, 11, 375), 5, 5, brass);
            float paperTop = (float)(DuckHuntGalleryLayout.PaperCenterV - DuckHuntGalleryLayout.PaperRadiusV) * GalleryTargetHeight;
            float paperBottom = (float)(DuckHuntGalleryLayout.PaperCenterV + DuckHuntGalleryLayout.PaperRadiusV) * GalleryTargetHeight;
            draw.DrawLine(x - 1, paperTop, x - 1, paperBottom, Color.FromArgb(255, 205, 194, 172), 1.8f);
            draw.FillRoundedRectangle(new Rect(x - 7, 6, 14, 40), 3, 3, brass);
            draw.FillRoundedRectangle(new Rect(x - 9, 184, 18, 33), 3, 3, brass);
            draw.FillRoundedRectangle(new Rect(x - 7, 344, 14, 47), 3, 3, brass);
        }
    }

    private void DisposeGalleryMiddle()
    {
        _galleryMiddleCurtain?.Dispose(); _galleryMiddleEdge?.Dispose();
        _galleryMiddleOpeningSource?.Dispose(); _galleryMiddleOpening?.Dispose();
        _galleryMiddleRecess?.Dispose();
        _galleryMiddleCurtain = _galleryMiddleEdge = null;
        _galleryMiddleOpeningSource = _galleryMiddleOpening = null;
        _galleryMiddleRecess = null;
    }

    private void DrawGalleryMiddleStage(CanvasDrawingSession drawing, DuckHuntGallerySnapshot state, double aspect)
    {
        if (state.MiddlePhase is DuckHuntGalleryMiddlePhase.Closed or DuckHuntGalleryMiddlePhase.Complete ||
            _galleryMiddleCurtain is null || _galleryMiddleOpeningSource is null) return;
        if (_galleryMiddleOpening is null || _galleryMiddleOpeningAspect != aspect)
        {
            _galleryMiddleOpening?.Dispose();
            var scene = DuckHuntGalleryLayout.SceneBounds(aspect);
            _galleryMiddleOpening = _galleryMiddleOpeningSource.Transform(
                Matrix3x2.CreateScale((float)(scene.Width * BoardSurfaceSize / 1672),
                    (float)(scene.Height * BoardSurfaceSize / 941)) *
                Matrix3x2.CreateTranslation((float)scene.X * BoardSurfaceSize, (float)scene.Y * BoardSurfaceSize));
            _galleryMiddleOpeningAspect = aspect;
        }
        var panel = GallerySceneRect(DuckHuntGalleryLayout.MiddlePanelSceneBounds, aspect);
        using (drawing.CreateLayer(1, _galleryMiddleOpening))
        {
            if (_galleryMiddleRecess is { } recess)
            {
                recess.StartPoint = new((float)panel.X, (float)panel.Y);
                recess.EndPoint = new((float)panel.X, (float)panel.Bottom);
                drawing.FillRectangle(panel, recess);
            }
            var pose = DuckHuntGalleryLayout.MiddleTargetPose(state, aspect);
            if (pose.Visible)
            {
                RefreshGalleryTargetTextures(state, aspect, _blackjackClock());
                DrawGalleryMiddleTarget(drawing, pose, aspect);
            }
            double rise = Math.Clamp(state.MiddleOpenProgress, 0, 1) * (panel.Height + 2);
            var curtain = new Rect(panel.X, panel.Y - rise, panel.Width, panel.Height);
            drawing.DrawImage(_galleryMiddleCurtain, curtain,
                new Rect(0, 0, _galleryMiddleCurtain.Size.Width, _galleryMiddleCurtain.Size.Height),
                1, CanvasImageInterpolation.HighQualityCubic);
            if (state.MiddleOpenProgress is > 0 and < 1)
                drawing.DrawLine((float)panel.X, (float)curtain.Bottom, (float)panel.Right,
                    (float)curtain.Bottom, Color.FromArgb(200, 66, 45, 25), 2);
        }
    }

    private void DrawGalleryMiddleTarget(CanvasDrawingSession drawing, DuckHuntGalleryMiddlePose pose, double aspect)
    {
        if (_galleryTargets is null || _galleryMiddleEdge is null) return;
        var quad = pose.Quad;
        double height = quad.Bounds.Height;
        double width = height * DuckHuntGalleryLayout.TargetAspect / aspect;
        var full = GalleryRect(new(pose.BaseContact.U - DuckHuntGalleryLayout.TargetBaseU * width,
            quad.TopLeft.V, width, height));
        float contactX = (float)pose.BaseContact.U * BoardSurfaceSize;
        float contactY = (float)pose.BaseContact.V * BoardSurfaceSize;
        drawing.FillEllipse(contactX, contactY, (float)full.Width * .30f, 1.5f,
            Color.FromArgb(130, 0, 0, 0));

        if (Math.Abs(pose.YawDegrees) < .001)
        {
            drawing.DrawImage(_galleryTargets[0], full, new Rect(0, 0, GalleryTargetWidth, GalleryTargetHeight),
                1, CanvasImageInterpolation.HighQualityCubic);
            return;
        }
        // The round pedestal is symmetric around its vertical axle. Keep it
        // grounded and full-width while the paper and upper frame rotate.
        double pedestalY = GalleryMiddlePedestalTop * GalleryTargetHeight;
        drawing.DrawImage(_galleryTargets[0],
            new Rect(full.X, full.Y + full.Height * GalleryMiddlePedestalTop,
                full.Width, full.Height * (1 - GalleryMiddlePedestalTop)),
            new Rect(0, pedestalY, GalleryTargetWidth, GalleryTargetHeight - pedestalY),
            1, CanvasImageInterpolation.HighQualityCubic);
        double thickness = Math.Abs(Math.Sin(pose.YawDegrees * Math.PI / 180));
        var edge = new Rect(contactX - (contactX - full.X) * thickness,
            full.Y, full.Width * thickness, full.Height);
        drawing.DrawImage(_galleryMiddleEdge, edge, new Rect(0, 0, GalleryTargetWidth, GalleryTargetHeight),
            1, CanvasImageInterpolation.HighQualityCubic);
        if (quad.Bounds.Width * BoardSurfaceSize < .02) return;
        // Orthographic yaw leaves an upright rectangle. Draw its source crop
        // directly so minification preserves the fine paper rings at each angle.
        var face = GalleryRect(quad.Bounds);
        face.Height *= GalleryMiddlePedestalTop;
        drawing.DrawImage(_galleryTargets[0], face,
            new Rect(0, 0, GalleryTargetWidth, pedestalY), 1, CanvasImageInterpolation.HighQualityCubic);
    }
}
