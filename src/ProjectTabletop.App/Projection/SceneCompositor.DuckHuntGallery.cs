using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const int GalleryTargetWidth = 440, GalleryTargetHeight = 500;
    private CanvasRenderTarget? _galleryFlapFront, _galleryFlapBack, _galleryTargetBase;
    private CanvasGeometry? _galleryPaperClip;
    private CanvasRenderTarget[]? _galleryTargets;
    private Transform3DEffect[]? _galleryTargetEffects;
    private Transform3DEffect? _galleryFlapEffect;
    private CanvasLinearGradientBrush? _galleryApertureBrush, _galleryBeltShade;
    private int _galleryCachedCycle = -1, _galleryCachedPunctures = -1;
    private int _galleryCachedImpactFrame = -1;
    private int _galleryCachedMiddleTarget = -1;
    private double _galleryCachedAspect;
    private float _galleryCachedRasterWidth;

    internal int GalleryVisualFrame(DateTimeOffset now)
    {
        var holes = _boardSession.GalleryPunctures;
        if (_boardSession.CurrentDuckHuntView != DuckHuntView.TargetPractice || holes.Count == 0) return 0;
        double elapsed = (now - holes[^1].ShotTime).TotalSeconds;
        return elapsed is >= 0 and < .22 ? 1 + (int)(elapsed * 1000) : 0;
    }

    private void PrepareDuckHuntGalleryResources(CanvasDevice device)
    {
        if (_galleryTargetBase is not null || _duckHuntGalleryImages?.Image("target-practice-saloon-v1.png") is not { } scene ||
            _duckHuntGalleryImages.Image("gallery-steampunk-target.png") is not { } assembly) return;
        var flap = DuckHuntGalleryLayout.FlapSceneBounds;
        int width = (int)Math.Ceiling(flap.Width * scene.Size.Width);
        int height = (int)Math.Ceiling(flap.Height * scene.Size.Height);
        _galleryFlapFront = new CanvasRenderTarget(device, width, height, 96);
        using (var draw = _galleryFlapFront.CreateDrawingSession())
        {
            draw.DrawImage(scene, new Rect(0, 0, width, height),
                new Rect(flap.X * scene.Size.Width, flap.Y * scene.Size.Height,
                    flap.Width * scene.Size.Width, flap.Height * scene.Size.Height));
        }
        _galleryFlapBack = new CanvasRenderTarget(device, width, height, 96);
        using (var draw = _galleryFlapBack.CreateDrawingSession())
        {
            // The underside is wood from this same cabinet, with slim structural
            // rails. The original beige face remains the closed front surface.
            draw.DrawImage(scene, new Rect(0, 0, width, height),
                new Rect(170, 815, 1300, 92), .92f, CanvasImageInterpolation.HighQualityCubic);
            draw.DrawRectangle(new Rect(7, 5, width - 14, height - 10), Color.FromArgb(220, 40, 25, 14), 9);
            draw.DrawLine(12, 12, width - 12, 12, Color.FromArgb(145, 174, 133, 77), 2);
        }
        _galleryTargetBase = new CanvasRenderTarget(device, GalleryTargetWidth, GalleryTargetHeight, 96);
        using (var draw = _galleryTargetBase.CreateDrawingSession())
        {
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            double scale = Math.Min(GalleryTargetWidth / assembly.Size.Width, GalleryTargetHeight / assembly.Size.Height);
            double assemblyWidth = assembly.Size.Width * scale, assemblyHeight = assembly.Size.Height * scale;
            draw.DrawImage(assembly,
                new Rect((GalleryTargetWidth - assemblyWidth) / 2, (GalleryTargetHeight - assemblyHeight) / 2,
                    assemblyWidth, assemblyHeight), new Rect(0, 0, assembly.Size.Width, assembly.Size.Height),
                1, CanvasImageInterpolation.HighQualityCubic);
        }
        _galleryPaperClip = CanvasGeometry.CreateEllipse(device,
            new Vector2((float)DuckHuntGalleryLayout.PaperCenterU * GalleryTargetWidth,
                (float)DuckHuntGalleryLayout.PaperCenterV * GalleryTargetHeight),
            (float)DuckHuntGalleryLayout.PaperRadiusU * GalleryTargetWidth,
            (float)DuckHuntGalleryLayout.PaperRadiusV * GalleryTargetHeight);
        foreach (var bounds in DuckHuntGalleryLayout.PaperClipBounds)
        {
            using var metalClip = CanvasGeometry.CreateRectangle(device,
                new Rect(bounds.X * GalleryTargetWidth, bounds.Y * GalleryTargetHeight,
                    bounds.Width * GalleryTargetWidth, bounds.Height * GalleryTargetHeight));
            var paperOnly = _galleryPaperClip.CombineWith(metalClip, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            _galleryPaperClip.Dispose();
            _galleryPaperClip = paperOnly;
        }
        _galleryTargets = new CanvasRenderTarget[DuckHuntGalleryGame.TargetCount];
        _galleryTargetEffects = new Transform3DEffect[DuckHuntGalleryGame.TargetCount];
        for (int i = 0; i < _galleryTargets.Length; i++)
        {
            _galleryTargets[i] = new CanvasRenderTarget(device, GalleryTargetWidth, GalleryTargetHeight, 96);
            _galleryTargetEffects[i] = new Transform3DEffect
            {
                Source = _galleryTargets[i], InterpolationMode = CanvasImageInterpolation.Cubic,
                BorderMode = EffectBorderMode.Soft
            };
        }
        _galleryFlapEffect = new Transform3DEffect
        {
            Source = _galleryFlapFront, InterpolationMode = CanvasImageInterpolation.Cubic,
            BorderMode = EffectBorderMode.Soft
        };
        _galleryApertureBrush = new CanvasLinearGradientBrush(device,
        [
            new() { Position = 0, Color = Color.FromArgb(255, 15, 13, 11) },
            new() { Position = .64f, Color = Color.FromArgb(255, 39, 31, 23) },
            new() { Position = 1, Color = Color.FromArgb(255, 68, 50, 32) }
        ]);
        _galleryBeltShade = new CanvasLinearGradientBrush(device,
        [
            new() { Position = 0, Color = Color.FromArgb(150, 0, 0, 0) },
            new() { Position = .35f, Color = Color.FromArgb(0, 0, 0, 0) },
            new() { Position = 1, Color = Color.FromArgb(110, 0, 0, 0) }
        ]);
        PrepareGalleryCandles(device);
        PrepareGallerySkulls(device);
        PrepareGalleryMiddle(device);
    }

    private void DisposeDuckHuntGalleryResources()
    {
        DisposeGalleryCandles();
        DisposeGallerySkulls();
        DisposeGalleryMiddle();
        _galleryFlapEffect?.Dispose();
        if (_galleryTargetEffects is not null) foreach (var effect in _galleryTargetEffects) effect?.Dispose();
        if (_galleryTargets is not null) foreach (var target in _galleryTargets) target?.Dispose();
        _galleryFlapFront?.Dispose(); _galleryFlapBack?.Dispose(); _galleryTargetBase?.Dispose();
        _galleryPaperClip?.Dispose();
        _galleryApertureBrush?.Dispose(); _galleryBeltShade?.Dispose();
        _galleryFlapEffect = null; _galleryTargetEffects = null; _galleryTargets = null;
        _galleryFlapFront = _galleryFlapBack = _galleryTargetBase = null;
        _galleryPaperClip = null;
        _galleryApertureBrush = _galleryBeltShade = null;
        _galleryCachedCycle = _galleryCachedPunctures = -1;
        _galleryCachedMiddleTarget = -1;
    }

    private void DrawDuckHuntGallery(CanvasDrawingSession drawing, DuckHuntGallerySnapshot state, double aspect)
    {
        drawing.Clear(Color.FromArgb(255, 25, 16, 10));
        if (_duckHuntGalleryImages?.Image("target-practice-saloon-v1.png") is not { } scene) return;
        var frame = DuckHuntGalleryLayout.SceneBounds(aspect);
        drawing.DrawImage(scene, GalleryRect(frame), new Rect(0, 0, scene.Size.Width, scene.Size.Height),
            1, CanvasImageInterpolation.HighQualityCubic);
        GalleryLampVisualFrame(_blackjackClock());
        DrawGalleryCandles(drawing, aspect);
        DrawGalleryUpperStage(drawing, state, aspect);
        DrawGalleryMiddleStage(drawing, state, aspect);
        if (state.FlapAngleDegrees > .001)
        {
            var aperture = GallerySceneRect(DuckHuntGalleryLayout.FlapSceneBounds, aspect);
            if (_galleryApertureBrush is { } cavity)
            {
                cavity.StartPoint = new((float)aperture.X, (float)aperture.Y);
                cavity.EndPoint = new((float)aperture.X, (float)aperture.Bottom);
                drawing.FillRectangle(aperture, cavity);
            }
            drawing.DrawRectangle(aperture, Color.FromArgb(255, 38, 25, 15), 2);
        }
        DrawGalleryBelt(drawing, state, aspect);
        if (state.TargetsVisible)
        {
            RefreshGalleryTargetTextures(state, aspect, _blackjackClock());
            DrawGalleryTargets(drawing, state, aspect, inFrontOfLip: false);
        }
        // This physical lip hides the holder bases until they pass over the edge.
        var lip = new BoardRect(.089, .791, .824, .021);
        drawing.DrawImage(scene, GallerySceneRect(lip, aspect),
            new Rect(lip.X * scene.Size.Width, lip.Y * scene.Size.Height, lip.Width * scene.Size.Width, lip.Height * scene.Size.Height));
        if (state.TargetsVisible)
        {
            using var clip = drawing.CreateLayer(1, GalleryRect(frame));
            DrawGalleryTargets(drawing, state, aspect, inFrontOfLip: true);
        }
        DrawGalleryFlap(drawing, state.FlapAngleDegrees, aspect);
        if (state.PlayVisible)
        {
            var bounds = GalleryRect(DuckHuntGalleryLayout.PlayBounds);
            drawing.FillRoundedRectangle(bounds, (float)(14 / aspect), 14, Color.FromArgb(255, 164, 184, 144));
            drawing.DrawRoundedRectangle(bounds, (float)(14 / aspect), 14, Color.FromArgb(255, 53, 70, 37), 2);
            DrawHoldButtonCaption(drawing, "PLAY", bounds, Color.FromArgb(255, 31, 48, 27), aspect);
        }
    }

    private void DrawGalleryBelt(CanvasDrawingSession drawing, DuckHuntGallerySnapshot state, double aspect)
    {
        if (state.Phase == DuckHuntGalleryPhase.Ready) return;
        var belt = GallerySceneRect(new(.092, .690, .820, .102), aspect);
        using var clip = drawing.CreateLayer(1, belt);
        if (_duckHuntGalleryImages?.Image("target-practice-saloon-v1.png") is { } scene)
        {
            // Extend the same belt material into the revealed recess. Targets
            // start on this rear deck before riding toward the front roller.
            drawing.DrawImage(scene, GallerySceneRect(new(.092, .690, .820, .030), aspect),
                new Rect(.092 * scene.Size.Width, .710 * scene.Size.Height,
                    .820 * scene.Size.Width, .010 * scene.Size.Height), 1,
                CanvasImageInterpolation.HighQualityCubic);
        }
        if (_galleryBeltShade is { } shade)
        {
            shade.StartPoint = new((float)belt.X, (float)belt.Y);
            shade.EndPoint = new((float)belt.X, (float)belt.Bottom);
            drawing.FillRectangle(belt, shade);
        }
        // Narrow conveyor joints advance toward the foreground. All texture
        // motion stays inside the belt rather than sliding the saloon artwork.
        var supportedPose = DuckHuntGalleryLayout.TargetPose(2,
            state with { Phase = DuckHuntGalleryPhase.Departing, FallProgress = 0 }, DuckHuntGalleryLayout.SceneAspect);
        double phase = Math.Max(0, supportedPose.BaseContact.V - .697) % .014;
        for (double v = .684 + phase; v < .805; v += .014)
        {
            var a = DuckHuntGalleryLayout.ScenePoint(.095, v, aspect);
            var b = DuckHuntGalleryLayout.ScenePoint(.908, v, aspect);
            float y = (float)a.V * BoardSurfaceSize;
            drawing.DrawLine((float)a.U * BoardSurfaceSize, y, (float)b.U * BoardSurfaceSize, y,
                Color.FromArgb(115, 8, 7, 6), 1.4f);
            drawing.DrawLine((float)a.U * BoardSurfaceSize, y + .9f, (float)b.U * BoardSurfaceSize, y + .9f,
                Color.FromArgb(55, 205, 185, 150), .7f);
        }
    }

    private void RefreshGalleryTargetTextures(DuckHuntGallerySnapshot state, double aspect, DateTimeOffset now)
    {
        if (_galleryTargets is null || _galleryTargetBase is null || _galleryPaperClip is null ||
            _duckHuntGalleryImages?.Image("gallery-paper-puncture.png") is not { } puncture) return;
        var holes = _boardSession.GalleryPunctures;
        int impactFrame = GalleryVisualFrame(now);
        int middleIndex = state.MiddleTargetIndex;
        float rasterWidth = Math.Max(1, _boardRasterPixels.Width);
        if (_galleryCachedCycle == state.Cycle && _galleryCachedPunctures == holes.Count &&
            _galleryCachedAspect == aspect && _galleryCachedRasterWidth == rasterWidth &&
            _galleryCachedImpactFrame == impactFrame && _galleryCachedMiddleTarget == middleIndex) return;
        var resting = state with { Phase = DuckHuntGalleryPhase.WaitingForHits, ConveyorPosition = 1, FallProgress = 0 };
        // Only one middle assembly is onstage, after the lower row has closed.
        // Reuse its first cached surface instead of retaining another five copies.
        int count = middleIndex >= 0 ? 1 : _galleryTargets.Length;
        for (int index = 0; index < count; index++)
        {
            using var draw = _galleryTargets[index].CreateDrawingSession();
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            draw.DrawImage(_galleryTargetBase);
            int logicalIndex = middleIndex >= 0 ? DuckHuntGalleryGame.MiddlePunctureOffset + middleIndex : index;
            var quad = middleIndex >= 0
                ? DuckHuntGalleryLayout.MiddleTargetQuad(state with { MiddlePhase = DuckHuntGalleryMiddlePhase.WaitingForHit }, aspect)
                : DuckHuntGalleryLayout.TargetQuad(index, resting, aspect);
            double diameter = Math.Clamp(20 / (quad.Bounds.Width * rasterWidth), .055, .18) * GalleryTargetWidth;
            double fullSize = diameter * 1254 / 807;
            using var paperLayer = draw.CreateLayer(1, _galleryPaperClip);
            foreach (var hole in holes)
            {
                if (hole.TargetIndex != logicalIndex) continue;
                double x = hole.U * GalleryTargetWidth, y = hole.V * GalleryTargetHeight;
                double progress = Math.Clamp((now - hole.ShotTime).TotalSeconds / .22, 0, 1);
                double spread = .72 + .28 * (1 - Math.Pow(1 - progress, 3)) + .06 * Math.Sin(progress * Math.PI);
                double size = fullSize * spread;
                draw.DrawImage(puncture, new Rect(x - size / 2, y - size / 2, size, size),
                    new Rect(0, 0, puncture.Size.Width, puncture.Size.Height), 1, CanvasImageInterpolation.HighQualityCubic);
            }
        }
        _galleryCachedCycle = state.Cycle; _galleryCachedPunctures = holes.Count;
        _galleryCachedAspect = aspect; _galleryCachedRasterWidth = rasterWidth;
        _galleryCachedImpactFrame = impactFrame;
        _galleryCachedMiddleTarget = middleIndex;
    }

    private void DrawGalleryTargets(CanvasDrawingSession drawing, DuckHuntGallerySnapshot state, double aspect, bool inFrontOfLip)
    {
        if (_galleryTargetEffects is null || _galleryTargets is null) return;
        for (int index = 0; index < DuckHuntGalleryGame.TargetCount; index++)
        {
            var pose = DuckHuntGalleryLayout.TargetPose(index, state, aspect);
            if (pose.InFrontOfLip != inFrontOfLip) continue;
            var quad = pose.Quad;
            if (Math.Abs(quad.TopLeft.V - quad.BottomLeft.V) < .0002) continue;
            if (pose.Supported)
            {
                float contactDepth = (float)(2.5 + 1.5 * Math.Cos(pose.TiltDegrees * Math.PI / 180));
                drawing.FillEllipse((float)pose.BaseContact.U * BoardSurfaceSize,
                    (float)pose.BaseContact.V * BoardSurfaceSize + .5f,
                    (float)quad.Bounds.Width * BoardSurfaceSize * .34f, contactDepth,
                    Color.FromArgb(125, 0, 0, 0));
            }
            if (Math.Abs(pose.TiltDegrees) < .001)
            {
                // The standing target plane is a rectangle. Direct high-quality
                // minification keeps its fine scoring rings and cross continuous;
                // the 3D effect only supports cubic sampling, without this filter.
                drawing.DrawImage(_galleryTargets[index], GalleryRect(quad.Bounds),
                    new Rect(0, 0, GalleryTargetWidth, GalleryTargetHeight), 1,
                    CanvasImageInterpolation.HighQualityCubic);
                continue;
            }
            var effect = _galleryTargetEffects[index];
            effect.TransformMatrix = GalleryProjectiveMatrix(quad, GalleryTargetWidth, GalleryTargetHeight);
            drawing.DrawImage(effect);
        }
    }

    private void DrawGalleryFlap(CanvasDrawingSession drawing, double degrees, double aspect)
    {
        if (_galleryFlapEffect is null || _galleryFlapFront is null || _galleryFlapBack is null) return;
        var flap = DuckHuntGalleryLayout.FlapSceneBounds;
        double angle = degrees * Math.PI / 180, depth = flap.Height * Math.Sin(angle);
        double center = flap.X + flap.Width / 2;
        double reach = flap.Width / (1 - depth * .35);
        double edgeY = flap.Y + flap.Height * Math.Cos(angle) - depth * .55;
        var quad = new DuckHuntGalleryQuad(
            DuckHuntGalleryLayout.ScenePoint(flap.X, flap.Y, aspect),
            DuckHuntGalleryLayout.ScenePoint(flap.X + flap.Width, flap.Y, aspect),
            DuckHuntGalleryLayout.ScenePoint(center + reach / 2, edgeY, aspect),
            DuckHuntGalleryLayout.ScenePoint(center - reach / 2, edgeY, aspect));
        if (Math.Abs(edgeY - flap.Y) > .0003)
        {
            bool reverse = edgeY < flap.Y;
            _galleryFlapEffect.Source = reverse ? _galleryFlapBack : _galleryFlapFront;
            _galleryFlapEffect.TransformMatrix = GalleryProjectiveMatrix(quad,
                (float)_galleryFlapFront.Size.Width, (float)_galleryFlapFront.Size.Height);
            drawing.DrawImage(_galleryFlapEffect);
            // The narrow edge follows the actual moving plane, never a rectangle
            // around its animation bounds.
            var a = quad.BottomLeft; var b = quad.BottomRight;
            drawing.DrawLine((float)a.U * BoardSurfaceSize, (float)a.V * BoardSurfaceSize,
                (float)b.U * BoardSurfaceSize, (float)b.V * BoardSurfaceSize,
                reverse ? Color.FromArgb(255, 100, 68, 34) : Color.FromArgb(255, 117, 88, 49), 2);
        }
        for (int side = 0; side < 2; side++)
        {
            double x = side == 0 ? flap.X + .006 : flap.X + flap.Width - .006;
            var hinge = GallerySceneRect(new(x - .012, flap.Y - .008, .024, .016), aspect);
            drawing.FillRoundedRectangle(hinge, 2, 2, Color.FromArgb(255, 103, 76, 37));
            drawing.DrawRoundedRectangle(hinge, 2, 2, Color.FromArgb(255, 207, 174, 111), 1);
            drawing.DrawLine((float)hinge.X + 2, (float)hinge.Y + 2, (float)hinge.Right - 2, (float)hinge.Y + 2,
                Color.FromArgb(255, 236, 211, 158), .8f);
        }
    }

    private static Rect GalleryRect(BoardRect rect) => new(rect.X * BoardSurfaceSize, rect.Y * BoardSurfaceSize,
        rect.Width * BoardSurfaceSize, rect.Height * BoardSurfaceSize);

    private static Rect GallerySceneRect(BoardRect rect, double aspect)
    {
        var frame = DuckHuntGalleryLayout.SceneBounds(aspect);
        return GalleryRect(new(frame.X + rect.X * frame.Width, frame.Y + rect.Y * frame.Height,
            rect.Width * frame.Width, rect.Height * frame.Height));
    }

    private static Matrix4x4 GalleryProjectiveMatrix(DuckHuntGalleryQuad quad, float width, float height)
    {
        // Horizontal paper/flap edges yield this exact homography directly,
        // avoiding a solver, heap allocation or bitmap copy on animation frames.
        double top = quad.TopRight.U - quad.TopLeft.U;
        double bottom = quad.BottomRight.U - quad.BottomLeft.U;
        double h = top / bottom - 1;
        return new Matrix4x4(
            (float)(top * BoardSurfaceSize / width), 0, 0, 0,
            (float)((quad.BottomLeft.U * (h + 1) - quad.TopLeft.U) * BoardSurfaceSize / height),
            (float)((quad.BottomLeft.V * (h + 1) - quad.TopLeft.V) * BoardSurfaceSize / height), 0, (float)(h / height),
            0, 0, 1, 0,
            (float)quad.TopLeft.U * BoardSurfaceSize, (float)quad.TopLeft.V * BoardSurfaceSize, 0, 1);
    }
}
