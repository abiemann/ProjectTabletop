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
    // All material detail is static and cached in the board texture. The capture
    // field never receives this texture, preserving a uniform camera background.
    private static void DrawMetalBackdrop(CanvasDrawingSession ds)
    {
        using var baseMetal = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(24, 40, 57) },
            new() { Position = .40f, Color = AppPalette.Background },
            new() { Position = 1, Color = ThemeColor(12, 20, 33) }
        ]) { StartPoint = new(0, 0), EndPoint = new(850, 1000) };
        ds.FillRectangle(new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize), baseMetal);
        using var ambient = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(75, 118, 145, 44) },
            new() { Position = 1, Color = ThemeColor(75, 118, 145, 0) }
        ]) { Center = new(810, 50), RadiusX = 720, RadiusY = 510 };
        ds.FillRectangle(new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize), ambient);
        ds.DrawRoundedRectangle(new Rect(28, 28, 944, 944), 30, 30, ThemeColor(132, 167, 190, 85), 1.5f);
        ds.DrawRoundedRectangle(new Rect(32, 32, 936, 936), 27, 27, ThemeColor(0, 0, 0, 150), 1);
        ds.DrawLine(80, 226, 920, 226, ThemeColor(118, 151, 178, 55), 1);
        ds.DrawLine(80, 857, 920, 857, ThemeColor(118, 151, 178, 55), 1);
        foreach (float x in new[] { 48f, 952f })
        foreach (float y in new[] { 48f, 952f })
        {
            float dx = x < 500 ? 23 : -23, dy = y < 500 ? 23 : -23;
            ds.DrawLine(x, y + dy, x, y, AppPalette.MetalEdge, 2);
            ds.DrawLine(x, y, x + dx, y, AppPalette.MetalEdge, 2);
        }
    }

    private static void DrawGlassPanel(CanvasDrawingSession ds, Rect rect, bool illuminated = false)
    {
        const float radius = 19;
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 6, rect.Width, rect.Height), radius, radius,
            ThemeColor(0, 3, 10, 150));
        using var bevel = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(145, 173, 192) },
            new() { Position = .10f, Color = AppPalette.MetalEdge },
            new() { Position = .48f, Color = ThemeColor(34, 54, 72) },
            new() { Position = .94f, Color = ThemeColor(72, 97, 118) },
            new() { Position = 1, Color = ThemeColor(30, 47, 64) }
        ]) { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, radius, radius, bevel);
        var inside = new Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4);
        using var glass = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(49, 73, 94) },
            new() { Position = .42f, Color = ThemeColor(29, 45, 65) },
            new() { Position = .55f, Color = ThemeColor(22, 36, 53) },
            new() { Position = 1, Color = ThemeColor(13, 24, 39) }
        ]) { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.X + 30, (float)rect.Bottom) };
        ds.FillRoundedRectangle(inside, radius - 2, radius - 2, glass);
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds.Device, inside, radius - 2, radius - 2);
        using (ds.CreateLayer(1, clip))
        {
            using var sheen = new CanvasLinearGradientBrush(ds.Device, ThemeColor(231, 248, 255, 23),
                ThemeColor(231, 248, 255, 0))
            {
                StartPoint = new((float)rect.X, (float)rect.Y),
                EndPoint = new((float)rect.X, (float)(rect.Y + rect.Height * .66))
            };
            ds.FillRectangle(inside, sheen);
            using var diagonal = CanvasGeometry.CreatePolygon(ds.Device,
            [
                new((float)(rect.X + rect.Width * .52), (float)rect.Y),
                new((float)(rect.X + rect.Width * .76), (float)rect.Y),
                new((float)(rect.X + rect.Width * .52), (float)rect.Bottom),
                new((float)(rect.X + rect.Width * .28), (float)rect.Bottom)
            ]);
            ds.FillGeometry(diagonal, ThemeColor(226, 244, 255, 5));
        }
        ds.DrawLine((float)rect.X + 23, (float)rect.Y + 2, (float)rect.Right - 23, (float)rect.Y + 2,
            ThemeColor(214, 237, 250, 90), 1);
        if (illuminated)
        {
            ds.DrawRoundedRectangle(rect, radius, radius, ThemeColor(91, 224, 255, 12), 13);
            ds.DrawRoundedRectangle(rect, radius, radius, ThemeColor(91, 224, 255, 35), 6);
            ds.DrawRoundedRectangle(rect, radius, radius, AppPalette.IndicatorOn, 1.8f);
        }
    }

    private static void DrawButtonSurface(CanvasDrawingSession ds, Rect rect, bool hovered)
    {
        DrawGlassPanel(ds, rect, hovered);
        var lamp = new Rect(rect.X + 11, rect.Y + rect.Height / 2 - 21, 3, 42);
        if (hovered)
            ds.FillRoundedRectangle(new Rect(lamp.X - 4, lamp.Y - 3, 11, 48), 5, 5,
                ThemeColor(102, 225, 255, 35));
        ds.FillRoundedRectangle(lamp, 1.5f, 1.5f,
            hovered ? AppPalette.IndicatorOn : AppPalette.IndicatorOff);
    }

    private static void DrawBoardSymbol(CanvasDrawingSession ds, BoardScreen screen, Vector2 center,
        float scale, Color color)
    {
        Matrix3x2 previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(center) * previous;
        try
        {
            switch (screen)
            {
                case BoardScreen.HandTracking:
                    Stroke([-16, 6, -9, 13, -9, -16, -4, -20, 1, -16, 1, -2, 6, -5, 11, -2,
                        16, -2, 19, 4, 17, 17, 9, 22, -4, 22, -20, 10, -16, 6]);
                    break;
                case BoardScreen.PhotoCopy:
                    ds.DrawRoundedRectangle(new Rect(-17, -19, 29, 32), 3, 3, color, 2.5f);
                    ds.DrawRoundedRectangle(new Rect(-10, -10, 29, 32), 3, 3, color, 2.5f);
                    ds.DrawLine(-4, 4, 12, 4, color, 2);
                    ds.DrawLine(-4, 11, 6, 11, color, 2);
                    break;
                case BoardScreen.Blackjack:
                    ds.DrawRoundedRectangle(new Rect(-17, -21, 34, 44), 5, 5, color, 2.5f);
                    using (var diamond = CanvasGeometry.CreatePolygon(ds.Device, [new(0, -10), new(8, 0), new(0, 10), new(-8, 0)]))
                        ds.FillGeometry(diamond, color);
                    break;
                case BoardScreen.Paint:
                    ds.DrawEllipse(new Vector2(-2, 1), 22, 19, color, 2.5f);
                    ds.FillCircle(new Vector2(-12, -5), 3.5f, color);
                    ds.FillCircle(new Vector2(-2, -11), 3.5f, color);
                    ds.FillCircle(new Vector2(10, -6), 3.5f, color);
                    Stroke([-9, 19, 13, -16, 20, -12, -3, 22, -9, 19]);
                    break;
                case BoardScreen.Gta:
                    Stroke([-23, 9, -19, -2, -11, -6, -6, -16, 11, -16, 18, -4, 24, 0, 24, 10, -23, 10, -23, 9]);
                    ds.DrawCircle(new Vector2(-13, 11), 5, color, 2.5f);
                    ds.DrawCircle(new Vector2(14, 11), 5, color, 2.5f);
                    ds.DrawLine(-6, -5, 12, -5, color, 2);
                    break;
                case BoardScreen.Diablo:
                    Stroke([0, -22, 17, -3, 0, 24, -17, -3, 0, -22]);
                    ds.DrawLine(0, -17, 0, 18, color, 2);
                    Stroke([-16, -5, -23, -18, -15, -14]);
                    Stroke([16, -5, 23, -18, 15, -14]);
                    break;
            }
        }
        finally { ds.Transform = previous; }

        void Stroke(float[] coordinates)
        {
            for (int index = 2; index < coordinates.Length; index += 2)
                ds.DrawLine(coordinates[index - 2], coordinates[index - 1],
                    coordinates[index], coordinates[index + 1], color, 2.5f);
        }
    }

    private static Color ThemeColor(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);
}
