using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection.Football;

internal sealed partial class FootballRenderer
{
    private void DrawKicker(CanvasDrawingSession ds, FootballKickerStyle style,
        Vector2 position, float heading, Color team, bool present, bool human = true)
    {
        var before = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(heading) * Matrix3x2.CreateTranslation(position) * before;
        try
        {
            using var opacity = ds.CreateLayer(present ? 1 : .30f);
            // Keep the cropped car's rear clear of artwork that could reach the ink.
            if (style != FootballKickerStyle.Car)
                ds.DrawCircle(Vector2.Zero, FootballGame.KickerRadius * 1000, Ink(team.R, team.G, team.B, 78), 1.5f);
            switch (style)
            {
                case FootballKickerStyle.Car: DrawCar(ds); break;
                case FootballKickerStyle.Glove: DrawGlove(ds, team); break;
                case FootballKickerStyle.Pan: DrawPan(ds, team); break;
                case FootballKickerStyle.Boot: DrawBoot(ds, team); break;
            }
            if (human) DrawMarkerHub(ds);
        }
        finally { ds.Transform = before; }
    }

    private static void DrawMarkerHub(CanvasDrawingSession ds)
    {
        // This neutral platinum inlay stays directly beneath a real marker.
        // Team-coloured artwork here would merge with the tip in the camera's
        // colour segmentation. Keep every pixel achromatic, including its rim.
        ds.FillCircle(Vector2.Zero, 32, Ink(49, 49, 49));
        using var metal = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = Ink(251, 251, 251) },
            new() { Position = .22f, Color = Ink(215, 215, 215) },
            new() { Position = .55f, Color = Ink(238, 238, 238) },
            new() { Position = 1, Color = Ink(178, 178, 178) }
        ]) { StartPoint = new(-22, -28), EndPoint = new(28, 30) };
        ds.FillCircle(Vector2.Zero, 30.5f, metal);
        ds.DrawCircle(Vector2.Zero, 29.5f, Ink(250, 250, 250), .9f);
        for (float y = -25; y <= 25; y += 2.3f)
        {
            float extent = MathF.Sqrt(28 * 28 - y * y);
            ds.DrawLine(-extent, y, extent, y, Ink(90, 90, 90, 12), .38f);
        }
    }

    private static CanvasLinearGradientBrush BodyPaint(CanvasDrawingSession ds, Color team) => new(ds.Device,
    [
        new() { Position = 0, Color = Ink((byte)Math.Min(255, team.R + 47), (byte)Math.Min(255, team.G + 47), (byte)Math.Min(255, team.B + 47)) },
        new() { Position = .35f, Color = team },
        new() { Position = .76f, Color = Ink((byte)(team.R * .83f), (byte)(team.G * .78f), (byte)(team.B * .76f)) },
        new() { Position = 1, Color = Ink((byte)(team.R * .40f), (byte)(team.G * .38f), (byte)(team.B * .37f)) }
    ]) { StartPoint = new(-38, -53), EndPoint = new(48, 64) };

    // Tight alpha bounds exclude the extraction padding. The image faces down;
    // one quarter turn maps its nose onto the shared collider's forward edge.
    private static readonly Rect CarSourceBounds = new(88, 61, 1172, 1057);
    private static Rect CarDestinationBounds => new(-62,
        FootballGame.KickerRadius * 1000 - 124 * CarSourceBounds.Height / CarSourceBounds.Width,
        124, 124 * CarSourceBounds.Height / CarSourceBounds.Width);
    // Preserve the identical source-to-pitch mapping while giving the soft
    // silhouette room outside the body crop, without a clipped shadow edge.
    private static readonly Rect CarShadowSourceBounds = new(46, 19, 1256, 1141);
    private static Rect CarShadowDestinationBounds
    {
        get
        {
            var body = CarDestinationBounds;
            double margin = 42 * 124 / CarSourceBounds.Width;
            return new(body.X - margin, body.Y - margin, body.Width + 2 * margin, body.Height + 2 * margin);
        }
    }

    private void DrawKickerShadow(CanvasDrawingSession ds, FootballKickerStyle style,
        Vector2 position, float heading, byte opacity)
    {
        if (style != FootballKickerStyle.Car)
        {
            DrawSoftShadow(ds, position + new Vector2(17, 23), 68, 47, opacity);
            return;
        }

        if (_carShadow is not { } shadow) return;
        var before = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(heading - MathF.PI / 2) *
            Matrix3x2.CreateTranslation(position + new Vector2(10, 14)) * before;
        try
        {
            ds.DrawImage(shadow, CarShadowDestinationBounds, CarShadowSourceBounds, opacity / 255f);
        }
        finally { ds.Transform = before; }
    }

    private void DrawCar(CanvasDrawingSession ds)
    {
        if (_kickerImages?.Image("car-front.png") is not { } car) return;
        var before = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(-MathF.PI / 2) * before;
        try { ds.DrawImage(car, CarDestinationBounds, CarSourceBounds); }
        finally { ds.Transform = before; }
    }

    private static void DrawGlove(CanvasDrawingSession ds, Color team)
    {
        // Wrist, curved padded knuckles and a folded thumb, all one collision size.
        using var paint = BodyPaint(ds, team);
        ds.FillRoundedRectangle(new Rect(-65, -28, 34, 56), 9, 9, Ink(38, 43, 40));
        ds.FillRoundedRectangle(new Rect(-62, -26, 31, 52), 8, 8, paint);
        ds.DrawLine(-56, -23, -56, 23, Ink(255, 234, 192, 180), 3.1f);
        using var p = new CanvasPathBuilder(ds.Device);
        p.BeginFigure(new Vector2(-35, -29));
        p.AddCubicBezier(new(-32, -49), new(12, -57), new(38, -45));
        p.AddCubicBezier(new(61, -34), new(69, -6), new(57, 24));
        p.AddCubicBezier(new(43, 46), new(15, 52), new(-9, 36));
        p.AddCubicBezier(new(-12, 58), new(-39, 57), new(-43, 37));
        p.AddCubicBezier(new(-48, 20), new(-47, -8), new(-35, -29));
        p.EndFigure(CanvasFigureLoop.Closed);
        using var shape = CanvasGeometry.CreatePath(p);
        ds.FillGeometry(shape, paint);
        ds.DrawGeometry(shape, Ink(74, 44, 32), 2.2f);
        using var highlight = new CanvasRadialGradientBrush(ds.Device, Ink(255, 240, 208, 110), Ink(255, 240, 208, 0))
            { Center = new(7, -24), RadiusX = 43, RadiusY = 23 };
        ds.FillEllipse(new(7, -24), 43, 23, highlight);
        using var seam = new CanvasPathBuilder(ds.Device);
        seam.BeginFigure(new Vector2(-28, -33));
        seam.AddCubicBezier(new(-39, -5), new(-24, 12), new(-11, 31));
        seam.EndFigure(CanvasFigureLoop.Open);
        using var fold = CanvasGeometry.CreatePath(seam);
        ds.DrawGeometry(fold, Ink(53, 28, 27, 145), 2);
        ds.DrawLine(-35, 33, -22, 43, Ink(255, 227, 192, 100), 1.3f);
        for (int y = -16; y <= 16; y += 8)
        {
            ds.DrawLine(-49, y - 3, -40, y + 3, Ink(246, 231, 195), 2.1f);
            ds.DrawLine(-49, y + 3, -40, y - 3, Ink(246, 231, 195), 2.1f);
        }
        ds.DrawLine(18, -40, 41, -26, Ink(255, 245, 222, 92), 2);
    }

    private static void DrawPan(CanvasDrawingSession ds, Color team)
    {
        ds.FillRoundedRectangle(new Rect(-68, -10, 42, 20), 7, 7, Ink(25, 31, 31));
        ds.DrawRoundedRectangle(new Rect(-68, -10, 42, 20), 7, 7, Ink(125, 139, 132), 1.6f);
        ds.FillRoundedRectangle(new Rect(-61, -5, 11, 10), 4, 4, Ink(78, 94, 77));
        ds.FillRectangle(new Rect(-42, -10, 18, 20), Ink(99, 110, 102));
        using var metal = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = Ink(191, 208, 201) },
            new() { Position = .17f, Color = Ink(75, 93, 88) },
            new() { Position = .55f, Color = Ink(41, 53, 51) },
            new() { Position = 1, Color = Ink(15, 23, 22) }
        ]) { StartPoint = new(-29, -42), EndPoint = new(44, 47) };
        ds.FillCircle(new(10, 0), 51, Ink(21, 30, 29));
        ds.FillCircle(new(10, -1), 48, metal);
        ds.DrawCircle(new(10, -1), 46.5f, Ink(187, 204, 196), 1.3f);
        using var cooking = new CanvasRadialGradientBrush(ds.Device, Ink(62, 75, 71), Ink(21, 29, 28))
            { Center = new(0, -14), RadiusX = 53, RadiusY = 55 };
        ds.FillCircle(new(10, -1), 40, cooking);
        ds.DrawCircle(new(10, -1), 39, Ink(12, 20, 19), 1.6f);
        // Fine machined circular highlights give the iron surface its material.
        for (float radius = 7; radius <= 36; radius += 3.2f)
            ds.DrawCircle(new(10, -1), radius, Ink(164, 181, 170, 11), .5f);
        ds.FillCircle(new(-25, -8), 2.4f, Ink(195, 207, 193));
        ds.FillCircle(new(-25, 7), 2.4f, Ink(195, 207, 193));
        ds.FillRoundedRectangle(new Rect(-48, -10, 8, 20), 2.5f, 2.5f, team);
        ds.DrawLine(-58, -7, -51, -7, Ink(228, 235, 207, 120), 1.5f);
    }

    private static void DrawBoot(CanvasDrawingSession ds, Color team)
    {
        using var path = new CanvasPathBuilder(ds.Device);
        path.BeginFigure(new Vector2(-48, -33));
        path.AddCubicBezier(new(-27, -42), new(-6, -31), new(5, -17));
        path.AddCubicBezier(new(34, -19), new(59, -16), new(65, 3));
        path.AddCubicBezier(new(69, 24), new(54, 40), new(26, 41));
        path.AddCubicBezier(new(-4, 44), new(-38, 30), new(-53, 13));
        path.AddCubicBezier(new(-62, -4), new(-60, -21), new(-48, -33));
        path.EndFigure(CanvasFigureLoop.Closed);
        using var body = CanvasGeometry.CreatePath(path);
        using var paint = BodyPaint(ds, team);
        ds.FillGeometry(body, Ink(25, 35, 33));
        ds.DrawGeometry(body, Ink(218, 228, 213), 5.2f);
        ds.FillGeometry(body, paint);
        ds.DrawGeometry(body, Ink(31, 41, 37), 1.7f);
        // Open ankle, thick padded tongue and individual crossed white laces.
        ds.FillEllipse(new(-34, -12), 19, 20, Ink(26, 37, 36));
        ds.DrawEllipse(new(-34, -12), 19, 20, Ink(255, 238, 211, 125), 2);
        using var tongue = Polygon(ds, new(-29, 4), new(-8, -9), new(21, 15), new(2, 29));
        ds.FillGeometry(tongue, Ink((byte)(team.R * .66f), (byte)(team.G * .65f), (byte)(team.B * .64f)));
        for (int i = 0; i < 5; i++)
        {
            var a = new Vector2(-20 + i * 7.3f, -1 + i * 4.4f);
            var b = new Vector2(-11 + i * 7.3f, 14 + i * 3.8f);
            ds.DrawLine(a, b, Ink(255, 245, 218), 2.6f);
            ds.DrawLine(a + new Vector2(7, 5), b - new Vector2(6, 3), Ink(255, 245, 218), 2.1f);
        }
        using var toeSeam = new CanvasPathBuilder(ds.Device);
        toeSeam.BeginFigure(new Vector2(25, -12));
        toeSeam.AddCubicBezier(new(39, 0), new(45, 21), new(35, 35));
        toeSeam.EndFigure(CanvasFigureLoop.Open);
        using var seam = CanvasGeometry.CreatePath(toeSeam);
        ds.DrawGeometry(seam, Ink(247, 222, 188, 125), 1.3f);
        ds.DrawLine(-39, 20, -13, 33, Ink(255, 242, 211, 210), 4);
        ds.DrawLine(-33, 15, -7, 29, Ink(255, 242, 211, 210), 3);
        using var shine = new CanvasRadialGradientBrush(ds.Device, Ink(255, 253, 221, 115), Ink(255, 253, 221, 0))
            { Center = new(41, 3), RadiusX = 17, RadiusY = 12 };
        ds.FillEllipse(new(41, 3), 17, 12, shine);
    }
}
