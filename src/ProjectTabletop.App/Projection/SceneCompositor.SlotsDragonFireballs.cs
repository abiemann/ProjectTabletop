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
        if (!TrySlotDragonAttack(game, now, layout, out var attack)) return;
        int index = attack.Index;
        float time = attack.PhaseTime, launch = attack.Launch, flight = attack.Flight, impact = attack.Impact;
        if (time < launch - .18f || time >= attack.End) return;
        EnsureSlotArtwork(ds.Device);
        // The fallback portrait has a different mouth. Avoid a disconnected
        // fireball if the independently articulated artwork could not load.
        if (_slotDragonBodyArtwork is null || _slotDragonHeadArtwork is null || _slotDragonSwivelArtwork is null) return;

        using var clip = CanvasGeometry.CreateRectangle(ds.Device, new Rect(0, 134, 1000, layout.Bottom - 134));
        using var layer = ds.CreateLayer(1, clip);
        var transform = ds.Transform;
        // All geometry and flame material use square physical coordinates,
        // including the rotation of the wake and the burst's flying sparks.
        ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1) * transform;
        try
        {
            var launchPose = SlotDragonPoseAt(index, attack.HatchedAt,
                attack.PhaseStartedAt.AddSeconds(launch), _slotVfxTime - (time - launch), layout, attack);
            var start = launchPose.Mouth;
            var target = attack.Target;
            var control = start + launchPose.Forward * Math.Max(55, Vector2.Distance(start, target) * .38f);
            float seed = index * 7.3f + (game.Revision % 997) * .013f;
            if (time < launch)
            {
                float charge = Math.Clamp((time - launch + .18f) / .18f, 0, 1);
                var pose = SlotDragonPoseAt(index, attack.HatchedAt, now, _slotVfxTime, layout, attack);
                DrawSlotDragonFireball(ds, pose.Mouth, pose.Forward, .16f + charge * .21f,
                    seed, charge * .85f, 0, game.Effect);
            }
            else if (time < impact)
            {
                float along = (time - launch) / flight;
                var point = (1 - along) * (1 - along) * start
                    + 2 * (1 - along) * along * control + along * along * target;
                var tangent = 2 * ((1 - along) * (control - start) + along * (target - control));
                float trail = Math.Clamp(Vector2.Distance(start, point) / 72, 0, 1);
                DrawSlotDragonFireball(ds, point, tangent, .65f + .23f * MathF.Sin(along * MathF.PI),
                    seed, 1, trail, game.Effect);
            }
            else
            {
                float burst = Math.Clamp((time - impact) / .28f, 0, 1);
                float fade = (1 - burst) * (1 - burst);
                DrawSlotDragonFireball(ds, target, Vector2.UnitY, .7f + (float)Ease(burst) * 1.1f,
                    seed, fade, 0, game.Effect);
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

    private void DrawSlotDragonFireball(CanvasDrawingSession ds, Vector2 point, Vector2 direction,
        float size, float seed, float opacity, float trail, SlotEffect power)
    {
        if (_slotDragonFireballDevice != ds.Device)
        {
            DisposeSlotDragonFireballs();
            _slotDragonFireballDevice = ds.Device;
        }
        _slotDragonFireball ??= new PixelShaderEffect<SlotDragonFireballShader>();
        _slotDragonFireball.ConstantBuffer = new SlotDragonFireballShader(_slotVfxTime, seed, opacity, trail, (int)power);
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
