using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void PublishColorTipDetection(CameraFrame frame, ColorTipDetectionResult detection,
        double milliseconds, long generation)
    {
        lock (_eyeTipGate)
        {
            if (_closing || generation != _eyeTipGeneration || !_eyeTipEnabled || !_cameraWanted ||
                _cameraHealthWarning || _camera.ActiveDeviceId != _eyeTipCameraId) return;
            var now = MonotonicClock.UtcNow;
            if (!EyeTipFrameFresh(frame, now))
            {
                _colorTipPreview = null;
                _colorTipTracker.Reset();
                _eyeTipReason = "waiting-for-fresh-camera-frame";
                PublishWaterStickTipLocked();
                return;
            }
            if (_colorTipPreview is { } previous &&
                (previous.Frame.Width != frame.Width || previous.Frame.Height != frame.Height))
                _colorTipTracker.Reset();
            var track = _colorTipTracker.Update(detection, frame.Timestamp, now);
            _colorTipPreview = new(frame, detection, track, milliseconds);
            _eyeTipPreview = null;
            _eyeTipReason = track.Reason;
            PublishWaterStickTipLocked();
        }
    }
}
