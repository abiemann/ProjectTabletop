using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.App.Projection.SlotsRendering;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasDevice? _slotMarqueeDevice;
    private CanvasGeometry? _slotMarqueeLetters;
    private CanvasGeometry? _slotMarqueeExtrusion;
    private PixelShaderEffect<SlotMarqueeFireShader>? _slotMarqueeFire;
    private Rect _slotMarqueeInk;
    private float _slotMarqueeAspect;

    // Called before the reel layer's y88 effects clip. Natural flame tips can
    // rise above the plaque; the lower bound keeps jackpot text untouched.
    private void DrawSlotMarqueeLive(CanvasDrawingSession ds, SlotLayout layout)
    {
        EnsureSlotMarquee(ds.Device, layout.Aspect);
        float time = _slotVfxTime;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1)
            * Matrix3x2.CreateTranslation(500, 0) * previous;
        try
        {
            using var clip = CanvasGeometry.CreateRectangle(ds.Device,
                new Rect(-480 * layout.Aspect, -28, 960 * layout.Aspect, 122));
            using var layer = ds.CreateLayer(1, clip);
            // One continuous fuel bed produces unequal curling sheets. Pass
            // physical dimensions so the fine folds keep their proportions
            // on wide and narrow boards; the opaque iron is drawn above it.
            float left = (float)_slotMarqueeInk.X - 46;
            float right = (float)_slotMarqueeInk.Right + 46;
            float width = right - left;
            const float height = 122;
            _slotMarqueeFire ??= new PixelShaderEffect<SlotMarqueeFireShader>();
            _slotMarqueeFire.ConstantBuffer = new SlotMarqueeFireShader(time, new Float2(width, height));
            ds.DrawImage(_slotMarqueeFire,
                new Rect(left, -28, width, height), new Rect(0, 0, width, height));
            for (int ember = 0; ember < 15; ember++)
            {
                float random = SlotRandom(ember * 41 + 67);
                float phase = (time * (.14f + random * .13f) + SlotRandom(ember * 31 + 19)) % 1;
                float x = (float)_slotMarqueeInk.X + random * (float)_slotMarqueeInk.Width
                    + MathF.Sin(phase * 6 + random * 8) * 5;
                float y = 87 - phase * 100;
                float alpha = MathF.Sin(phase * MathF.PI);
                ds.DrawLine(x, y, x - .4f, y + 1.8f + random * 2,
                    ThemeColor(255, 107, 24, (byte)(alpha * 155)), 1.7f);
                ds.DrawLine(x, y, x, y + .9f,
                    ThemeColor(255, 214, 132, (byte)(alpha * 220)), .65f);
            }
        }
        finally { ds.Transform = previous; }
        DrawSlotMarqueeIron(ds, layout.Aspect, time);
    }

    private void DrawSlotMarqueeIron(CanvasDrawingSession ds, float aspect, float time)
    {
        EnsureSlotMarquee(ds.Device, aspect);
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(1 / aspect, 1)
            * Matrix3x2.CreateTranslation(500, 0) * previous;
        try
        {
            float heat = .5f + .23f * MathF.Sin(time * 1.7f) + .16f * MathF.Sin(time * 2.91f + 1.3f);
            ds.DrawGeometry(_slotMarqueeLetters!, ThemeColor(204, 47, 9, (byte)(65 + heat * 35)), 5.2f);
            ds.FillGeometry(_slotMarqueeExtrusion!, ThemeColor(3, 4, 6));
            ds.DrawGeometry(_slotMarqueeExtrusion!, ThemeColor(61, 30, 20), 1.4f);
            using var heatedEdge = new CanvasLinearGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = ThemeColor(93, 85, 75) },
                new() { Position = .3f, Color = ThemeColor(174, 133, 91) },
                new() { Position = .55f, Color = ThemeColor(235, 111, 34) },
                new() { Position = .83f, Color = ThemeColor(255, 176, 60) },
                new() { Position = 1, Color = ThemeColor(182, 44, 9) }
            ])
            {
                StartPoint = new(0, (float)_slotMarqueeInk.Y - heat * 4),
                EndPoint = new(0, (float)_slotMarqueeInk.Bottom + 2)
            };
            ds.DrawGeometry(_slotMarqueeLetters!, heatedEdge, 1.8f);
            using var iron = new CanvasLinearGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = ThemeColor(56, 57, 59) },
                new() { Position = .15f, Color = ThemeColor(24, 26, 30) },
                new() { Position = .42f, Color = ThemeColor(9, 11, 15) },
                new() { Position = .63f, Color = ThemeColor(22, 23, 25) },
                new() { Position = 1, Color = ThemeColor(5, 7, 10) }
            ]) { StartPoint = new(-10, (float)_slotMarqueeInk.Y), EndPoint = new(10, (float)_slotMarqueeInk.Bottom) };
            ds.FillGeometry(_slotMarqueeLetters!, iron);
            // Shallow hammer scars are clipped to the face of the type. No
            // moving fill or bright interior turns the iron into gold lettering.
            using var layer = ds.CreateLayer(1, _slotMarqueeLetters!);
            for (int scar = 0; scar < 27; scar++)
            {
                float x = (float)_slotMarqueeInk.X + SlotRandom(scar * 47 + 173) * (float)_slotMarqueeInk.Width;
                float y = (float)_slotMarqueeInk.Y + SlotRandom(scar * 23 + 43) * (float)_slotMarqueeInk.Height;
                float length = 1.4f + SlotRandom(scar * 17 + 61) * 4.2f;
                ds.DrawLine(x, y, x + length, y - .45f, ThemeColor(111, 105, 98, 60), .45f);
                ds.DrawLine(x, y + .6f, x + length, y + .15f, ThemeColor(0, 0, 0, 150), .5f);
            }
        }
        finally { ds.Transform = previous; }
    }

    private void EnsureSlotMarquee(CanvasDevice device, float aspect)
    {
        if (_slotMarqueeLetters is not null && _slotMarqueeDevice == device && _slotMarqueeAspect == aspect) return;
        DisposeSlotMarquee();
        const string title = "Dragon’s Hoard";
        var box = new Rect(190, 14, 620, 72);
        using var format = FitSlotTextFormat(device, title, box, 64, aspect, "Georgia", true);
        using var text = new CanvasTextLayout(device, title, format, (float)box.Width * aspect, (float)box.Height);
        using var source = CanvasGeometry.CreateText(text);
        _slotMarqueeLetters = source.Transform(Matrix3x2.CreateTranslation(-(float)box.Width * aspect / 2, (float)box.Y));
        _slotMarqueeExtrusion = _slotMarqueeLetters.Transform(Matrix3x2.CreateTranslation(1.1f, 2.1f));
        _slotMarqueeInk = _slotMarqueeLetters.ComputeBounds();
        _slotMarqueeDevice = device;
        _slotMarqueeAspect = aspect;
    }

    private void DisposeSlotMarquee()
    {
        _slotMarqueeLetters?.Dispose();
        _slotMarqueeExtrusion?.Dispose();
        _slotMarqueeFire?.Dispose();
        _slotMarqueeLetters = null;
        _slotMarqueeExtrusion = null;
        _slotMarqueeFire = null;
        _slotMarqueeDevice = null;
        _slotMarqueeAspect = 0;
    }
}
