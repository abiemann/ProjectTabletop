using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const double GalleryEyeFadeSeconds = .10;
    private CanvasRenderTarget? _gallerySkullEyes;
    private readonly float[] _galleryEyeAmounts = new float[DuckHuntGalleryGame.SkullCount];
    private DateTimeOffset? _galleryEyesObservedAt;
    private long _galleryEyesInputEpoch = -1, _galleryEyesRoundRevision = -1;

    internal float GallerySkullEyeAmount(int index) => _galleryEyeAmounts[index];

    internal int GalleryEyeVisualFrame(DateTimeOffset animationNow) =>
        GalleryEyeVisualFrame(animationNow, MonotonicClock.UtcNow);

    internal int GalleryEyeVisualFrame(DateTimeOffset animationNow, DateTimeOffset aimNow)
    {
        var context = SyncDuckHuntInput(aimNow);
        var state = _boardSession.GalleryState;
        long epoch = context?.Epoch ?? -1;
        if (_galleryEyesInputEpoch != epoch || _galleryEyesRoundRevision != _boardSession.DuckHuntRoundRevision)
        {
            ClearGalleryEyes(animationNow);
            _galleryEyesInputEpoch = epoch;
            _galleryEyesRoundRevision = _boardSession.DuckHuntRoundRevision;
        }
        if (_boardSession.Screen != BoardScreen.DuckHunt || _boardSession.CurrentDuckHuntView != DuckHuntView.TargetPractice ||
            state.UpperPhase != DuckHuntGalleryUpperPhase.WaitingForHits || state.Paused ||
            context is null || !_duckHuntAudioReady || !DuckHuntArtworkReady)
        {
            ClearGalleryEyes(animationNow);
            return 0;
        }
        int desired = -1;
        if (_duckHuntMeasurement.GetSnapshot(aimNow).LiveDetectedXY is { } aim)
        {
            double aspect = PaintBoardAspect();
            for (int index = 0; index < _galleryEyeAmounts.Length; index++)
                if ((state.SkullHitMask & (1 << index)) == 0 && DuckHuntGalleryLayout.ContainsSkull(index, aim, aspect))
                {
                    desired = index;
                    break;
                }
        }
        double elapsed = _galleryEyesObservedAt is { } previous ? Math.Max(0, (animationNow - previous).TotalSeconds) : 0;
        _galleryEyesObservedAt = animationNow;
        int frame = 0;
        for (int index = 0; index < _galleryEyeAmounts.Length; index++)
        {
            // Confirmed aim lights the sockets immediately. Restarting a fade-in
            // on every valid/null transition could leave them dark indefinitely
            // during brief camera dropouts. Only the visual fade-out bridges gaps;
            // shot detection still requires its own fresh camera snapshot.
            float amount = (state.SkullHitMask & (1 << index)) != 0 ? 0 : index == desired ? 1 :
                (float)Math.Clamp(_galleryEyeAmounts[index] - elapsed / GalleryEyeFadeSeconds, 0, 1);
            _galleryEyeAmounts[index] = amount;
            frame |= (int)Math.Round(amount * 31) << (index * 5);
        }
        return frame;
    }

    private void ClearGalleryEyes(DateTimeOffset now)
    {
        Array.Clear(_galleryEyeAmounts);
        _galleryEyesObservedAt = now;
    }

    private void PrepareGalleryEyes(CanvasDevice device)
    {
        if (_gallerySkullEyes is not null || _gallerySkullSprite is null) return;
        using var sockets = new CanvasPathBuilder(device);
        for (int eye = 0; eye < 2; eye++)
        {
            Vector2 Point(float x, float y) => new Vector2(eye == 0 ? x : 1254 - x, y) * (GallerySkullRasterSize / 1254f);
            sockets.BeginFigure(Point(380, 527));
            sockets.AddCubicBezier(Point(427, 501), Point(496, 521), Point(535, 552));
            sockets.AddCubicBezier(Point(559, 572), Point(548, 609), Point(519, 637));
            sockets.AddCubicBezier(Point(485, 663), Point(425, 682), Point(383, 673));
            sockets.AddCubicBezier(Point(349, 648), Point(341, 603), Point(353, 560));
            sockets.AddCubicBezier(Point(358, 545), Point(367, 533), Point(380, 527));
            sockets.EndFigure(CanvasFigureLoop.Closed);
        }
        using var shape = CanvasGeometry.CreatePath(sockets);
        // Preserve the photographed facets as luminance inside the real sockets.
        // Warm luminous yellow keeps the crystal's bright and dark facets.
        // Green stays close to red and well above blue, so neither the glow nor
        // its highlights resemble a red/magenta laser or a clipped white core.
        using var yellow = new ColorMatrixEffect
        {
            Source = _gallerySkullSprite,
            ColorMatrix = new Matrix5x4
            {
                M11 = .120f, M12 = .1075f, M13 = .040f,
                M21 = .288f, M22 = .2580f, M23 = .096f,
                M31 = .072f, M32 = .0645f, M33 = .024f,
                M44 = 1, M51 = .42f, M52 = .37f, M53 = .06f
            },
            ClampOutput = true
        };
        _gallerySkullEyes = new CanvasRenderTarget(device, GallerySkullRasterSize, GallerySkullRasterSize, 96);
        using var draw = _gallerySkullEyes.CreateDrawingSession();
        draw.Clear(Color.FromArgb(0, 0, 0, 0));
        using (draw.CreateLayer(1, shape)) draw.DrawImage(yellow);
    }

    private void DisposeGalleryEyes()
    {
        _gallerySkullEyes?.Dispose(); _gallerySkullEyes = null;
        ClearGalleryEyes(DateTimeOffset.MinValue);
        _galleryEyesObservedAt = null;
        _galleryEyesInputEpoch = _galleryEyesRoundRevision = -1;
    }

    private void DrawGalleryEyes(CanvasDrawingSession drawing, int index, Rect bounds)
    {
        if (_gallerySkullEyes is null || _galleryEyeAmounts[index] <= 0) return;
        var previousBlend = drawing.Blend;
        try
        {
            drawing.Blend = CanvasBlend.SourceOver;
            drawing.DrawImage(_gallerySkullEyes, bounds, new Rect(0, 0, GallerySkullRasterSize, GallerySkullRasterSize),
                _galleryEyeAmounts[index], CanvasImageInterpolation.HighQualityCubic);
        }
        finally { drawing.Blend = previousBlend; }
    }
}
