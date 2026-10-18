using Microsoft.UI.Xaml;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private long _stickBoardEntrySequence;

    private void BoardOpenedForStickTracking(BoardScreen screen)
    {
        long entrySequence = Interlocked.Increment(ref _stickBoardEntrySequence);
        if (screen is not (BoardScreen.WaterGarden or BoardScreen.Paint)) return;
        long trackingGeneration = Volatile.Read(ref _markerTrackingGeneration);
        // BoardOpened can run under the compositor lock. Defer camera/tracker
        // access to avoid reversing the marker-to-compositor lock order.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || entrySequence != Volatile.Read(ref _stickBoardEntrySequence) ||
                _scene.CurrentBoardScreen != screen) return;
            lock (_markerTrackingGate)
            {
                // A toggle or camera change after entry supersedes this queued
                // request. Status refreshes and Reset never re-enable it.
                if (_markerTrackingGeneration != trackingGeneration || _markerTrackingEnabled ||
                    _markerTrackingCameraId != _selectedCameraId || LearnedWandMarkerProfileLocked() is null) return;
            }
            SetStickTrackingEnabled(true);
        });
    }

    private void WaterGarden_Click(object sender, RoutedEventArgs e) => ShowWaterGarden();

    private void ShowWaterGarden()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowWaterGarden();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_scene.HasBoardMediaClip
            ? "Water Garden: move the learned double-bar marker over the water. Open the bottom-left arrow for Exit, Reset, and Duck+."
            : "Water Garden selected. Scan the board and train the double-bar marker.");
    }

    // Called with _markerTrackingGate held. SceneCompositor owns the current camera-to-
    // projector and oriented board maps; camera observations never become cursor
    // or button input here. Its own reset timestamp rejects pre-calibration frames.
    // A detector result passes the camera frame that measured no marker: stamping
    // that absence with its own source time keeps the next, newer measurement
    // eligible. Resets, expiry and stale-frame waits pass none and use the current
    // time as a barrier against every older camera frame.
    private void PublishStickTipLocked(DateTimeOffset? measuredFrameTime = null)
    {
        var now = MonotonicClock.UtcNow;
        var absentTime = measuredFrameTime is { } measured && measured <= now ? measured : now;
        PixelPoint? observation = null;
        double paintRadius = 0;
        var sourceTime = now;
        if (LearnedWandMarkerProfileLocked() is not null && WandMarkerFreshLocked(now) &&
            _wandMarkerPreview is { Track.Observation: { } marker } markerPreview)
        {
            observation = marker.Center;
            sourceTime = markerPreview.ObservedAt;
            if (_scene.CurrentBoardScreen == BoardScreen.Paint)
                paintRadius = _scene.GetPaintMarkerRadius(marker.Center, marker.RadiusPixels);
        }
        PixelPoint? point = !_closing && _markerTrackingEnabled && !_wandMarkerLearning &&
            _cameraWanted && _camera.IsRunning && _cameraOperation.CurrentCount != 0 &&
            !Volatile.Read(ref _cameraHealthWarning) && !IsBoardScanMeasuring &&
            _camera.ActiveDeviceId == _markerTrackingCameraId && _cameraWantedDeviceId == _markerTrackingCameraId &&
            _scene.CurrentBoardScreen == _markerTrackingSceneBoard && CurrentWandMarkerInputRevision == _markerTrackingSceneRevision
                ? observation : null;
        _scene.SetWaterStickTip(_markerTrackingSceneBoard == BoardScreen.WaterGarden ? point : null,
            point is null ? absentTime : sourceTime);
        Point2? paintPoint = _markerTrackingSceneBoard == BoardScreen.Paint && point is { } markerPoint
            ? new(markerPoint.X, markerPoint.Y) : null;
        _scene.SetPaintMarkerTip(paintPoint, paintRadius, paintPoint is null ? absentTime : sourceTime,
            _markerTrackingSceneBoard == BoardScreen.Paint ? _markerTrackingSceneRevision : null);
    }
}
