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
    private sealed class GlobeRailInput
    {
        public PatternRailGeometry? Geometry;
        public DateTimeOffset SourceTime;
    }
    private readonly GlobeRailInput _globePitchRail = new(), _globeZoomRail = new();
    private long _globePitchEpoch;
    private DateTimeOffset _globePitchResetThrough;
    private const float GlobePitchPanelCornerRadius = 12;

    internal Rect GlobePitchRailBounds() => GlobeRailBounds(zoom: false);
    internal Rect GlobeZoomRailBounds() => GlobeRailBounds(zoom: true);

    private Rect GlobeRailBounds(bool zoom)
    {
        const double height = .64;
        // Round dots in physical board space, above both the swipe strip and
        // NASA credit. The stationary black backing excludes moving stars.
        double width = height * PatternRailGeometry.ReferenceHeight / PatternRailGeometry.ReferenceWidth / PaintBoardAspect();
        return new(zoom ? .032 : .968 - width, .16, width, height);
    }

    private Rect GlobePitchPanelBounds() => GlobeRailPanelBounds(zoom: false);

    private Rect GlobeRailPanelBounds(bool zoom)
    {
        var rail = GlobeRailBounds(zoom);
        double margin = .022 / PaintBoardAspect();
        return new(rail.X - margin, rail.Y - .036,
            rail.Width + 2 * margin, rail.Height + .044);
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
        _globePitchRail.Geometry = _globeZoomRail.Geometry = null;
        _globePitchResetThrough = _globePitchRail.SourceTime = _globeZoomRail.SourceTime = _globeClock();
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
        DateTimeOffset? visibleThrough = null) => GetGlobeRailGeometry(false, frameTime, visibleThrough);

    public PatternRailGeometry? GetGlobeZoomRailGeometry(DateTimeOffset? frameTime = null,
        DateTimeOffset? visibleThrough = null) => GetGlobeRailGeometry(true, frameTime, visibleThrough);

    private PatternRailGeometry? GetGlobeRailGeometry(bool zoom, DateTimeOffset? frameTime,
        DateTimeOffset? visibleThrough)
    {
        lock (_gate)
        {
            if (!SyncGlobePitchRail()) return null;
            var input = zoom ? _globeZoomRail : _globePitchRail;
            if (frameTime is { } sourceTime && (!GlobeRailFrameCurrent(input, sourceTime) ||
                (visibleThrough ?? sourceTime) <= _globePitchResetThrough)) return null;
            if (input.Geometry is not null) return input.Geometry;
            var rail = GlobeRailBounds(zoom);
            try
            {
                // Both canonical references run upward from minimum to maximum.
                var corners = new[] { new Point2(rail.X, rail.Bottom), new Point2(rail.X, rail.Y),
                    new Point2(rail.Right, rail.Y), new Point2(rail.Right, rail.Bottom) }
                    .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                    .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
                return input.Geometry = new(_globePitchEpoch, Array.AsReadOnly(corners));
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    public bool SetGlobePitchRailPosition(float? position, DateTimeOffset frameTime, long epoch)
        => SetGlobeRailPosition(false, position, frameTime, epoch);

    public bool SetGlobeZoomRailPosition(float? position, DateTimeOffset frameTime, long epoch)
        => SetGlobeRailPosition(true, position, frameTime, epoch);

    private bool SetGlobeRailPosition(bool zoom, float? position, DateTimeOffset frameTime, long epoch)
    {
        lock (_gate)
        {
            var input = zoom ? _globeZoomRail : _globePitchRail;
            if (!SyncGlobePitchRail() || epoch != _globePitchEpoch || !GlobeRailFrameCurrent(input, frameTime) ||
                position is { } invalid && (!float.IsFinite(invalid) || invalid is < 0 or > 1)) return false;
            input.SourceTime = frameTime;
            // Independent source times let both rails accept the same camera
            // frame. Lift/ambiguous contact freezes only its own chosen value.
            // A little end room
            // lets a fingertip covering several dots reach the exact limits.
            return position is not { } value ||
                (zoom ? _boardSession.SetGlobeZoomPosition(Math.Clamp((value - .04) / .92, 0, 1), frameTime)
                    : _boardSession.SetGlobePitchPosition(Math.Clamp((value - .04) / .92, 0, 1), frameTime));
        }
    }

    private bool GlobeRailFrameCurrent(GlobeRailInput input, DateTimeOffset frameTime)
    {
        var now = _globeClock();
        return frameTime > _globePitchResetThrough && frameTime > input.SourceTime &&
            frameTime <= now && now - frameTime <= TimeSpan.FromMilliseconds(250) &&
            _boardSession.GlobePitchInputReady(frameTime);
    }

    private void DrawGlobeRail(CanvasDrawingSession drawing, float drawerProgress, bool zoom, double position)
    {
        var rail = GlobeRailBounds(zoom);
        var panel = GlobeRailPanelBounds(zoom);
        double aspect = PaintBoardAspect();
        using var entrance = drawerProgress < 1 ? drawing.CreateLayer(drawerProgress) : null;
        drawing.FillRoundedRectangle(new Rect(panel.X * BoardSurfaceSize, panel.Y * BoardSurfaceSize,
            panel.Width * BoardSurfaceSize, panel.Height * BoardSurfaceSize),
            (float)(GlobePitchPanelCornerRadius / aspect), GlobePitchPanelCornerRadius, ThemeColor(0, 0, 0));
        // Match Roulette's RED caption, including its physical aspect correction.
        RouletteText(drawing, zoom ? "ZOOM" : "PITCH", new Rect(rail.X * BoardSurfaceSize,
            (rail.Y - .028) * BoardSurfaceSize, rail.Width * BoardSurfaceSize, 20),
            13, ThemeColor(255, 255, 255), aspect);

        // The pointer stays outside the stationary optical reference and follows
        // the rendered value, including its easing and retained release value.
        float y = (float)((rail.Bottom - rail.Height * Math.Clamp(position, 0, 1)) * BoardSurfaceSize);
        float tipX = (float)((zoom ? rail.Right : rail.X) * BoardSurfaceSize + (zoom ? 4 : -4) / aspect);
        float backX = tipX + (float)((zoom ? 8 : -8) / aspect);
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
