using System.Numerics;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.App.Projection.SlotsRendering;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelShaderEffect<SlotDragonLavaShader>? _slotDragonLava;
    private CanvasDevice? _slotDragonLavaDevice;
    // This rare presentation event keeps its clock through GPU recreation.
    // Neither scheduling nor droplet variation consumes game randomness.
    private DateTimeOffset? _slotLavaEpoch;
    private const float SlotLavaDuration = 6;

    private static double SlotLavaEventStart(int cycle) => cycle * 180d + 120
        + SlotRandom(unchecked(cycle * 193 + 5819)) * 60;

    private static float SlotLavaEventAge(double elapsed)
    {
        int cycle = (int)(Math.Max(0, elapsed) / 180);
        // The last event in a block can briefly extend into the next block.
        for (int candidate = cycle; candidate >= Math.Max(0, cycle - 1); candidate--)
        {
            double age = elapsed - SlotLavaEventStart(candidate);
            if (age is >= 0 and < SlotLavaDuration) return (float)age;
        }
        return -1;
    }

    private void DrawSlotDragonLava(CanvasDrawingSession ds, DateTimeOffset now, double aspect, SlotLayout layout)
    {
        _slotLavaEpoch ??= now;
        double elapsed = Math.Max(0, (now - _slotLavaEpoch.Value).TotalSeconds);
        float age = SlotLavaEventAge(elapsed);
        if (age < 0) return; // No shader or geometry work between rare dribbles.
        EnsureSlotArtwork(ds.Device);
        if (_slotBackdrop is null) return;
        var source = SlotBackdropSource(aspect);
        double sx = _slotBackdrop.Size.Width / 1536, sy = _slotBackdrop.Size.Height / 1024;
        // The exposed lower lip, not the long gold chin spike beneath it.
        if (368 * sx < source.X || 368 * sx >= source.Right
            || 320 * sy < source.Y || 320 * sy >= source.Bottom) return;
        var patch = new Rect(344 * sx, 303 * sy, 64 * sx, 224 * sy);
        var box = new Rect((patch.X - source.X) / source.Width * 1000,
            (patch.Y - source.Y) / source.Height * 1000,
            patch.Width / source.Width * 1000, patch.Height / source.Height * 1000);
        if (_slotDragonLavaDevice != ds.Device)
        {
            DisposeSlotDragonLava();
            _slotDragonLavaDevice = ds.Device;
        }
        _slotDragonLava ??= new PixelShaderEffect<SlotDragonLavaShader>();
        int cycle = (int)(elapsed / 180);
        if (elapsed - SlotLavaEventStart(cycle) < 0) cycle--;
        _slotDragonLava.ConstantBuffer = new SlotDragonLavaShader(age, SlotRandom(unchecked(cycle * 271 + 97)));

        // The live layer sits above the cached cabinet. Cut away its foreground
        // explicitly so lava remains in the cavern, behind the reel ornament.
        using var interior = SlotCutPanel(ds.Device, new Rect(20, 20, 960, 960), 22);
        using var upper = CanvasGeometry.CreateRectangle(ds.Device, new Rect(0, 0, 1000, 700));
        using var bounded = interior.CombineWith(upper, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);
        using var marquee = SlotCutPanel(ds.Device, new Rect(193, 15, 614, 78), 17);
        using var crownClip = bounded.CombineWith(marquee, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var reels = CanvasGeometry.CreateRectangle(ds.Device,
            new Rect(layout.Left - 25, layout.Top - 25, layout.Width + 50, layout.Height + 50));
        using var reelClip = crownClip.CombineWith(reels, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var leftMeter = CanvasGeometry.CreateRectangle(ds.Device,
            new Rect(500 - 222 / layout.Aspect, 94, 144 / layout.Aspect, 46));
        using var centerMeter = CanvasGeometry.CreateRectangle(ds.Device,
            new Rect(500 - 72 / layout.Aspect, 94, 144 / layout.Aspect, 46));
        using var rightMeter = CanvasGeometry.CreateRectangle(ds.Device,
            new Rect(500 + 78 / layout.Aspect, 94, 144 / layout.Aspect, 46));
        using var a = reelClip.CombineWith(leftMeter, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var b = a.CombineWith(centerMeter, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var clip = b.CombineWith(rightMeter, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using (ds.CreateLayer(1, clip))
            ds.DrawImage(_slotDragonLava, box, new Rect(0, 0, 64, 224));
    }

    private void DisposeSlotDragonLava()
    {
        _slotDragonLava?.Dispose();
        _slotDragonLava = null;
        _slotDragonLavaDevice = null;
    }
}
