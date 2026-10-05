using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace MenuPreviewGenerator;

// Preview composition lives only in this offline asset tool. Production menus
// load its finished PNGs and never execute these recipes or board helpers.
internal sealed partial class PreviewRecipes
{
    private const float MenuPreviewUnits = 160;
    // Preview scenes use a local space 160 units high; `span` is its width.
    // Focal content sits inside the fully sharp right-hand region.
    private void DrawHandTrackingPreview(CanvasDrawingSession ds, float span)
    {
        ds.Clear(Palette.Background);
        var center = new Vector2(span - 82, 84);
        for (float x = center.X % 32; x < span; x += 32)
            ds.DrawLine(x, 0, x, MenuPreviewUnits, Palette.GridLine, Math.Abs(x - center.X) < 1 ? 2 : 1);
        for (float y = 84 % 32; y < MenuPreviewUnits; y += 32)
            ds.DrawLine(0, y, span, y, Palette.GridLine, Math.Abs(y - 84) < 1 ? 2 : 1);
        using var light = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(236, 241, 246) },
            new() { Position = .62f, Color = ThemeColor(214, 223, 232) },
            new() { Position = 1, Color = ThemeColor(214, 223, 232, 0) }
        ]) { Center = center, RadiusX = 70, RadiusY = 70 };
        ds.FillCircle(center, 70, light);
        // A palm-down hand with its four fingers grouped, as boards expect.
        (Vector2 Base, Vector2 Tip)[] fingers =
        [
            (new(-17, 12), new(-21, -28)), (new(-5, 10), new(-7, -38)),
            (new(7, 10), new(7, -35)), (new(18, 13), new(20, -22))
        ];
        (Vector2 Base, Vector2 Tip) thumb = (new(-24, 32), new(-44, 8));
        using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        DrawHand(new Vector2(3, 4), null, ThemeColor(40, 52, 68, 70));
        using var skin = new CanvasLinearGradientBrush(ds.Device, ThemeColor(226, 184, 156), ThemeColor(191, 142, 114))
        { StartPoint = center + new Vector2(0, -40), EndPoint = center + new Vector2(0, 60) };
        DrawHand(Vector2.Zero, skin, default);
        foreach (var finger in fingers)
        {
            var direction = Vector2.Normalize(finger.Tip - finger.Base);
            var nail = center + finger.Tip - direction * 4.5f;
            ds.FillEllipse(nail, 3.6f, 4.4f, ThemeColor(241, 214, 198));
            ds.DrawLine(center + finger.Base + direction * 12 - new Vector2(3, 0),
                center + finger.Base + direction * 12 + new Vector2(3, 0), ThemeColor(168, 120, 96, 150), .8f);
        }
        for (int index = 0; index < fingers.Length; index++)
        {
            var marker = center + fingers[index].Tip;
            var ring = index == 1 ? ThemeColor(233, 190, 83) : Palette.IndicatorOn;
            ds.DrawCircle(marker, 7.5f, ring, 2.2f);
            if (index == 1) ds.FillCircle(marker, 2.4f, ring);
        }

        void DrawHand(Vector2 offset, ICanvasBrush? brush, Color color)
        {
            var origin = center + offset;
            using var palm = CanvasGeometry.CreateRoundedRectangle(ds.Device,
                new Rect(origin.X - 26, origin.Y + 8, 52, 70), 20, 20);
            if (brush is null) ds.FillGeometry(palm, color); else ds.FillGeometry(palm, brush);
            foreach (var (from, to) in fingers.Append(thumb))
                if (brush is null) ds.DrawLine(origin + from, origin + to, color, from == thumb.Base ? 13 : 11.6f, round);
                else ds.DrawLine(origin + from, origin + to, brush, from == thumb.Base ? 13 : 11.6f, round);
        }
    }

    private void DrawPhotoCopyPreview(CanvasDrawingSession ds, float span)
    {
        ds.Clear(Palette.PhotoCopyBackground);
        var center = new Vector2(span - 86, 80);
        // A patch of Swirl's square spiral: copies turn their tops inward.
        for (int row = -2; row <= 2; row++)
        for (int column = -4; column <= 2; column++)
        {
            if (row == 0 && column == 0) continue;
            var position = center + new Vector2(column * 46, row * 42);
            if (position.X < span - 210) continue;
            var inward = center - position;
            DrawCopy(position, MathF.Atan2(inward.X, -inward.Y), 1.02f);
        }
        // The grey surface falls into shadow beneath the tile's captions.
        using var shade = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = .30f, Color = ThemeColor(18, 20, 24, 0) },
            new() { Position = 1, Color = ThemeColor(18, 20, 24, 170) }
        ]) { Center = center, RadiusX = 250, RadiusY = 170 };
        ds.FillRectangle(new Rect(0, 0, span, MenuPreviewUnits), shade);
        using var glow = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(255, 255, 255, 170), ThemeColor(255, 255, 255, 0))
        { Center = center, RadiusX = 44, RadiusY = 44 };
        ds.FillCircle(center, 44, glow);
        DrawCopy(center, -.16f, 1.3f);

        void DrawCopy(Vector2 position, float angle, float scale)
        {
            var previous = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(angle) *
                Matrix3x2.CreateTranslation(position) * previous;
            try
            {
                ds.FillRoundedRectangle(new Rect(-10, -13, 23, 31), 3, 3, ThemeColor(20, 20, 20, 70));
                using var cover = new CanvasLinearGradientBrush(ds.Device, ThemeColor(52, 128, 84), ThemeColor(24, 84, 54))
                { StartPoint = new(-12, -16), EndPoint = new(12, 16) };
                ds.FillRoundedRectangle(new Rect(-12, -16, 24, 32), 3, 3, cover);
                ds.FillRectangle(new Rect(-12, -14, 3.5, 28), ThemeColor(18, 62, 40));
                ds.FillRoundedRectangle(new Rect(-5, -11, 12, 7), 1.2f, 1.2f, ThemeColor(236, 229, 205));
                ds.DrawLine(7.5f, -16, 7.5f, 16, ThemeColor(24, 24, 24), 2);
            }
            finally { ds.Transform = previous; }
        }
    }

    private void DrawBlackjackPreview(CanvasDrawingSession ds, float span)
    {
        // The table's own felt, viewed inside its rail around the dealer arc.
        float scale = MenuPreviewUnits / 330;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(-(940 - span / scale), -430) * Matrix3x2.CreateScale(scale) * previous;
        CallBoard("DrawCasinoFelt", ds);
        ds.Transform = previous;

        DrawChip(new Vector2(span - 176, 124), ThemeColor(147, 60, 70), "50");
        DrawChip(new Vector2(span - 150, 134), ThemeColor(37, 93, 153), "10");
        DrawCard(new BlackjackCard(1, BlackjackSuit.Spades), new Vector2(span - 104, 82), -11);
        DrawCard(new BlackjackCard(13, BlackjackSuit.Hearts), new Vector2(span - 58, 78), 9);

        void DrawCard(BlackjackCard card, Vector2 center, float degrees)
        {
            var saved = ds.Transform;
            ds.Transform = Matrix3x2.CreateRotation(degrees * MathF.PI / 180, center) * saved;
            try { CallBoard("DrawCasinoCard", ds, (BlackjackCard?)card, new Rect(center.X - 31, center.Y - 42.5f, 62, 85)); }
            finally { ds.Transform = saved; }
        }
        void DrawChip(Vector2 center, Color body, string value)
        {
            var saved = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(.56f, center) * saved;
            try { CallBoard("DrawCasinoChip", ds, center, 32f, body, value, false); }
            finally { ds.Transform = saved; }
        }
    }

    // A short run of the real fluid simulation. The caller disposes it after
    // the preview's drawing session has finished using its fields.
    private IDisposable DrawPaintPreview(CanvasDrawingSession ds, float span)
    {
        int fieldHeight = 96, fieldWidth = Math.Clamp((int)MathF.Round(fieldHeight * span / MenuPreviewUnits), 32, 512);
        var fluid = CreateFluid(ds.Device, fieldWidth, fieldHeight);
        // Positions are units from the right edge; radii are fractions of height.
        (float FromRight, float Y, float Radius, int Pigment)[] drops =
        [
            (30, .20f, .10f, 0), (70, .34f, .09f, 1), (112, .18f, .08f, 2), (52, .62f, .10f, 3),
            (96, .72f, .09f, 6), (140, .50f, .08f, 5), (22, .88f, .08f, 4), (16, .48f, .07f, 7),
            (84, .04f, .07f, 6), (126, .92f, .07f, 0), (170, .30f, .07f, 1), (60, .96f, .06f, 2),
            (180, .78f, .06f, 3), (44, .42f, .06f, 5)
        ];
        for (int index = 0; index < drops.Length; index++)
            Call(fluid, "AddDrop", new Vector2(1 - drops[index].FromRight / span, drops[index].Y), drops[index].Radius,
                PaintPigments[drops[index].Pigment], 1f, 101 + index * 37);
        // Four seconds lets the mounds relax into overlapping coats.
        for (int step = 0; step < 60; step++) Call(fluid, "Advance", 1 / 15.0);
        Call(fluid, "Draw", ds, new Rect(0, 0, span, MenuPreviewUnits));
        return fluid;
    }

    private void DrawMonopolyPreview(CanvasDrawingSession ds, float span)
    {
        // A native miniature of Crown & Deed's oval boulevard and city skyline.
        // Small physical shapes stay crisp at the tile's cached native density.
        ds.Clear(ThemeColor(9, 25, 22));
        var center = new Vector2(span - 88, 94);
        ds.FillEllipse(center + new Vector2(0, 7), 86, 48, ThemeColor(3, 10, 9));
        ds.FillEllipse(center, 85, 48, ThemeColor(43, 45, 29));
        ds.DrawEllipse(center, 85, 48, ThemeColor(202, 164, 88), 1.3f);
        ds.FillEllipse(center - new Vector2(0, 2), 77, 40, ThemeColor(15, 53, 42));
        ds.DrawEllipse(center - new Vector2(0, 2), 77, 40, ThemeColor(122, 119, 66), .7f);
        ds.DrawEllipse(center - new Vector2(0, 2), 68, 33, ThemeColor(215, 186, 114), .8f);
        for (int index = 0; index < 40; index++)
        {
            float angle = MathF.PI / 2 + index * MathF.Tau / 40;
            var radial = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            ds.DrawLine(center - new Vector2(0, 2) + radial * new Vector2(70, 35),
                center - new Vector2(0, 2) + radial * new Vector2(75, 39),
                ThemeColor(209, 183, 117, 180), .65f);
        }

        House(-48, -13, 14, 24, ThemeColor(113, 139, 113));
        House(-28, -23, 15, 32, ThemeColor(197, 172, 116));
        House(-7, -30, 18, 43, ThemeColor(150, 164, 131));
        House(17, -18, 15, 29, ThemeColor(211, 191, 143));
        House(38, -8, 14, 24, ThemeColor(131, 157, 130));
        House(-35, 12, 16, 24, ThemeColor(204, 186, 137));
        House(-10, 18, 19, 29, ThemeColor(151, 174, 145));
        House(18, 17, 16, 23, ThemeColor(197, 171, 118));
        DrawDie(ds, new Rect(span - 46, 118, 18, 18), 5, MonopolyIvory, MonopolyInk);
        DrawDie(ds, new Rect(span - 24, 125, 16, 16), 2, MonopolyIvory, MonopolyInk);

        void House(float x, float y, float width, float height, Color stone)
        {
            var foot = center + new Vector2(x, y);
            ds.FillEllipse(foot + new Vector2(width / 2 + 2, 2), width * .66f, 3.2f, ThemeColor(2, 15, 12, 160));
            ds.FillRectangle(new Rect(foot.X, foot.Y - height, width, height), stone);
            ds.FillRectangle(new Rect(foot.X + width * .72f, foot.Y - height, width * .28f, height), ThemeColor(76, 99, 79));
            using var roof = new CanvasPathBuilder(ds.Device);
            roof.BeginFigure(new Vector2(foot.X - 2, foot.Y - height));
            roof.AddLine(new Vector2(foot.X + width / 2, foot.Y - height - width * .44f));
            roof.AddLine(new Vector2(foot.X + width + 2, foot.Y - height));
            roof.EndFigure(CanvasFigureLoop.Closed);
            using var roofGeometry = CanvasGeometry.CreatePath(roof);
            ds.FillGeometry(roofGeometry, ThemeColor(44, 74, 65));
            ds.DrawGeometry(roofGeometry, ThemeColor(205, 173, 99), .65f);
            ds.DrawLine(foot.X, foot.Y - height + 2, foot.X, foot.Y, ThemeColor(230, 208, 150), .6f);
            for (float row = foot.Y - height + 5; row < foot.Y - 4; row += 7)
                for (float column = foot.X + 3; column < foot.X + width * .7f; column += 5)
                    ds.FillRectangle(new Rect(column, row, 2.2, 3.2), ThemeColor(246, 218, 139));
            ds.FillRectangle(new Rect(foot.X + width * .36f, foot.Y - 6, width * .22f, 6), ThemeColor(23, 49, 39));
        }
    }

}
