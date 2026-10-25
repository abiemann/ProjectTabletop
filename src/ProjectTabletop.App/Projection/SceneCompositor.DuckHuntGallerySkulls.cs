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
    private const int GallerySkullRasterSize = 512, GalleryShardRasterSize = 64;
    private const int GalleryShardVariants = 12, GalleryShardsPerSkull = 128;
    private const double GalleryShardDrag = 6.5, GalleryShardGravity = 32;
    private CanvasRenderTarget? _galleryUpperCurtain, _gallerySkullSprite, _gallerySkullLeftGlint, _gallerySkullRightGlint;
    private CanvasRenderTarget? _galleryCrystalShardAtlas;
    private CanvasGeometry? _galleryUpperArchSource, _galleryUpperArch;
    private CanvasLinearGradientBrush? _galleryUpperRecess;
    private double _galleryUpperArchAspect;
    private int _galleryShardCycle = -1;
    private readonly GalleryShardMotion[] _galleryShardMotion = new GalleryShardMotion[DuckHuntGalleryGame.SkullCount * GalleryShardsPerSkull];
    private readonly Vector2[][] _galleryShardVertices = new Vector2[GalleryShardVariants][];

    private readonly record struct GalleryShardMotion(int Variant, double StartU, double StartBottomV,
        double SpeedU, double SpeedV, double LandSeconds, double LandU, double BounceSeconds, double SettleSeconds,
        double ReboundU, double ReboundV, double SecondReboundV, double Side, double Angle, double Spin, double BounceSpin);

    private void PrepareGallerySkulls(CanvasDevice device)
    {
        if (_gallerySkullSprite is not null || _duckHuntGalleryImages?.Image("gallery-crystal-skull.png") is not { } skull ||
            _duckHuntGalleryImages.Image("target-practice-saloon-v1.png") is not { } scene) return;
        var panel = DuckHuntGalleryLayout.UpperPanelSceneBounds;
        int width = (int)Math.Ceiling(panel.Width * scene.Size.Width), height = (int)Math.Ceiling(panel.Height * scene.Size.Height);
        _galleryUpperCurtain = new CanvasRenderTarget(device, width, height, 96);
        using (var draw = _galleryUpperCurtain.CreateDrawingSession())
            draw.DrawImage(scene, new Rect(0, 0, width, height),
                new Rect(panel.X * scene.Size.Width, panel.Y * scene.Size.Height, panel.Width * scene.Size.Width, panel.Height * scene.Size.Height));
        using (var arch = new CanvasPathBuilder(device))
        {
            arch.BeginFigure(new(192, 322));
            arch.AddLine(new(1463, 322));
            arch.AddLine(new(1463, 273));
            arch.AddCubicBezier(new(1463, 215), new(1392, 165), new(1325, 164));
            arch.AddLine(new(342, 164));
            arch.AddCubicBezier(new(274, 165), new(196, 215), new(192, 273));
            arch.EndFigure(CanvasFigureLoop.Closed);
            _galleryUpperArchSource = CanvasGeometry.CreatePath(arch);
        }
        _galleryUpperRecess = new CanvasLinearGradientBrush(device,
        [
            new() { Position = 0, Color = Color.FromArgb(255, 14, 19, 24) },
            new() { Position = .75f, Color = Color.FromArgb(255, 29, 31, 33) },
            new() { Position = 1, Color = Color.FromArgb(255, 47, 39, 29) }
        ]);
        _gallerySkullSprite = new CanvasRenderTarget(device, GallerySkullRasterSize, GallerySkullRasterSize, 96);
        using (var draw = _gallerySkullSprite.CreateDrawingSession())
        {
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            draw.DrawImage(skull, new Rect(0, 0, GallerySkullRasterSize, GallerySkullRasterSize),
                new Rect(0, 0, skull.Size.Width, skull.Size.Height), 1, CanvasImageInterpolation.HighQualityCubic);
        }
        _gallerySkullLeftGlint = CreateGallerySkullGlint(device, left: true);
        _gallerySkullRightGlint = CreateGallerySkullGlint(device, left: false);
        _galleryCrystalShardAtlas = new CanvasRenderTarget(device, GalleryShardRasterSize * 4, GalleryShardRasterSize * 3, 96);
        using var atlas = _galleryCrystalShardAtlas.CreateDrawingSession();
        atlas.Clear(Color.FromArgb(0, 0, 0, 0));
        var random = new Random(85103);
        for (int i = 0; i < GalleryShardVariants; i++)
        {
            Vector2[] vertices = (i % 3) switch
            {
                0 => [new(-.40f, -.34f), new(.34f, -.42f), new(.45f, .22f), new(-.13f, .45f)],
                1 => [new(-.36f, -.39f), new(.43f, -.08f), new(-.10f, .46f)],
                _ => [new(-.12f, -.48f), new(.14f, -.25f), new(.09f, .47f), new(-.15f, .22f)]
            };
            for (int p = 0; p < vertices.Length; p++) vertices[p] *= .72f + (float)random.NextDouble() * .28f;
            _galleryShardVertices[i] = vertices;
            using var path = new CanvasPathBuilder(device);
            path.BeginFigure((vertices[0] + new Vector2(.5f)) * GalleryShardRasterSize);
            for (int p = 1; p < vertices.Length; p++) path.AddLine((vertices[p] + new Vector2(.5f)) * GalleryShardRasterSize);
            path.EndFigure(CanvasFigureLoop.Closed);
            using var shape = CanvasGeometry.CreatePath(path);
            atlas.Transform = Matrix3x2.CreateTranslation(i % 4 * GalleryShardRasterSize, i / 4 * GalleryShardRasterSize);
            using (atlas.CreateLayer(1, shape))
            {
                double x = (.24 + random.NextDouble() * .44) * GallerySkullRasterSize;
                double y = (.12 + random.NextDouble() * .22) * GallerySkullRasterSize;
                atlas.DrawImage(_gallerySkullSprite, new Rect(0, 0, GalleryShardRasterSize, GalleryShardRasterSize), new Rect(x, y, 52, 52));
            }
            atlas.DrawGeometry(shape, Color.FromArgb(205, 238, 244, 248), 1.2f);
        }
    }

    private CanvasRenderTarget CreateGallerySkullGlint(CanvasDevice device, bool left)
    {
        using var light = new CanvasRenderTarget(device, GallerySkullRasterSize, GallerySkullRasterSize, 96);
        using (var draw = light.CreateDrawingSession())
        using (var beam = new CanvasRadialGradientBrush(device, Color.FromArgb(245, 255, 248, 226), Color.FromArgb(0, 255, 248, 226))
        { Center = new(left ? 125 : 387, 215), RadiusX = 135, RadiusY = 245 })
        {
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            draw.FillRectangle(0, 0, GallerySkullRasterSize, GallerySkullRasterSize, beam);
        }
        using var facets = new LuminanceToAlphaEffect { Source = _gallerySkullSprite };
        using var masked = new AlphaMaskEffect { Source = light, AlphaMask = facets };
        var result = new CanvasRenderTarget(device, GallerySkullRasterSize, GallerySkullRasterSize, 96);
        using (var draw = result.CreateDrawingSession())
        {
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            draw.DrawImage(masked);
        }
        return result;
    }

    private void DisposeGallerySkulls()
    {
        _galleryUpperCurtain?.Dispose(); _gallerySkullSprite?.Dispose();
        _gallerySkullLeftGlint?.Dispose(); _gallerySkullRightGlint?.Dispose();
        _galleryCrystalShardAtlas?.Dispose();
        _galleryUpperArchSource?.Dispose(); _galleryUpperArch?.Dispose();
        _galleryUpperRecess?.Dispose();
        _galleryUpperCurtain = _gallerySkullSprite = _gallerySkullLeftGlint = _gallerySkullRightGlint = null;
        _galleryCrystalShardAtlas = null; _galleryUpperArchSource = _galleryUpperArch = null;
        _galleryUpperRecess = null; _galleryShardCycle = -1;
    }

    private void DrawGalleryUpperStage(CanvasDrawingSession drawing, DuckHuntGallerySnapshot state, double aspect)
    {
        if (!state.UpperTargetsVisible || _galleryUpperArchSource is null || _galleryUpperCurtain is null || _gallerySkullSprite is null) return;
        if (_galleryUpperArch is null || _galleryUpperArchAspect != aspect)
        {
            _galleryUpperArch?.Dispose();
            var frame = DuckHuntGalleryLayout.SceneBounds(aspect);
            _galleryUpperArch = _galleryUpperArchSource.Transform(
                Matrix3x2.CreateScale((float)(frame.Width * BoardSurfaceSize / 1672), (float)(frame.Height * BoardSurfaceSize / 941)) *
                Matrix3x2.CreateTranslation((float)frame.X * BoardSurfaceSize, (float)frame.Y * BoardSurfaceSize));
            _galleryUpperArchAspect = aspect;
        }
        PrepareGalleryShardMotion(state.Cycle);
        var panel = GallerySceneRect(DuckHuntGalleryLayout.UpperPanelSceneBounds, aspect);
        double leftLight = GalleryLampLight(GalleryLampSeconds, left: true), rightLight = GalleryLampLight(GalleryLampSeconds, left: false);
        using (drawing.CreateLayer(1, _galleryUpperArch))
        {
            if (_galleryUpperRecess is { } recess)
            {
                recess.StartPoint = new((float)panel.X, (float)panel.Y);
                recess.EndPoint = new((float)panel.X, (float)panel.Bottom);
                drawing.FillRectangle(panel, recess);
            }
            for (int index = 0; index < DuckHuntGalleryGame.SkullCount; index++)
            {
                var pose = DuckHuntGalleryLayout.SkullPose(index, aspect);
                if ((state.SkullHitMask & (1 << index)) == 0)
                {
                    var bounds = GalleryRect(pose.Bounds);
                    drawing.FillEllipse((float)pose.BaseContact.U * BoardSurfaceSize, (float)pose.BaseContact.V * BoardSurfaceSize,
                        (float)bounds.Width * .23f, 2.3f, Color.FromArgb(150, 0, 0, 0));
                    var source = new Rect(0, 0, GallerySkullRasterSize, GallerySkullRasterSize);
                    drawing.DrawImage(_gallerySkullSprite, bounds, source, 1, CanvasImageInterpolation.HighQualityCubic);
                    drawing.DrawImage(_gallerySkullLeftGlint!, bounds, source,
                        (float)((.08 + .18 * leftLight) * (1 - index * .1)), CanvasImageInterpolation.HighQualityCubic);
                    drawing.DrawImage(_gallerySkullRightGlint!, bounds, source,
                        (float)((.08 + .18 * rightLight) * (.6 + index * .1)), CanvasImageInterpolation.HighQualityCubic);
                }
                else DrawGallerySkullShards(drawing, index, pose.Bounds, state.AnimationSeconds - state.SkullHitSeconds(index), aspect);
            }
            double rise = Math.Clamp(state.UpperOpenProgress, 0, 1) * (panel.Height + 2);
            var curtain = new Rect(panel.X, panel.Y - rise, panel.Width, panel.Height);
            drawing.DrawImage(_galleryUpperCurtain, curtain,
                new Rect(0, 0, _galleryUpperCurtain.Size.Width, _galleryUpperCurtain.Size.Height), 1, CanvasImageInterpolation.HighQualityCubic);
            if (state.UpperOpenProgress is > 0 and < 1)
                drawing.DrawLine((float)panel.X, (float)curtain.Bottom, (float)panel.Right, (float)curtain.Bottom,
                    Color.FromArgb(190, 66, 45, 25), 2);
        }
    }

    private void PrepareGalleryShardMotion(int cycle)
    {
        if (_galleryShardCycle == cycle) return;
        var random = new Random(unchecked(36017 + cycle * 104729));
        for (int i = 0; i < _galleryShardMotion.Length; i++)
        {
            int variant = i % GalleryShardVariants;
            // Every shard leaves its own part of the skull immediately; the impulse points
            // outwards instead of lifting the entire skull into a falling pile.
            double direction = ((i % GalleryShardsPerSkull) + random.NextDouble()) * Math.Tau / GalleryShardsPerSkull;
            double radial = .15 + .85 * Math.Sqrt(random.NextDouble());
            double dx = Math.Cos(direction), dy = Math.Sin(direction);
            double startU = .5 + dx * .30 * radial, startV = .48 + dy * .40 * radial;
            double impulse = 8 + random.NextDouble() * 6, tangent = (random.NextDouble() - .5) * 2.4;
            double speedU = dx * impulse - dy * tangent, speedV = dy * impulse + dx * tangent - .6;
            double side = (i % 4) switch
            {
                0 => .055 + random.NextDouble() * .030,
                1 => .014 + random.NextDouble() * .011,
                _ => .030 + random.NextDouble() * .020
            };
            double angle = random.NextDouble() * Math.Tau;
            double startBottomV = startV + GalleryShardBottomOffset(variant, side, angle);
            double landingLow = 0, landingHigh = 1;
            for (int step = 0; step < 24; step++)
            {
                double time = (landingLow + landingHigh) * .5;
                double bottomV = startBottomV + speedV * GalleryShardTravel(time, GalleryShardDrag) + .5 * GalleryShardGravity * time * time;
                if (bottomV < DuckHuntGalleryLayout.SkullBaseV) landingLow = time;
                else landingHigh = time;
            }
            double landing = (landingLow + landingHigh) * .5;
            double drag = Math.Exp(-GalleryShardDrag * landing);
            double reboundV = (speedV * drag + GalleryShardGravity * landing) * (.20 + random.NextDouble() * .09);
            double secondReboundV = reboundV * (.22 + random.NextDouble() * .10);
            double bounceSeconds = 2 * reboundV / GalleryShardGravity;
            double settleSeconds = landing + bounceSeconds + 2 * secondReboundV / GalleryShardGravity;
            double spin = (random.Next(2) == 0 ? -1 : 1) * (18 + random.NextDouble() * 28);
            _galleryShardMotion[i] = new(variant, startU, startBottomV, speedU, speedV, landing,
                startU + speedU * GalleryShardTravel(landing, GalleryShardDrag), bounceSeconds, settleSeconds,
                speedU * drag * (.25 + random.NextDouble() * .20), reboundV, secondReboundV, side, angle, spin,
                spin * Math.Exp(-3 * landing) * (random.Next(3) == 0 ? -.45 : .45));
        }
        _galleryShardCycle = cycle;
    }

    private static double GalleryShardTravel(double time, double drag) => (1 - Math.Exp(-drag * time)) / drag;

    private double GalleryShardBottomOffset(int variant, double side, double angle)
    {
        double sine = Math.Sin(angle), cosine = Math.Cos(angle), maximum = double.MinValue;
        foreach (var vertex in _galleryShardVertices[variant])
            maximum = Math.Max(maximum, vertex.X * sine + vertex.Y * cosine);
        // Include the cached texture's half-stroke so even the thin shard rims touch the shelf.
        return side * (maximum + .6 / GalleryShardRasterSize);
    }

    private void DrawGallerySkullShards(CanvasDrawingSession drawing, int index, BoardRect bounds, double elapsed, double aspect)
    {
        if (_galleryCrystalShardAtlas is null) return;
        elapsed = Math.Max(0, elapsed);
        using var batch = drawing.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
        for (int piece = 0; piece < GalleryShardsPerSkull; piece++)
        {
            var shard = _galleryShardMotion[index * GalleryShardsPerSkull + piece];
            double t = Math.Min(elapsed, shard.SettleSeconds), u, bottomV, angle;
            if (t < shard.LandSeconds)
            {
                double travel = GalleryShardTravel(t, GalleryShardDrag);
                u = shard.StartU + shard.SpeedU * travel;
                bottomV = shard.StartBottomV + shard.SpeedV * travel + .5 * GalleryShardGravity * t * t;
                angle = shard.Angle + shard.Spin * GalleryShardTravel(t, 3);
            }
            else
            {
                double afterImpact = t - shard.LandSeconds;
                u = shard.LandU + shard.ReboundU * GalleryShardTravel(afterImpact, 12);
                double bounceTime = afterImpact <= shard.BounceSeconds ? afterImpact : afterImpact - shard.BounceSeconds;
                double rebound = afterImpact <= shard.BounceSeconds ? shard.ReboundV : shard.SecondReboundV;
                double bounceHeight = Math.Max(0, rebound * bounceTime - .5 * GalleryShardGravity * bounceTime * bounceTime);
                bottomV = DuckHuntGalleryLayout.SkullBaseV - bounceHeight;
                angle = shard.Angle + shard.Spin * GalleryShardTravel(shard.LandSeconds, 3) +
                    shard.BounceSpin * GalleryShardTravel(afterImpact, 10);
            }
            double v = bottomV - GalleryShardBottomOffset(shard.Variant, shard.Side, angle);
            double side = bounds.Height * shard.Side * BoardSurfaceSize;
            var center = new Vector2((float)((bounds.X + u * bounds.Width) * BoardSurfaceSize),
                (float)((bounds.Y + v * bounds.Height) * BoardSurfaceSize));
            var transform = Matrix3x2.CreateTranslation(-GalleryShardRasterSize / 2f, -GalleryShardRasterSize / 2f) *
                Matrix3x2.CreateScale((float)(side / GalleryShardRasterSize)) * Matrix3x2.CreateRotation((float)angle) *
                Matrix3x2.CreateScale((float)(1 / aspect), 1) * Matrix3x2.CreateTranslation(center);
            var source = new Rect(shard.Variant % 4 * GalleryShardRasterSize, shard.Variant / 4 * GalleryShardRasterSize,
                GalleryShardRasterSize, GalleryShardRasterSize);
            batch.DrawFromSpriteSheet(_galleryCrystalShardAtlas, transform, source, Vector4.One);
        }
    }
}
