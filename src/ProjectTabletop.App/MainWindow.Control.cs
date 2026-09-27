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
#if DEBUG
            case "verify_photo_copy_render":
                return await VerifyPhotoCopyRenderAsync();
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
                    outputOpen = _output is not null,
                    outputVisible = _output?.AppWindow.IsVisible ?? false,
                    outputFullScreen = _output?.IsFullScreen ?? false,
                    outputDisplayId = _output?.ActualDisplayId,
                    controlDisplayId = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                        AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.None)?.DisplayId.Value
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    boardSetupActive = Volatile.Read(ref _boardSetupActive),
                    boardClipReady = _scene.HasBoardMediaClip,
                    boardSetupStatus = BoardSetupControlStatus,
                    handTrackingEnabled = HandTrackingEnabled,
                    handTrackingStatus = HandTrackingControlStatus,
                    handCount = TrackedHandCount,
                    handExecuteActive = ExecutingHandCount > 0,
                    boardApp = _scene.CurrentBoardScreen.ToString(),
                    boardAppTitle = _scene.CurrentBoardTitle,
                    photoCopyStatus = _scene.PhotoCopyStatus,
                    photoCopyCount = _scene.PhotoCopyCount,
                    lastPhotoCopyCapture = _lastPhotoCopyCapture,
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
