using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly (SlotEffect Power, SlotSymbol Egg, Color Color)[] SlotDragonPowers =
    [
        (SlotEffect.Expand, SlotSymbol.EggGreen, ThemeColor(140, 240, 120)),
        (SlotEffect.Collect, SlotSymbol.EggRed, ThemeColor(255, 110, 72)),
        (SlotEffect.Boost, SlotSymbol.EggBlue, ThemeColor(130, 200, 255))
    ];

    // Physical spacing keeps wings apart even on tall boards. The extra room
    // above the reels is shared by the dormant egg and its emerged dragon.
    private static Vector2 SlotDragonCenter(int index, float aspect) => new(500 + (index - 1) * 150 / aspect, 184);

    private void DrawSlotDragons(CanvasDrawingSession ds, SlotSnapshot game, DateTimeOffset now, SlotLayout layout)
    {
        EnsureSlotArtwork(ds.Device);
        double phaseTime = Math.Max(0, (now - game.PhaseStartedAt).TotalSeconds);
        var visible = new List<(SlotCell Cell, SlotPosition Position)>();
        for (int reel = 0; reel < SlotGame.Reels; reel++)
            for (int row = game.FirstRow; row < game.FirstRow + game.RowCount; row++)
            {
                var position = new SlotPosition(reel, row);
                // The game chooses a result at spin start. Do not reveal its
                // contents in a header meter before the reel actually lands.
                if (game.Phase == SlotPhase.Spinning && phaseTime < SlotGame.ReelStop(reel, game.FreeSpin).TotalSeconds + .12)
                    continue;
                if (game.Phase == SlotPhase.Respinning && phaseTime < SlotGame.RespinLanding.TotalSeconds + reel * .06
                    && game.FreshCells.Contains(position)) continue;
                visible.Add((game.Cell(reel, row), position));
            }
        int coins = Math.Min(3, visible.Count(item => item.Cell.Symbol == SlotSymbol.Coin));
        if (game.Phase == SlotPhase.RespinIntro)
            DrawSlotHatchCharge(ds, visible, phaseTime, layout);

        using var clip = CanvasGeometry.CreateRectangle(ds.Device, new Rect(0, 134, 1000, 114));
        using var layer = ds.CreateLayer(1, clip);
        for (int index = 0; index < SlotDragonPowers.Length; index++)
        {
            var (power, egg, color) = SlotDragonPowers[index];
            var center = SlotDragonCenter(index, layout.Aspect);
            var hatch = game.DragonHatches.FirstOrDefault(item => item.Power == power && item.HatchedAt <= now);
            bool eggPresent = visible.Any(item => item.Cell.Symbol == egg || item.Cell.Symbol == SlotSymbol.EggRainbow);
            bool ready = game.InRespins && eggPresent;
            float age = hatch is null ? -1 : (float)(now - hatch.HatchedAt).TotalSeconds;
            bool acting = game.Phase == SlotPhase.RespinEffect && game.Effect == power;
            float emergence = age >= .3f ? (float)Ease(Math.Clamp((age - .3f) / .8f, 0, 1)) : 0;
            float pulse = .5f + .5f * MathF.Sin(_slotVfxTime * 3.5f + index * 2);
            float strength = hatch is not null ? .9f : ready ? .84f + .12f * pulse : .68f + coins * .045f;
            var seatedCenter = center + new Vector2(0, 11);
            DrawSlotEggSurroundings(ds, Vector2.Lerp(seatedCenter, center, emergence),
                layout.Aspect, color, index, strength);
            DrawSlotHeaderCoinPile(ds, center, layout.Aspect, index);
            DrawSlotNestContact(ds, center, layout.Aspect);

            if (age < .65f)
            {
                float crack = age >= 0 ? .6f + .4f * Math.Clamp(age / .35f, 0, 1)
                    : ready ? Math.Clamp((float)phaseTime / 2, 0, .55f) : 0;
                float shake = ready || age >= 0 ? MathF.Sin(_slotVfxTime * 51 + index) * (age >= 0 ? 2.4f : .9f) : 0;
                float alpha = age < 0 ? 1 : 1 - Math.Clamp((age - .3f) / .35f, 0, 1);
                var eggCenter = seatedCenter + new Vector2(shake / layout.Aspect, -.7f * pulse);
                DrawSlotArt(ds, egg, layout.Square(eggCenter, 76), alpha);
                if (crack > 0) DrawSlotEggCracks(ds, eggCenter, layout.Aspect, color, crack * alpha);
            }

            if (age >= .3f)
            {
                float roar = acting ? (float)(Ease(Math.Clamp((phaseTime - .43) / .13, 0, 1))
                    * (1 - Ease(Math.Clamp((phaseTime - 1.08) / .16, 0, 1)))) : 0;
                DrawSlotDragonPortrait(ds, index, center, layout.Aspect, emergence, roar);
                DrawSlotDragonMotes(ds, center, layout.Aspect, color, index, acting ? 1 : .4f);
                if (acting && power == SlotEffect.Collect)
                {
                    // A short, live flame plume accompanies the red dragon's
                    // roar. The shader animates continuously, not as a glow.
                    var transform = ds.Transform;
                    const float portraitScale = 96f / 113;
                    var mouth = new Vector2(center.X + 20 * portraitScale / layout.Aspect,
                        234 - (214 - 133) * portraitScale + (1 - emergence) * 34);
                    ds.Transform = Matrix3x2.CreateRotation(1.0f) * Matrix3x2.CreateScale(1 / layout.Aspect, 1)
                        * Matrix3x2.CreateTranslation(mouth) * transform;
                    try { DrawSlotFire(ds, new Rect(-10 * portraitScale, -25 * portraitScale,
                        20 * portraitScale, 36 * portraitScale), _slotVfxTime * 1.3f, 8.6f, roar * emergence * .9f); }
                    finally { ds.Transform = transform; }
                }
            }
            DrawSlotHeaderCoinFront(ds, center, layout.Aspect, index);
            if (age is >= .3f and < 1.4f)
                DrawSlotHatchBurst(ds, seatedCenter, layout.Aspect, color, index, age - .3f);

            // Three fire-coin jewels show the existing qualifying threshold.
            // These are per-spin progress, never an invented persistent meter.
            if (hatch is null)
                for (int pip = 0; pip < 3; pip++)
                {
                    var point = center + new Vector2((pip - 1) * 12 / layout.Aspect, 55);
                    ds.FillEllipse(point, 3.1f / layout.Aspect, 3.1f, ThemeColor(13, 10, 9, 235));
                    ds.DrawEllipse(point, 3.1f / layout.Aspect, 3.1f, ThemeColor(227, 181, 91), .7f);
                    if (pip < coins)
                        ds.FillEllipse(point, 2.1f / layout.Aspect, 2.1f, eggPresent ? color : SlotGold);
                }
        }
    }

    private static void DrawSlotHatchCharge(CanvasDrawingSession ds,
        List<(SlotCell Cell, SlotPosition Position)> visible, double time, SlotLayout layout)
    {
        var coins = visible.Where(item => item.Cell.Symbol == SlotSymbol.Coin).Take(3).ToArray();
        for (int index = 0; index < SlotDragonPowers.Length; index++)
        {
            var (_, egg, color) = SlotDragonPowers[index];
            if (!visible.Any(item => item.Cell.Symbol == egg || item.Cell.Symbol == SlotSymbol.EggRainbow)) continue;
            var target = SlotDragonCenter(index, layout.Aspect);
            for (int coin = 0; coin < coins.Length; coin++)
            {
                float progress = (float)((time - .12 - coin * .24 - index * .08) / .85);
                if (progress is < 0 or > 1.3f) continue;
                var cell = coins[coin].Position;
                var start = Center(layout.Cell(cell.Reel, cell.Row, SlotGame.BaseFirstRow, SlotGame.BaseRowCount));
                for (int tail = 0; tail < 12; tail++)
                {
                    float u = progress - tail * .018f;
                    if (u is < 0 or > 1) continue;
                    var p0 = SlotStreamPoint(start, target, u, (index - 1) * 27, layout.Aspect);
                    var p1 = SlotStreamPoint(start, target, Math.Max(0, u - .026f), (index - 1) * 27, layout.Aspect);
                    float alpha = (1 - tail / 12f) * MathF.Sin(u * MathF.PI);
                    ds.DrawLine(p0, p1, WithAlpha(color, (byte)(alpha * 60)), 7);
                    ds.DrawLine(p0, p1, WithAlpha(color, (byte)(alpha * 240)), 2.1f);
                    ds.DrawLine(p0, p1, WithAlpha(SlotIvory, (byte)(alpha * 230)), .75f);
                }
            }
        }
    }

    private static void DrawSlotEggCracks(CanvasDrawingSession ds, Vector2 center, float aspect, Color color, float opacity)
    {
        Vector2[] points = [new(0, -28), new(-5, -18), new(3, -11), new(-4, -2), new(6, 6), new(1, 16), new(7, 29)];
        for (int step = 1; step < points.Length; step++)
        {
            var from = center + new Vector2(points[step - 1].X / aspect, points[step - 1].Y);
            var to = center + new Vector2(points[step].X / aspect, points[step].Y);
            ds.DrawLine(from, to, WithAlpha(color, (byte)(opacity * 65)), 5);
            ds.DrawLine(from, to, WithAlpha(SlotIvory, (byte)(opacity * 245)), .9f);
            if (step is 2 or 4)
            {
                var fork = to + new Vector2((step == 2 ? -13 : 14) / aspect, -7);
                ds.DrawLine(to, fork, WithAlpha(color, (byte)(opacity * 170)), 1.1f);
            }
        }
    }

    private void DrawSlotDragonPortrait(CanvasDrawingSession ds, int index, Vector2 center, float aspect, float emergence, float roar)
    {
        float breathing = MathF.Sin(_slotVfxTime * 2.4f + index * 2.1f);
        float height = 96 * (1 + .013f * breathing);
        float width = 119 * (1 - .004f * breathing);
        float bottom = 234 + (1 - emergence) * 34;
        var pivot = new Vector2(center.X, bottom - 7);
        var transform = ds.Transform;
        // Rotate in physical square coordinates so the slight nod is natural
        // at either portrait or landscape projector proportions.
        ds.Transform = Matrix3x2.CreateScale(aspect, 1, pivot)
            * Matrix3x2.CreateRotation(.008f * MathF.Sin(_slotVfxTime * 1.2f + index), pivot)
            * Matrix3x2.CreateScale(1 / aspect, 1, pivot) * transform;
        try
        {
            var box = new Rect(center.X - width / aspect / 2, bottom - height, width / aspect, height);
            if (!DrawSlotDragonArt(ds, index, box, aspect, emergence, roar))
            {
                // Device/file failures remain playable, visibly falling back
                // to the existing dragon emblem instead of a missing image.
                DrawSlotArt(ds, SlotSymbol.Wild, new Rect(center.X - 37 / aspect, bottom - 76, 74 / aspect, 74), emergence);
            }
        }
        finally { ds.Transform = transform; }
    }

    private bool DrawSlotDragonArt(CanvasDrawingSession ds, int index, Rect box, float aspect, float opacity, float roar)
    {
        if (_slotDragonArtwork is null) return false;
        // Two authored poses share a framing rectangle; the second opens its
        // mouth and wings for the power activation. No random render state.
        double cellWidth = _slotDragonArtwork.Size.Width / 3;
        double cellHeight = _slotDragonArtwork.Size.Height / 2;
        if (opacity <= 0) return true;
        if (roar <= .001f || roar >= .999f)
        {
            DrawPose(ds, roar <= .001f ? 0 : 1, opacity);
            return true;
        }

        // Ordinary source-over draws at complementary opacity leave an opaque
        // torso only 75% opaque halfway through the blend. CrossFade interpolates
        // the two premultiplied images first, then emergence fades that result.
        // Keep this temporary effect graph only for the brief mixed-pose frames.
        using var resting = new CanvasCommandList(ds.Device);
        using (var drawing = resting.CreateDrawingSession()) DrawPose(drawing, 0, 1);
        using var roaring = new CanvasCommandList(ds.Device);
        using (var drawing = roaring.CreateDrawingSession()) DrawPose(drawing, 1, 1);
        using var blend = new CrossFadeEffect { Source1 = resting, Source2 = roaring, CrossFade = roar };
        using (ds.CreateLayer(opacity)) ds.DrawImage(blend);
        return true;

        void DrawPose(CanvasDrawingSession drawing, int pose, float alpha)
        {
            var source = new Rect(index * cellWidth, pose * cellHeight, cellWidth, cellHeight);
            double fit = Math.Min(box.Width * aspect / source.Width, box.Height / source.Height);
            double width = source.Width * fit / aspect, height = source.Height * fit;
            // Roaring shells sit 22 source pixels higher in their cells.
            // Align the painted shell bases, retaining a single scale.
            double baselineOffset = pose == 1 ? 22 * fit : 0;
            drawing.DrawImage(_slotDragonArtwork,
                new Rect(box.X + (box.Width - width) / 2, box.Bottom - height + baselineOffset, width, height), source,
                alpha, CanvasImageInterpolation.HighQualityCubic);
        }
    }

    private void DrawSlotDragonMotes(CanvasDrawingSession ds, Vector2 center, float aspect, Color color, int seed, float strength)
    {
        for (int index = 0; index < 10; index++)
        {
            float phase = (_slotVfxTime * (.22f + SlotRandom(index + 9) * .12f) + SlotRandom(index + seed * 27)) % 1;
            float x = (SlotRandom(index * 17 + seed) - .5f) * 99 + MathF.Sin(phase * 7 + index) * 6;
            float y = 235 - phase * 95;
            var point = new Vector2(center.X + x / aspect, y);
            float alpha = strength * MathF.Sin(phase * MathF.PI);
            ds.DrawLine(point, point + new Vector2(.8f / aspect, 3.1f), WithAlpha(color, (byte)(alpha * 220)), 1.2f);
        }
    }

    private static void DrawSlotHatchBurst(CanvasDrawingSession ds, Vector2 center, float aspect, Color color, int seed, float time)
    {
        float fade = 1 - Math.Clamp(time / 1.1f, 0, 1);
        float radius = 22 + time * 61;
        ds.DrawEllipse(center, radius / aspect, radius * .74f, WithAlpha(color, (byte)(fade * fade * 185)), 1.4f);
        for (int index = 0; index < 18; index++)
        {
            float random = SlotRandom(index * 23 + seed * 191);
            float angle = -MathF.PI + random * MathF.PI;
            float speed = 25 + SlotRandom(index * 7 + 3) * 53;
            var point = center + new Vector2(MathF.Cos(angle) * (22 + time * speed) / aspect,
                -8 + MathF.Sin(angle) * time * speed + time * time * 51);
            if (index < 8)
            {
                float spin = time * (4 + random * 9), size = (2 + random * 3.5f) * fade;
                Vector2 Offset(float angleOffset, float length) => new(MathF.Cos(spin + angleOffset) * length / aspect,
                    MathF.Sin(spin + angleOffset) * length);
                using var shard = CanvasGeometry.CreatePolygon(ds.Device,
                    [point + Offset(0, size), point + Offset(2, size * .8f), point + Offset(4.5f, size * 1.2f)]);
                ds.FillGeometry(shard, WithAlpha(color, (byte)(fade * 230)));
                ds.DrawGeometry(shard, WithAlpha(SlotGold, (byte)(fade * 245)), .7f);
            }
            else
                ds.DrawLine(point, point + new Vector2(MathF.Cos(angle) * 5 / aspect, MathF.Sin(angle) * 5),
                    WithAlpha(SlotIvory, (byte)(fade * 210)), .9f);
        }
    }
}
