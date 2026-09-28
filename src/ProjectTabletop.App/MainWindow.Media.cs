using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using ProjectTabletop.App.Media;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _outputOperation = new(1, 1);
    private Task<bool>? _openProjectionTask;

    private async void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        await OpenProjectionOutputAsync();
    }

    private Task<bool> OpenProjectionOutputAsync()
    {
        if (_openProjectionTask is { IsCompleted: false }) return _openProjectionTask;
        return _openProjectionTask = OpenProjectionOutputCoreAsync();
    }

    private async Task<bool> OpenProjectionOutputCoreAsync()
    {
        await _outputOperation.WaitAsync();
        try
        {
            if (_closing) return false;
            OpenOutputButton.IsEnabled = false;
            FullscreenButton.IsEnabled = false;
            InvalidateDisplayAudio(disconnectOutput: true);
            if (Volatile.Read(ref _boardSetupActive))
            {
                Volatile.Write(ref _boardSetupPhase, (int)BoardSetupPhase.Switching);
                Interlocked.Increment(ref _boardSetupGeneration);
                _scene.SetBlackOutput(true);
            }
            var display = ResolveProjectionDisplay();
            ClearHandTracking();
            _scene.ClearBoardMediaClip();
            if (_output is null)
            {
                var created = new ProjectionWindow(_scene);
                _output = created;
                created.Closed += (_, _) =>
                {
                    if (!ReferenceEquals(_output, created)) return;
                    _output = null;
                    _outputDisplayId = null;
                    InvalidateDisplayAudio(disconnectOutput: true);
                    ClearHandTracking();
                    _scene.ClearBoardMediaClip();
                    FullscreenButton.Content = "Full screen";
                    if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
                };
            }
            var output = _output;
            _scene.SetDisplayAspect((double)display.Width / display.Height);
            await output.ShowOnAsync(display.Area, AppWindow.Id);
            if (_closing || !ReferenceEquals(_output, output) || SelectedDisplay?.Id != display.Id)
                throw new InvalidOperationException("The output window or selected display changed while opening.");
            RequireProjectionOutput();
            _outputDisplayId = output.ActualDisplayId;
            _displayAudioVideoReady = true;
            _ = RefreshDisplayAudioAsync();
            FullscreenButton.Content = "Windowed";
            if (Volatile.Read(ref _boardSetupActive))
            {
                ClearBoardPreview();
                BoardSetupStatusText.Text = "Projector is black. Finding the physical cardboard again.";
            }
            SetStatus($"Projection output opened on {display}. Confirm hardware video decode on this laptop.");
            return true;
        }
        catch (Exception ex)
        {
            _output?.Close();
            _outputDisplayId = null;
            InvalidateDisplayAudio(disconnectOutput: true);
            if (!_closing) SetStatus("Could not open projection output: " + ex.Message);
            return false;
        }
        finally
        {
            _outputOperation.Release();
            if (!_closing)
            {
                OpenOutputButton.IsEnabled = true;
                FullscreenButton.IsEnabled = true;
            }
        }
    }

    private async void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_outputOperation.CurrentCount == 0) return;
        if (_output is null) { await OpenProjectionOutputAsync(); return; }
        await _outputOperation.WaitAsync();
        try
        {
            if (_closing || _output is null) return;
            OpenOutputButton.IsEnabled = false;
            FullscreenButton.IsEnabled = false;
            InvalidateDisplayAudio(disconnectOutput: true);
            StopBoardSetup();
            ClearHandTracking();
            _scene.ClearBoardMediaClip();
            var display = ResolveProjectionDisplay();
            var output = _output;
            await output.SetFullScreenAsync(!output.IsFullScreen);
            if (_closing || !ReferenceEquals(_output, output) || SelectedDisplay?.Id != display.Id ||
                output.ActualDisplayId != display.Id)
                throw new InvalidOperationException("The output window or selected display changed during the window-mode change.");
            _outputDisplayId = output.ActualDisplayId;
            _displayAudioVideoReady = true;
            _ = RefreshDisplayAudioAsync();
            FullscreenButton.Content = output.IsFullScreen ? "Windowed" : "Full screen";
            InvalidateCalibration("Output window mode changed. Recalibrate in full screen.");
        }
        catch (Exception ex)
        {
            _output?.Close();
            _outputDisplayId = null;
            InvalidateDisplayAudio(disconnectOutput: true);
            if (!_closing) SetStatus("Could not change output window mode: " + ex.Message);
        }
        finally
        {
            _outputOperation.Release();
            if (!_closing)
            {
                OpenOutputButton.IsEnabled = true;
                FullscreenButton.IsEnabled = true;
            }
        }
    }

    private void RequireProjectionOutput()
    {
        var display = ResolveProjectionDisplay();
        if (_output is null || !_output.AppWindow.IsVisible || !_output.IsFullScreen ||
            _output.ActualDisplayId != display.Id)
            throw new InvalidOperationException("Open the selected projector display in full screen first.");
        _output.VerifyTargetDisplay();
    }

    private async void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickMediaPathAsync();
        if (path is null) return;
        try { await SetBackgroundFromPathAsync(path); }
        catch (Exception ex) { SetStatus("Background media could not open: " + ex.Message); }
    }

    private async Task SetBackgroundFromPathAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Background media was not found.", fullPath);
        var asset = _scene.FindAssetByPath(fullPath) ??
            await MediaAsset.OpenAsync(CanvasDevice.GetSharedDevice(), fullPath);
        StopBoardSetup();
        _scene.SetBackground(asset);
        UpdateHandDetectionLogStatus();
        BackgroundPathText.Text = fullPath;
        SetStatus(_scene.HasBoardMediaClip
            ? $"Background: {Path.GetFileName(fullPath)}. Playback is limited to the detected board."
            : $"Background: {Path.GetFileName(fullPath)} loaded. Complete a board scan before it is projected.");
    }

    private void TestGrid_Click(object sender, RoutedEventArgs e)
    {
        StopBoardSetup();
        _scene.SetBackground(null);
        UpdateHandDetectionLogStatus();
        BackgroundPathText.Text = "Test grid";
        SetStatus(_scene.HasBoardMediaClip
            ? "Test grid is limited to the detected board."
            : "Complete a board scan before the test grid is projected.");
    }

    private async void ChooseOverlay_Click(object sender, RoutedEventArgs e)
    {
        var pieceId = OverlayPieceComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(pieceId)) pieceId = PieceIdTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pieceId))
        {
            SetStatus("Enter a piece ID or select a trained piece before assigning media.");
            return;
        }
        var path = await PickMediaPathAsync();
        if (path is null) return;
        try
        {
            var asset = _scene.FindAssetByPath(path) ??
                await MediaAsset.OpenAsync(CanvasDevice.GetSharedDevice(), path);
            _scene.SetOverlay(pieceId, asset);
            AddPieceChoice(pieceId);
            UpdateOverlayMappings();
            SetStatus($"Assigned {Path.GetFileName(path)} to {pieceId}.");
        }
        catch (Exception ex) { SetStatus("Piece media could not open: " + ex.Message); }
    }

    private void RemoveOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayPieceComboBox.SelectedItem is not string pieceId) return;
        _scene.SetOverlay(pieceId, null);
        UpdateOverlayMappings();
    }

    private void AddPieceChoice(string pieceId)
    {
        if (!OverlayPieceComboBox.Items.OfType<string>().Contains(pieceId, StringComparer.OrdinalIgnoreCase))
            OverlayPieceComboBox.Items.Add(pieceId);
        OverlayPieceComboBox.SelectedItem = OverlayPieceComboBox.Items.OfType<string>()
            .First(item => string.Equals(item, pieceId, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateOverlayMappings()
    {
        var mappings = _scene.OverlayPaths;
        OverlayMappingsText.Text = mappings.Count == 0 ? "No overlays assigned." :
            string.Join("\n", mappings.OrderBy(item => item.Key).Select(item =>
                $"{item.Key}: {Path.GetFileName(item.Value)}"));
    }

    private async Task<string?> PickMediaPathAsync()
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                FileTypeFilter = { ".mp4", ".mov", ".mkv", ".wmv", ".webm", ".avi",
                                   ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" }
            };
            return (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            SetStatus("File picker failed: " + ex.Message);
            return null;
        }
    }

    private void ProjectionPreview_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args) =>
        _scene.Draw(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height,
            preview: true, runningSlowly: args.Timing.IsRunningSlowly);
}
