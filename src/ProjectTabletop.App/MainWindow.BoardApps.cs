using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void BoardMenu_Click(object sender, RoutedEventArgs e) => ShowBoardMenu();

    private void HandTrackingTest_Click(object sender, RoutedEventArgs e) => ShowHandTrackingTest();

    private void ShowBoardMenu()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowBoardMenu();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_scene.HasBoardMediaClip
            ? "Board menu ready. Point at a button and pinch to open it."
            : "Board menu selected. Start board setup to project it.");
    }

    private void ShowHandTrackingTest()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowHandTrackingTest();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_scene.HasBoardMediaClip
            ? "Hand-Tracking test ready. Pinch for a one-second red circle."
            : "Hand-Tracking test selected. Start the camera and scan the board to project it.");
    }

    private void UpdateBoardAppStatus()
    {
        if (_closing) return;
        BoardAppStatusText.Text = _scene.CurrentBoardTitle + ". " +
            (IsBoardScanMeasuring ? "Board alignment is in progress." :
             !_scene.HasBoardMediaClip ? "Complete board setup to project it." :
             !_handTrackingEnabled ? "Enable hand tracking to use board buttons." :
             "Point and pinch to select. Release before selecting again.");
    }

    private void PrepareBoardApp()
    {
        _annotationMode = AnnotationMode.None;
        _frozenFrame = null;
        _scene.ShowCalibrationTarget(-1, pieceTop: false);
        CameraCanvas.Invalidate();
    }

    // Saves the actual compositor output for diagnostics without capturing desktop windows.
    private async Task<string> SaveProjectionPreviewAsync()
    {
        var directory = Path.Combine(_appDataDirectory, "ProjectionSnapshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"projection-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        var aspect = SelectedDisplay is { Width: > 0, Height: > 0 } display
            ? display.Width / (double)display.Height : 16.0 / 9;
        const float width = 1280;
        var height = (float)(width / aspect);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using (var drawing = target.CreateDrawingSession())
            _scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
