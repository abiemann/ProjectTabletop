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
        DrawSlotsCave(ds, boardAspect);

        DrawSlotMarquee(ds, a);

        // Jackpot headers share the colors and centers of the three dragons.
        // Eggs, hatched portraits and their hoards belong to the live layer.
        DrawSlotJackpots(ds, game, layout);
        DrawSlotReelFrame(ds, game, layout);
        DrawSlotKeys(ds, game, layout);

        DrawSlotStatusRail(ds);
        DrawSlotPlaque(ds, new Rect(60, 740, 270, 92), "CREDITS", SlotGame.Format(game.Balance), a);
        DrawSlotPlaque(ds, new Rect(365, 740, 270, 92), "BET", SlotGame.Format(game.Bet), a);
        DrawSlotPlaque(ds, new Rect(670, 740, 270, 92), game.InFreeSpins ? "FREE SPINS WIN" : "WIN",
            SlotGame.Format(game.InFreeSpins ? game.FreeSpinsWin : game.RoundWin), a, highlight: game.RoundWin > 0);

        foreach (var button in buttons) DrawSlotButton(ds, button, game, a);
    }

    private const double SlotWinMessageDuration = .65;
    private static readonly Rect SlotWinMessageBounds = new(60, 704, 880, 28);

    private static void DrawSlotWinMessage(CanvasDrawingSession ds, SlotSnapshot game,
        DateTimeOffset now, float aspect)
    {
        // RoundWin changes only when an award is actually credited. It retains
        // the paid round's total through free spins and features, and resets
        // on the next paid SPIN/BUY. Precomputed spinning results stay hidden.
        if (game.RoundWin <= 0) return;
        double slide = 0;
        if (game.Phase is SlotPhase.LineWins or SlotPhase.RespinOutro or SlotPhase.VaultOutro)
        {
            double fraction = Math.Clamp((now - game.PhaseStartedAt).TotalSeconds / SlotWinMessageDuration, 0, 1);
            // Smooth acceleration and arrival; phase timestamps make redraws
            // independent of frame order and preserve the entrance on resize.
            slide = SlotWinMessageBounds.Height * (1 - fraction * fraction * (3 - 2 * fraction));
        }
        var text = new Rect(SlotWinMessageBounds.X, SlotWinMessageBounds.Y + slide,
            SlotWinMessageBounds.Width, SlotWinMessageBounds.Height);
        using var clip = CanvasGeometry.CreateRectangle(ds.Device, SlotWinMessageBounds);
        using var layer = ds.CreateLayer(1, clip);
        SlotText(ds, $"YOU WON {SlotGame.Format(game.RoundWin)} CREDITS", text, 19, SlotIvory, aspect);
    }

    private void DrawSlotsCave(CanvasDrawingSession ds, double boardAspect)
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
        DrawSlotCabinetEdge(ds, boardAspect);
    }

    private void DrawSlotJackpots(CanvasDrawingSession ds, SlotSnapshot game, SlotLayout layout)
    {
        // Presentation order follows green, red, blue. MINI remains a game
        // award; it simply has no cabinet meter beside these three guardians.
        SlotJackpot[] tiers = [SlotJackpot.Major, SlotJackpot.Grand, SlotJackpot.Minor];
        bool vault = game.Phase is SlotPhase.VaultIntro or SlotPhase.VaultPicking or SlotPhase.VaultOutro;
        for (int index = 0; index < tiers.Length; index++)
        {
            var tier = tiers[index];
            var center = SlotDragonCenter(index, layout.Aspect);
            var rect = new Rect(center.X - 70 / layout.Aspect, 96, 140 / layout.Aspect, 37);
            var (light, dark) = SlotJackpotColors(tier);
            bool won = vault && game.VaultAward == tier;
            DrawSlotInlaidPanel(ds, rect, won ? SlotIvory : light, dark);
            float inset = 13 / layout.Aspect;
            SlotText(ds, SlotGame.JackpotName(tier).ToUpperInvariant(),
                new Rect(rect.X + inset, 97, rect.Width - inset * 2, 13), 12, light, layout.Aspect);
            SlotText(ds, SlotGame.Format(game.Jackpots[tier]),
                new Rect(rect.X + inset, 109, rect.Width - inset * 2, 21), 24,
                won ? SlotIvory : ThemeColor(255, 239, 200), layout.Aspect, "Georgia");
            if (vault)
            {
                int found = game.VaultChests.Count(chest => chest?.Gem == tier);
                for (int dot = 0; dot < 3; dot++)
                {
                    var point = new Vector2((float)rect.X + 8 / layout.Aspect, 104 + dot * 10);
                    if (dot < found)
                    {
                        var previous = ds.Transform;
                        ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1, point) * previous;
                        try { DrawSlotGem(ds, point, 3.2f, tier); }
                        finally { ds.Transform = previous; }
                    }
                    else ds.DrawEllipse(point, 2.4f / layout.Aspect, 2.4f, WithAlpha(light, 120), .65f);
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
            bool lit = game.Keys[reel];
            var box = SlotKeyChestBox(layout, reel);
            if (!DrawSlotKeyChestArtwork(ds, box, open: lit, layout.Aspect))
            {
                var center = new Vector2(layout.ReelCenter(reel), (float)(box.Y + box.Height / 2));
                DrawSlotKeySocket(ds, layout.Square(center, 50), layout.Aspect, lit);
            }
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
        float radius = BoardButtonCornerRadius(button);
        bool spin = button.Id is "slot-spin" or "slot-refill";
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 6, rect.Width, rect.Height), radius, radius, ThemeColor(0, 0, 0, 140));
        Color top = !button.Enabled ? ThemeColor(150, 138, 124) : spin ? ThemeColor(255, 228, 150) : ThemeColor(250, 240, 218);
        Color bottom = !button.Enabled ? ThemeColor(118, 106, 96) : spin ? ThemeColor(240, 170, 80) : ThemeColor(222, 204, 164);
        using var finish = new CanvasLinearGradientBrush(ds.Device, top, bottom)
        { StartPoint = new(0, (float)rect.Y), EndPoint = new(0, (float)rect.Bottom) };
        ds.FillRoundedRectangle(rect, radius, radius, finish);
        ds.DrawRoundedRectangle(rect, radius, radius, button.Enabled ? SlotDeepGold : ThemeColor(90, 80, 70), 2);
        ds.DrawLine((float)rect.X + 20, (float)rect.Y + 3, (float)rect.Right - 20, (float)rect.Y + 3, ThemeColor(255, 255, 255, 170), 1.2f);
        var text = SlotButtonTextSpec(button);
        SlotText(ds, text.Caption, text.Bounds, text.Size, button.Enabled ? ThemeColor(52, 28, 12) : ThemeColor(70, 62, 56), aspect);
        if (button.Id == "slot-buy")
            SlotText(ds, SlotGame.Format(game.BuyCost), new Rect(text.Bounds.X,
                text.Bounds.Y + text.Bounds.Height / 2 + 16, text.Bounds.Width, 24), 16,
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
        double topInset = button.Id == "slot-buy" ? 6 : 4;
        return (caption, new Rect(rect.X + 8, rect.Y + topInset, rect.Width - 16, rect.Height - 12), size);
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
