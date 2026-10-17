using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void PublishWandMarkerDetection(CameraFrame frame, MarkerTipDetectionResult detection,
        BlackTipProfile profile, BlackTipSearchHint? searchHint, double milliseconds, long generation)
    {
        lock (_eyeTipGate)
        {
            if (_closing || generation != _eyeTipGeneration || !_eyeTipEnabled || !_cameraWanted ||
                _cameraHealthWarning || _camera.ActiveDeviceId != _eyeTipCameraId ||
                !WandMarkerBoardSelected || _scene.CurrentBoardScreen != _eyeTipSceneBoard ||
                CurrentWandMarkerInputRevision != _eyeTipSceneRevision) return;
            var now = MonotonicClock.UtcNow;
            if (!EyeTipFrameFresh(frame, now))
            {
                _eyeTipReason = "Waiting for a fresh camera frame.";
                PublishStickTipLocked();
                return;
            }
            if (_wandMarkerPreview is { } previous &&
                (previous.Frame.Width != frame.Width || previous.Frame.Height != frame.Height))
            {
                _wandMarkerTracker.Reset();
                SetWandMarkerPreviewLocked(null);
            }
            var decision = _wandMarkerTracker.Update(profile, detection,
                camera => _scene.TryMapWandMarkerCameraPoint(camera, out var board) ? board : null,
                frame.Timestamp, now);
            var observation = _wandMarkerPreview?.Track.Observation;
            var observedAt = _wandMarkerPreview?.ObservedAt ?? default;
            bool confirmed = _wandMarkerPreview?.Track.Confirmed == true;
            if (decision.Action == FootballTipAction.Publish)
            {
                observation = decision.Tip;
                observedAt = frame.Timestamp;
                confirmed = true;
            }
            else if (decision.Action == FootballTipAction.Clear || decision.Source == "confirming")
            {
                observation = null;
                observedAt = default;
                confirmed = false;
            }
            // Hold preserves the measurement's original time, never the newer
            // empty frame. Wand input expires after 250 ms without a measurement.
            SetWandMarkerPreviewLocked(new(frame, detection,
                new(observation, confirmed, decision.Source), observedAt, milliseconds) { SearchHint = searchHint });
            SetEyeTipPreviewLocked(null);
            _eyeTipReason = decision.Reason;
            PublishStickTipLocked();
        }
    }
}
