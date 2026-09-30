using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    /// <summary>
    /// The reel window in the square board layout. Cells are physically square on
    /// the actual board aspect (limited so the side columns keep their room); five
    /// rows share the window's height when Expand opens the grid.
    /// </summary>
    private readonly record struct SlotLayout(float CellWidth, float Left, float Top, float Height, float Aspect)
    {
        public float Width => CellWidth * SlotGame.Reels;
        public float Right => Left + Width;
        public float Bottom => Top + Height;
        public Rect Window => new(Left, Top, Width, Height);
        public float ReelCenter(int reel) => Left + (reel + .5f) * CellWidth;

        public Rect Cell(int reel, float row, float firstRow, float rowCount)
        {
            float height = Height / rowCount;
            return new(Left + reel * CellWidth, Top + (row - firstRow) * height, CellWidth, height);
        }

        /// <summary>A physically square box of the given logical height, centred at a point.</summary>
        public Rect Square(Vector2 center, float size) => new(center.X - size / Aspect / 2, center.Y - size / 2, size / Aspect, size);

        public Rect SymbolBox(Rect cell, float fill = .86f)
        {
            float size = (float)Math.Min(cell.Height, cell.Width * Aspect) * fill;
            return Square(new((float)(cell.X + cell.Width / 2), (float)(cell.Y + cell.Height / 2)), size);
        }
    }

    private static SlotLayout SlotsLayout(double aspect)
    {
        float a = (float)(double.IsFinite(aspect) ? Math.Clamp(aspect, .6, 2.5) : 1);
        float cell = MathF.Min(140 / a, 118);
        return new(cell, 500 - cell * 2.5f, 255, 370, a);
    }

    private static readonly Color SlotIvory = ThemeColor(255, 244, 220);
    private static readonly Color SlotMuted = ThemeColor(214, 176, 150);

    /// <summary>The cabinet around the reels; the reel window's contents are a separate animated layer.</summary>
    private void DrawSlotsMachine(CanvasDrawingSession ds, SlotSnapshot game, IReadOnlyList<BoardButton> buttons,
        double boardAspect)
    {
        var layout = SlotsLayout(boardAspect);
        float a = layout.Aspect;
        DrawSlotsCave(ds);

        DrawSlotMarquee(ds, a);

        // Portraits and eggs are drawn in the live layer. Only their sockets
        // and nameplates belong in the cached cabinet.
        for (int index = 0; index < SlotDragonPowers.Length; index++)
        {
            var (power, _, label, color) = SlotDragonPowers[index];
            bool lit = game.DragonHatches.Any(hatch => hatch.Power == power);
            var center = SlotDragonCenter(index, a);
            DrawSlotPowerSocket(ds, center, color, a, lit);
            SlotText(ds, label, new Rect(center.X - 64 / a, 217, 128 / a, 17), 12, lit ? color : SlotMuted, a);
        }

        DrawSlotJackpots(ds, game, layout);
        DrawSlotReelFrame(ds, game, layout);
        DrawSlotKeys(ds, game, layout);
        DrawSlotSidePanel(ds, game, layout);

        DrawSlotStatusRail(ds);
        SlotText(ds, game.Status, new Rect(60, 704, 880, 28), 19, game.Phase == SlotPhase.Idle ? SlotIvory : SlotGold, a);
        DrawSlotPlaque(ds, new Rect(60, 740, 270, 92), "CREDITS", SlotGame.Format(game.Balance), a);
        DrawSlotPlaque(ds, new Rect(365, 740, 270, 92), "BET", SlotGame.Format(game.Bet), a);
        DrawSlotPlaque(ds, new Rect(670, 740, 270, 92), game.InFreeSpins ? "FREE SPINS WIN" : "WIN",
            SlotGame.Format(game.InFreeSpins ? game.FreeSpinsWin : game.RoundWin), a, highlight: game.RoundWin > 0);

        foreach (var button in buttons) DrawSlotButton(ds, button, game, a);
    }

    private void DrawSlotsCave(CanvasDrawingSession ds)
    {
        ds.Clear(ThemeColor(10, 5, 9));
        using var cave = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(84, 24, 30) },
            new() { Position = .45f, Color = ThemeColor(44, 14, 30) },
            new() { Position = 1, Color = ThemeColor(10, 5, 12) }
        ]) { Center = new(500, 430), RadiusX = 720, RadiusY = 640 };
        ds.FillRectangle(new Rect(0, 0, 1000, 1000), cave);
        DrawSlotBackdrop(ds);
        // A still, smoked-glass lower deck leaves the illustration visible around
        // the crown while making balances and camera-sensitive controls legible.
        using var shade = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(7, 8, 14, 15) },
            new() { Position = .6f, Color = ThemeColor(7, 8, 14, 45) },
            new() { Position = .82f, Color = ThemeColor(7, 8, 14, 224) },
            new() { Position = 1, Color = ThemeColor(7, 8, 14, 248) }
        ]) { StartPoint = Vector2.Zero, EndPoint = new(0, 1000) };
        ds.FillRectangle(new Rect(0, 0, 1000, 1000), shade);
        DrawSlotCabinetEdge(ds);
    }

    private void DrawSlotJackpots(CanvasDrawingSession ds, SlotSnapshot game, SlotLayout layout)
    {
        float left = 34, right = layout.Left - 26;
        if (right - left < 90) return;
        (SlotJackpot Tier, float Top, float Height)[] rows =
            [(SlotJackpot.Grand, 212, 104), (SlotJackpot.Major, 330, 88), (SlotJackpot.Minor, 432, 88), (SlotJackpot.Mini, 534, 80)];
        bool vault = game.Phase is SlotPhase.VaultIntro or SlotPhase.VaultPicking or SlotPhase.VaultOutro;
        foreach (var (tier, top, height) in rows)
        {
            var rect = new Rect(left, top, right - left, height);
            var (light, dark) = SlotJackpotColors(tier);
            bool won = vault && game.VaultAward == tier;
            DrawSlotInlaidPanel(ds, rect, won ? SlotIvory : light, dark);
            float size = tier == SlotJackpot.Grand ? 34 : 28;
            SlotText(ds, SlotGame.JackpotName(tier).ToUpperInvariant(), new Rect(rect.X, rect.Y + 7, rect.Width, 22),
                14, light, layout.Aspect);
            ds.DrawLine((float)rect.X + 24, top + 33, (float)rect.Right - 24, top + 33, WithAlpha(light, 85), .7f);
            SlotText(ds, SlotGame.Format(game.Jackpots[tier]), new Rect(rect.X, rect.Y + 30, rect.Width, size + 12),
                size, SlotIvory, layout.Aspect, "Georgia");
            if (vault && tier != SlotJackpot.Mini)
            {
                int found = game.VaultChests.Count(chest => chest?.Gem == tier);
                for (int dot = 0; dot < 3; dot++)
                {
                    var center = new Vector2((float)(rect.X + rect.Width / 2 + (dot - 1) * 26 / layout.Aspect), (float)rect.Bottom - 14);
                    if (dot < found)
                    {
                        var previous = ds.Transform;
                        ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1, center) * previous;
                        try { DrawSlotGem(ds, center, 9, tier); }
                        finally { ds.Transform = previous; }
                    }
                    else ds.DrawEllipse(center, 7 / layout.Aspect, 7, WithAlpha(light, 150), 1.5f);
                }
            }
        }
    }

    private static void DrawSlotReelFrame(CanvasDrawingSession ds, SlotSnapshot game, SlotLayout layout)
    {
        var window = layout.Window;
        Color accent = game.InFreeSpins ? ThemeColor(255, 110, 220) : game.InRespins ? SlotEmber : SlotGold;
        var outer = new Rect(window.X - 17, window.Y - 17, window.Width + 34, window.Height + 34);
        using var halo = new CanvasRadialGradientBrush(ds.Device, WithAlpha(accent, 90), WithAlpha(accent, 0))
        { Center = new((float)(outer.X + outer.Width / 2), (float)(outer.Y + outer.Height / 2)),
            RadiusX = (float)outer.Width * .62f, RadiusY = (float)outer.Height * .62f };
        ds.FillRectangle(new Rect(outer.X - 60, outer.Y - 40, outer.Width + 120, outer.Height + 80), halo);
        using var shell = SlotCutPanel(ds.Device, outer, 13);
        using var gold = SlotAntiqueMetal(ds.Device, outer);
        ds.FillGeometry(shell, gold);
        ds.DrawGeometry(shell, ThemeColor(32, 19, 12), 2);
        using var engraved = SlotCutPanel(ds.Device, SlotInset(outer, 4), 10);
        ds.DrawGeometry(engraved, ThemeColor(255, 232, 160, 185), .8f);
        using var recess = SlotCutPanel(ds.Device, SlotInset(outer, 8), 7);
        ds.FillGeometry(recess, ThemeColor(7, 9, 15));
        ds.DrawGeometry(recess, ThemeColor(84, 56, 27), 2);
        ds.DrawRectangle(new Rect(window.X - 2, window.Y - 2, window.Width + 4, window.Height + 4),
            ThemeColor(240, 194, 102, 165), 1);
        DrawSlotReelBed(ds, game, layout);
        DrawSlotReelEngraving(ds, layout, accent);
        DrawSlotReelJewelry(ds, layout, game.InRespins);
        foreach (var corner in new Vector2[] { new((float)outer.X, (float)outer.Y), new((float)outer.Right, (float)outer.Y),
            new((float)outer.X, (float)outer.Bottom), new((float)outer.Right, (float)outer.Bottom) })
            DrawSlotCornerClasp(ds, corner, corner.X < 500 ? 1 : -1, corner.Y < 400 ? 1 : -1);
    }

    private void DrawSlotKeys(CanvasDrawingSession ds, SlotSnapshot game, SlotLayout layout)
    {
        for (int reel = 0; reel < SlotGame.Reels; reel++)
        {
            var center = new Vector2(layout.ReelCenter(reel), 660);
            var box = layout.Square(center, 50);
            bool lit = game.Keys[reel];
            DrawSlotKeySocket(ds, box, layout.Aspect, lit);
            if (lit) DrawSlotArt(ds, SlotSymbol.Key, layout.Square(center, 44), 1);
            else
            {
                ds.FillEllipse(new Vector2(center.X, center.Y - 5), 6 / layout.Aspect, 6, ThemeColor(8, 4, 6));
                using var slot = CanvasGeometry.CreatePolygon(ds.Device,
                [
                    new(center.X - 3 / layout.Aspect, center.Y - 3), new(center.X + 3 / layout.Aspect, center.Y - 3),
                    new(center.X + 6 / layout.Aspect, center.Y + 13), new(center.X - 6 / layout.Aspect, center.Y + 13)
                ]);
                ds.FillGeometry(slot, ThemeColor(8, 4, 6));
            }
        }
        SlotText(ds, $"VAULT KEYS  {game.Keys.Count(lit => lit)} / 5", new Rect(layout.Left - 60, 686, layout.Width + 120, 18),
            13, SlotMuted, layout.Aspect);
    }

    private void DrawSlotSidePanel(CanvasDrawingSession ds, SlotSnapshot game, SlotLayout layout)
    {
        float left = layout.Right + 26, right = 966;
        if (right - left < 90) return;
        float a = layout.Aspect;
        var top = new Rect(left, 212, right - left, 176);
        DrawSlotPanel(ds, top, game.InFreeSpins ? ThemeColor(255, 110, 220) : SlotGold);
        if (game.InFreeSpins)
        {
            SlotText(ds, "FREE SPINS", new Rect(top.X, top.Y + 10, top.Width, 26), 20, ThemeColor(255, 160, 230), a);
            SlotText(ds, game.FreeSpinsRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Rect(top.X, top.Y + 38, top.Width, 70), 60, SlotIvory, a, fire: true);
            SlotText(ds, "LEFT", new Rect(top.X, top.Y + 108, top.Width, 22), 16, SlotMuted, a);
        }
        else
        {
            SlotText(ds, "GUARDIAN'S", new Rect(top.X, top.Y + 14, top.Width, 24), 17, SlotGold, a);
            SlotText(ds, "FREE SPINS", new Rect(top.X, top.Y + 38, top.Width, 28), 21, SlotIvory, a);
            SlotText(ds, "3 ELIXIRS ON", new Rect(top.X, top.Y + 76, top.Width, 20), 14, SlotMuted, a);
            SlotText(ds, "REELS 1 · 3 · 5", new Rect(top.X, top.Y + 96, top.Width, 20), 14, SlotMuted, a);
        }
        // Elixir level: each elixir during free spins adds a spin and more power eggs.
        for (int vial = 0; vial < SlotGame.MaximumElixirLevel; vial++)
        {
            float spacing = MathF.Min(30, (float)(top.Width - 24) * a / SlotGame.MaximumElixirLevel);
            var center = new Vector2((float)(top.X + top.Width / 2 + (vial - 2) * spacing / a), (float)top.Bottom - 26);
            DrawSlotArt(ds, SlotSymbol.Elixir, layout.Square(center, MathF.Min(30, spacing * .9f)), vial < game.ElixirLevel ? 1 : .38f);
        }

        var lower = new Rect(left, 402, right - left, 208);
        DrawSlotPanel(ds, lower, game.InRespins ? SlotEmber : SlotGold);
        if (game.Phase == SlotPhase.LineWins)
        {
            SlotText(ds, "LINE WIN", new Rect(lower.X, lower.Y + 12, lower.Width, 26), 20, SlotGold, a);
            SlotText(ds, $"{game.LineWins.Count} WINNING " + (game.LineWins.Count == 1 ? "LINE" : "LINES"),
                new Rect(lower.X, lower.Y + 178, lower.Width, 20), 13, SlotMuted, a);
        }
        else if (game.InRespins)
        {
            SlotText(ds, "RESPINS", new Rect(lower.X, lower.Y + 12, lower.Width, 26), 20, SlotEmber, a);
            for (int dot = 0; dot < SlotGame.RespinCount; dot++)
            {
                float spacing = MathF.Min(34, (float)(lower.Width - 24) * a / SlotGame.RespinCount);
                float radius = MathF.Min(12, spacing * .35f);
                var center = new Vector2((float)(lower.X + lower.Width / 2 + (dot - 1) * spacing / a), (float)lower.Y + 64);
                bool lit = dot < game.RespinsLeft;
                ds.FillEllipse(center, radius / a, radius, lit ? SlotEmber : ThemeColor(60, 30, 26));
                ds.DrawEllipse(center, radius / a, radius, SlotGold, 1.5f);
            }
            SlotText(ds, "TOTAL", new Rect(lower.X, lower.Y + 96, lower.Width, 22), 16, SlotMuted, a);
            SlotText(ds, SlotGame.Format(game.RespinTotal), new Rect(lower.X, lower.Y + 120, lower.Width, 44), 34,
                SlotIvory, a, fire: true);
        }
        else
        {
            SlotText(ds, "DRAGONFIRE", new Rect(lower.X, lower.Y + 12, lower.Width, 26), 20, SlotEmber, a);
            SlotText(ds, "RESPINS", new Rect(lower.X, lower.Y + 38, lower.Width, 26), 20, SlotIvory, a);
            SlotText(ds, "EGG + 3 COINS", new Rect(lower.X, lower.Y + 76, lower.Width, 20), 14, SlotMuted, a);
            // The fire orb is drawn by the live layer so its flames never freeze.
            SlotText(ds, $"BUY {SlotGame.BuyMultiplier}× BET", new Rect(lower.X, lower.Y + 178, lower.Width, 20), 13, SlotMuted, a);
        }
    }

    private static void DrawSlotPanel(CanvasDrawingSession ds, Rect rect, Color edge)
    {
        DrawSlotInlaidPanel(ds, rect, edge, ThemeColor(41, 29, 36));
    }

    private static void DrawSlotPlaque(CanvasDrawingSession ds, Rect rect, string title, string value, float aspect,
        bool highlight = false)
    {
        DrawSlotPanel(ds, rect, highlight ? SlotEmber : SlotGold);
        SlotText(ds, title, new Rect(rect.X, rect.Y + 8, rect.Width, 22), 13, SlotGold, aspect);
        SlotText(ds, value, new Rect(rect.X, rect.Y + 30, rect.Width, 50), 37, SlotIvory, aspect, "Georgia", fire: highlight);
    }

    // Pale opaque buttons with dark captions: palm-down fingers over dark glass
    // gave the camera almost no contrast. Captions are the long-press evidence.
    private static void DrawSlotButton(CanvasDrawingSession ds, BoardButton button, SlotSnapshot game, float aspect)
    {
        var rect = SlotButtonRect(button);
        bool spin = button.Id is "slot-spin" or "slot-refill";
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 6, rect.Width, rect.Height), 18, 18, ThemeColor(0, 0, 0, 140));
        Color top = !button.Enabled ? ThemeColor(150, 138, 124) : spin ? ThemeColor(255, 228, 150) : ThemeColor(250, 240, 218);
        Color bottom = !button.Enabled ? ThemeColor(118, 106, 96) : spin ? ThemeColor(240, 170, 80) : ThemeColor(222, 204, 164);
        using var finish = new CanvasLinearGradientBrush(ds.Device, top, bottom)
        { StartPoint = new(0, (float)rect.Y), EndPoint = new(0, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, 18, 18, finish);
        ds.DrawRoundedRectangle(rect, 18, 18, button.Enabled ? SlotDeepGold : ThemeColor(90, 80, 70), 2);
        ds.DrawLine((float)rect.X + 20, (float)rect.Y + 3, (float)rect.Right - 20, (float)rect.Y + 3, ThemeColor(255, 255, 255, 170), 1.2f);
        var text = SlotButtonTextSpec(button);
        SlotText(ds, text.Caption, text.Bounds, text.Size, button.Enabled ? ThemeColor(52, 28, 12) : ThemeColor(70, 62, 56), aspect);
        if (button.Id == "slot-buy")
            SlotText(ds, SlotGame.Format(game.BuyCost), new Rect(rect.X + 8, rect.Bottom - 34, rect.Width - 16, 24), 16,
                button.Enabled ? ThemeColor(110, 60, 20) : ThemeColor(80, 70, 62), aspect);
    }

    private static Rect SlotButtonRect(BoardButton button) => new(button.Bounds.X * BoardSurfaceSize,
        button.Bounds.Y * BoardSurfaceSize, button.Bounds.Width * BoardSurfaceSize, button.Bounds.Height * BoardSurfaceSize);

    private static (string Caption, Rect Bounds, float Size) SlotButtonTextSpec(BoardButton button)
    {
        var rect = SlotButtonRect(button);
        string caption = button.Id switch
        {
            "slot-exit" => "EXIT", "slot-bet-down" => "BET −", "slot-bet-up" => "BET +",
            "slot-buy" => "BUY", "slot-refill" => "REFILL", _ => "SPIN"
        };
        float size = button.Id == "slot-spin" ? 46 : 34;
        double lift = button.Id == "slot-buy" ? 12 : 0;
        return (caption, new Rect(rect.X + 8, rect.Y + 4 - lift, rect.Width - 16, rect.Height - 12), size);
    }

    /// <summary>The camera trigger region: the caption's ink, narrowed exactly as it is drawn.</summary>
    private HandTrackingBounds SlotButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        var spec = SlotButtonTextSpec(button);
        float aspect = SlotsLayout(PaintBoardAspect()).Aspect;
        using var format = FitSlotTextFormat(device, spec.Caption, spec.Bounds, spec.Size, aspect, "Bahnschrift", true);
        using var layout = new CanvasTextLayout(device, spec.Caption, format, (float)spec.Bounds.Width * aspect,
            (float)spec.Bounds.Height);
        var ink = layout.DrawBounds;
        double centerX = spec.Bounds.X + spec.Bounds.Width / 2;
        double left = centerX + (ink.X - spec.Bounds.Width * aspect / 2) / aspect;
        return ButtonInkRegion(button, new Rect(left - spec.Bounds.X, ink.Y, ink.Width / aspect, ink.Height),
            spec.Bounds.X, spec.Bounds.Y);
    }

    /// <summary>Draws the reel frame's controls on black: the camera reference for the hold detector.</summary>
    private void DrawSlotControlsReference(CanvasDrawingSession ds)
    {
        ds.Clear(Microsoft.UI.Colors.Black);
        var game = _boardSession.SlotsState;
        foreach (var button in _boardSession.Buttons) DrawSlotButton(ds, button, game, SlotsLayout(PaintBoardAspect()).Aspect);
    }

    /// <summary>Text drawn physically unstretched: the square layout is widened by the board aspect.</summary>
    private static void SlotText(CanvasDrawingSession ds, string text, Rect rect, float size, Color color, float aspect,
        string family = "Bahnschrift", bool bold = true, bool fire = false)
    {
        var center = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(1 / aspect, 1, center) * previous;
        try
        {
            using var format = FitSlotTextFormat(ds.Device, text, rect, size, aspect, family, bold);
            var wide = new Rect(center.X - rect.Width * aspect / 2, rect.Y, rect.Width * aspect, rect.Height);
            if (!fire)
            {
                ds.DrawText(text, wide, color, format);
                return;
            }
            using var layout = new CanvasTextLayout(ds.Device, text, format, (float)wide.Width, (float)wide.Height);
            using var glyphs = CanvasGeometry.CreateText(layout).Transform(Matrix3x2.CreateTranslation((float)wide.X, (float)wide.Y));
            using var shadow = glyphs.Transform(Matrix3x2.CreateTranslation(2, 3));
            ds.FillGeometry(shadow, ThemeColor(0, 0, 0, 170));
            var ink = glyphs.ComputeBounds();
            using var flame = new CanvasLinearGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = ThemeColor(255, 252, 214) },
                new() { Position = .45f, Color = color },
                new() { Position = 1, Color = ThemeColor(214, 70, 18) }
            ]) { StartPoint = new(0, (float)ink.Y), EndPoint = new(0, (float)ink.Bottom) };
            ds.FillGeometry(glyphs, flame);
            ds.DrawGeometry(glyphs, ThemeColor(90, 24, 6, 220), Math.Max(1, size / 30));
        }
        finally { ds.Transform = previous; }
    }

    private static CanvasTextFormat SlotTextFormat(float size, string family, bool bold) => new()
    {
        FontFamily = family, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    // Fit to physical width on narrow boards. The caption detector calls this
    // same function, so its ink mask remains identical to the visible lettering.
    private static CanvasTextFormat FitSlotTextFormat(CanvasDevice device, string text, Rect rect, float size,
        float aspect, string family, bool bold)
    {
        var format = SlotTextFormat(size, family, bold);
        float width = (float)rect.Width * aspect;
        if (text.Length * size <= width && size <= rect.Height) return format;
        using var measured = new CanvasTextLayout(device, text, format, Math.Max(1, width), Math.Max(1, (float)rect.Height));
        var ink = measured.DrawBounds;
        double fit = Math.Min(1, Math.Min(width * .94 / Math.Max(1, ink.Width), rect.Height * .96 / Math.Max(1, ink.Height)));
        if (fit < 1) format.FontSize = size * (float)fit;
        return format;
    }

    /// <summary>Places a cached symbol in a physically square box.</summary>
    private void DrawSlotArt(CanvasDrawingSession ds, SlotSymbol symbol, Rect box, float opacity)
    {
        ds.DrawImage(SlotSprite(ds.Device, symbol, false), box,
            new Rect(0, 0, SlotSpritePixels, SlotSpritePixels), opacity);
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
