using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Calibration;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly record struct GlobePitchRailKey(long Navigation, DateTimeOffset VisibleAfter,
        DateTimeOffset InputReset, Homography? Camera, Homography? Surface, double Aspect, bool Active);
    private GlobePitchRailKey? _globePitchRailKey;
    private PatternRailGeometry? _globePitchRail;
    private long _globePitchEpoch;
    private DateTimeOffset _globePitchResetThrough, _globePitchSourceTime;
    private const float GlobePitchPanelCornerRadius = 12;

    internal Rect GlobePitchRailBounds()
    {
        const double height = .64;
        // Round dots in physical board space, above both the swipe strip and
        // NASA credit. The stationary black backing excludes moving stars.
        double width = height * PatternRailGeometry.ReferenceHeight / PatternRailGeometry.ReferenceWidth / PaintBoardAspect();
        return new(.968 - width, .16, width, height);
    }

    private Rect GlobePitchPanelBounds()
    {
        var rail = GlobePitchRailBounds();
        double aspect = PaintBoardAspect();
        return new(rail.X - .022 / aspect, rail.Y - .036,
            rail.Width + .030 / aspect, rail.Height + .044);
    }

    private bool SyncGlobePitchRail()
    {
        var now = _globeClock();
        _boardSession.TickGlobe(now);
        _boardSession.TickControlVisibility(now);
        bool active = MappedBoardInputReady && _boardSession.GlobePitchAvailable(now);
        var key = new GlobePitchRailKey(_boardSession.NavigationRevision, _boardSession.GlobePitchReferenceNotBefore,
            _handVisualResetThrough, _boardCameraMap, _boardSurfaceMap, PaintBoardAspect(), active);
        if (_globePitchRailKey != key)
        {
            _globePitchRailKey = key;
            ClearGlobePitchObservations();
        }
        return active;
    }

    private void ClearGlobePitchObservations()
    {
        _globePitchEpoch++;
        _globePitchRail = null;
        _globePitchResetThrough = _globePitchSourceTime = _globeClock();
    }

    public void ClearGlobePitchInput()
    {
        lock (_gate)
        {
            _globePitchRailKey = null;
            ClearGlobePitchObservations();
        }
    }

    public PatternRailGeometry? GetGlobePitchRailGeometry(DateTimeOffset? frameTime = null,
        DateTimeOffset? visibleThrough = null)
    {
        lock (_gate)
        {
            if (!SyncGlobePitchRail()) return null;
            if (frameTime is { } sourceTime && (!GlobePitchFrameCurrent(sourceTime) ||
                (visibleThrough ?? sourceTime) <= _globePitchResetThrough)) return null;
            if (_globePitchRail is not null) return _globePitchRail;
            var rail = GlobePitchRailBounds();
            try
            {
                // Canonical progress runs upward: bottom is minimum pitch.
                var corners = new[] { new Point2(rail.X, rail.Bottom), new Point2(rail.X, rail.Y),
                    new Point2(rail.Right, rail.Y), new Point2(rail.Right, rail.Bottom) }
                    .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                    .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
                return _globePitchRail = new(_globePitchEpoch, Array.AsReadOnly(corners));
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    public bool SetGlobePitchRailPosition(float? position, DateTimeOffset frameTime, long epoch)
    {
        lock (_gate)
        {
            if (!SyncGlobePitchRail() || epoch != _globePitchEpoch || !GlobePitchFrameCurrent(frameTime) ||
                position is { } invalid && (!float.IsFinite(invalid) || invalid is < 0 or > 1)) return false;
            _globePitchSourceTime = frameTime;
            // Lift/ambiguous contact freezes the chosen tilt. A little end room
            // lets a fingertip covering several dots reach the exact limits.
            return position is not { } value ||
                _boardSession.SetGlobePitchPosition(Math.Clamp((value - .04) / .92, 0, 1), frameTime);
        }
    }

    private bool GlobePitchFrameCurrent(DateTimeOffset frameTime)
    {
        var now = _globeClock();
        return frameTime > _globePitchResetThrough && frameTime > _globePitchSourceTime &&
            frameTime <= now && now - frameTime <= TimeSpan.FromMilliseconds(250) &&
            _boardSession.GlobePitchInputReady(frameTime);
    }

    private void DrawGlobePitchRail(CanvasDrawingSession drawing, float drawerProgress, double latitude)
    {
        var rail = GlobePitchRailBounds();
        var panel = GlobePitchPanelBounds();
        double aspect = PaintBoardAspect();
        using var entrance = drawerProgress < 1 ? drawing.CreateLayer(drawerProgress) : null;
        drawing.FillRoundedRectangle(new Rect(panel.X * BoardSurfaceSize, panel.Y * BoardSurfaceSize,
            panel.Width * BoardSurfaceSize, panel.Height * BoardSurfaceSize),
            (float)(GlobePitchPanelCornerRadius / aspect), GlobePitchPanelCornerRadius, ThemeColor(0, 0, 0));
        // Match Roulette's RED caption, including its physical aspect correction.
        RouletteText(drawing, "PITCH", new Rect(rail.X * BoardSurfaceSize,
            (rail.Y - .028) * BoardSurfaceSize, rail.Width * BoardSurfaceSize, 20),
            13, ThemeColor(255, 255, 255), aspect);

        // The pointer stays outside the stationary optical reference and follows
        // the rendered latitude, including its easing and retained release value.
        double position = Math.Clamp((latitude / ProjectTabletop.Interaction.GlobeState.MaximumViewLatitude + 1) / 2, 0, 1);
        float y = (float)((rail.Bottom - rail.Height * position) * BoardSurfaceSize);
        float tipX = (float)(rail.X * BoardSurfaceSize - 4 / aspect);
        float backX = tipX - (float)(8 / aspect);
        using var pointer = CanvasGeometry.CreatePolygon(drawing.Device,
            [new(backX, y - 5), new(tipX, y), new(backX, y + 5)]);
        drawing.FillGeometry(pointer, ThemeColor(255, 255, 255));
        var previous = drawing.Transform;
        drawing.Transform = Matrix3x2.CreateRotation(-MathF.PI / 2) *
            Matrix3x2.CreateTranslation((float)rail.X * BoardSurfaceSize, (float)rail.Bottom * BoardSurfaceSize) * previous;
        try { DrawPatternRail(drawing, new Rect(0, 0, rail.Height * BoardSurfaceSize, rail.Width * BoardSurfaceSize)); }
        finally { drawing.Transform = previous; }
    }
}
