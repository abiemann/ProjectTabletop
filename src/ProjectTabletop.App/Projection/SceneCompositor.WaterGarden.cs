using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.App.Projection.WaterGarden;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _waterClock;
    private WaterGardenSimulation? _waterSimulation;
    private CanvasDevice? _waterDevice;
    private readonly Queue<(Vector2 Position, float Strength)> _waterDisturbances = new();
    private readonly Queue<(Vector2 Previous, Vector2 Current, float Strength)> _waterStickStrokes = new();
    private DateTimeOffset _waterAdvancedAt, _waterResetThrough, _waterFrameTime;
    private Vector2? _waterTip, _waterSurfaceTip;
    private Homography? _waterCameraMap, _waterSurfaceMap;
    private long _waterNavigation = -1, _waterResetRevision = -1, _waterVisualRevision, _waterInputCount;
    private bool _waterWasActive;
    private CanvasRenderTarget? _waterFrameTarget;
    private (long Visual, int Feedback)? _waterRenderedFrame;

    public double WaterGardenPreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }

    internal sealed record WaterGardenDiagnostics(bool Active, bool TipVisible, Point2? BoardTip,
        DateTimeOffset SourceTime, long InputCount, int PendingDisturbances, object? Simulation);

    internal WaterGardenDiagnostics GetWaterGardenDiagnostics()
    {
        lock (_gate)
        {
            SyncWaterGardenSession();
            ExpireWaterStick(_waterClock());
            return new(_waterWasActive, _waterTip is not null,
                _waterTip is { } tip ? new Point2(tip.X, tip.Y) : null, _waterFrameTime,
                _waterInputCount, _waterDisturbances.Count, _waterSimulation?.GetDiagnostics());
        }
    }

    public void ShowWaterGarden()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowWaterGarden(_waterClock());
            SyncPhotoCopySession();
            SyncWaterGardenSession();
        }
    }

    public bool ActivateWaterGardenButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.WaterGarden) return false;
            bool accepted = _boardSession.ActivateButton(id, _waterClock());
            SyncWaterGardenSession();
            return accepted;
        }
    }

    public bool ActivateWaterGardenAt(double u, double v)
    {
        lock (_gate)
        {
            if (!double.IsFinite(u) || !double.IsFinite(v)) return false;
            var button = _boardSession.Buttons.FirstOrDefault(item => item.Enabled && item.Bounds.Contains(u, v));
            return button is not null && ActivateWaterGardenButton(button.Id);
        }
    }

    public void DrawWaterGardenPreview(CanvasDrawingSession ds, float width, float height)
    {
        ds.Clear(Color.FromArgb(255, 9, 22, 21));
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.WaterGarden) return;
            if (!PrepareWaterGardenResources(ds.Device))
            {
                DrawArtworkLoading(ds, new Rect(0, 0, width, height));
                return;
            }
            double aspect = PaintBoardAspect();
            double drawWidth = Math.Min(width, height * aspect), drawHeight = drawWidth / aspect;
            ReserveBoardPixels(ds.Device, drawWidth * ds.Dpi / 96, drawHeight * ds.Dpi / 96);
            var rendered = GetWaterGardenFrame(ds.Device, _waterClock(), CurrentFingerSelectionFeedback);
            ds.DrawImage(rendered, new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight),
                new Rect(0, 0, rendered.SizeInPixels.Width, rendered.SizeInPixels.Height));
        }
    }

    private CanvasRenderTarget GetWaterGardenFrame(CanvasDevice device, DateTimeOffset now,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback)
    {
        var key = (WaterGardenVisualRevision(now), FingerSelectionRenderStep(feedback));
        if (EnsureBoardRenderTarget(ref _waterFrameTarget, device)) _waterRenderedFrame = null;
        if (_waterRenderedFrame != key)
        {
            using var drawing = _waterFrameTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_waterFrameTarget);
            DrawWaterGardenSurface(drawing, now);
            DrawWaterGardenRocks(drawing);
            DrawWaterGardenControls(drawing, _boardSession.Buttons, [], feedback);
            _waterRenderedFrame = key;
        }
        return _waterFrameTarget!;
    }

    private void DrawCachedWaterGarden(CanvasDrawingSession ds, DateTimeOffset now,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback)
    {
        var rendered = GetWaterGardenFrame(ds.Device, now, feedback);
        ds.DrawImage(rendered, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize),
            new Rect(0, 0, rendered.SizeInPixels.Width, rendered.SizeInPixels.Height));
    }

    // Stick observations never enter the hand or button-selection pipeline. The
    // calibrated plane supplies position, not physical contact or stick depth.
    public bool SetWaterStickTip(PixelPoint? cameraPoint, DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            SyncWaterGardenSession();
            var now = _waterClock();
            if (cameraPoint is not { } camera)
            {
                if (frameTime != default && frameTime < _waterFrameTime) return false;
                ClearWaterStick();
                if (frameTime > _waterFrameTime && frameTime <= now) _waterFrameTime = frameTime;
                return false;
            }
            if (!_waterWasActive || frameTime <= _waterResetThrough || frameTime <= _waterFrameTime ||
                frameTime > now + TimeSpan.FromMilliseconds(50) || now - frameTime > TimeSpan.FromMilliseconds(250) ||
                !double.IsFinite(camera.X) || !double.IsFinite(camera.Y))
            {
                if (now - _waterFrameTime > TimeSpan.FromMilliseconds(250)) ClearWaterStick();
                return false;
            }

            Point2 uv;
            try
            {
                var projected = _boardCameraMap!.Transform(new(camera.X, camera.Y));
                uv = _boardSurfaceMap!.InverseTransform(projected);
            }
            catch (InvalidOperationException)
            {
                ClearWaterStick();
                _waterFrameTime = frameTime;
                return false;
            }
            var boardPoint = new Vector2((float)uv.X, (float)uv.Y);
            if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y) || uv.X < .025 || uv.X > .975 ||
                uv.Y < .12 || uv.Y > .82 || IsWaterGardenRock(uv.X, uv.Y) ||
                !WaterGardenView.ContainsWater(boardPoint, (float)PaintBoardAspect()) ||
                WaterFountainLayout.OccludesSurface(boardPoint, (float)PaintBoardAspect()))
            {
                ClearWaterStick();
                _waterFrameTime = frameTime;
                return false;
            }
            // The calibrated point is where the marker appears on the physical
            // board. Invert the garden camera so the wave renders at that same
            // location even though the simulated water is viewed at an angle.
            var point = WaterGardenView.ScreenToSurface(boardPoint);
            Vector2? previous = _waterSurfaceTip;
            if (now - _waterFrameTime > TimeSpan.FromMilliseconds(250)) previous = null;
            float aspect = (float)PaintBoardAspect();
            float distance = previous is { } prior
                ? Vector2.Distance(new(prior.X * aspect, prior.Y), new(point.X * aspect, point.Y)) : 0;
            // Small camera jitter does not pump waves into a resting stick. A
            // jump/reacquisition starts locally instead of drawing across gaps.
            if (previous is null || distance > .18f)
                QueueWaterDisturbance(point, .005f);
            else if (distance >= .0035f)
            {
                int count = Math.Clamp((int)Math.Ceiling(distance / .013), 1, 6);
                float strength = Math.Clamp(distance * .10f, .0015f, .004f) / MathF.Sqrt(count);
                for (int i = 1; i <= count; i++)
                    QueueWaterDisturbance(Vector2.Lerp(previous.Value, point, (float)i / count), strength);
                // One accepted marker movement transfers momentum to nearby
                // ducks. Interpolated ripple samples must not multiply the shove.
                if (_waterStickStrokes.Count >= 24) _waterStickStrokes.Dequeue();
                _waterStickStrokes.Enqueue((previous.Value, point,
                    Math.Clamp(distance / .06f, .15f, 1f)));
            }
            // Keep the disturbance anchor during sub-threshold movement so a
            // very slow deliberate stroke can still accumulate enough distance.
            if (previous is null || distance >= .0035f) _waterSurfaceTip = point;
            _waterTip = boardPoint;
            _waterFrameTime = frameTime;
            return true;
        }
    }

    private void QueueWaterDisturbance(Vector2 position, float strength)
    {
        if (_waterDisturbances.Count >= 24) _waterDisturbances.Dequeue();
        _waterDisturbances.Enqueue((position, strength));
        _waterInputCount++;
        _waterVisualRevision++;
    }

    private void ClearWaterStick()
    {
        _waterTip = null;
        _waterSurfaceTip = null;
        _waterDisturbances.Clear();
        _waterStickStrokes.Clear();
        // Preserve source ordering across loss; only genuinely newer frames
        // may reacquire. The separate reset watermark blocks pre-calibration work.
    }

    private void ExpireWaterStick(DateTimeOffset now)
    {
        if (now - _waterFrameTime > TimeSpan.FromMilliseconds(250)) ClearWaterStick();
    }

    private void SyncWaterGardenSession()
    {
        bool active = !_disposed && _boardSession.Screen == BoardScreen.WaterGarden &&
            !_blackOutput && !_boardSetup && _calibrationTarget < 0 && !IsBoardRevealActive &&
            _boardCameraMap is not null && _boardSurfaceMap is not null;
        bool registrationChanged = !ReferenceEquals(_waterCameraMap, _boardCameraMap) ||
            !ReferenceEquals(_waterSurfaceMap, _boardSurfaceMap);
        if (_waterWasActive == active && !registrationChanged &&
            _waterNavigation == _boardSession.NavigationRevision &&
            _waterResetRevision == _boardSession.WaterGardenResetRevision) return;
        ClearWaterStick();
        _waterResetThrough = _waterClock();
        _waterFrameTime = default;
        _waterAdvancedAt = default;
        _waterInputCount = 0;
        _waterCameraMap = _boardCameraMap;
        _waterSurfaceMap = _boardSurfaceMap;
        _waterNavigation = _boardSession.NavigationRevision;
        _waterResetRevision = _boardSession.WaterGardenResetRevision;
        _waterWasActive = active;
        if (!active || registrationChanged) DisposeWaterGardenResources();
        else _waterSimulation?.Reset();
        _waterVisualRevision++;
        _renderedBoardState = null;
    }

    private long WaterGardenVisualRevision(DateTimeOffset now)
    {
        SyncWaterGardenSession();
        ExpireWaterStick(now);
        AdvanceWaterGarden(now);
        return unchecked(_waterVisualRevision * 1000000007 + now.UtcTicks / (TimeSpan.TicksPerSecond / 60));
    }

    private void AdvanceWaterGarden(DateTimeOffset now)
    {
        if (_waterSimulation is null || now <= _waterAdvancedAt) return;
        if (_waterAdvancedAt != default) _waterSimulation.Advance((now - _waterAdvancedAt).TotalSeconds);
        _waterAdvancedAt = now;
    }

    private void DrawWaterGardenSurface(CanvasDrawingSession ds, DateTimeOffset now)
    {
        if (_waterSimulation is null || _waterDevice != ds.Device)
        {
            _waterSimulation?.Dispose();
            double aspect = PaintBoardAspect();
            int width = aspect >= 1 ? 512 : Math.Max(64, (int)Math.Round(512 * aspect));
            int height = aspect <= 1 ? 512 : Math.Max(64, (int)Math.Round(512 / aspect));
            _waterSimulation = new(ds.Device, width, height, aspect,
                _waterImages?.Image("warm-limestone.png") ??
                throw new InvalidOperationException("Water Garden basin artwork was not loaded before rendering."),
                _waterImages?.Image("wet-slate.png") ??
                throw new InvalidOperationException("Water Garden cascade artwork was not loaded before rendering."));
            _waterDevice = ds.Device;
            _waterAdvancedAt = now;
        }
        AdvanceWaterGarden(now);
        while (_waterDisturbances.TryDequeue(out var ripple))
            _waterSimulation.AddDisturbance(ripple.Position, .034f, ripple.Strength);
        while (_waterStickStrokes.TryDequeue(out var stroke))
            _waterSimulation.AddStickStroke(stroke.Previous, stroke.Current, stroke.Strength);
        _waterSimulation.Draw(ds, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
    }

    private static readonly Color WaterInk = Color.FromArgb(255, 30, 49, 45);
    private static readonly Color WaterStone = Color.FromArgb(255, 211, 215, 196);

    private void DrawWaterGardenControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> feedback)
    {
        foreach (var button in buttons)
        {
            var b = button.Bounds;
            var rect = new Rect(b.X * BoardSurfaceSize, b.Y * BoardSurfaceSize,
                b.Width * BoardSurfaceSize, b.Height * BoardSurfaceSize);
            float radius = BoardButtonCornerRadius(button);
            ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 5, rect.Width, rect.Height), radius, radius,
                Color.FromArgb(170, 9, 28, 26));
            ds.FillRoundedRectangle(rect, radius, radius, WaterStone);
            ds.DrawRoundedRectangle(new Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), radius - 2, radius - 2,
                Color.FromArgb(255, 239, 239, 221), 2);
            using var text = PaintButtonTextFormat();
            DrawWaterText(ds, button.Label, new Rect(rect.X + 20, rect.Y + 6, rect.Width - 40, rect.Height - 18), WaterInk, text);
            DrawButtonFingerSelectionFeedback(ds, button, feedback, WaterInk);
        }
    }

    private void DrawWaterText(CanvasDrawingSession ds, string text, Rect bounds, Color color, CanvasTextFormat format)
    {
        float aspect = (float)PaintBoardAspect();
        format.FontSize *= Math.Min(1, aspect);
        var transform = ds.Transform;
        try
        {
            ds.Transform = Matrix3x2.CreateScale(1 / aspect, 1) * transform;
            ds.DrawText(text, new Rect(bounds.X * aspect, bounds.Y, bounds.Width * aspect, bounds.Height), color, format);
        }
        finally { ds.Transform = transform; }
    }

    private HandTrackingBounds WaterGardenButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        var b = button.Bounds;
        float aspect = (float)PaintBoardAspect();
        using var format = PaintButtonTextFormat();
        format.FontSize *= Math.Min(1, aspect);
        using var layout = new CanvasTextLayout(device, button.Label, format,
            (float)(b.Width * BoardSurfaceSize - 40) * aspect, (float)(b.Height * BoardSurfaceSize - 18));
        var ink = layout.DrawBounds;
        return ButtonInkRegion(button, new Rect(ink.X / aspect, ink.Y, ink.Width / aspect, ink.Height),
            b.X * BoardSurfaceSize + 20, b.Y * BoardSurfaceSize + 6);
    }

    private void DisposeWaterGardenResources()
    {
        _waterSimulation?.Dispose();
        _waterSimulation = null;
        _waterFrameTarget?.Dispose();
        _waterFrameTarget = null;
        _waterRenderedFrame = null;
        _waterDevice = null;
        _waterAdvancedAt = default;
        ClearWaterStick();
    }
}
