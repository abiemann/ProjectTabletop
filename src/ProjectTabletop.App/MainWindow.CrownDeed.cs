using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private Task? _crownDeedSaveTask;
    private long _queuedCrownDeedSaveRequest;
    private string? _crownDeedSaveError;
    private string CrownDeedSavePath => Path.Combine(_appDataDirectory, "CrownAndDeed", "saved-game.json");

    private void CrownDeed_Click(object sender, RoutedEventArgs e) => ShowCrownDeed();

    private void ShowCrownDeed()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowCrownDeed();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_crownDeedSaveError is { } error ? "Crown & Deed: " + error :
            "Crown & Deed ready. Select Start Game to choose human and AI players. Roll with the dice button; aim with four fingers together, then move your index sideways.");
    }

    private async Task InitializeCrownDeedSaveAsync()
    {
        try
        {
            // Keep the existing Crown & Deed installation's save location. The
            // typed reader handles payload versions without changing this file
            // until the player explicitly saves again.
            string? json = await CrownDeedSaveStore.LoadAsync(CrownDeedSavePath);
            if (!_closing && json is not null) _scene.LoadCrownDeedSave(json);
        }
        catch (Exception error)
        {
            _crownDeedSaveError = "The saved game could not be opened. Start a new game to continue.";
            AppLog.Write("Load Crown & Deed game", error);
        }
    }

    private void QueueCrownDeedSave()
    {
        if (_closing || _crownDeedSaveTask is { IsCompleted: false }) return;
        long requestId = 0;
        try
        {
            if (!_scene.TryGetCrownDeedSaveRequest(out requestId, out string json) ||
                requestId == _queuedCrownDeedSaveRequest) return;
            _queuedCrownDeedSaveRequest = requestId;
            _crownDeedSaveTask = SaveCrownDeedGameAsync(requestId, json);
        }
        catch (Exception error)
        {
            _crownDeedSaveError = error.Message;
            AppLog.Write("Prepare Crown & Deed save", error);
            if (requestId > 0) _scene.CompleteCrownDeedSave(requestId, success: false, "Save failed. Please try again.");
        }
    }

    private async Task SaveCrownDeedGameAsync(long requestId, string json)
    {
        try
        {
            await CrownDeedSaveStore.SaveAsync(CrownDeedSavePath, json);
            _crownDeedSaveError = null;
            if (!_closing) _scene.CompleteCrownDeedSave(requestId, success: true);
        }
        catch (Exception error)
        {
            _crownDeedSaveError = "Save failed. Your game is still open; please try again.";
            AppLog.Write("Save Crown & Deed game", error);
            if (!_closing) _scene.CompleteCrownDeedSave(requestId, success: false, _crownDeedSaveError);
        }
        finally { if (!_closing) UpdateBoardAppStatus(); }
    }

    private async Task<string> SaveCrownDeedPreviewAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "CrownAndDeedSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"crown-deed-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1600, 1600, 96);
        using (var drawing = target.CreateDrawingSession()) _scene.DrawCrownDeedPreview(drawing, 1600, 1600);
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
