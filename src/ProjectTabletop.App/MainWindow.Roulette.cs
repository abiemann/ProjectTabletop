using Microsoft.Graphics.Canvas;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void ShowRoulette()
    {
        StopBoardSetup(); PrepareBoardApp(); _scene.ShowRoulette();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus("Vice Royale Roulette. Choose a chip, then select numbers or outside bets. Hold SPIN for a second.");
    }

    private object RouletteStatus()
    {
        var game = _scene.RouletteState;
        return new { phase = game.Phase.ToString(), game.Balance, game.Chip, game.TotalBet, game.LastWin,
            game.LastProfit, game.RoundNumber, game.Status, game.Bets, game.History, game.AvailableActions };
    }

    private async Task<string> SaveRoulettePreviewAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "RouletteSnapshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"roulette-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1440, 1120, 96);
        using (var ds = target.CreateDrawingSession()) _scene.DrawRoulettePreview(ds, 1440, 1120);
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
