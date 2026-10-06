using Microsoft.Graphics.Canvas;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _eyeProjectionTarget;
    private readonly Queue<EyeTipProjectionFrame> _eyeProjectionFrames = new();
    private Homography? _eyeProjectionCameraMap, _eyeProjectionSurfaceMap;
    private long _eyeProjectionNavigation = -1;
    private DateTimeOffset _eyeProjectionCapturedAt;

    // Separate from stationary caption references: this image explains our own
    // projected artwork to the eye detector, and can never qualify a held button.
    internal IReadOnlyList<EyeTipProjectionFrame> GetEyeTipProjectionFrames()
    {
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
            var now = MonotonicClock.UtcNow;
            if (now - _eyeProjectionCapturedAt >= TimeSpan.FromMilliseconds(150))
            {
                var source = _boardApplicationTarget;
                var size = source.SizeInPixels;
                double scale = Math.Min(1, 2048.0 / Math.Max(size.Width, size.Height));
                int width = Math.Max(1, (int)Math.Round(size.Width * scale));
                int height = Math.Max(1, (int)Math.Round(size.Height * scale));
                if (_eyeProjectionTarget is null || _eyeProjectionTarget.Device != source.Device ||
                    _eyeProjectionTarget.SizeInPixels.Width != width || _eyeProjectionTarget.SizeInPixels.Height != height)
                {
                    _eyeProjectionTarget?.Dispose();
                    _eyeProjectionTarget = new(source.Device, width, height, 96);
                    _eyeProjectionFrames.Clear();
                }
                using (var drawing = _eyeProjectionTarget.CreateDrawingSession())
                    drawing.DrawImage(source, new Rect(0, 0, width, height), new Rect(0, 0, size.Width, size.Height),
                        1, CanvasImageInterpolation.Linear, CanvasComposite.Copy);
                var camera = _boardCameraMap.ToMatrix();
                var surface = _boardSurfaceMap.Inverse().ToMatrix();
                var mapping = new double[9];
                for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                for (int middle = 0; middle < 3; middle++)
                    mapping[row * 3 + column] += surface[row * 3 + middle] * camera[middle * 3 + column];
                _eyeProjectionFrames.Enqueue(new(width, height, _eyeProjectionTarget.GetPixelBytes(), mapping, now));
                while (_eyeProjectionFrames.Count > 3) _eyeProjectionFrames.Dequeue();
                _eyeProjectionCapturedAt = now;
            }
            return _eyeProjectionFrames.ToArray();
        }
    }

    private void ClearEyeProjectionHistory()
    {
        _eyeProjectionFrames.Clear();
        _eyeProjectionCapturedAt = default;
        _eyeProjectionCameraMap = _eyeProjectionSurfaceMap = null;
        _eyeProjectionNavigation = -1;
    }

    private void DisposeEyeProjectionReference()
    {
        ClearEyeProjectionHistory();
        _eyeProjectionTarget?.Dispose();
        _eyeProjectionTarget = null;
    }
}
