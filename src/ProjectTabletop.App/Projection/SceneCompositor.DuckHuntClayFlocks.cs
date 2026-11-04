using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const int ClayBirdWingFrames = 32;
    private CanvasGeometry[]? _clayBirdWings;
    private CanvasGeometry? _clayBirdBody;
    private DateTimeOffset _clayFlockOrigin;

    private void PrepareClayHorizonFlocks(CanvasDevice device)
    {
        if (_clayBirdWings is not null) return;
        _clayFlockOrigin = _blackjackClock();
        _clayBirdWings = new CanvasGeometry[ClayBirdWingFrames];
        // Small, tapered silhouettes stay sharp at projector resolution. Cache
        // the curved wing poses once; no paths or images are built per frame.
        for (int frame = 0; frame < ClayBirdWingFrames; frame++)
        {
            float lift = (float)Math.Sin(frame * Math.Tau / ClayBirdWingFrames);
            float tipY = -.30f * lift;
            float span = .48f - .08f * MathF.Abs(lift);
            using var path = new CanvasPathBuilder(device);
            foreach (int side in new[] { -1, 1 })
            {
                path.BeginFigure(new Vector2(side * .025f, -.015f));
                path.AddCubicBezier(new(side * .17f, -.08f - lift * .12f),
                    new(side * span * .78f, tipY - .055f), new(side * span, tipY));
                path.AddCubicBezier(new(side * span * .87f, tipY + .022f),
                    new(side * .24f, tipY * .5f + .065f), new(side * .055f, .075f));
                path.AddLine(new Vector2(side * .025f, -.015f));
                path.EndFigure(CanvasFigureLoop.Closed);
            }
            _clayBirdWings[frame] = CanvasGeometry.CreatePath(path);
        }
        using var body = new CanvasPathBuilder(device);
        body.BeginFigure(new Vector2(0, -.125f));
        body.AddCubicBezier(new(.07f, -.12f), new(.055f, .03f), new(.026f, .12f));
        body.AddLine(new Vector2(.052f, .20f));
        body.AddLine(new Vector2(0, .175f));
        body.AddLine(new Vector2(-.052f, .20f));
        body.AddLine(new Vector2(-.026f, .12f));
        body.AddCubicBezier(new(-.055f, .03f), new(-.07f, -.12f), new(0, -.125f));
        body.EndFigure(CanvasFigureLoop.Closed);
        _clayBirdBody = CanvasGeometry.CreatePath(body);
    }

    private void DisposeClayHorizonFlocks()
    {
        if (_clayBirdWings is not null)
            foreach (var wing in _clayBirdWings) wing?.Dispose();
        _clayBirdWings = null;
        _clayBirdBody?.Dispose();
        _clayBirdBody = null;
    }

    internal long ClayFlockVisualFrame(DateTimeOffset now) =>
        _boardSession.Screen == BoardScreen.DuckHunt &&
        _boardSession.CurrentDuckHuntView == DuckHuntView.ClayPigeon && _clayBirdWings is not null
            ? (long)(Math.Max(0, (now - _clayFlockOrigin).TotalSeconds) * 30) : 0;

    internal void DrawClayHorizonFlocks(CanvasDrawingSession drawing, DateTimeOffset now, double aspect)
    {
        if (_clayBirdWings is not { } wings || _clayBirdBody is not { } body ||
            _duckHuntClayImages?.Image("clay-valley-v1.png") is not { } valley) return;
        double time = ClayFlockVisualFrame(now) / 30d;
        var crop = DuckHuntSceneryCrop(valley, aspect, .04);
        double imageAspect = valley.Size.Width / valley.Size.Height;
        double magnification = valley.Size.Height / crop.Height;
        var previous = drawing.Transform;
        try
        {
            for (int flock = 0; flock < 3; flock++)
            {
                double orbit = time * (.045 - flock * .009) + .2 + flock * 2.1;
                double centerU = .5 + (.29 - flock * .035) * Math.Sin(orbit);
                double centerV = .405 + flock * .018 + .009 * Math.Cos(orbit);
                double heading = Math.Cos(orbit);
                double depth = .80 + .18 * Math.Sin(orbit);
                int count = flock == 0 ? 9 : flock == 1 ? 7 : 6;
                var color = Color.FromArgb(235, (byte)(38 + flock * 10),
                    (byte)(56 + flock * 10), (byte)(69 + flock * 11));
                for (int bird = 0; bird < count; bird++)
                {
                    int rank = (bird + 1) / 2;
                    int side = (bird & 1) == 0 ? 1 : -1;
                    double seed = bird * 2.39996323 + flock * 4.7;
                    // Loose V formations breathe and bank rather than moving
                    // as rigid stamps. The source coordinates keep them above
                    // the photographed ridge through every scenery crop.
                    double u = centerU - heading * rank * .014 * depth / imageAspect +
                        .0012 * Math.Sin(time * .61 + seed);
                    double v = centerV + side * rank * .0046 * depth +
                        .0011 * Math.Sin(time * .79 + seed);
                    u = (u * valley.Size.Width - crop.X) / crop.Width;
                    v = (v * valley.Size.Height - crop.Y) / crop.Height;
                    if (u < -.02 || u > 1.02 || v < 0 || v > .65) continue;
                    float size = (float)((8.8 - flock * 1.05) * depth *
                        (1 + .09 * Math.Sin(seed)) * magnification);
                    // Each bird alternates short glides and independent wingbeats.
                    double cycle = (time + seed + 30) % 6.4;
                    double envelope = Math.Clamp(Math.Min(cycle, 4.5 - cycle) / .28, 0, 1);
                    envelope = envelope * envelope * (3 - 2 * envelope);
                    double lift = Math.Sin(cycle * (2.5 + .12 * Math.Sin(seed)) * Math.Tau) * envelope;
                    int frame = ((int)Math.Round(Math.Asin(lift) / Math.Tau * ClayBirdWingFrames) +
                        ClayBirdWingFrames) % ClayBirdWingFrames;
                    float bank = (float)(-.18 * Math.Sin(orbit) + .055 * Math.Sin(time + seed));
                    drawing.Transform = Matrix3x2.CreateScale(size, size * (float)(.60 + .24 * Math.Abs(heading))) *
                        Matrix3x2.CreateRotation(bank) * Matrix3x2.CreateScale((float)(1 / aspect), 1) *
                        Matrix3x2.CreateTranslation((float)u * BoardSurfaceSize, (float)v * BoardSurfaceSize) * previous;
                    drawing.FillGeometry(wings[frame], color);
                    drawing.FillGeometry(body, color);
                }
            }
        }
        finally { drawing.Transform = previous; }
    }
}
