using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelPoint[]? _paintMarkerCameraPolygon;
    private Homography? _paintMarkerPolygonCameraMap, _paintMarkerPolygonSurfaceMap;

    // Both wand boards use the same camera-plane detector and saved marker.
    // Keep the polygon immutable while registration is unchanged.
    public IReadOnlyList<PixelPoint>? GetWandMarkerCameraPolygon()
    {
        lock (_gate)
        {
            if (_boardSession.Screen == BoardScreen.WaterGarden) return GetWaterMarkerCameraPolygon();
            if (!PaintInputReady || _markerSetup is not null) return null;
            if (_paintMarkerCameraPolygon is not null &&
                ReferenceEquals(_paintMarkerPolygonCameraMap, _boardCameraMap) &&
                ReferenceEquals(_paintMarkerPolygonSurfaceMap, _boardSurfaceMap)) return _paintMarkerCameraPolygon;
            try
            {
                _paintMarkerCameraPolygon = new[] { new Point2(0, 0), new Point2(1, 0), new Point2(1, 1), new Point2(0, 1) }
                    .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                    .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
                _paintMarkerPolygonCameraMap = _boardCameraMap;
                _paintMarkerPolygonSurfaceMap = _boardSurfaceMap;
                return _paintMarkerCameraPolygon;
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    public bool TryMapWandMarkerCameraPoint(PixelPoint camera, out PixelPoint board)
    {
        lock (_gate)
        {
            if (_boardSession.Screen == BoardScreen.WaterGarden) return TryMapWaterMarkerCameraPoint(camera, out board);
            board = default;
            if (!PaintInputReady || _markerSetup is not null || !double.IsFinite(camera.X) || !double.IsFinite(camera.Y)) return false;
            try
            {
                var point = _boardSurfaceMap!.InverseTransform(_boardCameraMap!.Transform(new(camera.X, camera.Y)));
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X is < 0 or > 1 || point.Y is < 0 or > 1)
                    return false;
                board = new(point.X, point.Y);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    // Radius is in board-height units, like Paint's aspect-corrected distances.
    // A conservative footprint keeps fresh ink behind the physical marker.
    public double GetPaintMarkerRadius(PixelPoint camera, double radiusPixels)
    {
        lock (_gate)
        {
            if (!PaintInputReady || !double.IsFinite(radiusPixels) || radiusPixels <= 0) return 0;
            try
            {
                Point2 Map(double x, double y) => _boardSurfaceMap!.InverseTransform(_boardCameraMap!.Transform(new(x, y)));
                var centre = Map(camera.X, camera.Y);
                var horizontal = Map(camera.X + radiusPixels, camera.Y);
                var vertical = Map(camera.X, camera.Y + radiusPixels);
                double aspect = PaintBoardAspect();
                double Distance(Point2 point) => Math.Sqrt(Math.Pow((point.X - centre.X) * aspect, 2) +
                    Math.Pow(point.Y - centre.Y, 2));
                double radius = Math.Max(Distance(horizontal), Distance(vertical));
                return double.IsFinite(radius) ? radius : 0;
            }
            catch (InvalidOperationException) { return 0; }
        }
    }
}
