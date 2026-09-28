using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private Task? _monopolySaveTask;
    private long _queuedMonopolySaveRequest;
    private string? _monopolySaveError;
    private string MonopolySavePath => Path.Combine(_appDataDirectory, "Monopoly", "saved-game.json");

    private void Monopoly_Click(object sender, RoutedEventArgs e) => ShowMonopoly();

    private void ShowMonopoly()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowMonopoly();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_monopolySaveError is { } error ? "Monopoly: " + error :
            "Monopoly ready. Select Start Game to choose human and AI players. Roll with the dice button; aim with four fingers together, then move your index sideways.");
    }

    private async Task InitializeMonopolySaveAsync()
    {
        try
        {
            string? json = await MonopolySaveStore.LoadAsync(MonopolySavePath);
            if (!_closing && json is not null) _scene.LoadMonopolySave(json);
        }
        catch (Exception error)
        {
            _monopolySaveError = "The saved game could not be opened. Start a new game to continue.";
            AppLog.Write("Load Monopoly game", error);
        }
    }

    private void QueueMonopolySave()
    {
        if (_closing || _monopolySaveTask is { IsCompleted: false }) return;
        long requestId = 0;
        try
        {
            if (!_scene.TryGetMonopolySaveRequest(out requestId, out string json) ||
                requestId == _queuedMonopolySaveRequest) return;
            _queuedMonopolySaveRequest = requestId;
            _monopolySaveTask = SaveMonopolyGameAsync(requestId, json);
        }
        catch (Exception error)
        {
            _monopolySaveError = error.Message;
            AppLog.Write("Prepare Monopoly save", error);
            if (requestId > 0) _scene.CompleteMonopolySave(requestId, success: false, "Save failed. Please try again.");
        }
    }

    private async Task SaveMonopolyGameAsync(long requestId, string json)
    {
        try
        {
            await MonopolySaveStore.SaveAsync(MonopolySavePath, json);
            _monopolySaveError = null;
            if (!_closing) _scene.CompleteMonopolySave(requestId, success: true);
        }
        catch (Exception error)
        {
            _monopolySaveError = "Save failed. Your game is still open; please try again.";
            AppLog.Write("Save Monopoly game", error);
            if (!_closing) _scene.CompleteMonopolySave(requestId, success: false, _monopolySaveError);
        }
        finally { if (!_closing) UpdateBoardAppStatus(); }
    }

    private async Task<string> SaveMonopolyPreviewAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "MonopolySnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"monopoly-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1600, 1600, 96);
        using (var drawing = target.CreateDrawingSession()) _scene.DrawMonopolyPreview(drawing, 1600, 1600);
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
