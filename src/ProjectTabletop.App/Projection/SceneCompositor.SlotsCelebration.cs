using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Presentation only: amounts come from the completed feature, while both
    // the count and the shower use the caller's injected phase clock.
    private static bool DrawSlotWinCelebration(CanvasDrawingSession ds, SlotSnapshot game,
        double t, double progress, SlotLayout layout)
    {
        decimal amount;
        string feature;
        bool grand = false;
        switch (game.Phase)
        {
            case SlotPhase.RespinOutro:
                amount = game.RespinTotal;
                feature = "DRAGONFIRE RESPINS";
                grand = game.GrandFill;
                break;
            case SlotPhase.FreeSpinsOutro:
                amount = game.FreeSpinsWin;
                feature = "FREE SPINS COMPLETE";
                break;
            case SlotPhase.VaultOutro:
                // Jackpot values in the snapshot already use the vault's bet.
                amount = game.Jackpots.TryGetValue(game.VaultAward, out decimal award) ? award : 0;
                feature = "TREASURE VAULT";
                grand = game.VaultAward == SlotJackpot.Grand;
                break;
            default:
                return false;
        }

        amount = Math.Max(0, amount);
        double elapsed = double.IsFinite(t) ? Math.Max(0, t) : 0;
        double phase = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        float enter = (float)Ease(elapsed / .28);
        decimal multiple = game.Bet > 0 ? amount / game.Bet : 0;
        string headline = grand ? "GRAND" : multiple >= 100 ? "MEGA" : multiple >= 50 ? "SUPER"
            : multiple >= 10 ? "BIG" : "YOU WON";
        bool tiered = grand || multiple >= 10;
        // Ease toward the earned total and leave the last 22% fully readable.
        double countProgress = Math.Clamp(phase / .78, 0, 1);
        decimal shown = countProgress >= 1 ? amount
            : Math.Floor(amount * (decimal)(1 - Math.Pow(1 - countProgress, 2.2)) * 100) / 100;

        using var clip = CanvasGeometry.CreateRectangle(ds.Device, layout.Window);
        using var layer = ds.CreateLayer(enter, clip);
        var center = new Vector2(layout.Left + layout.Width / 2, layout.Top + layout.Height / 2);
        var previous = ds.Transform;
        // Work in physical units throughout: round coins stay round and their
        // apparent width changes only because they rotate about their own axes.
        ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1, center) * previous;
        try
        {
            float width = layout.Width * layout.Aspect;
            var window = new Rect(center.X - width / 2, layout.Top, width, layout.Height);
            ds.FillRectangle(window, ThemeColor(9, 4, 15, 202));
            using (var glow = new CanvasRadialGradientBrush(ds.Device,
                ThemeColor(255, 160, 35, 165), ThemeColor(115, 32, 13, 0))
            { Center = center - new Vector2(0, 18), RadiusX = width * .70f, RadiusY = layout.Height * .70f })
                ds.FillRectangle(window, glow);
            DrawSlotCelebrationRays(ds, center, width, layout.Height, elapsed);
            DrawSlotCelebrationCoins(ds, window, elapsed, grand ? 34 : tiered ? 28 : 20);

            // A soft central veil lets the moving gold remain visible while
            // leaving the award lettering with an uninterrupted silhouette.
            using (var shade = new CanvasRadialGradientBrush(ds.Device,
                ThemeColor(31, 9, 12, 165), ThemeColor(31, 9, 12, 0))
            { Center = center + new Vector2(0, 20), RadiusX = width * .46f, RadiusY = 144 })
                ds.FillRectangle(window, shade);

            float inset = Math.Min(30, width * .065f);
            var titleBox = new Rect(window.X + inset, layout.Top + (tiered ? 69 : 120),
                width - inset * 2, tiered ? 78 : 88);
            float arrivalScale = .84f + .16f * enter;
            DrawSlotCelebrationLetters(ds, headline, titleBox, (tiered ? 84 : 68) * arrivalScale, elapsed, heavy: true);
            if (tiered)
                DrawSlotCelebrationLetters(ds, grand ? "JACKPOT" : "WIN!",
                    new Rect(window.X + inset, layout.Top + 143, width - inset * 2, 66),
                    grand ? 59 : 69, elapsed + .1, heavy: true);

            SlotText(ds, feature, new Rect(window.X + 12, layout.Top + 28, width - 24, 22),
                14, ThemeColor(255, 228, 168), 1, "Bahnschrift", true);
            DrawSlotCelebrationFlourish(ds, center.X, layout.Top + 55, Math.Min(width * .28f, 154), elapsed);
            DrawSlotCelebrationLetters(ds, SlotGame.Format(shown),
                new Rect(window.X + inset, layout.Top + 226, width - inset * 2, 77),
                68, elapsed + .25, heavy: false);
            SlotText(ds, "CREDITS", new Rect(window.X + 12, layout.Top + 307, width - 24, 21),
                14, ThemeColor(255, 228, 168), 1, "Bahnschrift", true);
            DrawSlotCelebrationFlourish(ds, center.X, layout.Top + 343, Math.Min(width * .23f, 122), elapsed + .5);
        }
        finally { ds.Transform = previous; }
        return true;
    }

    private static void DrawSlotCelebrationRays(CanvasDrawingSession ds, Vector2 center,
        float width, float height, double t)
    {
        using var rays = new CanvasPathBuilder(ds.Device);
        float reach = MathF.Sqrt(width * width + height * height) * .66f;
        for (int index = 0; index < 22; index++)
        {
            float angle = index * MathF.Tau / 22 + (float)t * .075f;
            float spread = .022f + SlotRandom(index * 23 + 81) * .033f;
            Vector2 Point(float theta, float radius) => center + new Vector2(MathF.Cos(theta), MathF.Sin(theta)) * radius;
            rays.BeginFigure(Point(angle, 22));
            rays.AddLine(Point(angle - spread, reach));
            rays.AddLine(Point(angle + spread, reach));
            rays.EndFigure(CanvasFigureLoop.Closed);
        }
        using var geometry = CanvasGeometry.CreatePath(rays);
        using var light = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(255, 225, 136, 67), ThemeColor(255, 174, 47, 0))
        { Center = center, RadiusX = reach, RadiusY = reach };
        ds.FillGeometry(geometry, light);
    }

    private static void DrawSlotCelebrationCoins(CanvasDrawingSession ds, Rect window, double t, int count)
    {
        using var gold = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 247, 186) },
            new() { Position = .22f, Color = ThemeColor(231, 170, 56) },
            new() { Position = .44f, Color = ThemeColor(255, 228, 118) },
            new() { Position = .54f, Color = ThemeColor(173, 94, 20) },
            new() { Position = .83f, Color = ThemeColor(255, 208, 82) },
            new() { Position = 1, Color = ThemeColor(235, 150, 35) }
        ]) { StartPoint = new(-.45f, -1), EndPoint = new(.45f, 1) };
        using var crest = CanvasGeometry.CreatePolygon(ds.Device,
        [
            new(0, -.49f), new(.14f, -.17f), new(.40f, 0), new(.14f, .17f),
            new(0, .49f), new(-.14f, .17f), new(-.40f, 0), new(-.14f, -.17f)
        ]);
        float height = (float)window.Height;
        for (int index = 0; index < count; index++)
        {
            float seed = SlotRandom(index * 41 + 507);
            float speed = 86 + SlotRandom(index * 71 + 93) * 110;
            float fall = (float)((t * speed + seed * (height + 92)) % (height + 92));
            float sway = MathF.Sin((float)t * 1.8f + index * 1.9f) * 20;
            float x = (float)window.X + (float)window.Width * SlotRandom(index * 59 + 205) + sway;
            float y = (float)window.Y - 40 + fall;
            float radius = 7 + SlotRandom(index * 37 + 341) * 10;
            float rotation = (float)t * (2 + seed * 4) + index * 2.7f;
            float faceWidth = .10f + .90f * MathF.Abs(MathF.Cos(rotation));
            float tilt = MathF.Sin((float)t * 1.5f + index) * .65f;
            var position = new Vector2(x, y);
            ds.DrawLine(position - new Vector2(sway * .04f, radius + 10 + speed * .1f),
                position - new Vector2(0, radius), ThemeColor(255, 212, 103, 65), radius * .12f);
            var previous = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(radius * faceWidth, radius)
                * Matrix3x2.CreateRotation(tilt) * Matrix3x2.CreateTranslation(position) * previous;
            try
            {
                ds.FillCircle(new Vector2(.12f / faceWidth, .05f), 1, ThemeColor(137, 65, 11));
                ds.FillCircle(Vector2.Zero, 1, gold);
                ds.DrawCircle(Vector2.Zero, .975f, ThemeColor(255, 245, 176), .045f);
                ds.DrawCircle(Vector2.Zero, .79f, ThemeColor(150, 78, 15), .062f);
                ds.DrawCircle(Vector2.Zero, .72f, ThemeColor(255, 235, 142), .033f);
                for (int groove = 0; groove < 14; groove++)
                {
                    float angle = groove * MathF.Tau / 14;
                    var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    ds.DrawLine(direction * .84f, direction * .92f, ThemeColor(144, 72, 11), .037f);
                }
                ds.FillGeometry(crest, ThemeColor(212, 144, 36));
                ds.DrawGeometry(crest, ThemeColor(255, 234, 139), .054f);
                ds.DrawLine(new(-.23f, -.32f), new(.12f, -.46f), ThemeColor(255, 253, 208), .047f);
            }
            finally { ds.Transform = previous; }
        }
    }

    private static void DrawSlotCelebrationLetters(CanvasDrawingSession ds, string text, Rect box,
        float size, double t, bool heavy)
    {
        using var format = FitSlotTextFormat(ds.Device, text, box, size, 1, "Bahnschrift", true);
        format.FontWeight = FontWeights.Bold;
        using var layout = new CanvasTextLayout(ds.Device, text, format, (float)box.Width, (float)box.Height);
        using var glyphs = CanvasGeometry.CreateText(layout).Transform(Matrix3x2.CreateTranslation((float)box.X, (float)box.Y));
        var ink = glyphs.ComputeBounds();
        float rim = heavy ? 3.2f : 2.4f;
        using var shadow = glyphs.Transform(Matrix3x2.CreateTranslation(0, heavy ? 5 : 3.5f));
        ds.DrawGeometry(shadow, ThemeColor(20, 3, 7, 230), rim * 2.6f);
        ds.FillGeometry(shadow, ThemeColor(107, 38, 6));
        ds.DrawGeometry(glyphs, ThemeColor(255, 215, 106), rim * 2);
        ds.DrawGeometry(glyphs, ThemeColor(91, 40, 10), rim);
        using var face = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 253, 216) },
            new() { Position = .20f, Color = ThemeColor(255, 234, 149) },
            new() { Position = .46f, Color = ThemeColor(240, 180, 53) },
            new() { Position = .50f, Color = ThemeColor(155, 74, 13) },
            new() { Position = .61f, Color = ThemeColor(238, 162, 29) },
            new() { Position = .88f, Color = ThemeColor(255, 222, 96) },
            new() { Position = 1, Color = ThemeColor(255, 245, 164) }
        ]) { StartPoint = new(0, (float)ink.Y), EndPoint = new(0, (float)ink.Bottom) };
        ds.FillGeometry(glyphs, face);
        ds.DrawGeometry(glyphs, ThemeColor(255, 242, 163, 220), .55f);
        float sweep = (float)((t * .40) % 1.7 - .35);
        float x = (float)ink.X + sweep * (float)ink.Width;
        using var glint = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 255, 224, 0) },
            new() { Position = .45f, Color = ThemeColor(255, 255, 224, 0) },
            new() { Position = .50f, Color = ThemeColor(255, 255, 224, 180) },
            new() { Position = .55f, Color = ThemeColor(255, 255, 224, 0) },
            new() { Position = 1, Color = ThemeColor(255, 255, 224, 0) }
        ]) { StartPoint = new(x - 150, (float)ink.Y), EndPoint = new(x + 150, (float)ink.Bottom) };
        ds.FillGeometry(glyphs, glint);
    }

    private static void DrawSlotCelebrationFlourish(CanvasDrawingSession ds, float x, float y, float length, double t)
    {
        var center = new Vector2(x, y);
        using var line = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 211, 108, 0) },
            new() { Position = .5f, Color = ThemeColor(255, 230, 152, 200) },
            new() { Position = 1, Color = ThemeColor(255, 211, 108, 0) }
        ]) { StartPoint = center - new Vector2(length, 0), EndPoint = center + new Vector2(length, 0) };
        ds.DrawLine(center - new Vector2(length, 0), center + new Vector2(length, 0), line, 1);
        float radius = 3.2f + MathF.Sin((float)t * 3) * .35f;
        using var jewel = CanvasGeometry.CreatePolygon(ds.Device,
            new[] { center + new Vector2(0, -radius), center + new Vector2(radius, 0),
                center + new Vector2(0, radius), center + new Vector2(-radius, 0) });
        ds.FillGeometry(jewel, ThemeColor(255, 234, 147));
    }
}
