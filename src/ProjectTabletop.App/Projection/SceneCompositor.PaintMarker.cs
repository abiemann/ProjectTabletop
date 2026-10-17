using System.Numerics;
using ProjectTabletop.Calibration;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const double PaintMarkerSpacing = .015;
    private static readonly TimeSpan PaintMarkerPourInterval = TimeSpan.FromMilliseconds(200);
    private readonly Queue<Point2> _paintMarkerPath = new();
    private Point2? _paintMarkerSample;
    private DateTimeOffset _paintMarkerFrameTime, _paintMarkerSeenAt, _paintMarkerResetThrough, _paintMarkerPourAt;
    private Homography? _paintMarkerCameraMap, _paintMarkerSurfaceMap;
    private long _paintMarkerInputRevision;
    private bool _paintMarkerActive;
    private Vector3 _paintMarkerPigment;

    public long PaintMarkerInputRevision
    {
        get { lock (_gate) { SyncPaintMarker(); return _paintMarkerInputRevision; } }
    }

    private void ResetPaintMarker()
    {
        _paintMarkerPath.Clear();
        _paintMarkerSample = null;
        _paintMarkerFrameTime = _paintMarkerSeenAt = _paintMarkerPourAt = default;
        _paintMarkerResetThrough = _paintClock();
        _paintMarkerInputRevision++;
    }

    private void SyncPaintMarker()
    {
        SyncPaintSession();
        bool active = PaintInputReady;
        if (_paintMarkerActive == active && ReferenceEquals(_paintMarkerCameraMap, _boardCameraMap) &&
            ReferenceEquals(_paintMarkerSurfaceMap, _boardSurfaceMap)) return;
        ResetPaintMarker();
        _paintMarkerActive = active;
        _paintMarkerCameraMap = _boardCameraMap;
        _paintMarkerSurfaceMap = _boardSurfaceMap;
    }

    // Diagnostics allow for the short delay between camera capture and display.
    private bool PaintMarkerRecentlySeen => _paintMarkerSeenAt != default &&
        _paintClock() - _paintMarkerSeenAt <= TimeSpan.FromMilliseconds(500);

    /// <summary>Confirmed double-bar centre in camera pixels; radius in board-height units.</summary>
    public bool SetPaintMarkerTip(Point2? cameraPoint, double radiusNormalized, DateTimeOffset sourceTime,
        long? expectedRevision = null)
    {
        lock (_gate)
        {
            SyncPaintMarker();
            var now = _paintClock();
            if (expectedRevision is { } revision && revision != _paintMarkerInputRevision) return false;
            if (sourceTime < _paintMarkerFrameTime) return false;
            if (cameraPoint is not { } camera)
            {
                _paintMarkerPath.Clear();
                _paintMarkerSample = null;
                if (sourceTime <= now) _paintMarkerFrameTime = sourceTime;
                return false;
            }
            if (!_paintMarkerActive || sourceTime <= _paintMarkerResetThrough || sourceTime <= _paintMarkerFrameTime ||
                sourceTime > now + TimeSpan.FromMilliseconds(50) || now - sourceTime > TimeSpan.FromMilliseconds(250) ||
                !double.IsFinite(camera.X) || !double.IsFinite(camera.Y) ||
                !double.IsFinite(radiusNormalized) || radiusNormalized <= 0) return false;

            Point2 point;
            try { point = _boardSurfaceMap!.InverseTransform(_boardCameraMap!.Transform(camera)); }
            catch (InvalidOperationException) { return false; }
            var ignored = PaintIgnoredRegions();
            bool Paintable(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) &&
                p.X is >= .02 and <= .98 && p.Y is >= .02 and <= .98 &&
                !ignored.Any(region => p.X >= region.X && p.X <= region.X + region.Width &&
                    p.Y >= region.Y && p.Y <= region.Y + region.Height);
            bool gap = sourceTime - _paintMarkerFrameTime > TimeSpan.FromMilliseconds(250);
            _paintMarkerFrameTime = sourceTime;
            if (!Paintable(point))
            {
                _paintMarkerPath.Clear();
                _paintMarkerSample = null;
                return false;
            }
            _paintMarkerSeenAt = now;
            NotePaintUserActivity();
            CancelPaintIntroduction();
            double aspect = PaintBoardAspect();
            double Distance(Point2 a, Point2 b) => Math.Sqrt(
                Math.Pow((a.X - b.X) * aspect, 2) + Math.Pow(a.Y - b.Y, 2));
            if (gap || _paintMarkerSample is not { } previous || Distance(previous, point) > .18)
            {
                _paintMarkerPath.Clear();
                _paintMarkerPath.Enqueue(point);
                _paintMarkerSample = point;
                _paintMarkerPourAt = sourceTime;
                _paintMarkerPigment = PaintPigments[(int)(_paintDropCount % PaintPigments.Length)];
                return false;
            }

            // Resample by distance, not frame rate. Hold a short piece of the
            // actual path behind the bars, so turns follow their travelled arc
            // rather than swinging ink around a velocity-offset point.
            double distance = Distance(previous, point);
            int steps = (int)(distance / PaintMarkerSpacing);
            if (steps == 0)
            {
                // Fresh observations keep a held marker pouring at its centre.
                // Consume missed intervals rather than catching up after a stall.
                if (sourceTime - _paintMarkerPourAt < PaintMarkerPourInterval) return false;
                _paintMarkerPourAt = sourceTime;
                return QueuePaintMarkerDrop(point, sourceTime);
            }
            _paintMarkerPourAt = sourceTime;
            int lagSamples = (int)Math.Ceiling(Math.Clamp(radiusNormalized + .012, .03, .09) / PaintMarkerSpacing);
            bool deposited = false;
            for (int i = 1; i <= steps; i++)
            {
                double t = i * PaintMarkerSpacing / distance;
                var sample = new Point2(previous.X + (point.X - previous.X) * t,
                    previous.Y + (point.Y - previous.Y) * t);
                _paintMarkerSample = sample;
                if (!Paintable(sample))
                {
                    _paintMarkerPath.Clear();
                    continue;
                }
                _paintMarkerPath.Enqueue(sample);
                while (_paintMarkerPath.Count > lagSamples)
                {
                    var drop = _paintMarkerPath.Dequeue();
                    deposited |= QueuePaintMarkerDrop(drop, sourceTime);
                }
            }
            return deposited;
        }
    }

    private bool QueuePaintMarkerDrop(Point2 point, DateTimeOffset sourceTime)
    {
        // Each deposit performs three full-field GPU passes. Both movement and
        // stationary pouring share the two-drop limit and discard excess input.
        if (_paintPendingDrops.Count >= 2) return false;
        QueuePaintDrop(new(point, .023f, _paintMarkerPigment,
            (int)((_paintDropCount + 1) % int.MaxValue)));
        _paintLastDropAt = sourceTime;
        return true;
    }

#if DEBUG
    internal (Point2 Center, float Radius)[] GetPendingPaintMarkerDropsForVerification()
    {
        lock (_gate) return _paintPendingDrops.Select(drop => (drop.Center, drop.Radius)).ToArray();
    }
#endif
}
