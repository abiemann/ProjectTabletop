using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private string AutoProfileDirectory => Path.Combine(_appDataDirectory, "VisionAutosave");

    private async Task TryLoadAutosavedVisionAsync()
    {
        if (!File.Exists(Path.Combine(AutoProfileDirectory, "vision-profile.json"))) return;
        VisionEngine? loaded = null;
        try
        {
            var initial = _vision;
            loaded = await Task.Run(() => VisionEngine.Load(AutoProfileDirectory));
            lock (_visionGate)
            {
                if (_closing || !ReferenceEquals(_vision, initial) || _vision.Captures.Count != 0)
                {
                    loaded.Dispose();
                    return;
                }
                _vision = loaded;
                initial.Dispose();
            }
            var active = loaded;
            loaded = null;
            foreach (var pieceId in active.PieceIds) AddPieceChoice(pieceId);
            SyncVisionSettingsControls();
            UpdateTrainingStatus();
            SetStatus("Loaded the saved vision profile from " + AutoProfileDirectory);
        }
        catch (Exception ex)
        {
            loaded?.Dispose();
            if (!_closing) SetStatus("Could not load saved vision profile: " + ex.Message);
        }
    }

    private async void AddSample_Click(object sender, RoutedEventArgs e)
    {
        var frame = _frozenFrame;
        var pieceId = PieceIdTextBox.Text.Trim();
        if (frame is null || _pieceOutline.Count < 3 || _pieceFront is null || pieceId.Length == 0)
        {
            SetStatus("Capture a frame, enter a piece ID, mark at least three outline points, and mark its front.");
            return;
        }
        try
        {
            var outline = _pieceOutline.ToArray();
            var front = _pieceFront.Value;
            var info = await Task.Run(() =>
            {
                lock (_visionGate)
                {
                    var result = _vision.AddLabeledCapture(pieceId, frame.Width, frame.Height,
                        frame.Stride, frame.Bgra, outline, front);
                    // Persist the complete original camera frame and annotations immediately.
                    _vision.Save(AutoProfileDirectory);
                    return result;
                }
            });
            _scene.SetDetections(Array.Empty<PieceDetection>(), DateTimeOffset.MinValue);
            AddPieceChoice(pieceId);
            _pieceOutline.Clear();
            _pieceFront = null;
            // Keep the same full camera frame available so every visible piece can
            // receive its own outline and front anchor without taking a new snapshot.
            _annotationMode = AnnotationMode.PieceOutline;
            CameraCanvas.Invalidate();
            UpdateTrainingStatus();
            SetStatus($"Saved full-resolution snapshot {info.CaptureId} with labels to {AutoProfileDirectory}. " +
                "Outline another piece in this snapshot, or resume live view. Train after collecting several views.");
        }
        catch (Exception ex) { SetStatus("Could not add labeled snapshot: " + ex.Message); }
    }

    private async void Train_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TrainingStatusText.Text = "Training shape recognition locally…";
            var report = await Task.Run(() =>
            {
                lock (_visionGate)
                {
                    var result = _vision.Train();
                    _vision.Save(AutoProfileDirectory);
                    return result;
                }
            });
            UpdateTrainingStatus();
            SetStatus($"Trained {report.PieceCount} piece IDs from {report.CaptureCount} snapshots. " +
                $"Leave-one-out identity accuracy on those captures: {report.LeaveOneOutIdentityAccuracy:P0}. " +
                "Real projection conditions still require a physical validation run.");
        }
        catch (Exception ex)
        {
            UpdateTrainingStatus();
            SetStatus("Shape training failed: " + ex.Message);
        }
    }

    private async void SaveModels_Click(object sender, RoutedEventArgs e)
    {
        var directory = await PickProfileDirectoryAsync();
        if (directory is null) return;
        try
        {
            await Task.Run(() => { lock (_visionGate) _vision.Save(directory); });
            SetStatus("Saved trained model, full camera snapshots, and annotations to " + directory);
        }
        catch (Exception ex) { SetStatus("Vision profile save failed: " + ex.Message); }
    }

    private async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        var directory = await PickProfileDirectoryAsync();
        if (directory is null) return;
        try
        {
            var loaded = await Task.Run(() => VisionEngine.Load(directory));
            lock (_visionGate)
            {
                var old = _vision;
                _vision = loaded;
                old.Dispose();
            }
            foreach (var pieceId in loaded.PieceIds) AddPieceChoice(pieceId);
            SyncVisionSettingsControls();
            ClearVisionDetections();
            UpdateTrainingStatus();
            SetStatus("Loaded vision profile from " + directory);
        }
        catch (Exception ex) { SetStatus("Vision profile load failed: " + ex.Message); }
    }

    private async Task<string?> PickProfileDirectoryAsync()
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            return (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception ex)
        {
            SetStatus("Folder picker failed: " + ex.Message);
            return null;
        }
    }

    private void UpdateTrainingStatus()
    {
        lock (_visionGate)
        {
            var captures = _vision.Captures;
            var pieces = _vision.PieceIds;
            TrainingStatusText.Text = _vision.IsTrained
                ? $"Trained: {pieces.Count} piece IDs, {captures.Count} full-resolution labeled snapshots. " +
                  string.Join(", ", pieces)
                : captures.Count == 0
                    ? "Untrained: no labeled samples. Capture and annotate real pieces when available."
                    : $"Untrained: {captures.Count} labeled snapshots for {pieces.Count} piece IDs. Press Train.";
        }
    }
}
