using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _rouletteMotionTarget, _rouletteRotor;
    private (long Revision, long Frame, double Aspect)? _rouletteMotionKey;
    internal readonly record struct RouletteMotion(double WheelAngle, double BallAngle, double BallRadius, double Progress);

    // An absolute-time path: four complete wheel turns return to the same
    // orientation, and the counter-rotating ball ends at the selected pocket.
    // Neither drawing order nor dropped frames changes the result or duration.
    internal static RouletteMotion GetRouletteMotion(RouletteSnapshot game, DateTimeOffset now)
    {
        double u = game.RoundNumber == 0 ? 1 : Math.Clamp((now - game.RoundStartedAt).TotalSeconds / game.SpinDuration.TotalSeconds, 0, 1);
        double step = Math.Tau / 37;
        double target = (game.PocketIndex ?? 0) * step - Math.PI / 2;
        if (game.Phase != RoulettePhase.Spinning || u >= 1) return new(0, target, 130, 1);
        double previous = (game.History.FirstOrDefault()?.PocketIndex ?? 0) * step - Math.PI / 2;
        double distance = 9 * Math.Tau + ((previous - target) % Math.Tau + Math.Tau) % Math.Tau;
        double smooth = u * u * u * (10 + u * (-15 + 6 * u));
        double wheel = 4 * Math.Tau * smooth;
        double flight = 1 - Math.Pow(1 - u, 3);
        double ball = previous - distance * flight;
        double lift = Math.Clamp(u / .07, 0, 1);
        lift = lift * lift * (3 - 2 * lift);
        double drop = Math.Clamp((u - .67) / .30, 0, 1);
        drop = drop * drop * (3 - 2 * drop);
        double bounce = u > .67 ? 3.3 * Math.Sin((u - .67) * 100) * Math.Pow(1 - drop, 2) : 0;
        double radius = 130 + 29 * lift - 29 * drop + bounce;
        // The final soft clicks settle inside the destination pocket while the
        // wheel slows; all displacement converges to zero at the deadline.
        ball += u > .82 ? Math.Sin((u - .82) * 142) * .012 * Math.Pow((1 - u) / .18, 2) : 0;
        return new(wheel, ball, radius, u);
    }

    private CanvasRenderTarget? DrawRouletteMotionLayer(CanvasDevice device, DateTimeOffset now, double aspect)
    {
        if (_boardSession.Screen != BoardScreen.Roulette) return null;
        if (EnsureBoardRenderTarget(ref _rouletteMotionTarget, device)) _rouletteMotionKey = null;
        var game = _boardSession.RouletteState;
        // Idle wheel rests in the winning pocket. A short reflected highlight
        // and winning glints continue without touching any caption/reference.
        var key = (game.Revision, now.ToUnixTimeMilliseconds() / 16, aspect);
        if (_rouletteMotionKey != key)
        {
            using var ds = _rouletteMotionTarget!.CreateDrawingSession();
            ds.Transform = BoardRasterTransform(_rouletteMotionTarget);
            ds.Clear(Colors.Transparent);
            DrawRouletteWheel(ds, game, now, new(274, 294), aspect);
            _rouletteMotionKey = key;
        }
        return _rouletteMotionTarget;
    }

    private void DrawRouletteWheel(CanvasDrawingSession ds, RouletteSnapshot game, DateTimeOffset now,
        Vector2 center, double aspect, float scale = 1)
    {
        float fit = Math.Min(1, (float)aspect * 1.22f) * scale;
        var original = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(-200, -200) * Matrix3x2.CreateScale(fit / (float)aspect, fit) *
            Matrix3x2.CreateTranslation(center) * original;
        try
        {
            var c = new Vector2(200, 200);
            using var shadow = new CanvasRadialGradientBrush(ds.Device,
                ThemeColor(0, 3, 6, 210), ThemeColor(0, 3, 6, 0))
                { Center = c + new Vector2(0, 9), RadiusX = 191, RadiusY = 184 };
            ds.FillEllipse(c + new Vector2(0, 9), 191, 184, shadow);
            using var mahogany = new CanvasRadialGradientBrush(ds.Device,
                [new() { Position = 0, Color = ThemeColor(30, 20, 23) },
                 new() { Position = .86f, Color = ThemeColor(48, 30, 29) },
                 new() { Position = .92f, Color = ThemeColor(119, 65, 46) },
                 new() { Position = 1, Color = ThemeColor(33, 21, 23) }])
                { Center = c - new Vector2(18, 20), RadiusX = 199, RadiusY = 199 };
            ds.FillCircle(c, 178, mahogany);
            for (int ring = 0; ring < 8; ring++)
                ds.DrawCircle(c, 172 - ring * 1.6f, ThemeColor(228, 170, 106, (byte)(ring % 2 == 0 ? 55 : 18)), .7f);
            using var gold = new CanvasLinearGradientBrush(ds.Device,
                [new() { Position = 0, Color = RouletteCream }, new() { Position = .25f, Color = RouletteGold },
                 new() { Position = .48f, Color = ThemeColor(101, 64, 31) },
                 new() { Position = .72f, Color = ThemeColor(240, 214, 159) }, new() { Position = 1, Color = ThemeColor(91, 60, 37) }])
                { StartPoint = new(30, 20), EndPoint = new(340, 380) };
            ds.DrawCircle(c, 176, gold, 3);
            ds.DrawCircle(c, 163, gold, 3.5f);
            ds.FillCircle(c, 159, ThemeColor(10, 15, 18));
            ds.DrawCircle(c, 155, ThemeColor(112, 109, 87), .8f);
            EnsureRouletteRotor(ds.Device);
            var motion = GetRouletteMotion(game, now);
            var physical = ds.Transform;
            ds.Transform = Matrix3x2.CreateRotation((float)motion.WheelAngle, c) * physical;
            ds.DrawImage(_rouletteRotor!, new Rect(0, 0, 400, 400), new Rect(0, 0, 800, 800),
                1, CanvasImageInterpolation.HighQualityCubic);
            ds.Transform = physical;
            // Fixed lighting falls across rotating polished metal; no rectangular
            // crop or edge fade is used to hide the wheel's true circular shape.
            using var glint = new CanvasRadialGradientBrush(ds.Device,
                ThemeColor(255, 244, 204, 35), ThemeColor(255, 244, 204, 0))
                { Center = new(144, 151), RadiusX = 82, RadiusY = 80 };
            ds.FillCircle(c, 113, glint);
            if (game.Phase == RoulettePhase.Spinning && motion.Progress < .87)
            {
                for (int tail = 6; tail > 0; tail--)
                {
                    var prior = GetRouletteMotion(game, now.AddMilliseconds(-tail * 7));
                    var point = c + new Vector2((float)Math.Cos(prior.BallAngle), (float)Math.Sin(prior.BallAngle)) * (float)prior.BallRadius;
                    ds.FillCircle(point, 3.8f - tail * .23f, ThemeColor(255, 247, 221, (byte)(27 - tail * 3)));
                }
            }
            var ball = c + new Vector2((float)Math.Cos(motion.BallAngle), (float)Math.Sin(motion.BallAngle)) * (float)motion.BallRadius;
            ds.FillCircle(ball + new Vector2(1.7f, 2.3f), 5.2f, ThemeColor(0, 0, 0, 200));
            using var ivory = new CanvasRadialGradientBrush(ds.Device,
                [new() { Position = 0, Color = Colors.White }, new() { Position = .45f, Color = RouletteCream },
                 new() { Position = 1, Color = ThemeColor(141, 121, 84) }])
                { Center = ball - new Vector2(1.7f, 1.8f), RadiusX = 7, RadiusY = 7 };
            ds.FillCircle(ball, 4.6f, ivory);
            ds.FillCircle(ball - new Vector2(1.2f, 1.4f), 1.05f, Colors.White);
            if (game.Phase == RoulettePhase.Betting && game.LastWin > 0 && game.RoundNumber > 0)
            {
                double age = (now - game.RoundStartedAt - game.SpinDuration).TotalSeconds;
                if (age is >= 0 and < 3)
                    for (int i = 0; i < 14; i++)
                    {
                        double phase = age + i * .21;
                        float alpha = (float)(Math.Pow(Math.Max(0, Math.Sin(phase * 5.3)), 8) * (1 - age / 3));
                        var p = c + new Vector2(MathF.Cos(i * 2.399f), MathF.Sin(i * 2.399f)) * (133 + i % 4 * 10);
                        var color = WithAlpha(RouletteCream, (byte)(180 * alpha));
                        ds.DrawLine(p - new Vector2(3, 0), p + new Vector2(3, 0), color, .8f);
                        ds.DrawLine(p - new Vector2(0, 3), p + new Vector2(0, 3), color, .8f);
                    }
            }
        }
        finally { ds.Transform = original; }
    }

    private void EnsureRouletteRotor(CanvasDevice device)
    {
        if (_rouletteRotor?.Device == device) return;
        _rouletteRotor?.Dispose();
        _rouletteRotor = new CanvasRenderTarget(device, 800, 800, 96);
        using var ds = _rouletteRotor.CreateDrawingSession();
        ds.Transform = Matrix3x2.CreateScale(2); ds.Clear(Colors.Transparent);
        var c = new Vector2(200, 200);
        double step = Math.Tau / 37;
        for (int i = 0; i < 37; i++)
        {
            double angle = i * step - Math.PI / 2;
            int number = RouletteGame.WheelOrder[i];
            using var pocket = RouletteSector(device, c, 113, 151, angle - step / 2, angle + step / 2);
            ds.FillGeometry(pocket, number == 0 ? ThemeColor(15, 121, 84) : RouletteGame.IsRed(number)
                ? ThemeColor(146, 25, 48) : ThemeColor(12, 21, 28));
            ds.DrawGeometry(pocket, ThemeColor(232, 204, 152, 175), .6f);
            var textTransform = ds.Transform;
            ds.Transform = Matrix3x2.CreateRotation((float)(angle + Math.PI / 2), c) * textTransform;
            using var text = RouletteTextFormat(10.5f);
            ds.DrawText(number.ToString(), new Rect(187, 50, 26, 20), RouletteCream, text);
            ds.Transform = textTransform;
            var radial = new Vector2((float)Math.Cos(angle - step / 2), (float)Math.Sin(angle - step / 2));
            ds.DrawLine(c + radial * 115, c + radial * 134, ThemeColor(255, 237, 188), 1.1f);
        }
        using var cone = new CanvasRadialGradientBrush(device,
            [new() { Position = 0, Color = ThemeColor(40, 42, 39) }, new() { Position = .25f, Color = ThemeColor(194, 149, 80) },
             new() { Position = .55f, Color = ThemeColor(39, 51, 51) }, new() { Position = .86f, Color = ThemeColor(18, 31, 33) },
             new() { Position = 1, Color = ThemeColor(139, 117, 75) }])
            { Center = c - new Vector2(9, 11), RadiusX = 123, RadiusY = 122 };
        ds.FillCircle(c, 112, cone);
        for (int i = 0; i < 37; i++)
        {
            var radial = new Vector2(MathF.Cos((float)(i * step)), MathF.Sin((float)(i * step)));
            ds.DrawLine(c + radial * 47, c + radial * 105, ThemeColor(219, 189, 127, 28), .5f);
        }
        ds.DrawCircle(c, 112, RouletteGold, 1.5f);
        ds.DrawCircle(c, 105, ThemeColor(97, 80, 56), .8f);
        using var brass = new CanvasLinearGradientBrush(device, RouletteCream, ThemeColor(92, 60, 30))
            { StartPoint = new(173, 174), EndPoint = new(225, 228) };
        for (int i = 0; i < 4; i++)
        {
            var end = c + new Vector2(MathF.Cos(i * MathF.PI / 2 + .3f), MathF.Sin(i * MathF.PI / 2 + .3f)) * 47;
            ds.DrawLine(c, end, ThemeColor(36, 32, 24), 10);
            ds.DrawLine(c, end, brass, 7);
            ds.FillCircle(end, 6, brass);
        }
        ds.FillCircle(c + new Vector2(1, 2), 25, ThemeColor(4, 7, 10, 150));
        ds.FillCircle(c, 23, brass);
        ds.DrawCircle(c, 19, ThemeColor(97, 71, 37), 1);
        ds.FillCircle(c - new Vector2(1, 3), 10, brass);
        ds.FillCircle(c - new Vector2(3, 6), 3, RouletteCream);
    }

    private static CanvasGeometry RouletteSector(CanvasDevice device, Vector2 center, float inner, float outer,
        double start, double end)
    {
        using var path = new CanvasPathBuilder(device);
        Vector2 Point(double a, float r) => center + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * r;
        path.BeginFigure(Point(start, inner)); path.AddLine(Point(start, outer));
        for (int i = 1; i <= 5; i++) path.AddLine(Point(start + (end - start) * i / 5, outer));
        path.AddLine(Point(end, inner));
        for (int i = 4; i >= 0; i--) path.AddLine(Point(start + (end - start) * i / 5, inner));
        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }

    private void DisposeRouletteLayers()
    {
        _rouletteMotionTarget?.Dispose(); _rouletteMotionTarget = null; _rouletteMotionKey = null;
        _rouletteRotor?.Dispose(); _rouletteRotor = null;
        _roulettePreviewTarget?.Dispose(); _roulettePreviewTarget = null; _roulettePreviewKey = null;
        _rouletteBackdrop?.Dispose(); _rouletteBackdrop = null; _rouletteBackdropFailed = false;
    }
}
