using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _rouletteMotionTarget, _rouletteFixedBowl, _rouletteForegroundRim;
    private (long Revision, long Frame, double Aspect)? _rouletteMotionKey;
    internal readonly record struct RouletteMotion(double WheelAngle, double BallAngle, double BallRadius, double Progress);

    internal static double RouletteRestingWheelAngle(long round) => PositiveRouletteAngle(round * 2.399963229728653);
    private static double PositiveRouletteAngle(double angle) => (angle % Math.Tau + Math.Tau) % Math.Tau;

    // Retain the rotor's orientation between rounds. The camera, bowl and
    // illumination remain fixed while physical pockets and spindle move.
    internal static RouletteMotion GetRouletteMotion(RouletteSnapshot game, DateTimeOffset now)
    {
        double u = game.RoundNumber == 0 ? 1 : Math.Clamp(
            (now - game.RoundStartedAt).TotalSeconds / game.SpinDuration.TotalSeconds, 0, 1);
        double end = RouletteRestingWheelAngle(game.RoundNumber);
        double pocket = (game.PocketIndex ?? 0) * Math.Tau / 37 - Math.PI / 2;
        if (game.Phase != RoulettePhase.Spinning || u >= 1) return new(end, pocket + end, 130, 1);
        double start = RouletteRestingWheelAngle(game.RoundNumber - 1);
        double previous = (game.History.FirstOrDefault()?.PocketIndex ?? 0) * Math.Tau / 37 - Math.PI / 2 + start;
        double wheelTravel = 4 * Math.Tau + PositiveRouletteAngle(end - start);
        double smooth = u * u * u * (10 + u * (-15 + 6 * u));
        double wheel = start + wheelTravel * smooth;
        double ballTravel = 9 * Math.Tau + PositiveRouletteAngle(previous - pocket - end);
        double ball = previous - ballTravel * (1 - Math.Pow(1 - u, 3));
        // Choose winding once from terminal geometry, then let the winning
        // pocket catch and carry the ball through the last deceleration.
        double turns = Math.Round((previous - ballTravel - start - wheelTravel - pocket) / Math.Tau);
        double attached = wheel + pocket + turns * Math.Tau;
        double catchWeight = RouletteEase((u - .90) / .07);
        ball = ball * (1 - catchWeight) + attached * catchWeight;
        double lift = RouletteEase(u / .07), drop = RouletteEase((u - .67) / .30);
        double bounce = u > .67 ? 3.3 * Math.Sin((u - .67) * 100) * Math.Pow(1 - drop, 2) : 0;
        double radius = Math.Min(159, 130 + 29 * lift - 29 * drop + bounce);
        if (u is > .82 and < .97)
            ball += Math.Sin((u - .82) * 142) * .012 * Math.Pow((1 - u) / .18, 2) * (1 - catchWeight);
        return new(wheel, ball, radius, u);
    }

    private static double RouletteEase(double value)
    {
        double u = Math.Clamp(value, 0, 1);
        return u * u * (3 - 2 * u);
    }

    // Rotate world coordinates before this single stationary camera projection.
    // Near-side parts grow slightly; height supplies bowl and spindle depth.
    internal static Vector2 ProjectRouletteWheelPoint(double x, double y, double height)
    {
        double perspective = 1 / (1 - y * .00045);
        return new((float)(200 + x * perspective), (float)(200 + (y * .79 - height * .64) * perspective));
    }

    internal static Vector2 ProjectRouletteBall(RouletteMotion motion) =>
        RoulettePolarPoint(motion.BallRadius, motion.BallAngle, RouletteBallHeight(motion));

    private static double RouletteBallHeight(RouletteMotion motion)
    {
        double track = RouletteEase((motion.BallRadius - 136) / 21);
        double bounce = motion.Progress is > .67 and < .97
            ? Math.Abs(Math.Sin((motion.Progress - .67) * 100)) * 2.5 * (1 - RouletteEase((motion.Progress - .67) / .30)) : 0;
        // Height describes the sphere's centre, not the track's contact plane:
        // keep its bottom on the pocket floor/bank and its crown above the lip.
        return -5.2 + track * 15.8 + bounce;
    }

    private static Vector2 RoulettePolarPoint(double radius, double angle, double height) =>
        ProjectRouletteWheelPoint(Math.Cos(angle) * radius, Math.Sin(angle) * radius, height);

    private CanvasRenderTarget? DrawRouletteMotionLayer(CanvasDevice device, DateTimeOffset now, double aspect)
    {
        if (_boardSession.Screen != BoardScreen.Roulette) return null;
        if (EnsureBoardRenderTarget(ref _rouletteMotionTarget, device)) _rouletteMotionKey = null;
        var game = _boardSession.RouletteState;
        // Once the ball rests, the rotor, spindle and their illumination are
        // unchanged. Retain their raster until another round or layout change;
        // only a spin and the short winning glints require new timed frames.
        double winAge = (now - game.RoundStartedAt - game.SpinDuration).TotalSeconds;
        bool animated = game.Phase == RoulettePhase.Spinning ||
            game.Phase == RoulettePhase.Betting && game.LastWin > 0 && game.RoundNumber > 0 &&
            winAge is >= 0 and < 3;
        var key = (game.Revision, animated ? now.ToUnixTimeMilliseconds() / 16 : -1, aspect);
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
            EnsureRouletteFixedBowl(ds.Device);
            ds.DrawImage(_rouletteFixedBowl!, new Rect(0, 0, 400, 400), new Rect(0, 0, 1600, 1600),
                1, CanvasImageInterpolation.HighQualityCubic);
            var motion = GetRouletteMotion(game, now);
            DrawRoulettePocketRotor(ds, motion.WheelAngle);
            DrawRouletteSpindle(ds, motion.WheelAngle);
            DrawRouletteBall(ds, motion, game.Phase == RoulettePhase.Spinning);
            // The entire near-side casing occludes the ball, not just a narrow
            // gold stripe that could leave its lower half painted on the wood.
            ds.DrawImage(_rouletteForegroundRim!, new Rect(0, 0, 400, 400), new Rect(0, 0, 1600, 1600),
                1, CanvasImageInterpolation.HighQualityCubic);
            DrawRouletteWinningGlints(ds, game, now);
        }
        finally { ds.Transform = original; }
    }

    private void EnsureRouletteFixedBowl(CanvasDevice device)
    {
        if (_rouletteFixedBowl?.Device == device && _rouletteForegroundRim?.Device == device) return;
        _rouletteFixedBowl?.Dispose();
        _rouletteForegroundRim?.Dispose();
        _rouletteFixedBowl = new CanvasRenderTarget(device, 1600, 1600, 96);
        using (var ds = _rouletteFixedBowl.CreateDrawingSession())
        {
            ds.Transform = Matrix3x2.CreateScale(4);
            ds.Clear(Colors.Transparent);
            DrawRouletteFixedBowl(ds);
        }
        _rouletteForegroundRim = new CanvasRenderTarget(device, 1600, 1600, 96);
        using (var ds = _rouletteForegroundRim.CreateDrawingSession())
        {
            ds.Transform = Matrix3x2.CreateScale(4);
            ds.Clear(Colors.Transparent);
            DrawRouletteCasing(ds, nearSideOnly: true);
            DrawRouletteNearLip(ds);
        }
    }

    private void DrawRouletteFixedBowl(CanvasDrawingSession ds)
    {
        using var shadow = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(0, 3, 6, 165), ThemeColor(0, 3, 6, 0))
            { Center = new(200, 230), RadiusX = 189, RadiusY = 137 };
        ds.FillEllipse(new(200, 230), 189, 137, shadow);
        DrawRouletteCasing(ds);
        using var bank = new CanvasLinearGradientBrush(ds.Device,
            [new() { Position = 0, Color = ThemeColor(15, 26, 28) },
             new() { Position = .40f, Color = ThemeColor(82, 82, 67) },
             new() { Position = .64f, Color = ThemeColor(17, 26, 28) },
             new() { Position = 1, Color = ThemeColor(124, 105, 65) }])
            { StartPoint = new(83, 93), EndPoint = new(300, 330) };
        using (var track = RouletteSurfaceBand(ds.Device, 153, -4, 159, 6))
            ds.FillGeometry(track, bank);
        DrawRouletteProjectedCircle(ds, 157.5, 5, ThemeColor(213, 194, 136, 120), .8f);
        using (var interior = RouletteSurfaceBand(ds.Device, 0, -12, 153, -4))
            ds.FillGeometry(interior, ThemeColor(16, 23, 24));
        // The figured wooden cone belongs to the rotor and is drawn with it.
        // Only the stationary track, casing and deflectors live in this cache.
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.Tau / 8 + .24;
            var tip = RoulettePolarPoint(155, angle, 2);
            using var diamond = RouletteQuad(ds.Device, RoulettePolarPoint(155, angle - .012, -.5),
                RoulettePolarPoint(154.1, angle, -.5), tip, RoulettePolarPoint(156.8, angle, -.5));
            ds.FillGeometry(diamond, ThemeColor(163, 128, 68));
            ds.DrawLine(RoulettePolarPoint(155, angle - .012, -.5), tip, RouletteCream, .55f);
        }
    }

    private void DrawRouletteCasing(CanvasDrawingSession ds, bool nearSideOnly = false)
    {
        double end = nearSideOnly ? Math.PI : Math.Tau;
        using var wall = new CanvasLinearGradientBrush(ds.Device,
            [new() { Position = 0, Color = ThemeColor(73, 38, 28) },
             new() { Position = .35f, Color = ThemeColor(131, 69, 40) },
             new() { Position = .64f, Color = ThemeColor(35, 23, 24) },
             new() { Position = 1, Color = ThemeColor(98, 51, 34) }])
            { StartPoint = new(30, 180), EndPoint = new(360, 350) };
        using (var body = RouletteSurfaceBand(ds.Device, 186, -10, 186, 8, 0, Math.PI))
            ds.FillGeometry(body, wall);
        DrawRouletteBurlSurface(ds, cone: false, nearSideOnly: nearSideOnly);
        using var gold = RouletteMetalBrush(ds.Device, new(40, 65), new(347, 344));
        using (var lip = RouletteSurfaceBand(ds.Device, 158, 6, 164, 8, 0, end))
            ds.FillGeometry(lip, gold);
        // Fine inlay and moulded edges leave broad, uninterrupted burl between
        // the brass details. The near-side wall supplies actual bowl thickness.
        DrawRouletteProjectedCircle(ds, 186, -9.5, ThemeColor(18, 14, 13), 2.5f, 0, Math.PI);
        DrawRouletteProjectedCircle(ds, 186, -6.5, gold, 1.1f, 0, Math.PI);
        DrawRouletteProjectedCircle(ds, 186, 8, ThemeColor(34, 20, 16), 2.7f, 0, end);
        DrawRouletteProjectedCircle(ds, 185, 8, gold, 1.2f, 0, end);
        DrawRouletteProjectedCircle(ds, 181.8, 8, ThemeColor(237, 205, 146, 175), .55f, 0, end);
        DrawRouletteProjectedCircle(ds, 180.3, 8, ThemeColor(29, 16, 12, 170), .65f, 0, end);
        DrawRouletteProjectedCircle(ds, 166.4, 8, ThemeColor(239, 204, 145, 170), .55f, 0, end);
        DrawRouletteProjectedCircle(ds, 162, 8, ThemeColor(255, 238, 184), 1.1f, 0, end);
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.Tau / 24;
            if (nearSideOnly && angle > Math.PI) continue;
            using var inlay = RouletteQuad(ds.Device, RoulettePolarPoint(177.6, angle, 8.1),
                RoulettePolarPoint(179, angle - .004, 8.1), RoulettePolarPoint(180.4, angle, 8.1),
                RoulettePolarPoint(179, angle + .004, 8.1));
            ds.FillGeometry(inlay, gold);
        }
    }

    private static CanvasLinearGradientBrush RouletteMetalBrush(CanvasDevice device, Vector2 start, Vector2 end) =>
        new(device, [new() { Position = 0, Color = ThemeColor(255, 241, 201) },
             new() { Position = .23f, Color = ThemeColor(185, 142, 78) },
             new() { Position = .48f, Color = ThemeColor(91, 66, 38) },
             new() { Position = .72f, Color = ThemeColor(235, 207, 145) },
             new() { Position = 1, Color = ThemeColor(98, 71, 39) }])
             { StartPoint = start, EndPoint = end };

    private static double RouletteConeHeight(double radius) => 24 - 36 * Math.Pow(radius / 112, .85);

    private void DrawRoulettePocketRotor(CanvasDrawingSession ds, double wheelAngle)
    {
        DrawRouletteBurlSurface(ds, cone: true, wheelAngle: wheelAngle);
        double step = Math.Tau / 37;
        using var text = RouletteTextFormat(12);
        using var gold = RouletteMetalBrush(ds.Device, new(42, 72), new(348, 350));
        DrawRouletteProjectedCircle(ds, 112.8, -11, gold, 2.1f);
        DrawRouletteProjectedCircle(ds, 109.2, -10.7, ThemeColor(242, 210, 151, 165), .6f);
        for (int i = 0; i < 37; i++)
        {
            double angle = i * step - Math.PI / 2 + wheelAngle;
            int number = RouletteGame.WheelOrder[i];
            double illumination = .88 + .12 * Math.Cos(angle + Math.PI * .65);
            Color face = number == 0 ? ThemeColor(12, (byte)(119 * illumination), 77)
                : RouletteGame.IsRed(number) ? ThemeColor((byte)(161 * illumination), 22, 39)
                : ThemeColor(9, 16, 19);
            using (var band = RouletteSurfaceBand(ds.Device, 137, -4, 153, -4, angle - step / 2, angle + step / 2))
            {
                ds.FillGeometry(band, face);
                ds.DrawGeometry(band, ThemeColor(212, 184, 131, 185), .55f);
            }
            Color pocket = number == 0 ? ThemeColor(10, 72, 54)
                : RouletteGame.IsRed(number) ? ThemeColor(86, 17, 31) : ThemeColor(7, 15, 19);
            using (var well = RouletteSurfaceBand(ds.Device, 114, -10, 136, -10, angle - step / 2, angle + step / 2))
                ds.FillGeometry(well, pocket);
            using (var edge = RouletteSurfaceBand(ds.Device, 136, -10, 137, -4, angle - step / 2, angle + step / 2))
                ds.FillGeometry(edge, gold);
            using (var gloss = RouletteSurfaceBand(ds.Device, 151.3, -3.9, 152.2, -3.9,
                angle - step * .43, angle + step * .43))
                ds.FillGeometry(gloss, ThemeColor(255, 226, 191, (byte)(12 + 16 * illumination)));
            DrawRoulettePocketLabel(ds, number, angle, text);
        }
        // Solid, bevelled brass fins rise out of the coloured pocket floors.
        // Render them after the wells so adjacent floors cannot erase a face.
        var dividers = Enumerable.Range(0, 37).Select(i => i * step - Math.PI / 2 + wheelAngle - step / 2)
            .OrderBy(angle => RoulettePolarPoint(126, angle, -4).Y);
        foreach (double angle in dividers)
        {
            var inner = RoulettePolarPoint(114, angle, -5);
            var left = RoulettePolarPoint(136, angle - .030, -4);
            var right = RoulettePolarPoint(136, angle + .030, -4);
            var crest = RoulettePolarPoint(136, angle, -2);
            var lower = RoulettePolarPoint(114, angle, -10);
            var tangent = new Vector3((float)-Math.Sin(angle), (float)Math.Cos(angle), 0);
            var radial = new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
            using (var side = RouletteQuad(ds.Device, lower, RoulettePolarPoint(136, angle + .030, -10), right, inner))
                ds.FillGeometry(side, RouletteBrassFacet(Vector3.Normalize(tangent * 2 + Vector3.UnitZ * .3f)));
            using (var face = RouletteQuad(ds.Device, inner, left, crest))
                ds.FillGeometry(face, RouletteBrassFacet(Vector3.Normalize(-tangent * .49f - radial * .14f + Vector3.UnitZ)));
            using (var face = RouletteQuad(ds.Device, inner, crest, right))
                ds.FillGeometry(face, RouletteBrassFacet(Vector3.Normalize(tangent * .49f - radial * .14f + Vector3.UnitZ)));
            ds.DrawLine(inner, crest, ThemeColor(255, 241, 198, 225), .6f);
            ds.DrawLine(inner, right, ThemeColor(126, 88, 40, 200), .45f);
        }
        DrawRouletteProjectedCircle(ds, 137, -4, gold, 1);
        DrawRouletteProjectedCircle(ds, 153, -4, ThemeColor(237, 212, 162), 1.05f);
        DrawRouletteProjectedCircle(ds, 36, RouletteConeHeight(36) + .2, gold, .65f);
        DrawRouletteProjectedCircle(ds, 39, RouletteConeHeight(39) + .2, ThemeColor(48, 24, 14), .6f);
        for (int i = 0; i < 37; i++)
        {
            double angle = i * step - Math.PI / 2 + wheelAngle;
            ds.DrawLine(RoulettePolarPoint(109.4, angle, RouletteConeHeight(109.4) + .2),
                RoulettePolarPoint(111.2, angle, RouletteConeHeight(111.2) + .2), ThemeColor(236, 206, 147, 190), .5f);
        }
    }

    private static Color RouletteBrassFacet(Vector3 normal)
    {
        // Facet normals rotate with the brass; room illumination does not.
        // The warm key and cool room reflection keep bevels visibly distinct.
        var light = Vector3.Normalize(new(-.55f, -.72f, 1.45f));
        var half = Vector3.Normalize(light + Vector3.Normalize(new(0, -.63f, .77f)));
        double diffuse = Math.Max(0, Vector3.Dot(normal, light));
        double reflection = Math.Pow(Math.Max(0, Vector3.Dot(normal, half)), 32);
        double room = Math.Max(0, Vector3.Dot(normal, Vector3.Normalize(new(.7f, .4f, .6f))));
        double red = .18 + diffuse * .52 + reflection * .72 + room * .045;
        double green = .095 + diffuse * .35 + reflection * .65 + room * .060;
        double blue = .027 + diffuse * .15 + reflection * .47 + room * .070;
        return ThemeColor((byte)(Math.Sqrt(Math.Clamp(red, 0, 1)) * 255),
            (byte)(Math.Sqrt(Math.Clamp(green, 0, 1)) * 255), (byte)(Math.Sqrt(Math.Clamp(blue, 0, 1)) * 255));
    }

    private static void DrawRoulettePocketLabel(CanvasDrawingSession ds, int number, double angle, CanvasTextFormat text)
    {
        double x = Math.Cos(angle) * 145, y = Math.Sin(angle) * 145;
        var center = ProjectRouletteWheelPoint(x, y, -4);
        var tangent = ProjectRouletteWheelPoint(x - Math.Sin(angle), y + Math.Cos(angle), -4) - center;
        var inward = ProjectRouletteWheelPoint(x - Math.Cos(angle), y - Math.Sin(angle), -4) - center;
        var origin = center - tangent * 12 - inward * 8;
        var prior = ds.Transform;
        ds.Transform = new Matrix3x2(tangent.X, tangent.Y, inward.X, inward.Y, origin.X, origin.Y) * prior;
        try
        {
            ds.DrawText(number.ToString(), new Rect(.4, -.45, 24, 16), ThemeColor(0, 0, 0, 150), text);
            ds.DrawText(number.ToString(), new Rect(0, 0, 24, 16), ThemeColor(255, 244, 218), text);
        }
        finally { ds.Transform = prior; }
    }

    private void DrawRouletteSpindle(CanvasDrawingSession ds, double angle)
    {
        var root = ProjectRouletteWheelPoint(0, 0, 30);
        using var metal = RouletteMetalBrush(ds.Device, root - new Vector2(35, 40), root + new Vector2(38, 35));
        using var foot = RouletteSurfaceBand(ds.Device, 0, 24, 22, 24);
        ds.FillGeometry(foot, metal);
        DrawRouletteProjectedCircle(ds, 22, 24, ThemeColor(77, 50, 25), .8f);
        var profile = new[] { (22d, 24d), (23d, 26d), (18d, 28d), (13.5d, 33d), (18d, 36d), (19d, 38d) };
        for (int i = 0; i < profile.Length - 1; i++)
        {
            var lower = profile[i]; var next = profile[i + 1];
            using var wall = RouletteSurfaceBand(ds.Device, lower.Item1, lower.Item2, next.Item1, next.Item2, 0, Math.PI);
            ds.FillGeometry(wall, metal);
            DrawRouletteProjectedCircle(ds, next.Item1, next.Item2, ThemeColor(248, 218, 158, 205), .65f);
        }
        var arms = Enumerable.Range(0, 4).Select(i =>
        {
            double a = i * Math.PI / 2 + .3 + angle;
            return (Angle: a, Point: RoulettePolarPoint(46, a, 31), Shadow: RoulettePolarPoint(46, a, 21));
        }).OrderBy(arm => arm.Point.Y);
        foreach (var arm in arms)
        {
            var join = RoulettePolarPoint(17, arm.Angle, 31);
            ds.DrawLine(RoulettePolarPoint(20, arm.Angle, 21), arm.Shadow, ThemeColor(8, 4, 2, 110), 6);
            ds.DrawLine(join + new Vector2(0, 1.3f), arm.Point + new Vector2(0, 1.3f), ThemeColor(83, 53, 25), 6);
            ds.DrawLine(join, arm.Point, metal, 4.4f);
            ds.DrawLine(join - new Vector2(.65f, .8f), arm.Point - new Vector2(.65f, .8f), ThemeColor(255, 235, 190, 205), .7f);
            using var handle = new CanvasRadialGradientBrush(ds.Device, RouletteCream, ThemeColor(108, 72, 33))
                { Center = arm.Point - new Vector2(1.2f, 1.6f), RadiusX = 6, RadiusY = 5 };
            ds.FillEllipse(arm.Point, 4.3f, 3.7f, handle);
            ds.FillEllipse(arm.Point - new Vector2(1.1f, 1.3f), 1.2f, .8f, RouletteCream);
        }
        var upper = ProjectRouletteWheelPoint(0, 0, 38);
        ds.FillEllipse(upper, 19, 12, metal);
        ds.DrawEllipse(upper, 16, 10, ThemeColor(115, 86, 46), .8f);
        ds.DrawEllipse(upper, 13.5f, 8.4f, ThemeColor(255, 226, 166, 205), .65f);
        for (int i = 0; i < 16; i++)
        {
            double a = i * Math.Tau / 16 + angle;
            ds.DrawLine(RoulettePolarPoint(14.5, a, 38), RoulettePolarPoint(17.8, a, 38),
                ThemeColor(82, 57, 27, 150), .65f);
        }
        var knob = ProjectRouletteWheelPoint(0, 0, 44);
        using var crown = new CanvasRadialGradientBrush(ds.Device, RouletteCream, ThemeColor(91, 68, 37))
            { Center = knob - new Vector2(3, 4), RadiusX = 11, RadiusY = 9 };
        ds.FillEllipse(knob, 10, 8, crown);
        ds.DrawEllipse(knob, 10, 8, ThemeColor(212, 177, 108, 180), .55f);
        ds.FillEllipse(knob - new Vector2(3, 3), 2.1f, 1.5f, RouletteCream);
    }

    private void DrawRouletteBall(CanvasDrawingSession ds, RouletteMotion motion, bool spinning)
    {
        if (spinning && motion.Progress < .84)
        {
            double speed = .035 * Math.Pow(1 - motion.Progress, 2);
            for (int i = 5; i > 0; i--)
            {
                var prior = motion with { BallAngle = motion.BallAngle + i * speed };
                ds.FillCircle(ProjectRouletteBall(prior), 3.5f - i * .3f, ThemeColor(255, 240, 199, (byte)(23 - i * 3)));
            }
        }
        var ball = ProjectRouletteBall(motion);
        double y = Math.Sin(motion.BallAngle) * motion.BallRadius;
        float size = (float)(4.6 / (1 - y * .00045));
        var shadow = RoulettePolarPoint(motion.BallRadius, motion.BallAngle, RouletteBallHeight(motion) - 4) + new Vector2(1.7f, 1);
        ds.FillEllipse(shadow, size * 1.25f, size * .65f, ThemeColor(0, 3, 5, 150));
        using var ivory = new CanvasRadialGradientBrush(ds.Device,
            [new() { Position = 0, Color = Colors.White }, new() { Position = .45f, Color = RouletteCream },
             new() { Position = 1, Color = ThemeColor(138, 118, 82) }])
            { Center = ball - new Vector2(size * .32f, size * .38f), RadiusX = size * 1.4f, RadiusY = size * 1.4f };
        ds.FillCircle(ball, size, ivory);
        ds.FillCircle(ball - new Vector2(size * .28f, size * .3f), size * .22f, Colors.White);
    }

    private void DrawRouletteNearLip(CanvasDrawingSession ds)
    {
        using var metal = RouletteMetalBrush(ds.Device, new(33, 290), new(356, 337));
        using var lip = RouletteSurfaceBand(ds.Device, 158, 6, 162, 8, .02, Math.PI - .02);
        ds.FillGeometry(lip, metal);
        DrawRouletteProjectedCircle(ds, 159, 6, ThemeColor(253, 225, 166, 200), .9f, .02, Math.PI - .02);
    }

    private void DrawRouletteWinningGlints(CanvasDrawingSession ds, RouletteSnapshot game, DateTimeOffset now)
    {
        if (game.Phase != RoulettePhase.Betting || game.LastWin <= 0 || game.RoundNumber == 0) return;
        double age = (now - game.RoundStartedAt - game.SpinDuration).TotalSeconds;
        if (age is < 0 or >= 3) return;
        for (int i = 0; i < 12; i++)
        {
            double phase = age + i * .21;
            float alpha = (float)(Math.Pow(Math.Max(0, Math.Sin(phase * 5.3)), 8) * (1 - age / 3));
            var point = RoulettePolarPoint(164 + i % 4 * 3, i * 2.399, 8);
            var color = WithAlpha(RouletteCream, (byte)(170 * alpha));
            ds.DrawLine(point - new Vector2(2.5f, 0), point + new Vector2(2.5f, 0), color, .7f);
            ds.DrawLine(point - new Vector2(0, 2.5f), point + new Vector2(0, 2.5f), color, .7f);
        }
    }

    private static CanvasGeometry RouletteSurfaceBand(CanvasDevice device, double inner, double innerHeight,
        double outer, double outerHeight, double start = 0, double end = Math.Tau)
    {
        using var path = new CanvasPathBuilder(device);
        int pieces = Math.Max(3, (int)Math.Ceiling((end - start) * 24));
        path.BeginFigure(RoulettePolarPoint(inner, start, innerHeight));
        path.AddLine(RoulettePolarPoint(outer, start, outerHeight));
        for (int i = 1; i <= pieces; i++)
            path.AddLine(RoulettePolarPoint(outer, start + (end - start) * i / pieces, outerHeight));
        path.AddLine(RoulettePolarPoint(inner, end, innerHeight));
        for (int i = pieces - 1; i >= 0; i--)
            path.AddLine(RoulettePolarPoint(inner, start + (end - start) * i / pieces, innerHeight));
        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }

    private static CanvasGeometry RouletteQuad(CanvasDevice device, params Vector2[] points)
    {
        using var path = new CanvasPathBuilder(device);
        path.BeginFigure(points[0]);
        foreach (var point in points.Skip(1)) path.AddLine(point);
        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }

    private static void DrawRouletteProjectedCircle(CanvasDrawingSession ds, double radius, double height,
        Color color, float width, double start = 0, double end = Math.Tau)
    {
        using var path = new CanvasPathBuilder(ds.Device);
        path.BeginFigure(RoulettePolarPoint(radius, start, height));
        int pieces = Math.Max(16, (int)Math.Ceiling((end - start) * 30));
        for (int i = 1; i <= pieces; i++) path.AddLine(RoulettePolarPoint(radius, start + (end - start) * i / pieces, height));
        path.EndFigure(end - start >= Math.Tau - .001 ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        using var line = CanvasGeometry.CreatePath(path);
        ds.DrawGeometry(line, color, width);
    }

    private static void DrawRouletteProjectedCircle(CanvasDrawingSession ds, double radius, double height,
        ICanvasBrush brush, float width, double start = 0, double end = Math.Tau)
    {
        using var path = new CanvasPathBuilder(ds.Device);
        path.BeginFigure(RoulettePolarPoint(radius, start, height));
        int pieces = Math.Max(16, (int)Math.Ceiling((end - start) * 30));
        for (int i = 1; i <= pieces; i++) path.AddLine(RoulettePolarPoint(radius, start + (end - start) * i / pieces, height));
        path.EndFigure(end - start >= Math.Tau - .001 ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        using var line = CanvasGeometry.CreatePath(path);
        ds.DrawGeometry(line, brush, width);
    }

    private void DisposeRouletteLayers()
    {
        _rouletteMotionTarget?.Dispose(); _rouletteMotionTarget = null; _rouletteMotionKey = null;
        _rouletteFixedBowl?.Dispose(); _rouletteFixedBowl = null;
        _rouletteForegroundRim?.Dispose(); _rouletteForegroundRim = null;
        _roulettePreviewTarget?.Dispose(); _roulettePreviewTarget = null; _roulettePreviewKey = null;
    }
}
