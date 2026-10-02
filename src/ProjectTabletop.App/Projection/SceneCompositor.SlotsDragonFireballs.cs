using System.Numerics;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.App.Projection.SlotsRendering;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelShaderEffect<SlotDragonFireballShader>? _slotDragonFireball;
    private CanvasDevice? _slotDragonFireballDevice;

    private void DrawSlotDragonFireballs(CanvasDrawingSession ds, SlotSnapshot game, DateTimeOffset now, SlotLayout layout)
    {
        // This is the presentation of a power already applied by SlotGame.
        // A retained baby never invents a new trigger, target, roll or award.
        if (game.Phase != SlotPhase.RespinEffect || !game.InRespins || game.EffectCell is not { } cell
            || cell.Reel is < 0 or >= SlotGame.Reels || cell.Row is < 0 or >= SlotGame.Rows
            || !game.IsActiveRow(cell.Row)) return;
        int index = Array.FindIndex(SlotDragonPowers, item => item.Power == game.Effect);
        if (index < 0) return;
        var hatch = game.DragonHatches.FirstOrDefault(item => item.Power == game.Effect);
        if (hatch is null || hatch.HatchedAt > now) return;
        var symbol = game.Cell(cell.Reel, cell.Row).Symbol;
        if (symbol != SlotDragonPowers[index].Egg && symbol != SlotSymbol.EggRainbow) return;

        float time = (float)(now - game.PhaseStartedAt).TotalSeconds;
        bool firstHatch = hatch.HatchedAt == game.PhaseStartedAt;
        // Let a new baby's face emerge before it spits. Later activations use
        // its settled portrait, with no new cracks or hatching sequence.
        float launch = firstHatch ? .82f : .52f;
        float flight = firstHatch ? .30f : .50f;
        float impact = launch + flight;
        if (time < launch - .18f || time >= impact + .28f) return;

        using var clip = CanvasGeometry.CreateRectangle(ds.Device, new Rect(0, 134, 1000, layout.Bottom - 134));
        using var layer = ds.CreateLayer(1, clip);
        var transform = ds.Transform;
        // All geometry and flame material use square physical coordinates,
        // including the rotation of the wake and the burst's flying sparks.
        ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1) * transform;
        try
        {
            var center = SlotDragonCenter(index, layout.Aspect);
            float ageAtLaunch = (float)(game.PhaseStartedAt - hatch.HatchedAt).TotalSeconds + launch;
            float emergence = (float)Ease(Math.Clamp((ageAtLaunch - .3f) / .8f, 0, 1));
            var start = SlotDragonFireballMouth(index, center, layout.Aspect, emergence,
                _slotVfxTime - (time - launch));
            float rowCount = game.RowCount, firstRow = game.FirstRow;
            if (game.Effect == SlotEffect.Expand)
            {
                float reveal = (float)Ease(Math.Min(1, impact / SlotGame.RespinEffectDuration.TotalSeconds / .6));
                rowCount = 3 + 2 * reveal;
                firstRow = 1 - reveal;
            }
            var target = Center(layout.Cell(cell.Reel, cell.Row, firstRow, rowCount));
            target.X *= layout.Aspect;
            var control = start + new Vector2(45, Math.Max(55, (target.Y - start.Y) * .38f));
            float seed = index * 7.3f + (game.Revision % 997) * .013f;
            if (time < launch)
            {
                float charge = Math.Clamp((time - launch + .18f) / .18f, 0, 1);
                float age = (float)(now - hatch.HatchedAt).TotalSeconds;
                var mouth = SlotDragonFireballMouth(index, center, layout.Aspect,
                    (float)Ease(Math.Clamp((age - .3f) / .8f, 0, 1)), _slotVfxTime);
                DrawSlotDragonFireball(ds, mouth, control - start, .16f + charge * .21f,
                    seed, charge * .85f, 0);
            }
            else if (time < impact)
            {
                float along = (time - launch) / flight;
                var point = (1 - along) * (1 - along) * start
                    + 2 * (1 - along) * along * control + along * along * target;
                var tangent = 2 * ((1 - along) * (control - start) + along * (target - control));
                float trail = Math.Clamp(Vector2.Distance(start, point) / 72, 0, 1);
                DrawSlotDragonFireball(ds, point, tangent, .65f + .23f * MathF.Sin(along * MathF.PI),
                    seed, 1, trail);
            }
            else
            {
                float burst = Math.Clamp((time - impact) / .28f, 0, 1);
                float fade = (1 - burst) * (1 - burst);
                DrawSlotDragonFireball(ds, target, Vector2.UnitY, .7f + (float)Ease(burst) * 1.1f,
                    seed, fade, 0);
                var color = SlotDragonPowers[index].Color;
                for (int spark = 0; spark < 14; spark++)
                {
                    float angle = SlotRandom(spark * 71 + index * 211) * MathF.Tau;
                    var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    float distance = 8 + burst * (18 + SlotRandom(spark * 19 + 37) * 22);
                    var point = target + direction * distance + new Vector2(0, burst * burst * 13);
                    ds.DrawLine(point, point - direction * (2 + (1 - burst) * 5), WithAlpha(color, (byte)(fade * 220)), 1.5f);
                    ds.DrawLine(point, point - direction * 2, WithAlpha(SlotIvory, (byte)(fade * 245)), .65f);
                }
            }
        }
        finally { ds.Transform = transform; }
    }

    private static Vector2 SlotDragonFireballMouth(int index, Vector2 center, float aspect, float emergence, float clock)
    {
        float height = 96 * (1 + .013f * MathF.Sin(clock * 2.4f + index * 2.1f));
        float scale = height / 113;
        float bottom = 234 + (1 - emergence) * 34;
        var mouth = new Vector2(center.X * aspect + 20 * scale, bottom - 81 * scale);
        var pivot = new Vector2(center.X * aspect, bottom - 7);
        return Vector2.Transform(mouth, Matrix3x2.CreateRotation(.008f * MathF.Sin(clock * 1.2f + index), pivot));
    }

    private void DrawSlotDragonFireball(CanvasDrawingSession ds, Vector2 point, Vector2 direction,
        float size, float seed, float opacity, float trail)
    {
        if (_slotDragonFireballDevice != ds.Device)
        {
            DisposeSlotDragonFireballs();
            _slotDragonFireballDevice = ds.Device;
        }
        _slotDragonFireball ??= new PixelShaderEffect<SlotDragonFireballShader>();
        _slotDragonFireball.ConstantBuffer = new SlotDragonFireballShader(_slotVfxTime, seed, opacity, trail);
        var transform = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(size)
            * Matrix3x2.CreateRotation(MathF.Atan2(direction.Y, direction.X) + MathF.PI / 2)
            * Matrix3x2.CreateTranslation(point) * transform;
        try { ds.DrawImage(_slotDragonFireball, new Rect(-24, -24, 48, 96), new Rect(0, 0, 256, 512)); }
        finally { ds.Transform = transform; }
    }

    private void DisposeSlotDragonFireballs()
    {
        _slotDragonFireball?.Dispose();
        _slotDragonFireball = null;
        _slotDragonFireballDevice = null;
    }
}
