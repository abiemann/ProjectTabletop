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
    private const double GalleryLampSceneWidth = 1672, GalleryLampSceneHeight = 941;
    private static readonly Vector2[] GalleryLampGlassOutline =
    [
        new(66, 174), new(90, 174), new(91, 204), new(103, 227),
        new(110, 246), new(106, 261), new(95, 273), new(62, 273),
        new(50, 261), new(46, 246), new(53, 226), new(65, 204)
    ];
    private static readonly double[] GalleryFlameBrightness = [1, .902, .709, .994, .950, 1.061, .966, .996];
    private CanvasRenderTarget[]? _galleryUnlitLampPatches;
    private CanvasRenderTarget? _galleryFlameAtlas;
    private CanvasRadialGradientBrush? _galleryLampGlow;
    private DateTimeOffset? _galleryLampObservedAt;
    private long _galleryLampRoundRevision = -1;
    private double _galleryLampSeconds;
    private bool _galleryLampPaused = true;

    internal static BoardRect GalleryLampSceneBounds(int side) =>
        new((44 + side * 1517) / GalleryLampSceneWidth, 172 / GalleryLampSceneHeight,
            68 / GalleryLampSceneWidth, 103 / GalleryLampSceneHeight);

    internal int GalleryLampVisualFrame(DateTimeOffset now)
    {
        if (_boardSession.Screen != BoardScreen.DuckHunt || _boardSession.CurrentDuckHuntView != DuckHuntView.TargetPractice)
        {
            _galleryLampObservedAt = null;
            return 0;
        }
        var state = _boardSession.GalleryState;
        if (_galleryLampRoundRevision != _boardSession.DuckHuntRoundRevision)
        {
            // A view re-entry resets the game to cycle zero. This also catches
            // navigation paths that did not draw an intervening board frame.
            if (state.Cycle == 0) _galleryLampObservedAt = null;
            _galleryLampRoundRevision = _boardSession.DuckHuntRoundRevision;
        }
        if (_galleryLampObservedAt is { } previous && now > previous && !_galleryLampPaused && !state.Paused)
            _galleryLampSeconds += (now - previous).TotalSeconds;
        _galleryLampObservedAt = now;
        _galleryLampPaused = state.Paused;
        return (int)Math.Floor(_galleryLampSeconds * 60);
    }

    private double GalleryLampSeconds => Math.Floor(_galleryLampSeconds * 60) / 60;

    private void PrepareGalleryCandles(CanvasDevice device)
    {
        if (_galleryFlameAtlas is not null || _duckHuntGalleryImages?.Image("gallery-unlit-lamps.png") is not { } unlit ||
            _duckHuntGalleryImages.Image("gallery-candle-flames.png") is not { } flames) return;
        _galleryUnlitLampPatches = new CanvasRenderTarget[2];
        for (int side = 0; side < 2; side++)
        {
            var patch = _galleryUnlitLampPatches[side] = new CanvasRenderTarget(device, 68, 103, 96);
            using var path = new CanvasPathBuilder(device);
            path.BeginFigure(GalleryLampGlassOutline[0] - new Vector2(44, 172));
            for (int point = 1; point < GalleryLampGlassOutline.Length; point++)
                path.AddLine(GalleryLampGlassOutline[point] - new Vector2(44, 172));
            path.EndFigure(CanvasFigureLoop.Closed);
            using var glass = CanvasGeometry.CreatePath(path);
            using var draw = patch.CreateDrawingSession();
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            using (draw.CreateLayer(1, glass))
                draw.DrawImage(unlit, new Rect(0, 0, 68, 103), new Rect(44 + side * 1517, 172, 68, 103));
        }
        _galleryFlameAtlas = new CanvasRenderTarget(device, (float)flames.Size.Width, (float)flames.Size.Height, 96);
        // The source remains untouched. Remove its measured one-level black
        // residue once on the GPU so additive sprites have truly neutral edges.
        // Green fill keeps the blue base cyan when it lights the warm glass,
        // avoiding magenta that resembles the laser. Its offset removes both
        // channels' black residue before the cached result is clamped.
        using (var blackPoint = new ColorMatrixEffect
        {
            Source = flames,
            ColorMatrix = new Matrix5x4
            {
                M11 = 1, M22 = 1, M32 = .75f, M33 = 1, M44 = 1,
                M51 = -2f / 255, M52 = -(2f * 1.75f) / 255, M53 = -2f / 255
            },
            ClampOutput = true
        })
        using (var draw = _galleryFlameAtlas.CreateDrawingSession())
        {
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            draw.DrawImage(blackPoint);
        }
        _galleryLampGlow = new CanvasRadialGradientBrush(device,
            Color.FromArgb(32, 255, 223, 165), Color.FromArgb(0, 255, 223, 165));
    }

    private void DisposeGalleryCandles()
    {
        if (_galleryUnlitLampPatches is not null) foreach (var patch in _galleryUnlitLampPatches) patch?.Dispose();
        _galleryUnlitLampPatches = null;
        _galleryFlameAtlas?.Dispose(); _galleryFlameAtlas = null;
        _galleryLampGlow?.Dispose(); _galleryLampGlow = null;
        _galleryLampObservedAt = null;
        _galleryLampRoundRevision = -1;
        _galleryLampSeconds = 0;
        _galleryLampPaused = true;
    }

    private static double GalleryLampPhase(double seconds, bool left) =>
        seconds * (left ? 8.3 : 9.7) + (left ? 0 : 3.7) + .22 * Math.Sin(seconds * 1.7 + (left ? 0 : 2.3));

    private static double GalleryLampLight(double seconds, bool left)
    {
        double phase = GalleryLampPhase(seconds, left);
        int index = (int)Math.Floor(phase) % GalleryFlameBrightness.Length;
        double fraction = phase - Math.Floor(phase);
        return (GalleryFlameBrightness[index] * (1 - fraction) +
            GalleryFlameBrightness[(index + 1) % GalleryFlameBrightness.Length] * fraction) *
            (.94 + .06 * Math.Sin(seconds * 6.7 + (left ? 0 : 1.9)));
    }

    private void DrawGalleryCandles(CanvasDrawingSession drawing, double aspect)
    {
        if (_galleryFlameAtlas is null || _galleryUnlitLampPatches is null) return;
        var frame = DuckHuntGalleryLayout.SceneBounds(aspect);
        double scaleX = frame.Width * BoardSurfaceSize / GalleryLampSceneWidth;
        double scaleY = frame.Height * BoardSurfaceSize / GalleryLampSceneHeight;
        double seconds = GalleryLampSeconds;
        var previousBlend = drawing.Blend;
        try
        {
            drawing.Blend = CanvasBlend.SourceOver;
            for (int side = 0; side < 2; side++)
                drawing.DrawImage(_galleryUnlitLampPatches[side], GallerySceneRect(GalleryLampSceneBounds(side), aspect),
                    new Rect(0, 0, 68, 103), 1, CanvasImageInterpolation.HighQualityCubic);
            drawing.Blend = CanvasBlend.Add;
            if (_galleryLampGlow is { } glow)
            {
                for (int side = 0; side < 2; side++)
                {
                    var center = DuckHuntGalleryLayout.ScenePoint((78 + side * 1517) / GalleryLampSceneWidth, 244 / GalleryLampSceneHeight, aspect);
                    glow.Center = new((float)center.U * BoardSurfaceSize, (float)center.V * BoardSurfaceSize);
                    glow.RadiusX = (float)(21 * scaleX);
                    glow.RadiusY = (float)(35 * scaleY);
                    glow.Opacity = (float)(.68 * GalleryLampLight(seconds, side == 0));
                    drawing.FillEllipse(glow.Center, glow.RadiusX, glow.RadiusY, glow);
                }
            }
            using var sprites = drawing.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
            double cellWidth = _galleryFlameAtlas.Size.Width / 4, cellHeight = _galleryFlameAtlas.Size.Height / 2;
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                double phase = GalleryLampPhase(seconds, left), fraction = phase - Math.Floor(phase);
                int current = (int)Math.Floor(phase) % 8;
                var wick = DuckHuntGalleryLayout.ScenePoint((78 + side * 1517) / GalleryLampSceneWidth, 267 / GalleryLampSceneHeight, aspect);
                float lean = (float)(.014 * Math.Sin(seconds * 4.3 + (left ? 0 : 2.1)));
                var transform = Matrix3x2.CreateTranslation(-(float)(cellWidth / 2), -(float)(cellHeight * (394 / 443.5))) *
                    Matrix3x2.CreateScale((float)(.15 * 443.5 / cellHeight)) * Matrix3x2.CreateRotation(lean) *
                    Matrix3x2.CreateScale((float)scaleX, (float)scaleY) *
                    Matrix3x2.CreateTranslation((float)wick.U * BoardSurfaceSize, (float)wick.V * BoardSurfaceSize);
                for (int blend = 0; blend < 2; blend++)
                {
                    int index = (current + blend) % 8;
                    var source = new Rect(index % 4 * cellWidth, index / 4 * cellHeight, cellWidth, cellHeight);
                    float opacity = (float)((blend == 0 ? 1 - fraction : fraction) *
                        (.94 + .06 * Math.Sin(seconds * 6.7 + (left ? 0 : 1.9))));
                    // Win2D converts this straight-alpha tint to premultiplied
                    // radiance; weighting RGB as well would dim the blend twice.
                    sprites.DrawFromSpriteSheet(_galleryFlameAtlas, transform, source, new Vector4(1, 1, 1, opacity));
                }
            }
        }
        finally { drawing.Blend = previousBlend; }
    }
}
