using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection.StrykersRun;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _strykersRunClock;
    private StrykersRunRenderer? _strykersRunRenderer;
    private CanvasDevice? _strykersRunDevice;
    private CanvasRenderTarget? _strykersRunThumbnail;
    private Homography? _strykersRunCameraMap, _strykersRunSurfaceMap;
    private long _strykersRunNavigation = -1, _strykersRunEpoch;
    private long _strykersRunSessionInputRevision = -1;
    private bool _strykersRunActive, _strykersRunArtworkPublished;
    private PatternRailGeometry? _strykersRunRail;
    private DateTimeOffset _strykersRunResetThrough, _strykersRunSourceTime;
    private readonly DateTimeOffset _strykersRunAnimationOrigin;

    private bool StrykersRunArtworkReady => _strykersRunRenderer?.Ready == true;
    public StrykersRunSnapshot StrykersRunState { get { lock (_gate) return _boardSession.StrykersRunState; } }
    public double StrykersRunPreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }
    public long StrykersRunInputRevision { get { lock (_gate) { SyncStrykersRunSession(); return _strykersRunEpoch; } } }
    public bool StrykersRunInputActive { get { lock (_gate) { SyncStrykersRunSession(); return _strykersRunActive; } } }
    public double StrykersRunRailProgress { get { lock (_gate) return _boardSession.StrykersRunRailProgress; } }
    public bool StrykersRunRailAnimating { get { lock (_gate) return _boardSession.StrykersRunRailAnimating; } }

    public bool HandDetectionRequired
    {
        get
        {
            lock (_gate) return _boardSession.Screen != BoardScreen.StrykersRun ||
                _boardSession.StrykersRunControlsVisible && !_boardSession.StrykersRunControlsAnimating &&
                AcquisitionBoardReady;
        }
    }

    public void ShowStrykersRun()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowStrykersRun(_strykersRunClock());
            SyncPhotoCopySession();
            SyncStrykersRunSession();
        }
    }

    public bool ActivateStrykersRunButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.StrykersRun ||
                !_boardSession.ActivateButton(id, _strykersRunClock())) return false;
            ClearStrykersRunObservations();
            SyncStrykersRunSession();
            return true;
        }
    }

    public bool ActivateStrykersRunAt(double u, double v)
    {
        lock (_gate)
        {
            var button = _boardSession.Buttons.FirstOrDefault(button => button.Enabled && button.Bounds.Contains(u, v));
            return button is not null && ActivateStrykersRunButton(button.Id);
        }
    }

    private void ClearStrykersRunObservations()
    {
        _strykersRunEpoch++;
        _strykersRunRail = null;
        _strykersRunResetThrough = _strykersRunClock();
        _strykersRunSourceTime = _strykersRunResetThrough;
        _boardSession.ClearStrykersRunInput(_strykersRunResetThrough);
        _strykersRunSessionInputRevision = _boardSession.StrykersRunInputRevision;
        // Shared hand resets also clear this session. Re-render only this board's
        // surface; other boards keep their rendered texture and camera references.
        if (_boardSession.Screen == BoardScreen.StrykersRun) _renderedBoardState = null;
    }

    private void SyncStrykersRunSession()
    {
        bool active = !_disposed && _boardSession.Screen == BoardScreen.StrykersRun &&
            _boardSession.StrykersRunPhase is StrykersRunPhase.WaitingForInput or StrykersRunPhase.Playing &&
            !_boardSession.StrykersRunRailAnimating && _boardSession.StrykersRunRailProgress == 1 &&
            StrykersRunArtworkReady &&
            !_blackOutput && !_boardSetup && _calibrationTarget < 0 && !IsBoardRevealActive &&
            _boardCameraMap is not null && _boardSurfaceMap is not null;
        if (active == _strykersRunActive && _strykersRunNavigation == _boardSession.NavigationRevision &&
            _strykersRunSessionInputRevision == _boardSession.StrykersRunInputRevision &&
            ReferenceEquals(_strykersRunCameraMap, _boardCameraMap) &&
            ReferenceEquals(_strykersRunSurfaceMap, _boardSurfaceMap)) return;
        _strykersRunActive = active;
        _strykersRunNavigation = _boardSession.NavigationRevision;
        _strykersRunCameraMap = _boardCameraMap;
        _strykersRunSurfaceMap = _boardSurfaceMap;
        ClearStrykersRunObservations();
    }

    internal Rect StrykersRunFieldBounds()
    {
        const double portraitAspect = 9d / 16, available = .98;
        double aspect = PaintBoardAspect();
        double width = Math.Min(available, available * portraitAspect / aspect);
        double height = width * aspect / portraitAspect;
        return new((1 - width) / 2, (1 - height) / 2, width, height);
    }

    internal Rect StrykersRunRailBounds()
    {
        var field = StrykersRunFieldBounds();
        return new(field.X + field.Width * .08, field.Y + field.Height * .930,
            field.Width * .84, field.Height * .04);
    }

    public PatternRailGeometry? GetStrykersRunRailGeometry(DateTimeOffset? frameTime = null,
        DateTimeOffset? visibleThrough = null)
    {
        lock (_gate)
        {
            SyncStrykersRunSession();
            if (!_strykersRunActive) return null;
            // A queued camera frame can predate the settled rail even when the
            // current scene is ready. Do not let it seed optical registration.
            if (frameTime is { } sourceTime && (!StrykersRunFrameCurrent(sourceTime) ||
                (visibleThrough ?? sourceTime) <= _strykersRunResetThrough)) return null;
            if (_strykersRunRail is not null) return _strykersRunRail;
            var rail = StrykersRunRailBounds();
            try
            {
                var corners = new[] { new Point2(rail.X, rail.Y), new Point2(rail.Right, rail.Y),
                    new Point2(rail.Right, rail.Bottom), new Point2(rail.X, rail.Bottom) }
                    .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                    .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
                return _strykersRunRail = new(_strykersRunEpoch, Array.AsReadOnly(corners));
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    public bool SetStrykersRunRailPosition(float? position, DateTimeOffset frameTime, long epoch)
    {
        lock (_gate)
        {
            SyncStrykersRunSession();
            if (!_strykersRunActive || epoch != _strykersRunEpoch || !StrykersRunFrameCurrent(frameTime)) return false;
            _strykersRunSourceTime = frameTime;
            _boardSession.SetStrykersRunInput(position is { } x && float.IsFinite(x) && x is >= 0 and <= 1 ? x : null, frameTime);
            return true;
        }
    }

    private bool StrykersRunFrameCurrent(DateTimeOffset frameTime)
    {
        var now = _strykersRunClock();
        return frameTime > _strykersRunResetThrough && frameTime > _strykersRunSourceTime &&
            frameTime <= now && now - frameTime <= StrykersRunGame.InputFreshness;
    }

    public void ClearStrykersRunInput()
    {
        lock (_gate) ClearStrykersRunObservations();
    }

    private long StrykersRunVisualRevision()
    {
        var now = _strykersRunClock();
        _boardSession.TickStrykersRun(now);
        SyncStrykersRunSession();
        // Idle scenery and the falling wall remain animated independently of input.
        long frame = (long)Math.Floor(Math.Max(0, (now - _strykersRunAnimationOrigin).TotalSeconds) * 60);
        return unchecked(_boardSession.StrykersRunState.Revision * 1_000_003 + frame);
    }

    internal async Task EnsureStrykersRunResourcesAsync(CanvasDevice device)
    {
        Task loading;
        lock (_gate) loading = GetStrykersRunRenderer(device).EnsureReadyAsync();
        await loading.ConfigureAwait(false);
        lock (_gate)
            if (!_disposed && _strykersRunDevice == device) PrepareStrykersRunResources(device);
    }

    private bool PrepareStrykersRunResources(CanvasDevice device)
    {
        var renderer = GetStrykersRunRenderer(device);
        _ = renderer.EnsureReadyAsync();
        if (!renderer.Ready) return false;
        if (!_strykersRunArtworkPublished)
        {
            _strykersRunArtworkPublished = true;
            if (_boardSession.Screen == BoardScreen.StrykersRun) InvalidateBoardArtworkSurface();
        }
        return true;
    }

    private StrykersRunRenderer GetStrykersRunRenderer(CanvasDevice device)
    {
        if (_strykersRunRenderer is null || _strykersRunDevice != device)
        {
            DisposeStrykersRunResources();
            _strykersRunDevice = device;
            _strykersRunRenderer = new(device);
        }
        return _strykersRunRenderer;
    }

    private void DrawStrykersRunSurface(CanvasDrawingSession drawing)
    {
        drawing.FillRectangle(new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize), Color.FromArgb(255, 7, 10, 17));
        var field = StrykersRunFieldBounds();
        float aspect = (float)PaintBoardAspect();
        var previous = drawing.Transform;
        drawing.Transform = Matrix3x2.CreateScale(1 / aspect, 1) * previous;
        try
        {
            GetStrykersRunRenderer(drawing.Device).Draw(drawing,
                new Rect(field.X * BoardSurfaceSize * aspect, field.Y * BoardSurfaceSize,
                    field.Width * BoardSurfaceSize * aspect, field.Height * BoardSurfaceSize),
                _boardSession.StrykersRunState, Math.Max(0, (_strykersRunClock() - _strykersRunAnimationOrigin).TotalSeconds));
        }
        finally { drawing.Transform = previous; }
        DrawStrykersRunRail(drawing);
    }

    private void DrawStrykersRunRail(CanvasDrawingSession drawing)
    {
        double progress = _boardSession.StrykersRunRailProgress;
        if (progress <= 0) return;
        var rail = StrykersRunRailBounds();
        double top = rail.Y + (1 - progress) * (1 - rail.Y);
        using var clip = drawing.CreateLayer(1, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
        var rectangle = new Rect(rail.X * BoardSurfaceSize, top * BoardSurfaceSize,
            rail.Width * BoardSurfaceSize, rail.Height * BoardSurfaceSize);
        drawing.FillRectangle(rectangle, Color.FromArgb(255, 0, 0, 0));
        float radiusX = (float)rectangle.Width * PatternRailGeometry.DotRadius / PatternRailGeometry.ReferenceWidth;
        float radiusY = (float)rectangle.Height * PatternRailGeometry.DotRadius / PatternRailGeometry.ReferenceHeight;
        for (int column = 0; column < PatternRailGeometry.Columns; column++)
        {
            float x = (float)(rectangle.X + rectangle.Width * (column * PatternRailGeometry.CellWidth +
                PatternRailGeometry.CellWidth / 2d) / PatternRailGeometry.ReferenceWidth);
            drawing.FillEllipse(new(x, (float)(rectangle.Y + rectangle.Height * PatternRailGeometry.DotY1ForColumn(column) /
                PatternRailGeometry.ReferenceHeight)), radiusX, radiusY, Color.FromArgb(255, 255, 255, 255));
            drawing.FillEllipse(new(x, (float)(rectangle.Y + rectangle.Height * PatternRailGeometry.DotY2ForColumn(column) /
                PatternRailGeometry.ReferenceHeight)), radiusX, radiusY, Color.FromArgb(255, 255, 255, 255));
        }
    }

    private void DrawStrykersRunControls(CanvasDrawingSession drawing)
    {
        // Shared Football controls keep their exact geometry and fade in place.
        // Their captions are available to the camera only after the fade settles.
        float opacity = (float)_boardSession.StrykersRunControlsProgress;
        if (opacity <= 0) return;
        using var fade = drawing.CreateLayer(opacity);
        DrawFieldGameControls(drawing);
    }

    public void DrawStrykersRunPreview(CanvasDrawingSession drawing, float width, float height)
    {
        drawing.Clear(Color.FromArgb(255, 7, 10, 17));
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.StrykersRun) return;
            if (!PrepareBoardArtwork(drawing.Device))
            {
                DrawArtworkLoading(drawing, new Rect(0, 0, width, height));
                return;
            }
            StrykersRunVisualRevision();
            double aspect = PaintBoardAspect();
            float drawWidth = (float)Math.Min(width, height * aspect), drawHeight = (float)(drawWidth / aspect);
            var previous = drawing.Transform;
            drawing.Transform = Matrix3x2.CreateScale(drawWidth / BoardSurfaceSize, drawHeight / BoardSurfaceSize) *
                Matrix3x2.CreateTranslation((width - drawWidth) / 2, (height - drawHeight) / 2) * previous;
            try { DrawStrykersRunSurface(drawing); DrawStrykersRunControls(drawing); }
            finally { drawing.Transform = previous; }
        }
    }

    private CanvasBitmap? StrykersRunMenuThumbnail(CanvasDevice device)
    {
        var renderer = GetStrykersRunRenderer(device);
        if (!renderer.Ready) return null;
        if (_strykersRunThumbnail is null)
        {
            _strykersRunThumbnail = new(device, 1200, 600, 96);
            using var drawing = _strykersRunThumbnail.CreateDrawingSession();
            renderer.DrawThumbnail(drawing, new Rect(0, 0, 1200, 600));
        }
        return _strykersRunThumbnail;
    }

    private void DisposeStrykersRunResources()
    {
        _strykersRunThumbnail?.Dispose();
        _strykersRunThumbnail = null;
        _strykersRunRenderer?.Dispose();
        _strykersRunRenderer = null;
        _strykersRunDevice = null;
        _strykersRunArtworkPublished = false;
    }
}
