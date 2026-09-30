using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Fire and suspended embers keep the reel window alive while idle; the
    // cabinet and camera-observed controls stay in the cached board image.
    private CanvasRenderTarget? _slotsReelTarget;
    private (long Revision, long Frame, double Aspect)? _slotsReelKey;
    private static readonly SlotSymbol[] SlotFillerSymbols =
    [
        SlotSymbol.Ten, SlotSymbol.Jack, SlotSymbol.Queen, SlotSymbol.King, SlotSymbol.Ace, SlotSymbol.Ten,
        SlotSymbol.Jack, SlotSymbol.Queen, SlotSymbol.Dagger, SlotSymbol.Goblet, SlotSymbol.Chest, SlotSymbol.Crown,
        SlotSymbol.Wild, SlotSymbol.Coin
    ];

    private CanvasRenderTarget? DrawSlotsReelLayer(CanvasDevice device, DateTimeOffset now, double aspect)
    {
        if (_boardSession.Screen != BoardScreen.Slots) return null;
        var game = _boardSession.SlotsState;
        if (EnsureBoardRenderTarget(ref _slotsReelTarget, device)) _slotsReelKey = null;
        var key = (game.Revision, GlobeVisualFrame(now), aspect);
        if (_slotsReelKey != key)
        {
            using var drawing = _slotsReelTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_slotsReelTarget);
            drawing.Clear(Colors.Transparent);
            DrawSlotsReels(drawing, game, now, SlotsLayout(aspect));
            _slotsReelKey = key;
        }
        return _slotsReelTarget;
    }

    private void DisposeSlotsLayers()
    {
        _slotsReelTarget?.Dispose();
        _slotsReelTarget = null;
        _slotsReelKey = null;
        _slotVfxEpoch = null;
        DisposeSlotVfx();
        DisposeSlotSprites();
        _slotsPreviewTarget?.Dispose();
        _slotsPreviewTarget = null;
        _slotsPreviewKey = null;
    }

    private void DrawSlotsReels(CanvasDrawingSession ds, SlotSnapshot game, DateTimeOffset now, SlotLayout layout)
    {
        // Elapsed injected time keeps shader coordinates small and reproducible
        // without tying the animation to wall time or resetting at midnight.
        _slotVfxEpoch ??= now;
        _slotVfxTime = (float)Math.Max(0, (now - _slotVfxEpoch.Value).TotalSeconds);
        double t = Math.Max(0, (now - game.PhaseStartedAt).TotalSeconds);
        double progress = game.PhaseDuration > TimeSpan.Zero ? Math.Clamp(t / game.PhaseDuration.TotalSeconds, 0, 1) : 1;
        bool vault = game.Phase is SlotPhase.VaultIntro or SlotPhase.VaultPicking or SlotPhase.VaultOutro;
        // Even an expanding feature or a large win cannot illuminate the
        // control captions used as stationary acquisition references.
        using var effectsClip = CanvasGeometry.CreateRectangle(ds.Device, new Rect(0, 88, 1000, 612));
        using var effectsLayer = ds.CreateLayer(1, effectsClip);
        DrawSlotAmbient(ds, layout, _slotVfxTime);
        if (!game.InRespins && game.Phase != SlotPhase.LineWins) DrawSlotPanelFire(ds, layout);
        using (var clip = CanvasGeometry.CreateRectangle(ds.Device, layout.Window))
        using (ds.CreateLayer(1, clip))
        {
            if (vault) DrawSlotVault(ds, game, t, layout);
            else if (game.InRespins) DrawSlotRespinGrid(ds, game, t, progress, layout);
            else DrawSlotBaseGrid(ds, game, now, t, layout);
            if (!game.InRespins && !vault && game.Phase == SlotPhase.LineWins) DrawSlotLineWins(ds, game, t, layout);
        }
        // Flame tips may spill over the reel frame; the outer effects clip
        // keeps this foreground pass away from camera-observed controls.
        if (!game.InRespins && !vault && _slotWildPortraits is not null && _slotWildColossus is not null)
            foreach (var run in SlotsWildPresentation(game, now)) DrawSlotWildRunFire(ds, run, layout);
        DrawSlotDragons(ds, game, now, layout);
        if (game.Phase == SlotPhase.LineWins) DrawSlotLineWinAward(ds, game, t, layout);
        DrawSlotBanner(ds, game, t, progress, layout);
    }

    private void DrawSlotBaseGrid(CanvasDrawingSession ds, SlotSnapshot game, DateTimeOffset now, double t, SlotLayout layout)
    {
        bool spinning = game.Phase == SlotPhase.Spinning;
        EnsureSlotArtwork(ds.Device);
        IReadOnlyList<SlotWildRun> wilds = _slotWildPortraits is not null && _slotWildColossus is not null
            ? SlotsWildPresentation(game, now) : [];
        for (int reel = 0; reel < SlotGame.Reels; reel++)
        {
            double stop = SlotGame.ReelStop(reel, game.FreeSpin).TotalSeconds;
            using var columnClip = CanvasGeometry.CreateRectangle(ds.Device, layout.Cell(reel, 0, 0, 1));
            using var columnLayer = ds.CreateLayer(1, columnClip);
            if (!spinning || t >= stop + .22)
            {
                for (int row = SlotGame.BaseFirstRow; row < SlotGame.BaseFirstRow + SlotGame.BaseRowCount; row++)
                    if (!wilds.Any(run => run.Reel == reel && row >= run.Row && row < run.Row + run.Count))
                        DrawSlotSettledCell(ds, game, reel, row, t, layout);
                continue;
            }
            // A strip of the previous symbols, fillers, then the result, scrolling down.
            int fillers = 8 + reel * 3;
            double travel = 3 + fillers, position, speed;
            if (t < stop)
            {
                double u = t / stop;
                const double ramp = .08, brake = .62, integral = .77;
                double distance = u < ramp ? u * u / (2 * ramp) : u < brake ? u - ramp / 2
                    : brake - ramp / 2 + (u - brake) - (u - brake) * (u - brake) / (2 * (1 - brake));
                position = travel * distance / integral;
                speed = travel / (integral * stop) * (u < ramp ? u / ramp : u < brake ? 1 : (1 - u) / (1 - brake));
            }
            else
            {
                double bounce = (t - stop) / .22;
                position = travel + (bounce < 1 ? .14 * Math.Sin(Math.PI * bounce) * Math.Exp(-1.8 * bounce) : 0);
                speed = 0;
            }
            bool blurred = speed > 3;
            int first = (int)Math.Floor(position) - 1;
            for (int strip = first; strip <= first + 5; strip++)
            {
                float rowPosition = (float)(2 - (strip - position));
                DrawSlotCell(ds, SlotStripCell(game, reel, strip, fillers), layout.Cell(reel, rowPosition, 0, 3), layout, blurred);
            }
            DrawSlotReelVelocity(ds, layout, reel, t, (float)speed);
            if (t >= stop && t < stop + .35)
            {
                float flash = (float)(1 - (t - stop) / .35);
                var column = layout.Cell(reel, 0, 0, 1);
                ds.FillRectangle(column, ThemeColor(255, 230, 160, (byte)(38 * flash)));
            }
        }
        // Foreground guardians can lean beyond a reel seam. The enclosing
        // window clip still keeps them inside the machine, above every drum.
        foreach (var run in wilds) DrawSlotWildRun(ds, game, run, layout);
    }

    private static SlotCell SlotStripCell(SlotSnapshot game, int reel, int strip, int fillers)
    {
        if (strip is >= 0 and <= 2) return game.PreviousGrid[SlotGame.Index(reel, SlotGame.BaseFirstRow + 2 - strip)];
        int final = strip - 3 - fillers;
        if (final is >= 0 and <= 2) return game.Cell(reel, SlotGame.BaseFirstRow + 2 - final);
        uint hash = (uint)(game.SpinNumber * 7919 + reel * 104729 + strip * 1299709);
        hash ^= hash >> 13;
        hash *= 0x5bd1e995;
        hash ^= hash >> 15;
        var symbol = SlotFillerSymbols[hash % SlotFillerSymbols.Length];
        return symbol == SlotSymbol.Coin ? new(SlotSymbol.Coin, game.Bet * (1 + hash % 5)) : new(symbol);
    }

    private void DrawSlotRespinGrid(CanvasDrawingSession ds, SlotSnapshot game, double t, double progress, SlotLayout layout)
    {
        float firstRow = game.FirstRow, rowCount = game.RowCount;
        bool expanding = game.Phase == SlotPhase.RespinEffect && game.Effect == SlotEffect.Expand;
        if (expanding)
        {
            float k = (float)Ease(Math.Min(1, progress / .6));
            rowCount = 3 + 2 * k;
            firstRow = 1 - k;
        }
        var fresh = game.FreshCells.ToHashSet();
        for (int reel = 0; reel < SlotGame.Reels; reel++)
            for (int row = 0; row < SlotGame.Rows; row++)
            {
                if (row < Math.Floor(firstRow) || row > firstRow + rowCount) continue;
                var rect = layout.Cell(reel, row, firstRow, rowCount);
                if (!game.IsActiveRow(row) && !expanding) continue;
                var cell = game.Cell(reel, row);
                var position = new SlotPosition(reel, row);
                if (game.Phase == SlotPhase.Respinning && (cell.Symbol == SlotSymbol.Empty || fresh.Contains(position)))
                {
                    double landing = SlotGame.RespinLanding.TotalSeconds + reel * .06;
                    if (t < landing)
                    {
                        DrawSlotSpinningCell(ds, rect, layout, game.SpinNumber * 31 + reel * 5 + row, t);
                        continue;
                    }
                    if (fresh.Contains(position))
                    {
                        double pop = (t - landing) / .35;
                        if (pop < 1)
                        {
                            float ring = (float)(pop * rect.Height * .6);
                            var center = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
                            ds.DrawEllipse(center, ring / layout.Aspect, ring, ThemeColor(255, 220, 120, (byte)(220 * (1 - pop))), 4);
                        }
                        DrawSlotHeld(ds, rect, layout, cell, pop < 1 ? 1 + .22f * (float)Math.Sin(Math.PI * pop) : 1, glow: true);
                        continue;
                    }
                }
                if (cell.Symbol == SlotSymbol.Empty)
                {
                    float reveal = expanding && !(row is >= 1 and <= 3) ? (float)Math.Min(1, progress / .6) : 1;
                    DrawSlotUnheldGhost(ds, rect, layout, game.SpinNumber * 31 + reel * 5 + row, .19f * reveal);
                    continue;
                }
                DrawSlotHeld(ds, rect, layout, SlotEffectValue(game, cell, position, progress), 1,
                    glow: game.Phase == SlotPhase.RespinEffect && game.EffectCell == position);
            }
        if (game.Phase == SlotPhase.RespinEffect) DrawSlotEffect(ds, game, progress, layout, firstRow, rowCount);
    }

    // Before a Boost or Collect lands its values, show the earlier ones.
    private static SlotCell SlotEffectValue(SlotSnapshot game, SlotCell cell, SlotPosition position, double progress)
    {
        if (game.Phase != SlotPhase.RespinEffect) return cell;
        if (game.Effect == SlotEffect.Boost && progress < .45 && game.BoostMultiplier > 1 &&
            cell is { Symbol: SlotSymbol.Coin, Jackpot: SlotJackpot.None })
            return cell with { Value = cell.Value / game.BoostMultiplier };
        if (game.Effect == SlotEffect.Collect && progress < .7 && game.EffectCell == position)
            return cell with { Value = cell.Value - game.EffectAmount };
        return cell;
    }

    private void DrawSlotEffect(CanvasDrawingSession ds, SlotSnapshot game, double progress, SlotLayout layout,
        float firstRow, float rowCount)
    {
        if (game.EffectCell is not { } source) return;
        var from = Center(layout.Cell(source.Reel, source.Row, firstRow, rowCount));
        var targets = new List<Vector2>();
        for (int reel = 0; reel < SlotGame.Reels; reel++)
            for (int row = 0; row < SlotGame.Rows; row++)
            {
                var cell = game.Cell(reel, row);
                if (cell.Symbol == SlotSymbol.Empty || source == new SlotPosition(reel, row)) continue;
                if (game.Effect == SlotEffect.Boost && cell is not { Symbol: SlotSymbol.Coin, Jackpot: SlotJackpot.None }) continue;
                targets.Add(Center(layout.Cell(reel, row, firstRow, rowCount)));
            }
        if (game.Effect == SlotEffect.Boost && progress < .6)
        {
            // Narrow white leaders, cobalt corona and short branches change
            // shape in controlled bursts rather than forming a static zigzag.
            int burst = (int)(_slotVfxTime * 18);
            float alpha = (float)(Math.Sin(Math.Min(1, progress / .12) * Math.PI / 2) * (1 - progress / .6));
            for (int index = 0; index < targets.Count; index++)
                DrawSlotLightning(ds, from, targets[index], layout.Aspect,
                    unchecked((int)game.Revision * 37 + index * 197 + burst * 101), alpha);
        }
        if (game.Effect == SlotEffect.Boost && progress >= .45)
            foreach (var target in targets)
                SlotText(ds, $"×{game.BoostMultiplier}", new Rect(target.X - 40, target.Y - 58, 80, 30), 26,
                    ThemeColor(150, 215, 255), layout.Aspect, fire: false);
        if (game.Effect == SlotEffect.Collect && progress is > .08 and < .72)
        {
            float k = (float)((progress - .08) / .64);
            for (int index = 0; index < targets.Count; index++)
                DrawSlotCollectStream(ds, targets[index], from, layout, k,
                    unchecked((int)game.Revision + index * 67));
        }
        if (game.Effect == SlotEffect.Expand)
        {
            float reveal = (float)Ease(Math.Min(1, progress / .6));
            float alpha = (float)(Math.Sin(Math.PI * progress) * .85);
            float center = layout.Top + layout.Height / 2;
            foreach (int direction in new[] { -1, 1 })
            {
                float y = center + direction * layout.Height * (.30f + .20f * reveal);
                var left = new Vector2(layout.Left, y);
                var right = new Vector2(layout.Right, y);
                ds.DrawLine(left, right, ThemeColor(80, 226, 106, (byte)(alpha * 60)), 15);
                ds.DrawLine(left, right, ThemeColor(177, 255, 149, (byte)(alpha * 230)), 2);
                for (int index = 0; index < 19; index++)
                {
                    float x = layout.Left + SlotRandom(index * 79 + direction * 19) * layout.Width;
                    float rise = (float)((progress * 3 + SlotRandom(index * 41)) % 1);
                    var point = new Vector2(x, y - direction * rise * 34);
                    ds.DrawLine(point, point + new Vector2(0, direction * 6),
                        ThemeColor(203, 255, 175, (byte)(alpha * (1 - rise) * 210)), 1.2f);
                }
            }
        }
    }

    private void DrawSlotSpinningCell(CanvasDrawingSession ds, Rect rect, SlotLayout layout, long seed, double t)
    {
        // Unheld positions retain the dim reel silhouettes seen behind prizes.
        double position = t * 9 + (seed % 7) * .37;
        for (int index = 0; index < 2; index++)
        {
            double offset = (position % 1) - index;
            var cellRect = new Rect(rect.X, rect.Y + offset * rect.Height, rect.Width, rect.Height);
            var symbol = SlotFillerSymbols[(int)((seed + (long)position + index) % 10)];
            using var clip = CanvasGeometry.CreateRectangle(ds.Device, rect);
            using (ds.CreateLayer(.25f, clip))
                ds.DrawImage(SlotSprite(ds.Device, symbol, true), layout.SymbolBox(cellRect, .7f),
                    new Rect(0, 0, SlotSpritePixels, SlotSpritePixels));
        }
    }

    private void DrawSlotHeld(CanvasDrawingSession ds, Rect rect, SlotLayout layout, SlotCell cell, float scale, bool glow)
    {
        if (glow)
        {
            var center = Center(rect);
            using var halo = new CanvasRadialGradientBrush(ds.Device, ThemeColor(255, 180, 70, 170), ThemeColor(255, 120, 20, 0))
            { Center = center, RadiusX = (float)rect.Width * .7f, RadiusY = (float)rect.Height * .7f };
            ds.FillRectangle(rect, halo);
        }
        DrawSlotPrizeSocket(ds, rect, layout, glow ? .9f : .32f);
        var box = layout.SymbolBox(rect, .84f * scale);
        DrawSlotSprite(ds, cell, box, layout, false);
    }

    private void DrawSlotCell(CanvasDrawingSession ds, SlotCell cell, Rect rect, SlotLayout layout, bool blurred, float opacity = 1)
    {
        if (cell.Symbol == SlotSymbol.Empty) return;
        DrawSlotSprite(ds, cell, layout.SymbolBox(rect), layout, blurred, opacity);
    }

    private void DrawSlotSprite(CanvasDrawingSession ds, SlotCell cell, Rect box, SlotLayout layout, bool blurred, float opacity = 1)
    {
        bool livingCoin = cell.Symbol == SlotSymbol.Coin && !blurred;
        if (livingCoin)
        {
            float seed = (float)(box.X * .021 + box.Y * .037);
            var fire = new Rect(box.X - box.Width * .10, box.Y - box.Height * .10, box.Width * 1.20, box.Height * 1.20);
            DrawSlotFire(ds, fire, _slotVfxTime, seed, opacity);
        }
        // The painted molten centre supplies material detail, with real,
        // evolving tongues behind it rather than a static fire illustration.
        var artBox = livingCoin ? new Rect(box.X + box.Width * .10, box.Y + box.Height * .20,
            box.Width * .80, box.Height * .80) : box;
        ds.DrawImage(SlotSprite(ds.Device, cell.Symbol, blurred), artBox, new Rect(0, 0, SlotSpritePixels, SlotSpritePixels), opacity);
        if (!blurred) DrawSlotSymbolEnergy(ds, cell.Symbol, artBox, layout, opacity);
        if (blurred || !cell.IsBonus) return;
        string value = cell.Jackpot != SlotJackpot.None ? SlotGame.JackpotName(cell.Jackpot).ToUpperInvariant() : SlotGame.Format(cell.Value);
        bool coin = cell.Symbol == SlotSymbol.Coin;
        float size = (float)box.Height * (coin ? value.Length > 5 ? .24f : .34f : .22f);
        var textRect = coin ? new Rect(box.X - 12 / layout.Aspect, box.Y + box.Height * .44, box.Width + 24 / layout.Aspect, box.Height * .40)
            : new Rect(box.X - 12 / layout.Aspect, box.Y + box.Height * .72, box.Width + 24 / layout.Aspect, box.Height * .26);
        SlotText(ds, value, new Rect(textRect.X + 1.5, textRect.Y + 2, textRect.Width, textRect.Height), size,
            ThemeColor(40, 8, 0, (byte)(230 * opacity)), layout.Aspect);
        SlotText(ds, value, textRect, size, cell.Jackpot != SlotJackpot.None ? SlotJackpotColors(cell.Jackpot).Light : SlotIvory,
            layout.Aspect);
    }

    private void DrawSlotLineWins(CanvasDrawingSession ds, SlotSnapshot game, double t, SlotLayout layout)
    {
        var winning = game.LineWins.SelectMany(win => win.Cells).ToHashSet();
        // Dim the losing symbols so the winning lines read at a glance.
        for (int reel = 0; reel < SlotGame.Reels; reel++)
            for (int row = SlotGame.BaseFirstRow; row < SlotGame.BaseFirstRow + SlotGame.BaseRowCount; row++)
                if (!winning.Contains(new SlotPosition(reel, row)))
                    ds.FillRectangle(layout.Cell(reel, row, 1, 3), ThemeColor(6, 2, 10, 150));
        float pulse = .6f + .4f * (float)Math.Sin(t * 9);
        // Cycle through the lines one at a time when several win.
        int shown = game.LineWins.Count == 0 ? 0 : (int)(t / .45) % game.LineWins.Count;
        for (int index = 0; index < game.LineWins.Count; index++)
        {
            var win = game.LineWins[index];
            bool focus = game.LineWins.Count == 1 || index == shown;
            if (!focus) continue;
            var rows = SlotGame.Paylines[win.Line];
            var points = Enumerable.Range(0, SlotGame.Reels)
                .Select(reel => Center(layout.Cell(reel, SlotGame.BaseFirstRow + rows[reel], 1, 3))).ToArray();
            Color color = LineColor(win.Line);
            for (int reel = 1; reel < points.Length; reel++)
            {
                if (focus)
                    ds.DrawLine(points[reel - 1], points[reel], WithAlpha(color, 45), 12);
                ds.DrawLine(points[reel - 1], points[reel], WithAlpha(color, (byte)(focus ? 230 : 45)), focus ? 2.4f : 1);
                if (focus)
                {
                    float sweep = (float)((t * 1.25 + reel * .17) % 1);
                    var gleam = Vector2.Lerp(points[reel - 1], points[reel], sweep);
                    float size = 3 + 2 * MathF.Sin(sweep * MathF.PI);
                    ds.DrawLine(gleam - new Vector2(size * 2 / layout.Aspect, 0),
                        gleam + new Vector2(size * 2 / layout.Aspect, 0), ThemeColor(255, 249, 206, 220), 1.2f);
                    ds.DrawLine(gleam - new Vector2(0, size), gleam + new Vector2(0, size), ThemeColor(255, 249, 206, 220), 1.2f);
                }
            }
            if (!focus) continue;
            foreach (var cell in win.Cells)
            {
                var rect = layout.Cell(cell.Reel, cell.Row, 1, 3);
                ds.DrawRoundedRectangle(new Rect(rect.X + 4, rect.Y + 4, rect.Width - 8, rect.Height - 8), 10, 10,
                    WithAlpha(SlotGold, (byte)(200 * pulse)), 2);
            }
        }
    }

    private static Color LineColor(int line)
    {
        Color[] palette = [ThemeColor(255, 214, 90), ThemeColor(255, 120, 90), ThemeColor(120, 230, 140),
            ThemeColor(120, 190, 255), ThemeColor(230, 140, 255), ThemeColor(255, 170, 60)];
        return palette[line % palette.Length];
    }

    private void DrawSlotVault(CanvasDrawingSession ds, SlotSnapshot game, double t, SlotLayout layout)
    {
        ds.FillRectangle(layout.Window, ThemeColor(30, 12, 8));
        using var gold = new CanvasRadialGradientBrush(ds.Device, ThemeColor(255, 190, 80, 90), ThemeColor(255, 150, 40, 0))
        { Center = Center(layout.Window), RadiusX = layout.Width * .7f, RadiusY = layout.Height * .7f };
        ds.FillRectangle(layout.Window, gold);
        int newest = game.Phase == SlotPhase.VaultPicking ? game.VaultNewest : -1;
        for (int index = 0; index < SlotGame.VaultChestCount; index++)
        {
            int column = index % 4, row = index / 4;
            var cell = new Rect(layout.Left + column * layout.Width / 4, layout.Top + 20 + row * (layout.Height - 40) / 3,
                layout.Width / 4, (layout.Height - 40) / 3);
            var box = layout.SymbolBox(cell, .9f);
            var chest = game.VaultChests[index];
            bool winning = chest is not null && game.Phase == SlotPhase.VaultOutro && chest.Gem == game.VaultAward;
            if (chest is null)
            {
                DrawSlotArt(ds, SlotSymbol.Chest, box, 1);
                continue;
            }
            float pop = index == newest ? (float)Math.Min(1, t / .3) : 1;
            if (winning || index == newest)
            {
                using var halo = new CanvasRadialGradientBrush(ds.Device, ThemeColor(255, 240, 160, 200), ThemeColor(255, 200, 80, 0))
                { Center = Center(cell), RadiusX = (float)cell.Width * .6f, RadiusY = (float)cell.Height * .6f };
                ds.FillRectangle(cell, halo);
            }
            // Keep the same illustrated, metal-bound chest as the reel art.
            // Treasure emerges on narrow, translucent shafts of golden light.
            var lightOrigin = new Vector2((float)(box.X + box.Width * .5), (float)(box.Y + box.Height * .55));
            using (var beam = new CanvasLinearGradientBrush(ds.Device, ThemeColor(255, 222, 142, (byte)(110 * pop)),
                ThemeColor(255, 209, 112, 0))
            {
                StartPoint = lightOrigin,
                EndPoint = new Vector2(lightOrigin.X, (float)(box.Y - box.Height * .12))
            })
                for (int ray = 0; ray < 5; ray++)
                {
                    float angle = (ray - 2) * .13f + MathF.Sin(_slotVfxTime * .45f + index + ray) * .025f;
                    float height = (float)box.Height * (.56f + SlotRandom(index * 19 + ray) * .20f);
                    var tip = lightOrigin + new Vector2(angle * height / layout.Aspect, -height);
                    using var shaft = CanvasGeometry.CreatePolygon(ds.Device,
                        new[] { lightOrigin, tip - new Vector2(7 / layout.Aspect, 0), tip + new Vector2(7 / layout.Aspect, 0) });
                    ds.FillGeometry(shaft, beam);
                }
            DrawSlotOpenChest(ds, box);
            var gemCenter = new Vector2((float)(box.X + box.Width / 2), (float)(box.Y + box.Height * (.35 - .15 * pop)));
            var gemTransform = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1, gemCenter) * gemTransform;
            try { DrawSlotGem(ds, gemCenter, (float)box.Height * .26f * pop, chest.Gem); }
            finally { ds.Transform = gemTransform; }
        }
    }

    private void DrawSlotBanner(CanvasDrawingSession ds, SlotSnapshot game, double t, double progress, SlotLayout layout)
    {
        if (DrawSlotWinCelebration(ds, game, t, progress, layout)) return;
        string title = string.Empty, detail = string.Empty;
        switch (game.Phase)
        {
            case SlotPhase.RespinIntro: title = "DRAGONFIRE RESPINS"; detail = "3 RESPINS · NEW SYMBOLS RESET"; break;
            case SlotPhase.RespinOutro: title = game.Banner; detail = SlotGame.Format(game.RespinTotal); break;
            case SlotPhase.FreeSpinsIntro: title = game.Banner; detail = "EACH ELIXIR ADDS A SPIN"; break;
            case SlotPhase.FreeSpinsOutro: title = game.Banner; detail = SlotGame.Format(game.FreeSpinsWin); break;
            case SlotPhase.VaultIntro: title = "TREASURE VAULT"; detail = "THREE MATCHING GEMS WIN"; break;
            case SlotPhase.VaultOutro: title = game.Banner; detail = SlotGame.Format(SlotGame.JackpotMultipliers[game.VaultAward] * game.Bet); break;
            case SlotPhase.RespinEffect:
                title = game.Banner;
                break;
        }
        if (title.Length == 0) return;
        bool small = game.Phase == SlotPhase.RespinEffect;
        float appear = (float)Math.Min(1, t / .25);
        float height = small ? 64 : 150;
        float y = small ? layout.Top + 10 : layout.Top + layout.Height / 2 - height / 2;
        var band = new Rect(layout.Left - 30, y, layout.Width + 60, height);
        ds.FillRoundedRectangle(band, 18, 18, ThemeColor(12, 4, 8, (byte)(215 * appear)));
        ds.DrawRoundedRectangle(band, 18, 18, WithAlpha(SlotGold, (byte)(230 * appear)), 2.5f);
        float scale = .8f + .2f * appear;
        SlotText(ds, title, new Rect(band.X, band.Y + (small ? 6 : 16), band.Width, small ? 52 : 64), (small ? 34 : 50) * scale,
            SlotGold, layout.Aspect, "Georgia", true, fire: true);
        if (detail.Length > 0)
            SlotText(ds, detail, new Rect(band.X, band.Y + 84, band.Width, 50), game.Phase is SlotPhase.RespinOutro or
                SlotPhase.FreeSpinsOutro or SlotPhase.VaultOutro ? 44 : 24, SlotIvory, layout.Aspect,
                fire: game.Phase is SlotPhase.RespinOutro or SlotPhase.FreeSpinsOutro or SlotPhase.VaultOutro);
    }

    private static Vector2 Center(Rect rect) => new((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));

    private static double Ease(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return 1 - Math.Pow(1 - value, 3);
    }
}
