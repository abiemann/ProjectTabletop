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
    // All cabinet ornament is static. The pale hold buttons and their generated
    // caption reference remain separate, with no decorative strokes on the glass.
    private static Rect SlotInset(Rect rect, double inset) => new(rect.X + inset, rect.Y + inset,
        rect.Width - 2 * inset, rect.Height - 2 * inset);

    private static CanvasGeometry SlotCutPanel(CanvasDevice device, Rect rect, float cut)
    {
        float x = (float)rect.X, y = (float)rect.Y, right = (float)rect.Right, bottom = (float)rect.Bottom;
        cut = MathF.Min(cut, (float)Math.Min(rect.Width, rect.Height) / 3);
        return CanvasGeometry.CreatePolygon(device,
        [
            new(x + cut, y), new(right - cut, y), new(right, y + cut), new(right, bottom - cut),
            new(right - cut, bottom), new(x + cut, bottom), new(x, bottom - cut), new(x, y + cut)
        ]);
    }

    private static CanvasLinearGradientBrush SlotAntiqueMetal(CanvasDevice device, Rect rect) => new(device,
    [
        new() { Position = 0, Color = ThemeColor(255, 230, 160) },
        new() { Position = .08f, Color = ThemeColor(164, 117, 52) },
        new() { Position = .25f, Color = ThemeColor(231, 187, 103) },
        new() { Position = .48f, Color = ThemeColor(99, 65, 31) },
        new() { Position = .68f, Color = ThemeColor(177, 128, 59) },
        new() { Position = .86f, Color = ThemeColor(246, 213, 143) },
        new() { Position = 1, Color = ThemeColor(90, 57, 29) }
    ]) { StartPoint = new((float)rect.X, (float)rect.Y), EndPoint = new((float)rect.Right, (float)rect.Bottom) };

    private static void DrawSlotCabinetEdge(CanvasDrawingSession ds)
    {
        var border = new Rect(10, 10, 980, 980);
        using var outline = SlotCutPanel(ds.Device, border, 25);
        using var metal = SlotAntiqueMetal(ds.Device, border);
        ds.DrawGeometry(outline, ThemeColor(0, 0, 0, 200), 6);
        ds.DrawGeometry(outline, metal, 2);
        using var inner = SlotCutPanel(ds.Device, SlotInset(border, 7), 22);
        ds.DrawGeometry(inner, ThemeColor(219, 174, 91, 70), .7f);
        foreach (var (x, y, sx, sy) in new (float, float, float, float)[]
        {
            (23, 23, 1, 1), (977, 23, -1, 1), (23, 977, 1, -1), (977, 977, -1, -1)
        })
        {
            ds.DrawLine(x + sx * 9, y, x + sx * 52, y, ThemeColor(225, 184, 109, 170), 1.4f);
            ds.DrawLine(x, y + sy * 9, x, y + sy * 44, ThemeColor(225, 184, 109, 170), 1.4f);
            using var tip = CanvasGeometry.CreatePolygon(ds.Device,
            [new(x, y + sy * 6), new(x + sx * 6, y), new(x + sx * 14, y + sy * 14)]);
            ds.FillGeometry(tip, ThemeColor(211, 165, 84));
        }
    }

    private static void DrawSlotMarquee(CanvasDrawingSession ds, float aspect)
    {
        // The name is the only large serif line; the smaller legends belong to
        // the instruments below, keeping the cabinet readable at projection size.
        var plaque = new Rect(193, 15, 614, 78);
        using var shape = SlotCutPanel(ds.Device, plaque, 17);
        using var fill = new CanvasLinearGradientBrush(ds.Device, ThemeColor(12, 13, 21, 230), ThemeColor(16, 10, 17, 205))
        { StartPoint = new(0, 15), EndPoint = new(0, 93) };
        ds.FillGeometry(shape, fill);
        ds.DrawGeometry(shape, ThemeColor(213, 168, 91, 95), .8f);
        ds.DrawLine(225, 19, 775, 19, ThemeColor(250, 218, 147, 175), .8f);
        ds.DrawLine(243, 91, 757, 91, ThemeColor(166, 115, 57, 170), 1);
        SlotText(ds, "DRAGON'S HOARD", new Rect(172, 26, 660, 46), 42,
            ThemeColor(0, 0, 0, 235), aspect, "Georgia");
        SlotText(ds, "DRAGON'S HOARD", new Rect(170, 23, 660, 46), 42,
            ThemeColor(250, 222, 160), aspect, "Georgia");
        SlotText(ds, "40 LINES  ·  HOLD A BUTTON FOR ONE SECOND", new Rect(205, 72, 590, 17), 11.5f,
            ThemeColor(224, 204, 168), aspect);
        foreach (int side in new[] { -1, 1 })
        {
            float start = side < 0 ? 48 : 830, end = side < 0 ? 170 : 952;
            ds.DrawLine(start, 52, end, 52, ThemeColor(207, 158, 83, 145), .8f);
            ds.DrawLine(start + 22, 57, end - 22, 57, ThemeColor(207, 158, 83, 70), .7f);
            float point = side < 0 ? 181 : 819;
            using var spear = CanvasGeometry.CreatePolygon(ds.Device,
                [new(point, 52), new(point - side * 9, 48), new(point - side * 6, 52), new(point - side * 9, 56)]);
            ds.FillGeometry(spear, ThemeColor(226, 184, 103));
        }
    }

    private static void DrawSlotPowerSocket(CanvasDrawingSession ds, Vector2 center, Color accent, float aspect, bool lit)
    {
        ds.FillEllipse(new Vector2(center.X, center.Y + 3), 35 / aspect, 35, ThemeColor(0, 0, 0, 165));
        using var fill = new CanvasRadialGradientBrush(ds.Device, WithAlpha(accent, lit ? (byte)85 : (byte)28),
            ThemeColor(10, 11, 17, 220)) { Center = center, RadiusX = 34 / aspect, RadiusY = 34 };
        ds.FillEllipse(center, 34 / aspect, 34, fill);
        ds.DrawEllipse(center, 34 / aspect, 34, ThemeColor(211, 165, 83, 165), 1.3f);
        ds.DrawEllipse(center, 30 / aspect, 30, ThemeColor(234, 207, 149, 50), .7f);
        for (int index = 0; index < 8; index++)
        {
            float angle = index * MathF.PI / 4;
            Vector2 radial = new(MathF.Cos(angle) / aspect, MathF.Sin(angle));
            ds.DrawLine(center + radial * 35, center + radial * 38,
                lit ? WithAlpha(accent, 220) : ThemeColor(165, 127, 67, 140), 1);
        }
    }

    private static void DrawSlotInlaidPanel(CanvasDrawingSession ds, Rect rect, Color edge, Color tint)
    {
        using var shadow = SlotCutPanel(ds.Device, new Rect(rect.X, rect.Y + 5, rect.Width, rect.Height), 10);
        ds.FillGeometry(shadow, ThemeColor(0, 0, 0, 170));
        using var shape = SlotCutPanel(ds.Device, rect, 10);
        using var metal = SlotAntiqueMetal(ds.Device, rect);
        ds.FillGeometry(shape, metal);
        using var inset = SlotCutPanel(ds.Device, SlotInset(rect, 2.5), 8);
        using var glass = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = tint },
            new() { Position = .38f, Color = ThemeColor(21, 23, 33) },
            new() { Position = 1, Color = ThemeColor(8, 11, 19) }
        ]) { StartPoint = new(0, (float)rect.Y), EndPoint = new(0, (float)rect.Bottom) };
        ds.FillGeometry(inset, glass);
        ds.DrawGeometry(inset, ThemeColor(0, 0, 0, 200), 1.1f);
        using var engraving = SlotCutPanel(ds.Device, SlotInset(rect, 6), 6);
        ds.DrawGeometry(engraving, WithAlpha(edge, 75), .7f);
        ds.DrawLine((float)rect.X + 13, (float)rect.Y + 3, (float)rect.Right - 13, (float)rect.Y + 3,
            WithAlpha(edge, 220), 1.1f);
        ds.DrawLine((float)rect.X + 16, (float)rect.Bottom - 3, (float)rect.Right - 16, (float)rect.Bottom - 3,
            ThemeColor(247, 218, 151, 85), .7f);
    }

    private static void DrawSlotReelEngraving(CanvasDrawingSession ds, SlotLayout layout, Color accent)
    {
        for (int reel = 0; reel < SlotGame.Reels; reel++)
        {
            float center = layout.ReelCenter(reel);
            float half = layout.CellWidth / 2 - 14;
            foreach (float y in new[] { layout.Top - 12, layout.Bottom + 12 })
            {
                ds.DrawLine(center - half, y, center - 6, y, ThemeColor(36, 24, 15, 180), 1.1f);
                ds.DrawLine(center + 6, y, center + half, y, ThemeColor(36, 24, 15, 180), 1.1f);
                using var lozenge = CanvasGeometry.CreatePolygon(ds.Device,
                    [new(center, y - 2.8f), new(center + 4, y), new(center, y + 2.8f), new(center - 4, y)]);
                ds.FillGeometry(lozenge, ThemeColor(56, 33, 17));
                ds.DrawGeometry(lozenge, WithAlpha(accent, 220), .7f);
            }
        }
    }

    private static void DrawSlotCornerClasp(CanvasDrawingSession ds, Vector2 corner, int sx, int sy)
    {
        using var shape = CanvasGeometry.CreatePolygon(ds.Device,
        [
            corner + new Vector2(sx * 2, sy * 12), corner + new Vector2(sx * 12, sy * 2),
            corner + new Vector2(sx * 28, sy * 2), corner + new Vector2(sx * 14, sy * 8),
            corner + new Vector2(sx * 8, sy * 14), corner + new Vector2(sx * 2, sy * 28)
        ]);
        ds.FillGeometry(shape, ThemeColor(248, 219, 146));
        ds.DrawGeometry(shape, ThemeColor(105, 65, 26), 1);
        ds.DrawLine(corner + new Vector2(sx * 7, sy * 12), corner + new Vector2(sx * 12, sy * 7),
            ThemeColor(77, 47, 26), 1.2f);
    }

    private static void DrawSlotKeySocket(CanvasDrawingSession ds, Rect rect, float aspect, bool lit)
    {
        var center = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
        ds.FillEllipse(new Vector2(center.X, center.Y + 2), 25 / aspect, 25, ThemeColor(0, 0, 0, 185));
        using var metal = SlotAntiqueMetal(ds.Device, rect);
        ds.FillEllipse(center, 25 / aspect, 25, metal);
        ds.FillEllipse(center, 22 / aspect, 22, lit ? ThemeColor(60, 41, 19) : ThemeColor(12, 15, 23));
        ds.DrawEllipse(center, 20 / aspect, 20, lit ? SlotGold : ThemeColor(126, 100, 64), .8f);
        ds.DrawEllipse(center, 25 / aspect, 25, ThemeColor(29, 22, 17), .8f);
    }

    private static void DrawSlotStatusRail(CanvasDrawingSession ds)
    {
        using var rail = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(10, 11, 17, 0) },
            new() { Position = .15f, Color = ThemeColor(10, 11, 17, 220) },
            new() { Position = .85f, Color = ThemeColor(10, 11, 17, 220) },
            new() { Position = 1, Color = ThemeColor(10, 11, 17, 0) }
        ]) { StartPoint = new(35, 0), EndPoint = new(965, 0) };
        ds.FillRectangle(new Rect(35, 704, 930, 28), rail);
        ds.DrawLine(90, 733, 910, 733, ThemeColor(212, 171, 95, 65), .7f);
    }
}
