using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void PublishWandMarkerDetection(CameraFrame frame, MarkerTipDetectionResult detection,
        BlackTipProfile profile, BlackTipSearchHint? searchHint, double milliseconds, long generation)
    {
        lock (_markerTrackingGate)
        {
            if (_closing || generation != _markerTrackingGeneration || !_markerTrackingEnabled || !_cameraWanted ||
                _cameraHealthWarning || _camera.ActiveDeviceId != _markerTrackingCameraId ||
                !WandMarkerBoardSelected || _scene.CurrentBoardScreen != _markerTrackingSceneBoard ||
                CurrentWandMarkerInputRevision != _markerTrackingSceneRevision) return;
            var now = MonotonicClock.UtcNow;
            if (!MarkerFrameFresh(frame, now))
            {
                _markerTrackingReason = "Waiting for a fresh camera frame.";
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
            // Paint strokes and water stirring along the control row are not sweeps.
            if (MarkerResultForControlSwipes(detection, decision.Action == FootballTipAction.Publish) is { } markerSeen)
                _scene.CompleteControlSwipeMarkerCheck(frame.Timestamp, markerSeen);
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
            // empty frame. Marker input expires after 250 ms without a measurement.
            SetWandMarkerPreviewLocked(new(frame, detection,
                new(observation, confirmed, decision.Source), observedAt, milliseconds) { SearchHint = searchHint });
            _markerTrackingReason = decision.Reason;
            PublishStickTipLocked(frame.Timestamp);
        }
    }
}
