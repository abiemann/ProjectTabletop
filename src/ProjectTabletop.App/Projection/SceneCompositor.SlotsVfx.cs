using System.Numerics;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.App.Projection.SlotsRendering;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelShaderEffect<SlotFireShader>? _slotFire;
    private CanvasDevice? _slotFireDevice;
    private float _slotVfxTime;
    private DateTimeOffset? _slotVfxEpoch;

    private void DrawSlotFire(CanvasDrawingSession ds, Rect box, float time, float seed, float opacity = 1)
    {
        if (_slotFireDevice != ds.Device)
        {
            DisposeSlotVfx();
            _slotFireDevice = ds.Device;
        }
        _slotFire ??= new PixelShaderEffect<SlotFireShader>();
        _slotFire.ConstantBuffer = new SlotFireShader(time, seed, opacity);
        ds.DrawImage(_slotFire, box, new Rect(0, 0, 256, 256));
    }

    private void DisposeSlotVfx()
    {
        DisposeSlotDragonFireballs();
        DisposeSlotWildFire();
        _slotFire?.Dispose();
        _slotFire = null;
        _slotFireDevice = null;
    }

    private static float SlotRandom(int seed)
    {
        uint value = unchecked((uint)seed * 747796405u + 2891336453u);
        value = ((value >> (int)((value >> 28) + 4)) ^ value) * 277803737u;
        value = (value >> 22) ^ value;
        return (value & 0xffffff) / 16777216f;
    }


    private static void DrawSlotAmbient(CanvasDrawingSession ds, SlotLayout layout, float time)
    {
        // Fine suspended gold sits behind the prizes, inside the blue reel
        // window. Clip the whole grain, halo and glint below the gold frame.
        // A grain picks a new path only at its invisible birth, using the
        // injected presentation clock rather than game randomness.
        var bounds = layout.Window;
        using var clip = CanvasGeometry.CreateRectangle(ds.Device, bounds);
        using (ds.CreateLayer(1, clip))
        {
            // Work in physical coordinates so grains, halos and glints stay
            // round on both the tabletop and a widescreen preview.
            var transform = ds.Transform;
            var physicalTransform = Matrix3x2.CreateScale(1 / layout.Aspect, 1) * transform;
            using var halo = new CanvasRadialGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = ThemeColor(255, 224, 141, 100) },
                new() { Position = .18f, Color = ThemeColor(255, 210, 105, 48) },
                new() { Position = .52f, Color = ThemeColor(255, 191, 71, 12) },
                new() { Position = 1, Color = ThemeColor(255, 191, 71, 0) }
            ]);
            using var glint = CanvasGeometry.CreatePolygon(ds.Device,
            [
                new(0, -1), new(.13f, -.13f), new(.68f, 0), new(.13f, .13f),
                new(0, 1), new(-.13f, .13f), new(-.68f, 0), new(-.13f, -.13f)
            ]);
            try
            {
                ds.Transform = physicalTransform;
                for (int index = 0; index < 120; index++)
                {
                    float lifetime = 18 + SlotRandom(index * 23 + 41) * 22;
                    float age = time / lifetime + SlotRandom(index + 209);
                    int cycle = (int)MathF.Floor(age);
                    float phase = age - cycle;
                    int seed = unchecked(index * 197 + cycle * 1301 + 83);
                    float random = SlotRandom(seed);
                    float drift = 3 + SlotRandom(seed + 19) * 9;
                    float x = (float)bounds.X * layout.Aspect
                        + SlotRandom(seed + 17) * (float)bounds.Width * layout.Aspect
                        + MathF.Sin(phase * 6.3f + random * MathF.Tau) * drift
                        + MathF.Sin(phase * 13.7f + SlotRandom(seed + 37) * MathF.Tau) * drift * .28f;
                    float y = (float)bounds.Bottom - phase * (float)bounds.Height;
                    float birth = Math.Clamp(phase / .075f, 0, 1);
                    float death = Math.Clamp((1 - phase) / .16f, 0, 1);
                    float envelope = birth * birth * (3 - 2 * birth) * death * death * (3 - 2 * death);
                    float shimmer = .62f + .38f * MathF.Sin(time * (1.6f + random * 1.1f) + random * 17);
                    float radius = .28f + MathF.Pow(SlotRandom(seed + 53), 2) * .66f;
                    float opacity = envelope * (.28f + random * .46f) * shimmer;
                    var point = new Vector2(x, y);

                    // Most grains remain pin-fine. Just a few catch the light
                    // with a soft optical bloom and a short pointed glint.
                    float twinkle = index % 6 == 0
                        ? MathF.Pow(Math.Max(0, MathF.Sin(time * (1.35f + random * .8f)
                            + SlotRandom(seed + 71) * MathF.Tau)), 18) * envelope : 0;
                    if (index % 4 == 0 || twinkle > .02f)
                    {
                        float glowRadius = 2.1f + radius * 1.7f + twinkle * 1.8f;
                        halo.Center = point;
                        halo.RadiusX = halo.RadiusY = glowRadius;
                        halo.Opacity = Math.Min(1, opacity * .75f + twinkle * .75f);
                        ds.FillCircle(point, glowRadius, halo);
                    }
                    ds.FillCircle(point, radius,
                        ThemeColor(255, (byte)(207 + random * 35), (byte)(111 + random * 73), (byte)(opacity * 220)));
                    if (twinkle > .025f)
                    {
                        float ray = 1.25f + twinkle * 2.1f;
                        ds.Transform = Matrix3x2.CreateScale(ray)
                            * Matrix3x2.CreateRotation(random * .8f - .4f)
                            * Matrix3x2.CreateTranslation(point) * physicalTransform;
                        ds.FillGeometry(glint, ThemeColor(255, 244, 202, (byte)(twinkle * 200)));
                        ds.Transform = physicalTransform;
                        ds.FillCircle(point, .34f + twinkle * .22f,
                            ThemeColor(255, 255, 236, (byte)(twinkle * 245)));
                    }
                }
            }
            finally { ds.Transform = transform; }
        }
    }

    private void DrawSlotSymbolEnergy(CanvasDrawingSession ds, SlotSymbol symbol, Rect box, SlotLayout layout, float opacity)
    {
        Color color = symbol switch
        {
            SlotSymbol.EggBlue => ThemeColor(109, 192, 255),
            SlotSymbol.EggGreen => ThemeColor(142, 246, 133),
            SlotSymbol.EggRed => ThemeColor(255, 141, 75),
            SlotSymbol.EggRainbow => ThemeColor(236, 174, 255),
            SlotSymbol.Elixir => ThemeColor(152, 239, 212),
            SlotSymbol.Wild => ThemeColor(255, 207, 115),
            _ => default
        };
        if (color.A == 0) return;
        float seed = (float)(box.X * .013 + box.Y * .007);
        var center = Center(box);
        for (int index = 0; index < 3; index++)
        {
            float phase = (_slotVfxTime * .2f + seed + index / 3f) % 1;
            float angle = phase * MathF.Tau;
            float x = center.X + MathF.Cos(angle) * (float)box.Width * .34f;
            float y = center.Y + MathF.Sin(angle) * (float)box.Height * .31f;
            float a = MathF.Sin(phase * MathF.PI) * opacity;
            float arm = (float)box.Height * (.019f + .009f * MathF.Sin(angle * 3));
            var point = new Vector2(x, y);
            ds.DrawLine(point - new Vector2(arm / layout.Aspect, 0), point + new Vector2(arm / layout.Aspect, 0),
                WithAlpha(color, (byte)(a * 180)), 1);
            ds.DrawLine(point - new Vector2(0, arm), point + new Vector2(0, arm),
                WithAlpha(color, (byte)(a * 230)), 1);
        }
    }

    private static Vector2 SlotStreamPoint(Vector2 start, Vector2 end, float along, float bend, float aspect)
    {
        Vector2 delta = end - start;
        var perpendicular = new Vector2(-delta.Y / aspect, delta.X * aspect);
        float length = perpendicular.Length();
        if (length > .01f) perpendicular /= length;
        return Vector2.Lerp(start, end, along) + perpendicular * (MathF.Sin(along * MathF.PI) * bend);
    }

    private static void DrawSlotLightning(CanvasDrawingSession ds, Vector2 from, Vector2 target, float aspect,
        int seed, float alpha)
    {
        Vector2 previous = from;
        for (int step = 1; step <= 11; step++)
        {
            float along = step / 11f;
            var point = Vector2.Lerp(from, target, along);
            float taper = MathF.Sin(along * MathF.PI);
            point += new Vector2((SlotRandom(seed + step * 71) - .5f) * 24 / aspect,
                (SlotRandom(seed + step * 37) - .5f) * 24) * taper;
            ds.DrawLine(previous, point, ThemeColor(66, 125, 255, (byte)(alpha * 48)), 12);
            ds.DrawLine(previous, point, ThemeColor(94, 188, 255, (byte)(alpha * 200)), 4.2f);
            ds.DrawLine(previous, point, ThemeColor(232, 250, 255, (byte)(alpha * 255)), 1.15f);
            if (step is 3 or 7)
            {
                var branch = point + new Vector2((SlotRandom(seed + step) - .5f) * 48 / aspect,
                    (SlotRandom(seed + step * 5) - .5f) * 48);
                ds.DrawLine(point, branch, ThemeColor(144, 212, 255, (byte)(alpha * 130)), .9f);
            }
            previous = point;
        }
    }

    private static void DrawSlotCollectStream(CanvasDrawingSession ds, Vector2 target, Vector2 from, SlotLayout layout,
        float progress, int seed)
    {
        float bend = (SlotRandom(seed) - .5f) * 110;
        float head = (float)Ease(Math.Clamp(progress * 1.12f, 0, 1));
        for (int segment = 0; segment < 18; segment++)
        {
            float along = head - segment * .021f;
            if (along is <= 0 or > 1) continue;
            float end = Math.Max(0, along - .024f);
            float fade = (1 - segment / 18f) * MathF.Sin(head * MathF.PI);
            var p0 = SlotStreamPoint(target, from, along, bend, layout.Aspect);
            var p1 = SlotStreamPoint(target, from, end, bend, layout.Aspect);
            ds.DrawLine(p0, p1, ThemeColor(255, 65, 12, (byte)(fade * 85)), 10);
            ds.DrawLine(p0, p1, ThemeColor(255, 149, 32, (byte)(fade * 230)), 3.5f);
            ds.DrawLine(p0, p1, ThemeColor(255, 237, 175, (byte)(fade * 255)), 1.2f);
            if (segment % 4 == 0)
            {
                var drift = new Vector2((SlotRandom(seed + segment * 7) - .5f) * 18 / layout.Aspect,
                    (SlotRandom(seed + segment * 13) - .5f) * 18);
                ds.DrawLine(p0 + drift, p0 + drift + new Vector2(0, 3), ThemeColor(255, 190, 71, (byte)(fade * 210)), 1);
            }
        }
    }
}
