using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private bool _visionSettingsBusy;

    private void SyncVisionSettingsControls()
    {
        lock (_visionGate)
        {
            var settings = _vision.Settings;
            VisionSegmentationComboBox.SelectedIndex = (int)settings.SegmentationMode;
            VisionBrightnessThresholdBox.Value = settings.BrightnessThreshold;
            VisionDifferenceThresholdBox.Value = settings.DifferenceThreshold;
            VisionMinimumAreaBox.Value = settings.MinimumAreaPixels;
            VisionPatternThresholdBox.Value = settings.PatternBrightnessThreshold;
            VisionReferenceStatusText.Text = _vision.HasEmptyBoardReference
                ? "Empty-board reference saved. Capture it again if the camera or fixed image changes."
                : "No empty-board reference saved.";
        }
    }

    private async void ApplyVisionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_visionSettingsBusy) return;
        var modeIndex = VisionSegmentationComboBox.SelectedIndex;
        if (modeIndex < 0 || modeIndex > 2 ||
            !TryReadCutoff(VisionBrightnessThresholdBox, out int brightness) ||
            !TryReadCutoff(VisionDifferenceThresholdBox, out int difference) ||
            !TryReadCutoff(VisionPatternThresholdBox, out int pattern) ||
            !double.IsFinite(VisionMinimumAreaBox.Value) || VisionMinimumAreaBox.Value < 1 ||
            VisionMinimumAreaBox.Value > 1_000_000)
        {
            SetStatus("Enter valid vision cutoffs (0–255) and a minimum area of at least one pixel.");
            return;
        }
        var mode = (SegmentationMode)modeIndex;
        double minimumArea = VisionMinimumAreaBox.Value;
        if (_closing) return;
        var version = ++_visionLoadVersion;
        _visionSettingsBusy = true;
        ApplyVisionSettingsButton.IsEnabled = false;
        CaptureEmptyBoardButton.IsEnabled = false;
        try
        {
            bool retrained = await Task.Run(() =>
            {
                lock (_visionGate)
                {
                    if (_closing || version != Interlocked.Read(ref _visionLoadVersion)) throw new OperationCanceledException();
                    if (mode == SegmentationMode.BackgroundDifference && !_vision.HasEmptyBoardReference)
                        throw new InvalidOperationException("Capture an empty board with a fixed projected image first.");
                    var settings = _vision.Settings;
                    bool patternChanged = settings.PatternBrightnessThreshold != pattern;
                    settings.SegmentationMode = mode;
                    settings.BrightnessThreshold = brightness;
                    settings.DifferenceThreshold = difference;
                    settings.MinimumAreaPixels = minimumArea;
                    settings.PatternBrightnessThreshold = pattern;
                    bool retrain = patternChanged && _vision.Captures.Count > 0;
                    if (retrain) _vision.Train();
                    _vision.Save(AutoProfileDirectory);
                    return retrain;
                }
            });
            if (_closing || version != _visionLoadVersion) return;
            ClearVisionDetections();
            UpdateTrainingStatus();
            SetStatus(retrained
                ? "Vision tuning saved. Existing labeled captures were retrained for the new pattern cutoff."
                : "Vision tuning saved. Check live detections against real cards.");
        }
        catch (Exception ex)
        {
            if (!_closing && version == _visionLoadVersion) SetStatus("Vision tuning failed: " + ex.Message);
        }
        finally
        {
            _visionSettingsBusy = false;
            if (!_closing)
            {
                ApplyVisionSettingsButton.IsEnabled = true;
                CaptureEmptyBoardButton.IsEnabled = true;
            }
        }
    }

    private async void CaptureEmptyBoard_Click(object sender, RoutedEventArgs e)
    {
        if (_visionSettingsBusy) return;
        if (!_camera.IsRunning || SelectedCamera?.Device.Id != _camera.ActiveDeviceId ||
            _camera.CaptureSnapshot() is not { } frame)
        {
            SetStatus("Start the selected webcam and wait for a frame before capturing the empty board.");
            return;
        }
        if (_closing) return;
        var version = ++_visionLoadVersion;
        _visionSettingsBusy = true;
        ApplyVisionSettingsButton.IsEnabled = false;
        CaptureEmptyBoardButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                lock (_visionGate)
                {
                    if (_closing || version != Interlocked.Read(ref _visionLoadVersion)) throw new OperationCanceledException();
                    _vision.SetEmptyBoardReference(frame.Width, frame.Height, frame.Stride, frame.Bgra);
                    _vision.Settings.SegmentationMode = SegmentationMode.BackgroundDifference;
                    _vision.Save(AutoProfileDirectory);
                }
            });
            if (_closing || version != _visionLoadVersion) return;
            SyncVisionSettingsControls();
            ClearVisionDetections();
            SetStatus("Empty-board reference saved and difference mode selected. Keep the projected image fixed; moving video will appear as motion to the detector.");
        }
        catch (Exception ex)
        {
            if (!_closing && version == _visionLoadVersion) SetStatus("Could not save empty-board reference: " + ex.Message);
        }
        finally
        {
            _visionSettingsBusy = false;
            if (!_closing)
            {
                ApplyVisionSettingsButton.IsEnabled = true;
                CaptureEmptyBoardButton.IsEnabled = true;
            }
        }
    }

    private void ClearVisionDetections()
    {
        ResetPieceDetections();
        CameraCanvas.Invalidate();
    }

    private static bool TryReadCutoff(NumberBox box, out int value)
    {
        value = 0;
        if (!double.IsFinite(box.Value) || box.Value < 0 || box.Value > 255) return false;
        value = (int)Math.Round(box.Value);
        return true;
    }
}
