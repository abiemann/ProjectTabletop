using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using ProjectTabletop.App.Media;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDisplay is not { } display)
        {
            SetStatus("Choose an HDMI display before opening the projection window.");
            return;
        }
        try
        {
            _scene.ClearBoardMediaClip();
            if (_output is null)
            {
                _output = new ProjectionWindow(_scene);
                _output.Closed += (_, _) =>
                {
                    _output = null;
                    _outputDisplayId = null;
                    _scene.ClearBoardMediaClip();
                    FullscreenButton.Content = "Full screen";
                    if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
                };
            }
            _scene.SetDisplayAspect((double)display.Width / display.Height);
            _output.ShowOn(display.Area);
            _output.SetFullScreen(true);
            _outputDisplayId = display.Id;
            FullscreenButton.Content = "Windowed";
            if (Volatile.Read(ref _boardSetupActive)) RescanBoardSetup();
            SetStatus($"Projection output opened on {display}. Confirm hardware video decode on this laptop.");
        }
        catch (Exception ex) { SetStatus("Could not open projection output: " + ex.Message); }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_output is null) { OpenOutput_Click(sender, e); return; }
        try
        {
            if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
            _scene.ClearBoardMediaClip();
            _output.SetFullScreen(!_output.IsFullScreen);
            FullscreenButton.Content = _output.IsFullScreen ? "Windowed" : "Full screen";
            InvalidateCalibration("Output window mode changed. Recalibrate in full screen.");
        }
        catch (Exception ex) { SetStatus("Could not change output window mode: " + ex.Message); }
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
        if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
        _scene.SetBackground(asset);
        BackgroundPathText.Text = fullPath;
        SetStatus(_scene.HasBoardMediaClip
            ? $"Background: {Path.GetFileName(fullPath)}. Playback is limited to the detected board."
            : $"Background: {Path.GetFileName(fullPath)} loaded. Complete a board scan before it is projected.");
    }

    private void TestGrid_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
        _scene.SetBackground(null);
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
