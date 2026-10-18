using Microsoft.Graphics.Canvas;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Pooled capture target. A capture takes ownership while it reads pixels
    // outside _gate, so this is null until that capture returns it.
    private CanvasRenderTarget? _markerProjectionTarget;
    private readonly Queue<MarkerProjectionFrame> _markerProjectionFrames = new();
    private Homography? _markerProjectionCameraMap, _markerProjectionSurfaceMap;
    private long _markerProjectionNavigation = -1, _markerProjectionHistory;
    private DateTimeOffset _markerProjectionCapturedAt;

    // Separate from stationary caption references: this image explains our own
    // projected artwork to the marker detector, and can never qualify a held button.
    internal IReadOnlyList<MarkerProjectionFrame> GetMarkerProjectionFrames()
    {
        CanvasRenderTarget capture;
        CanvasRenderTarget source;
        Homography cameraMap, surfaceMap;
        BoardScreen screen;
        double[] mapping;
        DateTimeOffset now;
        long history, navigation;
        int width, height;
        lock (_gate)
        {
            if (_disposed || _blackOutput || _boardSetup || _calibrationTarget >= 0 || IsBoardRevealActive ||
                _boardCameraMap is null || _boardSurfaceMap is null || _boardApplicationTarget is null ||
                _renderedBoardState is not { } rendered || rendered.Screen != _boardSession.Screen ||
                _boardSession.Screen == BoardScreen.Media)
            {
                ClearMarkerProjectionHistory();
                return [];
            }
            if (!ReferenceEquals(_markerProjectionCameraMap, _boardCameraMap) ||
                !ReferenceEquals(_markerProjectionSurfaceMap, _boardSurfaceMap) ||
                _markerProjectionNavigation != _boardSession.NavigationRevision)
            {
                ClearMarkerProjectionHistory();
                _markerProjectionCameraMap = _boardCameraMap;
                _markerProjectionSurfaceMap = _boardSurfaceMap;
                _markerProjectionNavigation = _boardSession.NavigationRevision;
            }
            now = MonotonicClock.UtcNow;
            if (now - _markerProjectionCapturedAt < TimeSpan.FromMilliseconds(150))
                return _markerProjectionFrames.ToArray();
            source = _boardApplicationTarget;
            cameraMap = _boardCameraMap;
            surfaceMap = _boardSurfaceMap;
            screen = _boardSession.Screen;
            navigation = _boardSession.NavigationRevision;
            var size = source.SizeInPixels;
            (width, height) = MarkerProjectionSize(cameraMap, surfaceMap, (int)size.Width, (int)size.Height);
            if (_markerProjectionTarget is { } pooled && pooled.Device == source.Device &&
                pooled.SizeInPixels.Width == width && pooled.SizeInPixels.Height == height)
                capture = pooled;
            else
            {
                _markerProjectionTarget?.Dispose();
                capture = new(source.Device, width, height, 96);
            }
            _markerProjectionTarget = null;
            try
            {
                using (var drawing = capture.CreateDrawingSession())
                    // A large reduction needs a prefiltered downscale; linear sampling
                    // would skip source pixels and break up thin lines.
                    drawing.DrawImage(source, new Rect(0, 0, width, height), new Rect(0, 0, size.Width, size.Height),
                        1, CanvasImageInterpolation.HighQualityCubic, CanvasComposite.Copy);
            }
            catch
            {
                capture.Dispose();
                throw;
            }
            var camera = _boardCameraMap.ToMatrix();
            var surface = _boardSurfaceMap.Inverse().ToMatrix();
            mapping = new double[9];
            for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            for (int middle = 0; middle < 3; middle++)
                mapping[row * 3 + column] += surface[row * 3 + middle] * camera[middle * 3 + column];
            _markerProjectionCapturedAt = now;
            history = _markerProjectionHistory;
        }

        // The readback waits for the GPU. Keep the render loop's lock free
        // meanwhile; the copy above already fixed this snapshot's content.
        byte[] pixels;
        try { pixels = capture.GetPixelBytes(); }
        catch
        {
            lock (_gate) ReturnMarkerProjectionTarget(capture);
            throw;
        }
        lock (_gate)
        {
            ReturnMarkerProjectionTarget(capture);
            // A reset, new registration or navigation during the readback
            // makes this snapshot describe a board that is no longer shown.
            // Another reference request is not guaranteed during that change,
            // so checking the history counter alone cannot detect it.
            if (_disposed || _blackOutput || _boardSetup || _calibrationTarget >= 0 || IsBoardRevealActive ||
                !ReferenceEquals(cameraMap, _boardCameraMap) || !ReferenceEquals(surfaceMap, _boardSurfaceMap) ||
                !ReferenceEquals(source, _boardApplicationTarget) || navigation != _boardSession.NavigationRevision ||
                screen != _boardSession.Screen || _renderedBoardState is not { } current || current.Screen != screen)
            {
                if (history == _markerProjectionHistory) ClearMarkerProjectionHistory();
                return [];
            }
            if (history != _markerProjectionHistory) return [];
            if (_markerProjectionFrames.Any(frame => frame.Width != width || frame.Height != height))
                _markerProjectionFrames.Clear();
            _markerProjectionFrames.Enqueue(new(width, height, pixels, mapping, now));
            while (_markerProjectionFrames.Count > 3) _markerProjectionFrames.Dequeue();
            return _markerProjectionFrames.ToArray();
        }
    }

    // Tip detectors compare this image with the camera's own view of the board, one
    // sample per camera pixel. Match that resolution along each board axis: extra
    // pixels were skipped by the comparison, yet each capture paid for reading
    // them back from the GPU and converting them on the detection thread.
    private static (int Width, int Height) MarkerProjectionSize(Homography cameraMap, Homography surfaceMap,
        int sourceWidth, int sourceHeight)
    {
        try
        {
            Point2 Camera(double u, double v) => cameraMap.InverseTransform(surfaceMap.Transform(new Point2(u, v)));
            static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            Point2[] corners = [Camera(0, 0), Camera(1, 0), Camera(1, 1), Camera(0, 1)];
            // Use the longer of opposite edges so the nearer side of an oblique view keeps its detail.
            double across = Math.Max(Distance(corners[0], corners[1]), Distance(corners[3], corners[2]));
            double down = Math.Max(Distance(corners[0], corners[3]), Distance(corners[1], corners[2]));
            if (double.IsFinite(across) && double.IsFinite(down) && across >= 1 && down >= 1)
                return (Fit(across, sourceWidth), Fit(down, sourceHeight));
        }
        catch (InvalidOperationException) { }
        double scale = Math.Min(1, 2048.0 / Math.Max(sourceWidth, sourceHeight));
        return (Math.Max(1, (int)Math.Round(sourceWidth * scale)), Math.Max(1, (int)Math.Round(sourceHeight * scale)));

        static int Fit(double length, int sourceLength) =>
            Math.Max(1, Math.Min(Math.Min(2048, sourceLength), (int)Math.Ceiling(length)));
    }

    private void ReturnMarkerProjectionTarget(CanvasRenderTarget capture)
    {
        if (_disposed || _markerProjectionTarget is not null) capture.Dispose();
        else _markerProjectionTarget = capture;
    }

    private void ClearMarkerProjectionHistory()
    {
        _markerProjectionFrames.Clear();
        _markerProjectionCapturedAt = default;
        _markerProjectionCameraMap = _markerProjectionSurfaceMap = null;
        _markerProjectionNavigation = -1;
        _markerProjectionHistory++;
    }

    private void DisposeMarkerProjectionReference()
    {
        ClearMarkerProjectionHistory();
        _markerProjectionTarget?.Dispose();
        _markerProjectionTarget = null;
    }
}
