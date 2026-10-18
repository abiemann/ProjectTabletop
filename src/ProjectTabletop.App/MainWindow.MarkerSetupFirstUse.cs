using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly MarkerSetupFirstUse _markerSetupFirstUse = new();
    private long _markerSetupFirstUseEntrySequence;
    private bool _markerSetupFirstUseActive;

    private void BoardOpenedForMarkerSetup(BoardScreen board)
    {
        long entry = Interlocked.Increment(ref _markerSetupFirstUseEntrySequence);
        // BoardOpened can run under the scene lock. Profile and camera access
        // belongs on the UI thread, after navigation and its lock have finished.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || entry != Volatile.Read(ref _markerSetupFirstUseEntrySequence) ||
                _scene.CurrentBoardScreen != board) return;
            // Cancel the old visit before arming this one. A later timer must
            // not dismiss the newly opened board while retiring its predecessor.
            // Manual setup may already have opened its matching board/session.
            if (_markerSetupSession is not null && _markerSetupBoard != board)
                CancelMarkerSetup("Board changed. Your saved marker is unchanged.");
            _markerSetupFirstUse.EnterBoard(board);
            TryBeginFirstUseMarkerSetup();
        });
    }

    private MarkerSetupFirstUseContext FirstUseMarkerContext(string camera, bool ready)
    {
        bool doubleBarTrained;
        lock (_markerTrackingGate)
            doubleBarTrained = DoubleBarProfileSelection.Usable(_wandMarkerProfiles.GetValueOrDefault(camera)) is not null;
        bool player1UsesMarker, player2UsesMarker, player1Trained, player2Trained;
        lock (_footballInputGate)
        {
            var profiles = _footballMarkerProfiles.GetValueOrDefault(camera);
            player1UsesMarker = !_footballFingerInputs[0];
            player2UsesMarker = !_footballFingerInputs[1];
            player1Trained = profiles?.Player1 is { IsValid: true };
            player2Trained = profiles?.Player2 is { IsValid: true };
        }
        return new(_scene.CurrentBoardScreen, ready, _markerSetupSession is not null, doubleBarTrained,
            player1UsesMarker, player1Trained, _scene.FootballState.Mode == FootballMode.TwoHumans,
            player2UsesMarker, player2Trained);
    }

    private void TryBeginFirstUseMarkerSetup()
    {
        if (_closing || _markerSetupSession is not null ||
            _scene.CurrentBoardScreen is not (BoardScreen.WaterGarden or BoardScreen.Paint or BoardScreen.Football) ||
            IsLearningDoubleBarMarker || IsLearningFootballTip ||
            !_cameraWanted || !_camera.IsRunning || _cameraHealthWarning || _cameraManualRestartRequired ||
            _cameraOperation.CurrentCount == 0 || _camera.ActiveDeviceId is not { } camera ||
            camera != _selectedCameraId || camera != _cameraWantedDeviceId ||
            IsBoardScanMeasuring || _output?.AppWindow.IsVisible != true || !_scene.MarkerSetupReady) return;
        using var lease = AcquireLatestCameraFrame();
        bool fresh = lease?.Frame is { } frame && MarkerFrameFresh(frame, MonotonicClock.UtcNow);
        int? target = _markerSetupFirstUse.NextTarget(FirstUseMarkerContext(camera, fresh));
        if (target is null) return;
        MarkerSetupTargetCombo.SelectedIndex = target.Value;
        BeginMarkerSetup(recheck: false, firstUse: true);
    }

    // Do not force a player back to marker mode if they choose index-finger
    // input, or turn player two into the computer, while the trainer is open.
    private bool FirstUseMarkerInputStillSelected()
    {
        if (!_markerSetupFirstUseActive || _markerSetupTarget == 0) return true;
        lock (_footballInputGate)
            return !_footballFingerInputs[_markerSetupTarget - 1] &&
                (_markerSetupTarget == 1 || _scene.FootballState.Mode == FootballMode.TwoHumans);
    }
}
