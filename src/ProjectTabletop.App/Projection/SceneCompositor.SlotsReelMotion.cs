using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // The caller substitutes live WILD runs where available. Drawing an ordinary
    // cell here also preserves the cached WILD fallback during its landing.
    private void DrawSlotSettledCell(CanvasDrawingSession ds, SlotSnapshot game, int reel, int row,
        double t, SlotLayout layout)
    {
        var cell = game.Cell(reel, row);
        var rect = layout.Cell(reel, row, SlotGame.BaseFirstRow, SlotGame.BaseRowCount);
        bool winning = game.Phase == SlotPhase.LineWins && game.LineWins.Any(line =>
            line.Cells.Any(position => position.Reel == reel && position.Row == row));
        if (!winning)
        {
            DrawSlotCell(ds, cell, rect, layout, false);
            return;
        }

        double elapsed = double.IsFinite(t) ? Math.Max(0, t) : 0;
        float pulse = (float)((.5 + .5 * Math.Sin(elapsed * 6.4 + reel * .17)) * Ease(elapsed / .16));
        float lift = pulse * 3.8f;
        var center = Center(rect) - new Vector2(0, lift);
        using (var glow = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(255, 197, 78, (byte)(26 + pulse * 29)), ThemeColor(255, 162, 35, 0))
        { Center = center, RadiusX = (float)rect.Width * .55f, RadiusY = (float)rect.Height * .55f })
            ds.FillRectangle(rect, glow);
        var raised = new Rect(rect.X, rect.Y - lift, rect.Width, rect.Height);
        var box = layout.SymbolBox(raised, .86f * (1 + pulse * .07f));
        DrawSlotSprite(ds, cell, box, layout, false);

        // A small glint stays near the painted edge, instead of outlining each
        // winning position as a separate tile or obscuring its illustration.
        float gleam = .35f + pulse * .65f;
        var point = new Vector2((float)(box.X + box.Width * .74), (float)(box.Y + box.Height * .20));
        float arm = 2.1f + 2.4f * pulse;
        ds.DrawLine(point - new Vector2(arm * 1.8f / layout.Aspect, 0),
            point + new Vector2(arm * 1.8f / layout.Aspect, 0), ThemeColor(255, 208, 92, (byte)(gleam * 140)), 2.3f);
        ds.DrawLine(point - new Vector2(arm / layout.Aspect, 0),
            point + new Vector2(arm / layout.Aspect, 0), ThemeColor(255, 252, 214, (byte)(gleam * 235)), .7f);
        ds.DrawLine(point - new Vector2(0, arm), point + new Vector2(0, arm),
            ThemeColor(255, 248, 190, (byte)(gleam * 215)), .7f / layout.Aspect);
    }

    // Called inside the reel's column clip. Fixed downward phase avoids streaks
    // reversing as the caller's instantaneous reel velocity decreases.
    private static void DrawSlotReelVelocity(CanvasDrawingSession ds, SlotLayout layout, int reel,
        double t, float speed)
    {
        if (!float.IsFinite(speed) || speed <= 3 || !double.IsFinite(t)) return;
        float strength = Math.Clamp((speed - 3) / 21, 0, 1);
        double elapsed = Math.Max(0, t);
        float left = layout.Left + reel * layout.CellWidth;
        for (int index = 0; index < 7; index++)
        {
            int seed = reel * 503 + index * 79;
            float spread = SlotRandom(seed + 43);
            float length = (13 + SlotRandom(seed + 103) * 38) * (.5f + strength * .5f);
            float phase = (float)((elapsed * (3.7 + spread * 1.4) + SlotRandom(seed + 239)) % 1);
            float x = left + layout.CellWidth * (.11f + spread * .78f);
            float y = layout.Top - length + phase * (layout.Height + length * 2);
            float edge = Math.Clamp(Math.Min(y + length - layout.Top, layout.Bottom - y) / 22, 0, 1);
            byte alpha = (byte)(strength * edge * (20 + SlotRandom(seed + 313) * 28));
            if (alpha == 0) continue;
            ds.DrawLine(x, y, x, y + length, ThemeColor(161, 194, 255, alpha), 1.25f / layout.Aspect);
            ds.DrawLine(x, y + length * .58f, x, y + length,
                ThemeColor(255, 232, 170, (byte)(alpha * 1.25f)), .55f / layout.Aspect);
        }
    }

    private void DrawSlotUnheldGhost(CanvasDrawingSession ds, Rect rect, SlotLayout layout, long seed, float opacity)
    {
        if (!float.IsFinite(opacity) || opacity <= 0) return;
        uint hash = unchecked((uint)seed ^ (uint)((ulong)seed >> 32) * 0x9e3779b9u);
        hash ^= hash >> 16;
        hash *= 0x7feb352du;
        hash ^= hash >> 15;
        // The first ten fillers are ordinary symbols only, so decorative ghosts
        // cannot resemble a newly earned WILD, fire orb, or power egg.
        var symbol = SlotFillerSymbols[hash % 10];
        DrawSlotArt(ds, symbol, layout.SymbolBox(rect, .76f), Math.Min(.28f, opacity));
    }

}
