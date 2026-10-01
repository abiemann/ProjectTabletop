using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.App.Projection.SlotsRendering;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelShaderEffect<SlotDragonEyeShader>? _slotDragonEye;
    private CanvasDevice? _slotDragonEyeDevice;
    // Presentation history survives GPU/cabinet target recreation. Rendering
    // never modifies SlotGame or uses camera landmarks to aim the eye.
    private DateTimeOffset? _slotEyeEpoch;
    private SlotButtonPress? _slotEyePress;
    private Vector2 _slotEyeGazeFrom;
    private Vector2 _slotEyeGazeTarget;

    private Rect SlotDragonEyeBox(double aspect)
    {
        if (_slotBackdrop is null) return default;
        var source = SlotBackdropSource(aspect);
        double sx = _slotBackdrop.Size.Width / 1536, sy = _slotBackdrop.Size.Height / 1024;
        var patch = new Rect(333 * sx, 148 * sy, 42 * sx, 44 * sy);
        if (353.4 * sx < source.X || 353.4 * sx >= source.Right
            || 171.2 * sy < source.Y || 171.2 * sy >= source.Bottom) return default;
        return new((patch.X - source.X) / source.Width * 1000,
            (patch.Y - source.Y) / source.Height * 1000,
            patch.Width / source.Width * 1000, patch.Height / source.Height * 1000);
    }

    private void DrawSlotDragonEye(CanvasDrawingSession ds, DateTimeOffset now, double aspect)
    {
        EnsureSlotArtwork(ds.Device);
        var box = SlotDragonEyeBox(aspect);
        if (box.Width <= 0 || box.Height <= 0) return;
        _slotEyeEpoch ??= now;
        var source = SlotBackdropSource(aspect);
        if (_boardSession.SlotsLastButtonPress is { } press && press.Sequence != _slotEyePress?.Sequence)
        {
            // Sample the previous glance at the new press timestamp, so quick
            // repeated bets retarget smoothly instead of snapping to neutral.
            _slotEyeGazeFrom = SlotEyeGaze(press.StartedAt);
            _slotEyePress = press;
            double eyeX = (353.4 * _slotBackdrop!.Size.Width / 1536 - source.X) / source.Width;
            double eyeY = (171.2 * _slotBackdrop.Size.Height / 1024 - source.Y) / source.Height;
            var direction = new Vector2((float)((press.Bounds.X + press.Bounds.Width / 2 - eyeX) * aspect),
                (float)(press.Bounds.Y + press.Bounds.Height / 2 - eyeY));
            if (direction.LengthSquared() > .0001f) direction = Vector2.Normalize(direction);
            _slotEyeGazeTarget = new(direction.X * 2.1f, direction.Y * 1.55f);
        }
        float time = (float)Math.Max(0, (now - _slotEyeEpoch.Value).TotalSeconds);
        int blinkCycle = (int)(time / 7.8f);
        float blinkStart = blinkCycle * 7.8f + 2.1f + SlotRandom(blinkCycle * 71 + 43) * 3.2f;
        float closing = SlotEyeBlink(time - blinkStart);
        float age = _slotEyePress is null ? -1 : (float)(now - _slotEyePress.StartedAt).TotalSeconds;
        float attention = age >= 0 ? 1 - SlotEyeSmooth((age - 1.1f) / 1.2f) : 0;
        if (age >= 0) closing = Math.Max(closing, SlotEyeBlink(age - .035f));
        Vector2 gaze = SlotEyeGaze(now);
        if (_slotDragonEyeDevice != ds.Device)
        {
            DisposeSlotDragonEye();
            _slotDragonEyeDevice = ds.Device;
        }
        _slotDragonEye ??= new PixelShaderEffect<SlotDragonEyeShader>();
        _slotDragonEye.Sources[0] = _slotBackdrop;
        var scale = new Float2((float)_slotBackdrop!.Size.Width / 1536, (float)_slotBackdrop.Size.Height / 1024);
        _slotDragonEye.ConstantBuffer = new SlotDragonEyeShader(scale, new(gaze.X, gaze.Y), closing,
            attention, (float)(source.Y / scale.Y), (float)(source.Height / scale.Y));
        // Cached foreground ornament must still occlude the illustrated eye.
        // A wide crop puts it behind the title, a narrow one near the trim.
        using var interior = SlotCutPanel(ds.Device, new Rect(20, 20, 960, 960), 22);
        using var upperBoard = CanvasGeometry.CreateRectangle(ds.Device, new Rect(0, 0, 1000, 700));
        using var bounded = interior.CombineWith(upperBoard, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);
        using var marquee = SlotCutPanel(ds.Device, new Rect(193, 15, 614, 78), 17);
        using var clip = bounded.CombineWith(marquee, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using (ds.CreateLayer(1, clip))
            ds.DrawImage(_slotDragonEye, box, new Rect(333 * scale.X, 148 * scale.Y, 42 * scale.X, 44 * scale.Y));
    }

    private Vector2 SlotEyeGaze(DateTimeOffset now)
    {
        if (_slotEyePress is null) return Vector2.Zero;
        float age = (float)(now - _slotEyePress.StartedAt).TotalSeconds;
        if (age < 0) return _slotEyeGazeFrom;
        Vector2 gaze = Vector2.Lerp(_slotEyeGazeFrom, _slotEyeGazeTarget, SlotEyeSmooth(age / .22f));
        return gaze * (1 - SlotEyeSmooth((age - 1.25f) / 1.05f));
    }

    private static float SlotEyeSmooth(float value)
    {
        float t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float SlotEyeBlink(float age)
    {
        if (age < 0 || age >= .29f) return 0;
        if (age < .085f) return SlotEyeSmooth(age / .085f);
        return age < .115f ? 1 : 1 - SlotEyeSmooth((age - .115f) / .175f);
    }

    private void DisposeSlotDragonEye()
    {
        _slotDragonEye?.Dispose();
        _slotDragonEye = null;
        _slotDragonEyeDevice = null;
    }
}
