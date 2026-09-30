using System.Numerics;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
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

    private void DrawSlotPanelFire(CanvasDrawingSession ds, SlotLayout layout)
    {
        float left = layout.Right + 26, right = 966;
        var center = new Vector2((left + right) / 2, 542);
        // The art and flame are bounded between the two static text captions.
        // Nothing here participates in the lower control/acquisition surface.
        using var clip = CanvasGeometry.CreateRectangle(ds.Device, new Rect(left + 8, 500, right - left - 16, 78));
        using (ds.CreateLayer(1, clip))
        {
            var fire = layout.Square(new Vector2(center.X, center.Y - 9), 91);
            DrawSlotFire(ds, fire, _slotVfxTime, 3.7f, .95f);
            DrawSlotArt(ds, SlotSymbol.Coin, layout.Square(center, 70), 1);
        }
    }

    private static void DrawSlotAmbient(CanvasDrawingSession ds, SlotLayout layout, float time)
    {
        // Strictly above the opaque camera-observed controls. The sparse field
        // gives the idle cabinet life without flashing its titles or captions.
        float top = layout.Top - 17;
        var bounds = new Rect(layout.Left - 38, top, layout.Width + 76, Math.Min(685, layout.Bottom + 36) - top);
        using var clip = CanvasGeometry.CreateRectangle(ds.Device, bounds);
        using (ds.CreateLayer(1, clip))
            for (int index = 0; index < 24; index++)
            {
                float random = SlotRandom(index * 23 + 41);
                float phase = (time * (.025f + random * .023f) + SlotRandom(index + 209)) % 1;
                float x = (float)bounds.X + SlotRandom(index * 17 + 83) * (float)bounds.Width
                    + MathF.Sin(phase * 8 + random * 9) * 12 / layout.Aspect;
                float y = (float)bounds.Bottom - phase * (float)bounds.Height;
                float alpha = MathF.Sin(phase * MathF.PI) * (.25f + random * .35f);
                float length = 1.8f + random * 4;
                var point = new Vector2(x, y);
                ds.DrawLine(point, point + new Vector2(-.8f / layout.Aspect, length),
                    ThemeColor(255, 138, 36, (byte)(alpha * 130)), 3 / MathF.Sqrt(layout.Aspect));
                ds.DrawLine(point, point + new Vector2(0, length * .5f),
                    ThemeColor(255, 226, 148, (byte)(alpha * 245)), .9f / MathF.Sqrt(layout.Aspect));
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
