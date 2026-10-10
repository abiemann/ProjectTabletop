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
    private CanvasRenderTarget? _eyeProjectionTarget;
    private readonly Queue<EyeTipProjectionFrame> _eyeProjectionFrames = new();
    private Homography? _eyeProjectionCameraMap, _eyeProjectionSurfaceMap;
    private long _eyeProjectionNavigation = -1, _eyeProjectionHistory;
    private DateTimeOffset _eyeProjectionCapturedAt;

    // Separate from stationary caption references: this image explains our own
    // projected artwork to the eye detector, and can never qualify a held button.
    internal IReadOnlyList<EyeTipProjectionFrame> GetEyeTipProjectionFrames()
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
                ClearEyeProjectionHistory();
                return [];
            }
            if (!ReferenceEquals(_eyeProjectionCameraMap, _boardCameraMap) ||
                !ReferenceEquals(_eyeProjectionSurfaceMap, _boardSurfaceMap) ||
                _eyeProjectionNavigation != _boardSession.NavigationRevision)
            {
                ClearEyeProjectionHistory();
                _eyeProjectionCameraMap = _boardCameraMap;
                _eyeProjectionSurfaceMap = _boardSurfaceMap;
                _eyeProjectionNavigation = _boardSession.NavigationRevision;
            }
            now = MonotonicClock.UtcNow;
            if (now - _eyeProjectionCapturedAt < TimeSpan.FromMilliseconds(150))
                return _eyeProjectionFrames.ToArray();
            source = _boardApplicationTarget;
            cameraMap = _boardCameraMap;
            surfaceMap = _boardSurfaceMap;
            screen = _boardSession.Screen;
            navigation = _boardSession.NavigationRevision;
            var size = source.SizeInPixels;
            (width, height) = EyeProjectionSize(cameraMap, surfaceMap, (int)size.Width, (int)size.Height);
            if (_eyeProjectionTarget is { } pooled && pooled.Device == source.Device &&
                pooled.SizeInPixels.Width == width && pooled.SizeInPixels.Height == height)
                capture = pooled;
            else
            {
                _eyeProjectionTarget?.Dispose();
                capture = new(source.Device, width, height, 96);
            }
            _eyeProjectionTarget = null;
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
            _eyeProjectionCapturedAt = now;
            history = _eyeProjectionHistory;
        }

        // The readback waits for the GPU. Keep the render loop's lock free
        // meanwhile; the copy above already fixed this snapshot's content.
        byte[] pixels;
        try { pixels = capture.GetPixelBytes(); }
        catch
        {
            lock (_gate) ReturnEyeProjectionTarget(capture);
            throw;
        }
        lock (_gate)
        {
            ReturnEyeProjectionTarget(capture);
            // A reset, new registration or navigation during the readback
            // makes this snapshot describe a board that is no longer shown.
            // Another reference request is not guaranteed during that change,
            // so checking the history counter alone cannot detect it.
            if (_disposed || _blackOutput || _boardSetup || _calibrationTarget >= 0 || IsBoardRevealActive ||
                !ReferenceEquals(cameraMap, _boardCameraMap) || !ReferenceEquals(surfaceMap, _boardSurfaceMap) ||
                !ReferenceEquals(source, _boardApplicationTarget) || navigation != _boardSession.NavigationRevision ||
                screen != _boardSession.Screen || _renderedBoardState is not { } current || current.Screen != screen)
            {
                if (history == _eyeProjectionHistory) ClearEyeProjectionHistory();
                return [];
            }
            if (history != _eyeProjectionHistory) return [];
            if (_eyeProjectionFrames.Any(frame => frame.Width != width || frame.Height != height))
                _eyeProjectionFrames.Clear();
            _eyeProjectionFrames.Enqueue(new(width, height, pixels, mapping, now));
            while (_eyeProjectionFrames.Count > 3) _eyeProjectionFrames.Dequeue();
            return _eyeProjectionFrames.ToArray();
        }
    }

    // Tip detectors compare this image with the camera's own view of the board, one
    // sample per camera pixel. Match that resolution along each board axis: extra
    // pixels were skipped by the comparison, yet each capture paid for reading
    // them back from the GPU and converting them on the detection thread.
    private static (int Width, int Height) EyeProjectionSize(Homography cameraMap, Homography surfaceMap,
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

    private void ReturnEyeProjectionTarget(CanvasRenderTarget capture)
    {
        if (_disposed || _eyeProjectionTarget is not null) capture.Dispose();
        else _eyeProjectionTarget = capture;
    }

    private void ClearEyeProjectionHistory()
    {
        _eyeProjectionFrames.Clear();
        _eyeProjectionCapturedAt = default;
        _eyeProjectionCameraMap = _eyeProjectionSurfaceMap = null;
        _eyeProjectionNavigation = -1;
        _eyeProjectionHistory++;
    }

    private void DisposeEyeProjectionReference()
    {
        ClearEyeProjectionHistory();
        _eyeProjectionTarget?.Dispose();
        _eyeProjectionTarget = null;
    }
}
