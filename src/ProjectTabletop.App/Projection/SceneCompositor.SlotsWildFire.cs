using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection.SlotsRendering;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelShaderEffect<SlotWildFireShader>? _slotWildFire;
    private CanvasDevice? _slotWildFireDevice;

    private void DrawSlotWildFire(CanvasDrawingSession ds, Rect box, float aspect, float time,
        float seed, float opacity, int heads, float swelling, float joining, float mouthY, float reveal = 0,
        float mouthStride = 0, float edgeDecay = 0, Rect? coreBox = null)
    {
        if (box.Width <= 0 || box.Height <= 0 || !double.IsFinite(box.Width) || !double.IsFinite(box.Height)
            || !float.IsFinite(opacity) || opacity <= 0) return;
        if (_slotWildFireDevice != ds.Device)
        {
            DisposeSlotWildFire();
            _slotWildFireDevice = ds.Device;
        }
        _slotWildFire ??= new PixelShaderEffect<SlotWildFireShader>();
        float safeAspect = float.IsFinite(aspect) ? Math.Clamp(aspect, .1f, 10) : 1;
        // Padding gives the side tongues space without zooming or shifting the
        // fire field, or changing any mouth coordinates calculated by the rig.
        var core = coreBox is { } supplied && supplied.Width > 0 && supplied.Height > 0
            && double.IsFinite(supplied.X) && double.IsFinite(supplied.Y)
            && double.IsFinite(supplied.Width) && double.IsFinite(supplied.Height) ? supplied : box;
        var bounds = new Float4((float)((core.X - box.X) / box.Width), (float)((core.Y - box.Y) / box.Height),
            (float)(core.Width / box.Width), (float)(core.Height / box.Height));
        float ratio = (float)Math.Clamp(core.Height / (core.Width * safeAspect), .25, 12);
        float clock = float.IsFinite(time) ? Math.Max(0, time) : 0;
        float variation = float.IsFinite(seed) ? seed : 0;
        float grow = float.IsFinite(swelling) ? Math.Clamp(swelling, 0, 1) : 0;
        float join = float.IsFinite(joining) ? Math.Clamp(joining, 0, 1) : 0;
        float mouth = float.IsFinite(mouthY) ? Math.Clamp(mouthY, 0, 1) : .56f;
        float clear = float.IsFinite(reveal) ? Math.Clamp(reveal, 0, 1) : 0;
        float decay = float.IsFinite(edgeDecay) ? Math.Clamp(edgeDecay, 0, 1) : 0;
        int count = Math.Clamp(heads, 1, 3);
        float stride = float.IsFinite(mouthStride) && mouthStride > 0 ? Math.Clamp(mouthStride, .1f, 1) : 1f / count;
        _slotWildFire.ConstantBuffer = new SlotWildFireShader(clock, variation, Math.Clamp(opacity, 0, 1),
            ratio, count, grow, join, mouth, clear, stride, decay, bounds);
        ds.DrawImage(_slotWildFire, box, new Rect(0, 0, 384, 1024));
    }

    private void DisposeSlotWildFire()
    {
        _slotWildFire?.Dispose();
        _slotWildFire = null;
        _slotWildFireDevice = null;
    }
}
