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
        RequestCameraPictureProfile();
        UpdateHandDetectionLogStatus();
        bool blackjack = _scene.CurrentBoardScreen == BoardScreen.Blackjack;
        bool crownDeed = _scene.CurrentBoardScreen == BoardScreen.CrownDeed;
        bool globe = _scene.CurrentBoardScreen == BoardScreen.Globe;
        bool slots = _scene.CurrentBoardScreen == BoardScreen.Slots;
        bool roulette = _scene.CurrentBoardScreen == BoardScreen.Roulette;
        bool water = _scene.CurrentBoardScreen == BoardScreen.WaterGarden;
        bool football = _scene.CurrentBoardScreen == BoardScreen.Football;
        bool strykersRun = _scene.CurrentBoardScreen == BoardScreen.StrykersRun;
        SyncFootballControls();
        SettingsToolsPanel.Visibility = _scene.CurrentBoardScreen == BoardScreen.Settings
            ? Visibility.Visible : Visibility.Collapsed;
        // During a camera outage all boards use the shared projection warning;
        // hide the interactive game preview until explicit camera recovery.
        bool boardGame = !_cameraManualRestartRequired &&
            (blackjack || crownDeed || globe || slots || roulette || water || football || strykersRun);
        BoardGamePreviewTitle.Text = strykersRun ? "STRYKER'S RUN  ·  slide along the dots  ·  automatic fire" :
            football ? "FOOTBALL  ·  black cardboard bars or index fingers  ·  first to five" :
            water ? "WATER GARDEN  ·  double-bar marker makes ripples  ·  click controls" :
            roulette ? "VICE ROYALE  ·  ROULETTE  ·  click to place chips" :
            slots ? "DRAGON SLOTS  ·  click a button to play  ·  virtual credits" :
            globe ? "GLOBE  ·  zoom Earth in and out" : crownDeed ? "CROWN & DEED  ·  click the table to play" :
            "BLACKJACK  ·  click the table to play  ·  virtual chips";
        BlackjackPreviewPanel.Visibility = boardGame ? Visibility.Visible : Visibility.Collapsed;
        // Water keeps the camera beside its interactive preview so the user can see
        // whether the double-bar marker is acquired.
        Microsoft.UI.Xaml.Controls.Grid.SetRow(BlackjackPreviewPanel, water || football || strykersRun ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(BlackjackPreviewPanel, water || football || strykersRun ? 1 : 2);
        CameraPreviewPanel.Visibility = water || football || strykersRun || !boardGame ? Visibility.Visible : Visibility.Collapsed;
        ProjectionPreviewPanel.Visibility = boardGame ? Visibility.Collapsed : Visibility.Visible;
        ApplyProjectionPreviewState();
        BoardAppStatusText.Text = _scene.CurrentBoardTitle + ". " +
            (_cameraManualRestartRequired ? "Check Camera. " + ManualCameraRecoveryHint :
             IsBoardScanMeasuring ? "Board alignment is in progress." :
             blackjack ? _scene.BlackjackState.Status :
             slots ? _scene.SlotsState.Status :
             roulette ? _scene.RouletteState.Status :
             crownDeed ? _scene.CrownDeedState.Status :
             globe ? (_scene.GetGlobeSaveStatus(MonotonicClock.UtcNow) ??
                "Open the arrow for EXIT, MODEL and SAVE. The side bars control zoom and pitch; SAVE keeps only Earth and stars in Pictures/Globe.") :
             water ? "Move the learned double-bar marker across the water. Open the bottom-left arrow for Exit, Reset, and Duck+; you can also click those controls here." :
             football ? "Choose Football input for each player. Play the computer or switch to two players. First to five wins; tracking loss pauses the match." :
             strykersRun ? _scene.StrykersRunState.Banner :
             !_scene.HasBoardMediaClip ? "Complete board setup to project it." :
             !_handTrackingEnabled ? "Enable hand tracking to use board buttons." :
             _scene.CurrentBoardScreen == BoardScreen.PhotoCopy ? _scene.PhotoCopyStatus :
             _scene.CurrentBoardScreen == BoardScreen.Paint ?
                (_scene.GetPaintSaveStatus(MonotonicClock.UtcNow) ?? "Paint anywhere around the floating controls. Save keeps the artwork; Exit returns to the menu.") :
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
            ? "Photo Copy: place an object and lift your hand. Hold the up arrow to reveal EXIT, SWIRL and COPY."
            : "Photo Copy selected. Scan the board before taking a photo.");
    }

    private void PrepareBoardApp()
    {
        _calibrationResults.Invalidate();
        _annotationMode = AnnotationMode.None;
        FinishCameraPictureCalibration();
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
