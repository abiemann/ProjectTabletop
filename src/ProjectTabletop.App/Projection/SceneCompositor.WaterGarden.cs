using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
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
    private long _waterNavigation = -1, _waterResetRevision = -1;
    private int _waterAppliedDuckCount;
    private long _waterVisualRevision, _waterInputCount;
    private bool _waterWasActive;
    private bool _waterIntroPending;
    private float _waterEntranceProgress = 1;
    private CanvasRenderTarget? _waterFrameTarget;
    private (long Visual, int Feedback)? _waterRenderedFrame;

    public double WaterGardenPreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }
    public bool WaterGardenDrawerOpen { get { lock (_gate) return _boardSession.WaterGardenDrawerOpen; } }
    public double GetWaterGardenDrawerProgress(DateTimeOffset now)
    {
        lock (_gate) return _boardSession.GetWaterGardenDrawerProgress(now);
    }

    public bool TickWaterGarden(DateTimeOffset now)
    {
        lock (_gate)
        {
            RefreshWaterStickPresence();
            return _boardSession.TickWaterGarden(now);
        }
    }

    private bool HasWaterGardenDrawerAnimation(DateTimeOffset now)
    {
        _boardSession.TickWaterGarden(now);
        return _boardSession.WaterGardenDrawerOpen &&
            _boardSession.GetWaterGardenDrawerProgress(now) < 1;
    }

    internal sealed record WaterGardenDiagnostics(bool Active, bool TipVisible, Point2? BoardTip,
        DateTimeOffset SourceTime, long InputCount, int PendingDisturbances, object? Simulation);

    internal WaterGardenDiagnostics GetWaterGardenDiagnostics()
    {
        lock (_gate)
        {
            SyncWaterGardenSession();
            ApplyWaterDuckAdds();
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
            RefreshWaterStickPresence();
            bool accepted = _boardSession.ActivateButton(id, _waterClock());
            SyncWaterGardenSession();
            if (accepted)
            {
                ApplyWaterDuckAdds();
                _waterVisualRevision++;
                _waterRenderedFrame = null;
            }
            return accepted;
        }
    }

    public bool ActivateWaterGardenAt(double u, double v)
    {
        lock (_gate)
        {
            if (!double.IsFinite(u) || !double.IsFinite(v)) return false;
            RefreshWaterStickPresence();
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
            DrawWaterGardenTitle(drawing, now);
            DrawWaterGardenControls(drawing, _boardSession.Buttons, [], feedback, now);
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

    // Stick observations cannot select buttons, but disarm controls while the
    // tip is in the water. The calibrated plane supplies position, not contact
    // or stick depth.
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
            // During the dolly-in the screen-to-water inverse changes each
            // frame. Wait for the settled calibrated view before accepting a
            // physical stick location, then reacquire it locally.
            if (WaterGardenEntranceProgress(now) < 1)
            {
                ClearWaterStick();
                _waterFrameTime = frameTime;
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
                uv.Y < .12 || uv.Y > .87 || IsWaterGardenRock(uv.X, uv.Y) ||
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
                QueueWaterDisturbance(point, .012f);
            else if (distance >= .0035f)
            {
                int count = Math.Clamp((int)Math.Ceiling(distance / .013), 1, 6);
                float strength = Math.Clamp(distance * .28f, .004f, .012f) / MathF.Sqrt(count);
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
            SetWaterStickPresence(true);
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
        SetWaterStickPresence(false);
        // Preserve source ordering across loss; only genuinely newer frames
        // may reacquire. The separate reset watermark blocks pre-calibration work.
    }

    private void ExpireWaterStick(DateTimeOffset now)
    {
        if (now - _waterFrameTime > TimeSpan.FromMilliseconds(250)) ClearWaterStick();
    }

    private void RefreshWaterStickPresence()
    {
        if (_boardSession.Screen != BoardScreen.WaterGarden) return;
        SyncWaterGardenSession();
        ExpireWaterStick(_waterClock());
    }

    private void SetWaterStickPresence(bool present)
    {
        if (!_boardSession.SetWaterGardenStickPresent(present, _waterClock())) return;
        // Reject in-flight caption results even if the marker enters and leaves
        // between two hold-context captures. Re-arming requires a fresh clear.
        _holdSceneKey = null;
        _holdExpectedScene = null;
        _holdRevision++;
        _waterVisualRevision++;
        _waterRenderedFrame = null;
    }

    private void SyncWaterGardenSession()
    {
        bool entered = _boardSession.Screen == BoardScreen.WaterGarden &&
            _waterNavigation != _boardSession.NavigationRevision;
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
        _waterAppliedDuckCount = 0;
        _waterWasActive = active;
        if (entered)
        {
            // Start the entrance on the first rendered frame, after artwork
            // finishes loading, so users see the whole move.
            _waterIntroPending = true;
            _waterIntroStartedAt = default;
            _waterEntranceProgress = 0;
        }
        else if (_boardSession.Screen != BoardScreen.WaterGarden)
        {
            _waterIntroPending = false;
            _waterEntranceProgress = 1;
        }
        if (!active || registrationChanged) DisposeWaterGardenResources();
        else _waterSimulation?.Reset();
        _waterVisualRevision++;
        _renderedBoardState = null;
    }

    private long WaterGardenVisualRevision(DateTimeOffset now)
    {
        _boardSession.TickWaterGarden(now);
        SyncWaterGardenSession();
        ApplyWaterDuckAdds();
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
        if (_waterIntroPending)
        {
            ResetWaterGardenIntro(now);
            _waterIntroPending = false;
        }
        _waterEntranceProgress = WaterGardenEntranceProgress(now);
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
                throw new InvalidOperationException("Water Garden cascade artwork was not loaded before rendering."),
                _waterImages?.Image("sand-ground.png") ??
                throw new InvalidOperationException("Water Garden sand artwork was not loaded before rendering."));
            _waterDevice = ds.Device;
            _waterAdvancedAt = now;
            _waterAppliedDuckCount = 0;
        }
        _waterSimulation.SetEntranceProgress(_waterEntranceProgress);
        ApplyWaterDuckAdds();
        AdvanceWaterGarden(now);
        while (_waterDisturbances.TryDequeue(out var ripple))
            _waterSimulation.AddDisturbance(ripple.Position, .046f, ripple.Strength);
        while (_waterStickStrokes.TryDequeue(out var stroke))
            _waterSimulation.AddStickStroke(stroke.Previous, stroke.Current, stroke.Strength);
        _waterSimulation.Draw(ds, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
    }

    private float WaterGardenEntranceProgress(DateTimeOffset now)
    {
        if (_waterIntroPending) return 0;
        if (_waterIntroStartedAt == default) return 1;
        return Math.Clamp((float)(now - _waterIntroStartedAt).TotalSeconds /
            WaterGardenView.EntranceDurationSeconds, 0, 1);
    }

    private void ApplyWaterDuckAdds()
    {
        if (_waterSimulation is null) return;
        bool added = false;
        // Reconstruct the current session's added ducks after a registration or
        // device change. Reset starts at ten again and clears this count.
        while (_waterAppliedDuckCount < _boardSession.WaterGardenAddedDuckCount)
        {
            if (!_waterSimulation.AddDuck()) break;
            _waterAppliedDuckCount++;
            added = true;
        }
        if (added)
        {
            _waterVisualRevision++;
            _waterRenderedFrame = null;
        }
    }

    private static readonly Color WaterInk = Color.FromArgb(255, 30, 49, 45);
    private static readonly Color WaterStone = Color.FromArgb(255, 211, 215, 196);

    private void DrawWaterGardenControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> feedback, DateTimeOffset now)
    {
        var drawerButtons = buttons.Where(button => !IsBoardDrawerHandle(button)).ToArray();
        if (_boardSession.WaterGardenDrawerOpen && drawerButtons.Length > 0)
        {
            using var clip = CanvasGeometry.CreateRectangle(ds.Device,
                new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
            using var layer = ds.CreateLayer(1, clip);
            var previous = ds.Transform;
            float rowTop = (float)drawerButtons.Min(button => button.Bounds.Y) * BoardSurfaceSize;
            float progress = (float)_boardSession.GetWaterGardenDrawerProgress(now);
            ds.Transform = Matrix3x2.CreateTranslation(0, BoardDrawerSlide(progress, rowTop)) * previous;
            try
            {
                foreach (var button in drawerButtons)
                    DrawWaterGardenButton(ds, button, hovered.Contains(button.Id), feedback);
            }
            finally { ds.Transform = previous; }
        }
        // The fixed handle remains available while its three actions rise.
        foreach (var button in buttons.Where(IsBoardDrawerHandle))
            DrawWaterGardenButton(ds, button, hovered.Contains(button.Id), feedback);
    }

    private void DrawWaterGardenButton(CanvasDrawingSession ds, BoardButton button, bool hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> feedback)
    {
        var b = button.Bounds;
        var rect = new Rect(b.X * BoardSurfaceSize, b.Y * BoardSurfaceSize,
            b.Width * BoardSurfaceSize, b.Height * BoardSurfaceSize);
        float radius = BoardButtonCornerRadius(button);
        ds.FillRoundedRectangle(new Rect(rect.X, rect.Y + 5, rect.Width, rect.Height), radius, radius,
            Color.FromArgb(170, 9, 28, 26));
        ds.FillRoundedRectangle(rect, radius, radius, WaterStone);
        ds.DrawRoundedRectangle(new Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), radius - 2, radius - 2,
            hovered ? Color.FromArgb(255, 151, 174, 134) : Color.FromArgb(255, 239, 239, 221), hovered ? 3 : 2);
        if (IsBoardDrawerHandle(button))
        {
            using var arrow = CanvasGeometry.CreatePolygon(ds.Device, DrawerArrowVertices(button, PaintBoardAspect()));
            ds.FillGeometry(arrow, button.Enabled ? WaterInk : Color.FromArgb(255, 104, 115, 107));
            DrawButtonFingerSelectionFeedback(ds, button, feedback, WaterInk, showCaption: false);
        }
        else
        {
            using var text = WaterGardenButtonTextFormat();
            DrawBoardAspectText(ds, button.Label, WaterGardenButtonTextRectangle(button),
                button.Enabled ? WaterInk : Color.FromArgb(255, 104, 115, 107), text);
            DrawButtonFingerSelectionFeedback(ds, button, feedback, WaterInk);
        }
    }

    private static Rect WaterGardenButtonTextRectangle(BoardButton button)
    {
        var b = button.Bounds;
        return new Rect(b.X * BoardSurfaceSize + 10, b.Y * BoardSurfaceSize + 6,
            b.Width * BoardSurfaceSize - 20, b.Height * BoardSurfaceSize - 18);
    }

    private static CanvasTextFormat WaterGardenButtonTextFormat()
    {
        var format = PaintButtonTextFormat();
        format.FontSize = 28;
        return format;
    }

    private HandTrackingBounds WaterGardenButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        using var format = WaterGardenButtonTextFormat();
        return BoardAspectButtonTextRegion(device, button, format, WaterGardenButtonTextRectangle(button));
    }

    private void DisposeWaterGardenResources()
    {
        DisposeWaterGardenTitle();
        _waterSimulation?.Dispose();
        _waterSimulation = null;
        _waterFrameTarget?.Dispose();
        _waterFrameTarget = null;
        _waterRenderedFrame = null;
        _waterDevice = null;
        _waterAdvancedAt = default;
        _waterAppliedDuckCount = 0;
        ClearWaterStick();
    }
}
