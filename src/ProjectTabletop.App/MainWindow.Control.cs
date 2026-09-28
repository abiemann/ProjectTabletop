using System.Text.Json;
using ProjectTabletop.App.Control;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private ControlPipeHost? _controlHost;

    private void StartControlHost() =>
        _controlHost ??= new ControlPipeHost(DispatchControlAsync);

    private Task<object?> DispatchControlAsync(string method, JsonElement parameters, CancellationToken token)
    {
        var commandParameters = parameters.Clone();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { completion.TrySetResult(await HandleControlOnUiAsync(method, commandParameters)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }))
            completion.TrySetException(new InvalidOperationException("The app UI is unavailable."));
        return completion.Task.WaitAsync(token);
    }

    private async Task<object?> HandleControlOnUiAsync(string method, JsonElement parameters)
    {
        if (_closing) throw new InvalidOperationException("The app is closing.");
        switch (method)
        {
            case "set_projection_size":
                return SetProjectionSize(parameters);
            case "set_board_facing":
                return SetBoardFacingForControl(parameters);
#if DEBUG
            case "set_hand_diagnostic_logging":
                _boardHandDiagnosticLogging = parameters.GetProperty("enabled").GetBoolean();
                LogHandTrackingEvent("board_diagnostic_logging", new { enabled = _boardHandDiagnosticLogging }, force: true);
                return new { enabled = _boardHandDiagnosticLogging, status = _handDetectionLog?.Status };
            case "verify_photo_copy_memory_save":
                return await VerifyPhotoCopyMemorySaveAsync();
            case "verify_board_resolution":
                return await VerifyBoardResolutionAsync();
            case "verify_board_orientation":
                return await VerifyBoardOrientationAsync();
            case "play_photo_copy_sound":
                _photocopierSound.Play();
                return _photocopierSound.Status;
            case "refresh_display_audio":
                await RefreshDisplayAudioAsync();
                return DisplayAudioDiagnostics;
            case "set_display_audio":
                SetDisplayAudioEnabled(parameters.GetProperty("enabled").GetBoolean());
                return DisplayAudioDiagnostics;
            case "capture_photo_copy_diagnostics":
                return await SavePhotoCopyDiagnosticsAsync();
            case "capture_photo_copy_now":
                return await CapturePhotoCopyForVerificationAsync();
            case "capture_projection_settings":
                string settingsSnapshots = Path.Combine(_appDataDirectory, "ProjectionSettingsSnapshots", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(settingsSnapshots);
                return await SaveLaptopThemeSnapshotAsync(settingsSnapshots, ProjectionOutputPanel);
            case "verify_projection_window":
                return await VerifyProjectionWindowAsync();
            case "verify_hand_tracking_input":
                return VerifyHandTrackingInput();
            case "verify_hand_pose_feedback":
                return await VerifyHandPoseFeedbackAsync();
            case "verify_hand_spotlights":
                return await VerifyHandSpotlightsAsync();
            case "verify_hand_spotlight_selection":
                return await VerifyHandSpotlightSelectionAsync();
            case "verify_hand_visual_smoothing":
                return await VerifyHandVisualSmoothingAsync();
            case "verify_hand_acquisition":
                return await VerifyHandAcquisitionAsync();
            case "verify_photo_copy_acquisition":
                return await VerifyPhotoCopyAcquisitionAsync();
            case "verify_shared_board_acquisition":
                return await VerifySharedBoardAcquisitionAsync(parameters.TryGetProperty("board", out var acquisitionBoard)
                    ? acquisitionBoard.GetString() : null);
            case "verify_paint":
                return await VerifyPaintAsync();
            case "verify_paint_save":
                return await VerifyPaintSaveAsync();
            case "capture_hand_acquisition":
                return await SaveHandAcquisitionSnapshotAsync();
            case "verify_hand_detection_log":
                return await HandDetectionLogVerification.RunAsync(Path.Combine(_appDataDirectory,
                    "LogVerification", Guid.NewGuid().ToString("N")));
            case "verify_hand_video_recording":
                return await HandTrackingVideoRecorderVerification.RunAsync(Path.Combine(_appDataDirectory,
                    "VideoVerification", Guid.NewGuid().ToString("N")));
            case "verify_theme":
                return await VerifyThemeAsync();
            case "verify_blackjack":
                return await VerifyBlackjackAsync();
            case "verify_monopoly":
                return await VerifyMonopolyAsync();
            case "verify_monopoly_dice":
                return await VerifyMonopolyDiceAnimationAsync();
            case "verify_globe":
                return await VerifyGlobeAsync();
            case "verify_blackjack_animation":
                return await VerifyBlackjackAnimationAsync();
            case "verify_board_reveal":
                return await VerifyBoardRevealAsync();
            case "verify_board_size":
                return await VerifyBoardSizeAsync();
            case "verify_projection_sizing":
                return VerifyProjectionSizing();
            case "verify_finger_selection":
                return await VerifyFingerSelectionAsync();
            case "verify_photo_copy_render":
                return await VerifyPhotoCopyRenderAsync();
            case "verify_photo_copy_save":
                return await VerifyPhotoCopySaveAsync();
            case "verify_photo_copy_actions":
                return await VerifyPhotoCopyActionsAsync();
            case "verify_photo_copy_gestures":
                return await VerifyPhotoCopyGesturesAsync();
            case "verify_photo_copy_proportions":
                return await VerifyPhotoCopyProportionsAsync();
#endif
            case "get_status":
                var frame = Volatile.Read(ref _latestCameraFrame);
                return new
                {
                    cameraRunning = _camera.IsRunning,
                    cameraStatus = CameraStatusText.Text,
                    cameraDevice = SelectedCamera?.Device.DisplayName,
                    cameraFrameUtc = frame?.Timestamp,
                    cameraWidth = frame?.Width,
                    cameraHeight = frame?.Height,
                    cameraHealthWarning = _cameraHealthWarning,
                    display = SelectedDisplay?.ToString(),
                    projectionSetup = new
                    {
                        displayProperties = SelectedDisplay?.PhysicalMode,
                        lensHeightCentimeters = EnteredProjectionProfile().LensHeightCentimeters,
                        throwRatio = EnteredProjectionProfile().ThrowRatio,
                        measuredBoardShortSideCentimeters = EnteredProjectionProfile().MeasuredBoardShortSideCentimeters,
                        measuredBoardLongSideCentimeters = EnteredProjectionProfile().MeasuredBoardLongSideCentimeters,
                        boardFacingDegrees = _boardFacingDegrees,
                        profileSavedPerOutput = ProjectTabletop.App.Projection.ProjectionSizeProfile.PersistentKey(SelectedDisplay?.PhysicalMode) is not null,
                        settingsPath = ProjectionSetupPath,
                        settingsInitialized = _projectionSetupInitialized,
                        activeProfileKey = _projectionProfileKey,
                        settingsError = _projectionSettingsError,
                        estimateSource = BoardSizeSource,
                        boardSizeEstimate = CurrentBoardSizeEstimate(),
                        opticalBoardSizeEstimate = CurrentOpticalBoardSizeEstimate(),
                        status = BoardSizeEstimateText.Text
                    },
                    outputOpen = _output is not null,
                    photoCopyAudio = _photocopierSound.Status,
                    displayAudio = DisplayAudioDiagnostics,
                    outputVisible = _output?.AppWindow.IsVisible ?? false,
                    outputFullScreen = _output?.IsFullScreen ?? false,
                    outputAlwaysOnTop = _output?.IsAlwaysOnTop ?? false,
                    outputDisplayId = _output?.ActualDisplayId,
                    controlDisplayId = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                        AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.None)?.DisplayId.Value
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    boardSetupActive = Volatile.Read(ref _boardSetupActive),
                    boardClipReady = _scene.HasBoardMediaClip,
                    boardSetupStatus = BoardSetupControlStatus,
                    boardReveal = _scene.GetBoardRevealDiagnostics(),
            boardResolution = _scene.GetBoardResolutionDiagnostics(),
            outputRendering = _output?.RenderResolution,
                    handTrackingEnabled = HandTrackingEnabled,
                    handSpotlightCount = _scene.ActiveHandSpotlightCount,
                    handTrackingStatus = HandTrackingControlStatus,
                    handCount = TrackedHandCount,
                    handExecuteActive = ExecutingHandCount > 0,
                    spreadOutHandCount = SpreadOutHandCount,
                    fourFingerHandCount = FourFingerHandCount,
                    fingerSelectionFeedback = _scene.CurrentFingerSelectionFeedback,
                    lastBoardSelection = _scene.GetLastHandBoardSelection(),
                    handTestStatus = _scene.HandTrackingTestStatus,
                    lastHandDetection = _lastHandDetection,
                    handDetectionLog = _handDetectionLog?.Status,
                    paint = _scene.GetPaintDiagnostics(),
                    paintInput = _scene.GetPaintInputDiagnostics(),
                    paintSaveStatus = _scene.GetPaintSaveStatus(DateTimeOffset.UtcNow),
                    lastSavedPaintPath = _lastSavedPaintPath,
                    paintSaveDirectory = PaintSaveDirectory,
                    lastPaintDetection = _lastPaintDetection,
                    handVideoRecording = _handVideoRecorder?.Status,
                    handLighting = _scene.GetHandLightingDiagnostics(),
                    handAcquisition = new { lighting = _scene.GetHandAcquisitionDiagnostics(),
                        lastDetection = _lastHandAcquisitionDetection },
                    boardApp = _scene.CurrentBoardScreen.ToString(),
                    boardAppTitle = _scene.CurrentBoardTitle,
                    blackjack = _scene.BlackjackState,
                    monopoly = _scene.MonopolyState,
                    globe = _scene.GlobeState,
                    monopolySavePath = MonopolySavePath,
                    monopolySaveError = _monopolySaveError,
                    blackjackAnimation = _scene.GetBlackjackAnimationDiagnostics(),
                    monopolyDiceAnimation = _scene.GetMonopolyDiceAnimationDiagnostics(DateTimeOffset.UtcNow),
                    hoveredBoardButtons = _scene.HoveredBoardButtons,
                    photoCopyStatus = _scene.PhotoCopyStatus,
                    photoCopyLighting = _scene.PhotoCopyLightingStatus,
                    photoCopyObservation = _lastPhotoCopyObservation,
                    photoCopyCount = _scene.PhotoCopyCount,
                    lastPhotoCopyCapture = _lastPhotoCopyCapture,
                    lastSavedPhotoPath = _lastSavedPhotoPath,
                    photoCopySaveDirectory = PhotoCopyImageStore.DefaultDirectory,
                    renderStatus = RenderStatusText.Text,
                    status = StatusText.Text
                };
            case "set_hand_tracking":
                if (!parameters.TryGetProperty("enabled", out var handEnabled) ||
                    handEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Provide enabled as a JSON boolean.");
                SetHandTrackingEnabled(handEnabled.GetBoolean());
                return new { handTrackingEnabled = HandTrackingEnabled,
                    handTrackingStatus = HandTrackingControlStatus, handCount = TrackedHandCount,
                    handExecuteActive = ExecutingHandCount > 0 };
            case "start_board_scan":
                await StartBoardSetupAsync();
                return new { status = BoardSetupControlStatus };
            case "rescan_board":
                if (Volatile.Read(ref _boardSetupActive)) await RescanBoardSetupAsync();
                else await StartBoardSetupAsync();
                return new { status = BoardSetupControlStatus };
            case "black_output":
                if (_output is null || !_output.IsFullScreen || _outputDisplayId != SelectedDisplay?.Id)
                    throw new InvalidOperationException("Open the selected projector output in full screen first.");
                ShowBlackOutputForControl();
                return new { outputOpen = _output is not null, outputFullScreen = _output?.IsFullScreen ?? false,
                    cameraRunning = _camera.IsRunning, status = StatusText.Text };
            case "stop_scan":
                StopBoardSetup();
                return new { status = BoardSetupControlStatus };
            case "set_background_media":
                var path = parameters.TryGetProperty("path", out var pathElement) &&
                    pathElement.ValueKind == JsonValueKind.String ? pathElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("Provide the absolute local media path.");
                await SetBackgroundFromPathAsync(path);
                return new { background = _scene.BackgroundLabel,
                    boardClipReady = _scene.HasBoardMediaClip, status = StatusText.Text };
            case "show_test_grid":
                TestGrid_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs());
                return new { boardClipReady = _scene.HasBoardMediaClip, status = StatusText.Text };
            case "show_board_menu":
                ShowBoardMenu();
                return new { boardApp = _scene.CurrentBoardScreen.ToString(),
                    boardClipReady = _scene.HasBoardMediaClip, status = StatusText.Text };
            case "show_hand_tracking_test":
                ShowHandTrackingTest();
                return new { boardApp = _scene.CurrentBoardScreen.ToString(),
                    boardClipReady = _scene.HasBoardMediaClip, status = StatusText.Text };
            case "capture_projection_preview":
                return new { path = await SaveProjectionPreviewAsync() };
            case "show_photo_copy":
                ShowPhotoCopy();
                return new { boardApp = _scene.CurrentBoardScreen.ToString(),
                    boardClipReady = _scene.HasBoardMediaClip, status = StatusText.Text };
            case "show_paint":
                ShowPaint();
                return new { boardApp = _scene.CurrentBoardScreen.ToString(),
                    boardClipReady = _scene.HasBoardMediaClip, status = StatusText.Text };
            case "show_blackjack":
                ShowBlackjack();
                return new { boardApp = _scene.CurrentBoardScreen.ToString(), blackjack = _scene.BlackjackState };
            case "blackjack_action":
                if (!parameters.TryGetProperty("id", out var action) || action.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Provide a Blackjack button id.");
                bool accepted = _scene.ActivateBlackjackButton(action.GetString()!);
                UpdateBoardAppStatus();
                return new { accepted, blackjack = _scene.BlackjackState };
            case "capture_blackjack_preview":
                return new { path = await SaveBlackjackPreviewAsync() };
            case "show_monopoly":
                ShowMonopoly();
                return new { boardApp = _scene.CurrentBoardScreen.ToString(), monopoly = _scene.MonopolyState };
            case "monopoly_action":
                if (!parameters.TryGetProperty("id", out var monopolyAction) || monopolyAction.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Provide a Monopoly button id.");
                bool monopolyAccepted = _scene.ActivateMonopolyButton(monopolyAction.GetString()!);
                QueueMonopolySave();
                UpdateBoardAppStatus();
                return new { accepted = monopolyAccepted, monopoly = _scene.MonopolyState };
            case "capture_monopoly_preview":
                return new { path = await SaveMonopolyPreviewAsync() };
            case "show_globe":
                ShowGlobe();
                return new { boardApp = _scene.CurrentBoardScreen.ToString(), globe = _scene.GlobeState };
            case "globe_action":
                if (!parameters.TryGetProperty("id", out var globeAction) || globeAction.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Provide a Globe button id.");
                bool globeAccepted = _scene.ActivateGlobeButton(globeAction.GetString()!);
                UpdateBoardAppStatus();
                return new { accepted = globeAccepted, globe = _scene.GlobeState };
            case "capture_globe_preview":
                return new { path = await SaveGlobePreviewAsync() };
            case "capture_raw_frame":
                return new { path = await SaveRawSnapshotAsync() };
            case "start_camera":
                return new { started = await StartSelectedCameraAsync(), status = CameraStatusText.Text };
            case "stop_camera":
                await StopCameraAsync();
                return new { status = CameraStatusText.Text };
            case "open_output":
                await OpenProjectionOutputAsync();
                return new { outputOpen = _output is not null, outputFullScreen = _output?.IsFullScreen ?? false,
                    status = StatusText.Text };
            case "shutdown":
                var queue = DispatcherQueue;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(350);
                    queue.TryEnqueue(() => Close());
                });
                return new { status = "App shutdown scheduled." };
            default:
                throw new ArgumentException("Unknown control method: " + method);
        }
    }
}
