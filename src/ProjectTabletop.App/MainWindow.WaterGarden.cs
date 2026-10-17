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
        long trackingGeneration = Volatile.Read(ref _eyeTipGeneration);
        // BoardOpened can run under the compositor lock. Defer camera/tracker
        // access to avoid reversing the eye-tip-to-compositor lock order.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || entrySequence != Volatile.Read(ref _stickBoardEntrySequence) ||
                _scene.CurrentBoardScreen != screen) return;
            lock (_eyeTipGate)
            {
                // A toggle or camera change after entry supersedes this queued
                // request. Status refreshes and Reset never re-enable it.
                if (_eyeTipGeneration != trackingGeneration || _eyeTipEnabled ||
                    _eyeTipCameraId != _selectedCameraId ||
                    (LearnedWandMarkerProfileLocked() is null &&
                        (screen == BoardScreen.Paint || LearnedEyeTipRadiusLocked() is null))) return;
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
            ? "Water Garden: move the learned stick tip over the water. Open the bottom-left arrow for Exit, Reset, and Duck+."
            : "Water Garden selected. Scan the board and learn the double-bar wand marker.");
    }

    // Called with _eyeTipGate held. SceneCompositor owns the current camera-to-
    // projector and oriented board maps; camera observations never become cursor
    // or button input here. Its own reset timestamp rejects pre-calibration frames.
    private void PublishStickTipLocked()
    {
        var now = MonotonicClock.UtcNow;
        PixelPoint? observation = null;
        double paintRadius = 0;
        bool barObservation = false;
        var sourceTime = now;
        if (ActiveWandMarkerProfileLocked() is not null)
        {
            if (WandMarkerFreshLocked(now) && _wandMarkerPreview is { Track.Observation: { } marker } wandPreview)
            {
                observation = marker.Center;
                sourceTime = wandPreview.ObservedAt;
                barObservation = true;
                if (_scene.CurrentBoardScreen == BoardScreen.Paint)
                    paintRadius = _scene.GetPaintMarkerRadius(marker.Center, marker.RadiusPixels);
            }
        }
        else if (_eyeTipPreview is { Track: { Confirmed: true, Observation: { } eye } } preview &&
            EyeTipFrameFresh(preview.Frame, now))
        { observation = eye.Center; sourceTime = preview.Frame.Timestamp; }
        PixelPoint? point = !_closing && _eyeTipEnabled && !_eyeTipLearning && !_wandMarkerLearning &&
            _cameraWanted && _camera.IsRunning && _cameraOperation.CurrentCount != 0 &&
            !Volatile.Read(ref _cameraHealthWarning) && !IsBoardScanMeasuring &&
            _camera.ActiveDeviceId == _eyeTipCameraId && _cameraWantedDeviceId == _eyeTipCameraId &&
            _scene.CurrentBoardScreen == _eyeTipSceneBoard && CurrentWandMarkerInputRevision == _eyeTipSceneRevision
                ? observation : null;
        _scene.SetWaterStickTip(_eyeTipSceneBoard == BoardScreen.WaterGarden ? point : null,
            point is null ? now : sourceTime);
        Point2? paintPoint = _eyeTipSceneBoard == BoardScreen.Paint && barObservation && point is { } markerPoint
            ? new(markerPoint.X, markerPoint.Y) : null;
        _scene.SetPaintMarkerTip(paintPoint, paintRadius, paintPoint is null ? now : sourceTime,
            _eyeTipSceneBoard == BoardScreen.Paint ? _eyeTipSceneRevision : null);
    }
}
