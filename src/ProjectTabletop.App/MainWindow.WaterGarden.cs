using Microsoft.UI.Xaml;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private long _waterBoardEntrySequence;

    private void BoardOpenedForStickTracking(BoardScreen screen)
    {
        long entrySequence = Interlocked.Increment(ref _waterBoardEntrySequence);
        if (screen != BoardScreen.WaterGarden) return;
        long trackingGeneration = Volatile.Read(ref _eyeTipGeneration);
        // BoardOpened can run under the compositor lock. Defer camera/tracker
        // access to avoid reversing the eye-tip-to-compositor lock order.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || entrySequence != Volatile.Read(ref _waterBoardEntrySequence) ||
                _scene.CurrentBoardScreen != BoardScreen.WaterGarden) return;
            lock (_eyeTipGate)
            {
                // A toggle or camera change after entry supersedes this queued
                // request. Status refreshes and Reset never re-enable it.
                if (_eyeTipGeneration != trackingGeneration || _eyeTipEnabled ||
                    _eyeTipCameraId != _selectedCameraId ||
                    (LearnedEyeTipRadiusLocked() is null && LearnedColorTipProfileLocked() is null)) return;
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
            : "Water Garden selected. Scan the board and learn the coloured tip or eye sticker.");
    }

    // Called with _eyeTipGate held. SceneCompositor owns the current camera-to-
    // projector and oriented board maps; camera observations never become cursor
    // or button input here. Its own reset timestamp rejects pre-calibration frames.
    private void PublishWaterStickTipLocked()
    {
        var now = MonotonicClock.UtcNow;
        PixelPoint? observation = null;
        var sourceTime = now;
        if (ActiveColorTipProfileLocked() is not null)
        {
            if (_colorTipPreview is { Track: { Confirmed: true, Observation: { } colour } } colorPreview &&
                EyeTipFrameFresh(colorPreview.Frame, now))
            { observation = colour.Center; sourceTime = colorPreview.Frame.Timestamp; }
        }
        else if (_eyeTipPreview is { Track: { Confirmed: true, Observation: { } eye } } preview &&
            EyeTipFrameFresh(preview.Frame, now))
        { observation = eye.Center; sourceTime = preview.Frame.Timestamp; }
        PixelPoint? point = !_closing && _eyeTipEnabled && !_eyeTipLearning && !_colorTipLearning &&
            _cameraWanted && _camera.IsRunning && _cameraOperation.CurrentCount != 0 &&
            !Volatile.Read(ref _cameraHealthWarning) && !IsBoardScanMeasuring &&
            _camera.ActiveDeviceId == _eyeTipCameraId && _cameraWantedDeviceId == _eyeTipCameraId ? observation : null;
        _scene.SetWaterStickTip(point, point is null ? now : sourceTime);
    }
}
