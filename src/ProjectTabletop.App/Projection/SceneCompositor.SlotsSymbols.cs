using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Illustrated and sculpted type symbols are cached once per device, sharp
    // and motion-blurred, then placed physically square on any board aspect.
    private const int SlotSpritePixels = 512;
    private readonly Dictionary<(SlotSymbol Symbol, bool Blurred), CanvasRenderTarget> _slotSprites = [];
    private CanvasDevice? _slotSpriteDevice;

    private static readonly Color SlotGold = ThemeColor(240, 201, 116);
    private static readonly Color SlotDeepGold = ThemeColor(163, 108, 38);
    private static readonly Color SlotEmber = ThemeColor(255, 122, 36);

    private CanvasRenderTarget SlotSprite(CanvasDevice device, SlotSymbol symbol, bool blurred)
    {
        if (_slotSpriteDevice != device)
        {
            if (_slotSpriteDevice is not null) DisposeSlotSprites();
            _slotSpriteDevice = device;
        }
        if (_slotSprites.TryGetValue((symbol, blurred), out var cached)) return cached;
        var sprite = new CanvasRenderTarget(device, SlotSpritePixels, SlotSpritePixels, 96);
        using (var ds = sprite.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            if (blurred)
            {
                // Vertical motion blur of the sharp sprite, for fast-turning reels.
                using var blur = new DirectionalBlurEffect
                {
                    Source = SlotSprite(device, symbol, false), Angle = MathF.PI / 2, BlurAmount = 16,
                    BorderMode = EffectBorderMode.Soft, Optimization = EffectOptimization.Quality
                };
                ds.DrawImage(blur);
            }
            else
            {
                ds.Transform = Matrix3x2.CreateScale(SlotSpritePixels / 100f);
                if (!DrawIllustratedSlotSymbol(ds, symbol)) DrawSlotSymbolArt(ds, symbol);
            }
        }
        _slotSprites[(symbol, blurred)] = sprite;
        return sprite;
    }

    private void DisposeSlotSprites()
    {
        foreach (var sprite in _slotSprites.Values) sprite.Dispose();
        _slotSprites.Clear();
        _slotSpriteDevice = null;
        DisposeSlotArtwork();
    }

    /// <summary>Draws a symbol in a 100 × 100 box.</summary>
    private static void DrawSlotSymbolArt(CanvasDrawingSession ds, SlotSymbol symbol)
    {
        switch (symbol)
        {
            case SlotSymbol.Ten: DrawSlotRoyal(ds, "10", ThemeColor(94, 220, 214), ThemeColor(18, 96, 110)); break;
            case SlotSymbol.Jack: DrawSlotRoyal(ds, "J", ThemeColor(140, 230, 110), ThemeColor(30, 104, 44)); break;
            case SlotSymbol.Queen: DrawSlotRoyal(ds, "Q", ThemeColor(214, 142, 255), ThemeColor(84, 34, 128)); break;
            case SlotSymbol.King: DrawSlotRoyal(ds, "K", ThemeColor(120, 176, 255), ThemeColor(28, 58, 142)); break;
            case SlotSymbol.Ace: DrawSlotRoyal(ds, "A", ThemeColor(255, 128, 104), ThemeColor(136, 24, 26)); break;
            case SlotSymbol.Dagger: DrawSlotDagger(ds); break;
            case SlotSymbol.Goblet: DrawSlotGoblet(ds); break;
            case SlotSymbol.Chest: DrawSlotChest(ds, open: false); break;
            case SlotSymbol.Crown: DrawSlotCrown(ds); break;
            case SlotSymbol.Wild: DrawSlotWild(ds); break;
            case SlotSymbol.Coin: DrawSlotCoin(ds); break;
            case SlotSymbol.EggGreen: DrawSlotEgg(ds, ThemeColor(150, 240, 120), ThemeColor(30, 128, 52), ThemeColor(12, 60, 26)); break;
            case SlotSymbol.EggRed: DrawSlotEgg(ds, ThemeColor(255, 170, 90), ThemeColor(214, 52, 28), ThemeColor(96, 12, 10)); break;
            case SlotSymbol.EggBlue: DrawSlotEgg(ds, ThemeColor(150, 220, 255), ThemeColor(38, 112, 226), ThemeColor(12, 34, 104)); break;
            case SlotSymbol.EggRainbow: DrawSlotEgg(ds, default, default, default, rainbow: true); break;
            case SlotSymbol.Elixir: DrawSlotElixir(ds); break;
            case SlotSymbol.Key: DrawSlotKey(ds); break;
        }
    }

    private static void DrawSlotRoyal(CanvasDrawingSession ds, string text, Color light, Color dark)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Georgia", FontSize = text.Length > 1 ? 62 : 74, FontWeight = FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        using var layout = new CanvasTextLayout(ds.Device, text, format, 100, 100);
        using var glyphs = CanvasGeometry.CreateText(layout);
        using var shadow = glyphs.Transform(Matrix3x2.CreateTranslation(3.5f, 5));
        ds.DrawGeometry(shadow, ThemeColor(0, 0, 0, 170), 6);
        ds.FillGeometry(shadow, ThemeColor(0, 0, 0, 210));
        // A real stepped edge gives the enamel lettering weight. Small metallic
        // reflections survive projection without the previous flat outline.
        for (int depth = 4; depth >= 1; depth--)
        {
            using var side = glyphs.Transform(Matrix3x2.CreateTranslation(depth * .65f, depth * .85f));
            ds.DrawGeometry(side, ThemeColor((byte)(82 + depth * 9), (byte)(40 + depth * 6), 18), 5);
            ds.FillGeometry(side, SlotDeepGold);
        }
        using var metal = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 250, 220) },
            new() { Position = .30f, Color = ThemeColor(234, 179, 77) },
            new() { Position = .48f, Color = ThemeColor(107, 59, 21) },
            new() { Position = .53f, Color = ThemeColor(255, 242, 181) },
            new() { Position = .78f, Color = ThemeColor(151, 89, 28) },
            new() { Position = 1, Color = ThemeColor(252, 219, 139) }
        ]) { StartPoint = new(28, 14), EndPoint = new(70, 88) };
        ds.DrawGeometry(glyphs, metal, 5);
        using var fill = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 255, 255) },
            new() { Position = .25f, Color = light },
            new() { Position = .49f, Color = dark },
            new() { Position = .52f, Color = light },
            new() { Position = .75f, Color = dark },
            new() { Position = 1, Color = ThemeColor((byte)(dark.R / 2), (byte)(dark.G / 2), (byte)(dark.B / 2)) }
        ]) { StartPoint = new(50, 14), EndPoint = new(50, 88) };
        ds.FillGeometry(glyphs, fill);
        ds.DrawGeometry(glyphs, ThemeColor(30, 18, 8, 230), .85f);
        using (ds.CreateLayer(1, glyphs))
        {
            for (int line = 0; line < 12; line++)
                ds.DrawLine(10, 25 + line * 5, 88, 17 + line * 5, ThemeColor(255, 255, 255, 18), .28f);
            using var shine = new CanvasLinearGradientBrush(ds.Device,
                ThemeColor(255, 255, 255, 110), ThemeColor(255, 255, 255, 0))
            { StartPoint = new(35, 17), EndPoint = new(66, 65) };
            ds.FillEllipse(new Vector2(32, 26), 38, 22, shine);
        }
    }

    private static void DrawSlotDagger(CanvasDrawingSession ds)
    {
        var turn = Matrix3x2.CreateRotation(-.62f, new Vector2(50, 50));
        var previous = ds.Transform;
        ds.Transform = turn * previous;
        try
        {
            using var blade = CanvasGeometry.CreatePolygon(ds.Device,
                [new(50, 4), new(58, 18), new(57, 62), new(43, 62), new(42, 18)]);
            using var steel = new CanvasLinearGradientBrush(ds.Device, ThemeColor(246, 250, 255), ThemeColor(120, 136, 158))
            { StartPoint = new(42, 30), EndPoint = new(58, 34) };
            ds.FillGeometry(blade, steel);
            ds.DrawLine(50, 8, 50, 60, ThemeColor(255, 255, 255, 200), 1.2f);
            ds.DrawGeometry(blade, ThemeColor(40, 44, 58), 1.4f);
            using var gold = new CanvasLinearGradientBrush(ds.Device, SlotGold, SlotDeepGold)
            { StartPoint = new(30, 60), EndPoint = new(70, 70) };
            ds.FillRoundedRectangle(new Rect(28, 61, 44, 8), 4, 4, gold);
            ds.FillRoundedRectangle(new Rect(45, 69, 10, 19), 3, 3, ThemeColor(84, 40, 26));
            for (float y = 72; y < 88; y += 4) ds.DrawLine(45, y, 55, y + 2, ThemeColor(150, 90, 50), 1);
            ds.FillCircle(new Vector2(50, 92), 6, gold);
            ds.FillCircle(new Vector2(50, 92), 3, ThemeColor(230, 40, 40));
            ds.FillCircle(new Vector2(28, 65), 4, ThemeColor(90, 220, 140));
            ds.FillCircle(new Vector2(72, 65), 4, ThemeColor(90, 220, 140));
        }
        finally { ds.Transform = previous; }
    }

    private static void DrawSlotGoblet(CanvasDrawingSession ds)
    {
        using var builder = new CanvasPathBuilder(ds.Device);
        builder.BeginFigure(22, 14);
        builder.AddLine(78, 14);
        builder.AddCubicBezier(new(78, 44), new(64, 56), new(55, 58));
        builder.AddLine(55, 76);
        builder.AddCubicBezier(new(64, 78), new(74, 82), new(76, 90));
        builder.AddLine(24, 90);
        builder.AddCubicBezier(new(26, 82), new(36, 78), new(45, 76));
        builder.AddLine(45, 58);
        builder.AddCubicBezier(new(36, 56), new(22, 44), new(22, 14));
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var cup = CanvasGeometry.CreatePath(builder);
        using (var shadow = cup.Transform(Matrix3x2.CreateTranslation(2, 3))) ds.FillGeometry(shadow, ThemeColor(0, 0, 0, 140));
        using var gold = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 243, 190) },
            new() { Position = .35f, Color = SlotGold },
            new() { Position = .7f, Color = SlotDeepGold },
            new() { Position = 1, Color = ThemeColor(255, 220, 140) }
        ]) { StartPoint = new(22, 50), EndPoint = new(78, 50) };
        ds.FillGeometry(cup, gold);
        ds.DrawGeometry(cup, ThemeColor(90, 52, 14), 1.5f);
        ds.FillEllipse(new Vector2(50, 15), 28, 5, ThemeColor(120, 16, 40));
        ds.DrawEllipse(new Vector2(50, 15), 28, 5, ThemeColor(255, 236, 170), 1.4f);
        ds.FillCircle(new Vector2(50, 36), 6, ThemeColor(40, 160, 230));
        ds.FillCircle(new Vector2(36, 32), 3.5f, ThemeColor(220, 40, 60));
        ds.FillCircle(new Vector2(64, 32), 3.5f, ThemeColor(220, 40, 60));
        ds.FillCircle(new Vector2(48, 34), 1.8f, ThemeColor(255, 255, 255, 200));
        ds.DrawLine(30, 22, 34, 42, ThemeColor(255, 255, 230, 150), 2);
    }

    private static void DrawSlotChest(CanvasDrawingSession ds, bool open)
    {
        ds.FillRoundedRectangle(new Rect(12, 36, 80, 58), 6, 6, ThemeColor(0, 0, 0, 130));
        using var wood = new CanvasLinearGradientBrush(ds.Device, ThemeColor(168, 84, 40), ThemeColor(86, 36, 18))
        { StartPoint = new(50, 30), EndPoint = new(50, 92) };
        using var gold = new CanvasLinearGradientBrush(ds.Device, SlotGold, SlotDeepGold)
        { StartPoint = new(50, 20), EndPoint = new(50, 92) };
        if (open)
        {
            ds.FillRoundedRectangle(new Rect(14, 8, 72, 30), 10, 10, wood);
            ds.DrawRoundedRectangle(new Rect(14, 8, 72, 30), 10, 10, gold, 3);
            using var glow = new CanvasRadialGradientBrush(ds.Device, ThemeColor(255, 240, 160, 230), ThemeColor(255, 180, 60, 0))
            { Center = new(50, 46), RadiusX = 44, RadiusY = 26 };
            ds.FillEllipse(new Vector2(50, 46), 44, 26, glow);
        }
        else
        {
            using var lid = new CanvasPathBuilder(ds.Device);
            lid.BeginFigure(10, 48);
            lid.AddCubicBezier(new(10, 22), new(90, 22), new(90, 48));
            lid.EndFigure(CanvasFigureLoop.Closed);
            using var lidGeometry = CanvasGeometry.CreatePath(lid);
            ds.FillGeometry(lidGeometry, wood);
            ds.DrawGeometry(lidGeometry, gold, 3);
        }
        ds.FillRoundedRectangle(new Rect(10, 48, 80, 42), 4, 4, wood);
        for (float x = 18; x < 88; x += 9) ds.DrawLine(x, 50, x, 88, ThemeColor(60, 24, 10, 120), 1);
        ds.DrawRoundedRectangle(new Rect(10, 48, 80, 42), 4, 4, gold, 3);
        ds.FillRectangle(new Rect(24, 36, 7, 54), gold);
        ds.FillRectangle(new Rect(69, 36, 7, 54), gold);
        ds.FillRoundedRectangle(new Rect(42, 50, 16, 18), 3, 3, gold);
        ds.FillCircle(new Vector2(50, 57), 2.6f, ThemeColor(40, 20, 6));
        ds.FillRectangle(new Rect(49, 57, 2, 6), ThemeColor(40, 20, 6));
    }

    private static void DrawSlotCrown(CanvasDrawingSession ds)
    {
        using var builder = new CanvasPathBuilder(ds.Device);
        builder.BeginFigure(12, 78);
        builder.AddLine(8, 30);
        builder.AddLine(30, 52);
        builder.AddLine(50, 18);
        builder.AddLine(70, 52);
        builder.AddLine(92, 30);
        builder.AddLine(88, 78);
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var crown = CanvasGeometry.CreatePath(builder);
        using (var shadow = crown.Transform(Matrix3x2.CreateTranslation(2, 3))) ds.FillGeometry(shadow, ThemeColor(0, 0, 0, 140));
        using var gold = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 246, 196) },
            new() { Position = .45f, Color = SlotGold },
            new() { Position = 1, Color = SlotDeepGold }
        ]) { StartPoint = new(50, 18), EndPoint = new(50, 90) };
        ds.FillGeometry(crown, gold);
        ds.DrawGeometry(crown, ThemeColor(96, 56, 12), 1.6f);
        ds.FillRoundedRectangle(new Rect(10, 72, 80, 16), 5, 5, gold);
        ds.DrawRoundedRectangle(new Rect(10, 72, 80, 16), 5, 5, ThemeColor(96, 56, 12), 1.4f);
        foreach (var (x, color) in new[] { (26f, ThemeColor(40, 150, 240)), (50f, ThemeColor(230, 30, 50)), (74f, ThemeColor(40, 200, 110)) })
        {
            ds.FillCircle(new Vector2(x, 80), 5, color);
            ds.FillCircle(new Vector2(x - 1.5f, 78.5f), 1.6f, ThemeColor(255, 255, 255, 210));
        }
        foreach (var point in new Vector2[] { new(8, 30), new(50, 18), new(92, 30) })
            ds.FillCircle(point, 5, ThemeColor(255, 236, 150));
        ds.FillCircle(new Vector2(50, 50), 6.5f, ThemeColor(200, 20, 60));
        ds.FillCircle(new Vector2(48, 48), 2, ThemeColor(255, 255, 255, 200));
    }

    private static void DrawSlotWild(CanvasDrawingSession ds)
    {
        // A dragon's eye in a burning shield, with the WILD scroll across it.
        using var flames = new CanvasPathBuilder(ds.Device);
        flames.BeginFigure(50, 2);
        (float X, float Y)[] points = [(62, 18), (76, 8), (78, 28), (94, 24), (88, 46), (96, 62), (78, 66), (70, 92),
            (50, 80), (30, 92), (22, 66), (4, 62), (12, 46), (6, 24), (22, 28), (24, 8), (38, 18)];
        foreach (var (x, y) in points) flames.AddLine(x, y);
        flames.EndFigure(CanvasFigureLoop.Closed);
        using var fire = CanvasGeometry.CreatePath(flames);
        using var fireBrush = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 240, 150) },
            new() { Position = .45f, Color = ThemeColor(255, 140, 30) },
            new() { Position = 1, Color = ThemeColor(170, 20, 10) }
        ]) { Center = new(50, 46), RadiusX = 50, RadiusY = 50 };
        ds.FillGeometry(fire, fireBrush);
        ds.DrawGeometry(fire, ThemeColor(90, 10, 4), 1.2f);
        ds.FillEllipse(new Vector2(50, 42), 30, 20, ThemeColor(30, 8, 6));
        using var iris = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 250, 170) },
            new() { Position = .6f, Color = ThemeColor(250, 170, 20) },
            new() { Position = 1, Color = ThemeColor(150, 60, 0) }
        ]) { Center = new(50, 42), RadiusX = 26, RadiusY = 16 };
        using var eyeBuilder = new CanvasPathBuilder(ds.Device);
        eyeBuilder.BeginFigure(22, 42);
        eyeBuilder.AddCubicBezier(new(34, 26), new(66, 26), new(78, 42));
        eyeBuilder.AddCubicBezier(new(66, 58), new(34, 58), new(22, 42));
        eyeBuilder.EndFigure(CanvasFigureLoop.Closed);
        using var eye = CanvasGeometry.CreatePath(eyeBuilder);
        ds.FillGeometry(eye, iris);
        ds.FillEllipse(new Vector2(50, 42), 3.4f, 13, ThemeColor(10, 4, 2));
        ds.FillCircle(new Vector2(58, 36), 2.6f, ThemeColor(255, 255, 255, 220));
        ds.DrawGeometry(eye, ThemeColor(60, 14, 4), 1.4f);
        using var scroll = new CanvasLinearGradientBrush(ds.Device, ThemeColor(255, 240, 190), SlotGold)
        { StartPoint = new(50, 62), EndPoint = new(50, 86) };
        ds.FillRoundedRectangle(new Rect(12, 63, 76, 23), 6, 6, scroll);
        ds.DrawRoundedRectangle(new Rect(12, 63, 76, 23), 6, 6, ThemeColor(110, 40, 6), 1.6f);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Bahnschrift", FontSize = 20, FontWeight = FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center
        };
        ds.DrawText("WILD", new Rect(12, 62, 76, 25), ThemeColor(120, 20, 6), format);
    }

    private static void DrawSlotCoin(CanvasDrawingSession ds)
    {
        using var glow = new CanvasRadialGradientBrush(ds.Device, ThemeColor(255, 150, 40, 170), ThemeColor(255, 80, 0, 0))
        { Center = new(50, 50), RadiusX = 50, RadiusY = 50 };
        ds.FillCircle(new Vector2(50, 50), 50, glow);
        using var flame = new CanvasPathBuilder(ds.Device);
        flame.BeginFigure(50, 3);
        for (int index = 1; index < 16; index++)
        {
            float angle = index / 16f * MathF.Tau - MathF.PI / 2;
            float radius = index % 2 == 0 ? 46 : 38;
            flame.AddLine(50 + MathF.Cos(angle) * radius, 50 + MathF.Sin(angle) * radius);
        }
        flame.EndFigure(CanvasFigureLoop.Closed);
        using var flames = CanvasGeometry.CreatePath(flame);
        ds.FillGeometry(flames, ThemeColor(255, 110, 20, 210));
        using var body = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(255, 214, 110) },
            new() { Position = .7f, Color = ThemeColor(222, 90, 18) },
            new() { Position = 1, Color = ThemeColor(120, 26, 6) }
        ]) { Center = new(44, 40), RadiusX = 44, RadiusY = 44 };
        ds.FillCircle(new Vector2(50, 50), 35, body);
        ds.DrawCircle(new Vector2(50, 50), 35, SlotGold, 3.2f);
        ds.DrawCircle(new Vector2(50, 50), 30, ThemeColor(255, 230, 160, 160), 1);
        ds.FillEllipse(new Vector2(38, 32), 11, 6, ThemeColor(255, 255, 230, 110));
    }

    private static void DrawSlotEgg(CanvasDrawingSession ds, Color light, Color mid, Color dark, bool rainbow = false)
    {
        using var builder = new CanvasPathBuilder(ds.Device);
        builder.BeginFigure(50, 6);
        builder.AddCubicBezier(new(76, 6), new(86, 50), new(84, 64));
        builder.AddCubicBezier(new(82, 84), new(66, 95), new(50, 95));
        builder.AddCubicBezier(new(34, 95), new(18, 84), new(16, 64));
        builder.AddCubicBezier(new(14, 50), new(24, 6), new(50, 6));
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var egg = CanvasGeometry.CreatePath(builder);
        using (var shadow = egg.Transform(Matrix3x2.CreateTranslation(2, 3))) ds.FillGeometry(shadow, ThemeColor(0, 0, 0, 140));
        ICanvasBrush brush;
        if (rainbow)
            brush = new CanvasLinearGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = ThemeColor(255, 90, 80) },
                new() { Position = .25f, Color = ThemeColor(255, 210, 70) },
                new() { Position = .5f, Color = ThemeColor(90, 230, 110) },
                new() { Position = .75f, Color = ThemeColor(70, 170, 255) },
                new() { Position = 1, Color = ThemeColor(190, 90, 255) }
            ]) { StartPoint = new(20, 10), EndPoint = new(80, 92) };
        else
            brush = new CanvasRadialGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = light },
                new() { Position = .55f, Color = mid },
                new() { Position = 1, Color = dark }
            ]) { Center = new(40, 36), RadiusX = 56, RadiusY = 64 };
        using (brush) ds.FillGeometry(egg, brush);
        // Overlapping dragon scales.
        using (ds.CreateLayer(1, egg))
            for (int row = 0; row < 9; row++)
                for (int column = -1; column < 7; column++)
                {
                    float x = 14 + column * 13 + (row % 2) * 6.5f, y = 14 + row * 9.5f;
                    ds.DrawEllipse(new Vector2(x, y), 7, 6, ThemeColor(0, 0, 0, 55), 1.2f);
                    ds.DrawEllipse(new Vector2(x, y - 1), 6, 4.5f, ThemeColor(255, 255, 255, 40), .8f);
                }
        ds.DrawGeometry(egg, SlotGold, 2.6f);
        ds.FillEllipse(new Vector2(38, 26), 8, 12, ThemeColor(255, 255, 255, 90));
    }

    private static void DrawSlotElixir(CanvasDrawingSession ds)
    {
        using var builder = new CanvasPathBuilder(ds.Device);
        builder.BeginFigure(42, 30);
        builder.AddLine(42, 16);
        builder.AddLine(58, 16);
        builder.AddLine(58, 30);
        builder.AddCubicBezier(new(82, 38), new(86, 62), new(80, 74));
        builder.AddCubicBezier(new(74, 90), new(26, 90), new(20, 74));
        builder.AddCubicBezier(new(14, 62), new(18, 38), new(42, 30));
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var bottle = CanvasGeometry.CreatePath(builder);
        using var glow = new CanvasRadialGradientBrush(ds.Device, ThemeColor(255, 90, 210, 150), ThemeColor(255, 60, 200, 0))
        { Center = new(50, 62), RadiusX = 48, RadiusY = 42 };
        ds.FillEllipse(new Vector2(50, 62), 48, 42, glow);
        ds.FillGeometry(bottle, ThemeColor(230, 240, 255, 70));
        using var liquid = new CanvasLinearGradientBrush(ds.Device, ThemeColor(255, 130, 220), ThemeColor(150, 20, 150))
        { StartPoint = new(50, 44), EndPoint = new(50, 88) };
        using (ds.CreateLayer(1, bottle))
            ds.FillRectangle(new Rect(0, 46, 100, 54), liquid);
        for (int bubble = 0; bubble < 5; bubble++)
            ds.DrawCircle(new Vector2(34 + bubble * 7, 70 - bubble % 3 * 7), 2 + bubble % 2, ThemeColor(255, 220, 255, 170), 1);
        ds.DrawGeometry(bottle, ThemeColor(255, 220, 250), 2);
        ds.FillRoundedRectangle(new Rect(40, 6, 20, 12), 3, 3, ThemeColor(150, 92, 50));
        ds.DrawRoundedRectangle(new Rect(40, 6, 20, 12), 3, 3, SlotGold, 1.2f);
        ds.DrawLine(30, 44, 26, 64, ThemeColor(255, 255, 255, 170), 2.4f);
    }

    private static void DrawSlotKey(CanvasDrawingSession ds)
    {
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(-.75f, new Vector2(50, 50)) * previous;
        try
        {
            using var gold = new CanvasLinearGradientBrush(ds.Device, ThemeColor(255, 240, 170), SlotDeepGold)
            { StartPoint = new(30, 20), EndPoint = new(70, 80) };
            ds.FillCircle(new Vector2(50, 24), 17, gold);
            ds.FillCircle(new Vector2(50, 24), 8, ThemeColor(60, 30, 10));
            ds.DrawCircle(new Vector2(50, 24), 17, ThemeColor(110, 60, 14), 1.4f);
            ds.FillRoundedRectangle(new Rect(46, 38, 8, 50), 3, 3, gold);
            ds.FillRectangle(new Rect(54, 70, 14, 7), gold);
            ds.FillRectangle(new Rect(54, 81, 10, 7), gold);
            ds.DrawRoundedRectangle(new Rect(46, 38, 8, 50), 3, 3, ThemeColor(110, 60, 14), 1.2f);
            ds.FillCircle(new Vector2(50, 24), 3, ThemeColor(90, 200, 255));
        }
        finally { ds.Transform = previous; }
    }

    /// <summary>A treasure-vault gem: ruby for Grand, emerald for Major, sapphire for Minor.</summary>
    private void DrawSlotGem(CanvasDrawingSession ds, Vector2 center, float size, SlotJackpot tier)
    {
        int treasure = tier == SlotJackpot.Grand ? 1 : tier == SlotJackpot.Major ? 2 : 3;
        if (DrawSlotTreasure(ds, treasure, new Rect(center.X - size, center.Y - size, size * 2, size * 2))) return;
        var (light, dark) = SlotJackpotColors(tier);
        Vector2[] outline = [new(-.38f, -.93f), new(.38f, -.93f), new(.85f, -.4f), new(.82f, .36f),
            new(.35f, .9f), new(-.35f, .9f), new(-.82f, .36f), new(-.85f, -.4f)];
        using var gem = CanvasGeometry.CreatePolygon(ds.Device, outline.Select(point => center + point * size).ToArray());
        using var brush = new CanvasLinearGradientBrush(ds.Device, light, dark)
        { StartPoint = center - new Vector2(size, size), EndPoint = center + new Vector2(size, size) };
        ds.FillGeometry(gem, brush);
        Vector2 tableCenter = center + new Vector2(-.1f, -.14f) * size;
        for (int facet = 0; facet < outline.Length; facet++)
        {
            Vector2 p = center + outline[facet] * size, next = center + outline[(facet + 1) % outline.Length] * size;
            Vector2 inner = tableCenter + outline[facet] * size * .43f;
            Vector2 innerNext = tableCenter + outline[(facet + 1) % outline.Length] * size * .43f;
            using var face = CanvasGeometry.CreatePolygon(ds.Device, [p, next, innerNext, inner]);
            ds.FillGeometry(face, facet is 0 or 1 or 7 ? WithAlpha(SlotIvory, (byte)(facet == 0 ? 190 : 100)) :
                facet is 3 or 4 ? WithAlpha(dark, 225) : WithAlpha(light, 125));
            ds.DrawLine(p, inner, ThemeColor(255, 255, 255, 100), Math.Max(.4f, size * .018f));
        }
        using var table = CanvasGeometry.CreatePolygon(ds.Device,
            outline.Select(point => tableCenter + point * size * .43f).ToArray());
        ds.FillGeometry(table, WithAlpha(light, 130));
        ds.DrawGeometry(table, ThemeColor(255, 255, 255, 155), Math.Max(.5f, size * .025f));
        ds.DrawGeometry(gem, ThemeColor(255, 235, 190, 210), Math.Max(.7f, size * .045f));
        var glint = center + new Vector2(-.38f, -.72f) * size;
        ds.DrawLine(glint - new Vector2(size * .21f, 0), glint + new Vector2(size * .21f, 0),
            ThemeColor(255, 255, 255, 240), Math.Max(.6f, size * .025f));
        ds.DrawLine(glint - new Vector2(0, size * .21f), glint + new Vector2(0, size * .21f),
            ThemeColor(255, 255, 255, 240), Math.Max(.6f, size * .025f));
    }

    private static (Color Light, Color Dark) SlotJackpotColors(SlotJackpot tier) => tier switch
    {
        SlotJackpot.Grand => (ThemeColor(255, 170, 90), ThemeColor(190, 30, 16)),
        SlotJackpot.Major => (ThemeColor(150, 245, 150), ThemeColor(20, 120, 50)),
        SlotJackpot.Minor => (ThemeColor(140, 205, 255), ThemeColor(24, 76, 190)),
        _ => (ThemeColor(226, 170, 255), ThemeColor(110, 40, 170))
    };
}
