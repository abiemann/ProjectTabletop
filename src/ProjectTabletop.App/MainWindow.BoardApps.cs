using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void BoardMenu_Click(object sender, RoutedEventArgs e) => ShowBoardMenu();

    private void HandTrackingTest_Click(object sender, RoutedEventArgs e) => ShowHandTrackingTest();

    private void PhotoCopy_Click(object sender, RoutedEventArgs e) => ShowPhotoCopy();

    private void ShowBoardMenu()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowBoardMenu();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_scene.HasBoardMediaClip
            ? "Board menu ready. Aim with four fingers together, then separate your index finger sideways to select. Pinch also works."
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
            ? "Hand-Tracking test ready. Aim with the middle fingertip, bring four fingers together, then move the index sideways to select a button. Pinch still shows the red circle."
            : "Hand-Tracking test selected. Start the camera and scan the board to project it.");
    }

    private void UpdateBoardAppStatus()
    {
        if (_closing) return;
        UpdateHandDetectionLogStatus();
        bool blackjack = _scene.CurrentBoardScreen == BoardScreen.Blackjack;
        BlackjackPreviewPanel.Visibility = blackjack ? Visibility.Visible : Visibility.Collapsed;
        CameraPreviewPanel.Visibility = ProjectionPreviewPanel.Visibility = blackjack ? Visibility.Collapsed : Visibility.Visible;
        BoardAppStatusText.Text = _scene.CurrentBoardTitle + ". " +
            (IsBoardScanMeasuring ? "Board alignment is in progress." :
             blackjack ? _scene.BlackjackState.Status :
             !_scene.HasBoardMediaClip ? "Complete board setup to project it." :
             !_handTrackingEnabled ? "Enable hand tracking to use board buttons." :
             _scene.CurrentBoardScreen == BoardScreen.PhotoCopy ? _scene.PhotoCopyStatus :
             "Aim with the middle fingertip and four fingers together, then move the index sideways to select. Pinch also works.");
    }

    private void ShowPhotoCopy()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowPhotoCopy();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_scene.HasBoardMediaClip
            ? "Photo Copy: place an object above the controls and lift your hand. Select Swirl or Copy; Exit returns to the menu."
            : "Photo Copy selected. Scan the board before taking a photo.");
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
        var display = SelectedDisplay;
        float width = display?.PhysicalMode is { Width: > 0 } mode ? mode.Width : display?.Width ?? 1280;
        float height = display?.PhysicalMode is { Height: > 0 } heightMode ? heightMode.Height : display?.Height ?? 720;
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using (var drawing = target.CreateDrawingSession())
            _scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
