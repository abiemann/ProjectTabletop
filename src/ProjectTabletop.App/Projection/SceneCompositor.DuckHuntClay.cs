using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const int ClayShardCount = 18, ClayShardTileSize = 256;
    private static readonly ClayShard[] ClayShards = CreateClayShards();
    private CanvasRenderTarget? _clayShardAtlas, _clayDustSprite;
    private CanvasTextFormat? _clayHeading, _clayCaption;
    private Rect _clayArtworkBounds;
    private int _clayDrawnScore = -1, _clayDrawnPairs = -1;
    private string _clayScoreText = string.Empty, _clayPairText = string.Empty, _clayResultText = string.Empty;

    private readonly record struct ClayShard(Vector2[] Vertices, Rect Bounds, Vector2 Center,
        Vector2 Impulse, float Spin, float Flutter);

    private void PrepareDuckHuntClayResources(CanvasDevice device)
    {
        if (_clayShardAtlas is not null || _duckHuntClayImages?.Image("clay-pigeon-v1.png") is not { } disc) return;
        _clayHeading = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 40,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        _clayCaption = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontWeight = FontWeights.SemiBold, FontSize = 23,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        // Read alpha once at publication. Both the untouched native texture and
        // its ceramic fragments use this same tight silhouette, so transparent
        // image margins can never enlarge the target's visible hit footprint.
        var pixels = disc.GetPixelBytes();
        int width = (int)disc.SizeInPixels.Width, height = (int)disc.SizeInPixels.Height;
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] <= 8) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        _clayArtworkBounds = right >= left
            ? new Rect(left, top, right - left + 1, bottom - top + 1)
            : new Rect(0, 0, width, height);

        // Cache actual pieces of the photographed surface, not generic white
        // particles. Eighteen adjoining angular pieces reconstruct the disc at
        // impact and immediately separate along their own radial impulses.
        _clayShardAtlas = new CanvasRenderTarget(device, ClayShardTileSize * 6, ClayShardTileSize * 3, 96);
        using (var atlas = _clayShardAtlas.CreateDrawingSession())
        {
            atlas.Clear(Color.FromArgb(0, 0, 0, 0));
            for (int i = 0; i < ClayShards.Length; i++)
            {
                var shard = ClayShards[i];
                using var path = new CanvasPathBuilder(device);
                path.BeginFigure(shard.Vertices[0]);
                for (int p = 1; p < shard.Vertices.Length; p++) path.AddLine(shard.Vertices[p]);
                path.EndFigure(CanvasFigureLoop.Closed);
                using var shape = CanvasGeometry.CreatePath(path);
                atlas.Transform = Matrix3x2.CreateTranslation((float)-shard.Bounds.X, (float)-shard.Bounds.Y) *
                    Matrix3x2.CreateScale((float)(ClayShardTileSize / shard.Bounds.Width),
                        (float)(ClayShardTileSize / shard.Bounds.Height)) *
                    Matrix3x2.CreateTranslation(i % 6 * ClayShardTileSize, i / 6 * ClayShardTileSize);
                using (atlas.CreateLayer(1, shape))
                    atlas.DrawImage(disc, new Rect(-.5, -.5, 1, 1), _clayArtworkBounds,
                        1, CanvasImageInterpolation.HighQualityCubic);
                atlas.DrawGeometry(shape, Color.FromArgb(210, 219, 215, 193), .004f);
            }
        }
        _clayDustSprite = new CanvasRenderTarget(device, 128, 128, 96);
        using (var dust = _clayDustSprite.CreateDrawingSession())
        using (var powder = new CanvasRadialGradientBrush(device,
            Color.FromArgb(175, 220, 219, 204), Color.FromArgb(0, 220, 219, 204))
            { Center = new(64, 64), RadiusX = 60, RadiusY = 60 })
        {
            dust.Clear(Color.FromArgb(0, 0, 0, 0));
            dust.FillEllipse(64, 64, 60, 60, powder);
        }
    }

    private void DisposeDuckHuntClayResources()
    {
        _clayShardAtlas?.Dispose(); _clayDustSprite?.Dispose();
        _clayHeading?.Dispose(); _clayCaption?.Dispose();
        _clayShardAtlas = _clayDustSprite = null;
        _clayHeading = _clayCaption = null;
        _clayArtworkBounds = default;
        _clayDrawnScore = _clayDrawnPairs = -1;
    }

    private void DrawDuckHuntClay(CanvasDrawingSession drawing, DuckHuntClaySnapshot state, double aspect)
    {
        if (_clayHeading is not { } heading || _clayCaption is not { } caption) return;
        var targets = _boardSession.ClayTargets;
        // The later target is nearer. Ordered drawing preserves natural depth
        // when the two launch paths cross, without sorting or allocating.
        for (int i = 0; i < targets.Count; i++)
            DrawDuckHuntClayTarget(drawing, targets[i], aspect);
        if (_clayDrawnScore != state.Score)
        {
            _clayDrawnScore = state.Score;
            _clayScoreText = $"SCORE  {state.Score}";
            _clayResultText = $"{state.Score} OF 20 CLAYS";
        }
        if (_clayDrawnPairs != state.PairsLaunched)
        {
            _clayDrawnPairs = state.PairsLaunched;
            _clayPairText = $"PAIRS  {state.PairsLaunched} / 10";
        }
        DrawClayCaption(drawing, _clayScoreText, new Rect(30, 25, 210, 52), caption);
        DrawClayCaption(drawing, _clayPairText, new Rect(710, 25, 260, 52), caption);
        if (state.Phase != DuckHuntClayPhase.Finished) return;
        DrawClayCaption(drawing, "ROUND COMPLETE", new Rect(200, 290, 600, 66), heading);
        DrawClayCaption(drawing, _clayResultText, new Rect(240, 360, 520, 45), caption);
    }

    private void DrawClayCaption(CanvasDrawingSession drawing, string text, Rect bounds, CanvasTextFormat format)
    {
        DrawBoardAspectText(drawing, text, new Rect(bounds.X, bounds.Y + 2, bounds.Width, bounds.Height),
            Color.FromArgb(230, 14, 35, 30), format);
        DrawBoardAspectText(drawing, text, bounds, Color.FromArgb(255, 255, 248, 224), format);
    }

    internal void DrawDuckHuntClayTarget(CanvasDrawingSession drawing, DuckHuntClayTarget target, double aspect)
    {
        if (!target.Visible || _duckHuntClayImages?.Image("clay-pigeon-v1.png") is not { } disc) return;
        if (target.Hit)
        {
            DrawDuckHuntClayShatter(drawing, target, aspect);
            return;
        }
        var bounds = DuckHuntClayLayout.DiscBounds(target, aspect);
        var previous = drawing.Transform;
        try
        {
            // Rotate in physical coordinates before applying tabletop aspect.
            // The shared ellipse layout uses this exact same bank and squash.
            drawing.Transform = Matrix3x2.CreateScale((float)(bounds.Width * aspect * BoardSurfaceSize),
                    (float)(bounds.Height * BoardSurfaceSize)) *
                Matrix3x2.CreateRotation((float)target.BankAngle) *
                Matrix3x2.CreateScale((float)(1 / aspect), 1) *
                Matrix3x2.CreateTranslation((float)target.U * BoardSurfaceSize, (float)target.V * BoardSurfaceSize) * previous;
            drawing.DrawImage(disc, new Rect(-.5, -.5, 1, 1), _clayArtworkBounds,
                1, CanvasImageInterpolation.HighQualityCubic);
        }
        finally { drawing.Transform = previous; }
    }

    private void DrawDuckHuntClayShatter(CanvasDrawingSession drawing, DuckHuntClayTarget target, double aspect)
    {
        if (_clayShardAtlas is not { } atlas || _clayDustSprite is not { } dust) return;
        double time = Math.Max(0, target.HitElapsed);
        double duration = DuckHuntClayGame.HitDuration.TotalSeconds;
        if (time >= duration) return;
        var bounds = DuckHuntClayLayout.DiscBounds(target, aspect);
        float diameter = (float)(bounds.Width * aspect * BoardSurfaceSize);
        float face = (float)target.FaceRatio;
        var bank = Matrix3x2.CreateRotation((float)target.BankAngle);
        var previous = drawing.Transform;
        var placement = Matrix3x2.CreateScale((float)(1 / aspect), 1) *
            Matrix3x2.CreateTranslation((float)target.U * BoardSurfaceSize, (float)target.V * BoardSurfaceSize) * previous;
        float age = (float)time;
        float travel = (float)((1 - Math.Exp(-1.9 * time)) / 1.9);
        float gravity = 2.35f * age * age;
        float retreat = 1 / (1 + .40f * age);
        float opacity = (float)Math.Clamp((duration - time) / .28, 0, 1);
        try
        {
            // A small irregular ceramic powder cloud disperses between pieces;
            // each puff has its own radial silhouette and short lifetime.
            if (time < .42)
            {
                for (int i = 0; i < 7; i++)
                {
                    double angle = i * 2.39996323 + target.Id * .73;
                    float radius = diameter * (.11f + age * (.65f + i * .045f));
                    var position = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * diameter * age * .72f;
                    position.Y += diameter * age * age * .6f;
                    drawing.Transform = Matrix3x2.CreateScale(radius / 64) *
                        Matrix3x2.CreateTranslation(position) * placement;
                    drawing.DrawImage(dust, new Rect(-64, -64, 128, 128), new Rect(0, 0, 128, 128),
                        (float)((1 - time / .42) * .42), CanvasImageInterpolation.Linear);
                }
            }
            for (int i = 0; i < ClayShards.Length; i++)
            {
                var shard = ClayShards[i];
                var center = Vector2.Transform(new Vector2(shard.Center.X, shard.Center.Y * face), bank);
                var impulse = Vector2.Transform(shard.Impulse, bank);
                float variation = 1 + .08f * MathF.Sin(target.Id * 1.73f + i);
                var position = (center + impulse * travel * variation + new Vector2(0, gravity)) * diameter;
                float tumble = MathF.Cos(age * shard.Flutter);
                float thickness = .18f + .82f * MathF.Abs(tumble);
                float fragmentWidth = (float)shard.Bounds.Width * diameter * retreat;
                float fragmentHeight = (float)shard.Bounds.Height * diameter * face * retreat;
                drawing.Transform = Matrix3x2.CreateScale(fragmentWidth * thickness, fragmentHeight) *
                    Matrix3x2.CreateRotation((float)target.BankAngle + age * shard.Spin * variation) *
                    Matrix3x2.CreateTranslation(position) * placement;
                var source = new Rect(i % 6 * ClayShardTileSize, i / 6 * ClayShardTileSize, ClayShardTileSize, ClayShardTileSize);
                drawing.DrawImage(atlas, new Rect(-.5, -.5, 1, 1), source, opacity, CanvasImageInterpolation.HighQualityCubic);
            }
        }
        finally { drawing.Transform = previous; }
    }

    private static ClayShard[] CreateClayShards()
    {
        var pieces = new ClayShard[ClayShardCount];
        var inner = new Vector2[12];
        var outer = new Vector2[24];
        for (int i = 0; i < inner.Length; i++)
        {
            double angle = i * Math.Tau / inner.Length;
            float radius = .21f + .035f * MathF.Sin(i * 2.13f);
            inner[i] = new((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius);
        }
        for (int i = 0; i < outer.Length; i++)
        {
            double angle = i * Math.Tau / outer.Length;
            outer[i] = new((float)Math.Cos(angle) * .505f, (float)Math.Sin(angle) * .505f);
        }
        for (int i = 0; i < pieces.Length; i++)
        {
            Vector2[] vertices = i < 12
                ? [inner[i], outer[i * 2], outer[(i * 2 + 1) % 24], outer[(i * 2 + 2) % 24], inner[(i + 1) % 12]]
                : [new(.025f, -.035f), inner[(i - 12) * 2], inner[((i - 12) * 2 + 1) % 12], inner[((i - 12) * 2 + 2) % 12]];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var point in vertices)
            {
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
            }
            // Bounds-centred local textures keep the instant of impact aligned
            // with the intact disc, with a small safety band for cut edges.
            var center = new Vector2((minX + maxX) * .5f, (minY + maxY) * .5f);
            var rectangle = new Rect(minX - .006, minY - .006, maxX - minX + .012, maxY - minY + .012);
            var direction = Vector2.Normalize(center);
            float speed = 1.1f + .55f * (.5f + .5f * MathF.Sin(i * 7.31f));
            var impulse = direction * speed + new Vector2(.08f * MathF.Sin(i * 3.1f), -.23f);
            pieces[i] = new(vertices, rectangle, center, impulse, (i % 2 == 0 ? 1 : -1) * (4.5f + i * .37f), 7.1f + i * .41f);
        }
        return pieces;
    }
}
