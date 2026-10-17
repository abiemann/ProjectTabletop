namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void ShowPaint()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowPaint();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_scene.HasBoardMediaClip ? "Paint: move the marker to trail ink. Hold the bottom-left arrow for EXIT, RESET and SAVE; swipe along the bottom to hide or show controls."
            : "Paint selected. Scan the board to start painting.");
    }
}
