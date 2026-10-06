using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void Slots_Click(object sender, RoutedEventArgs e) => ShowSlots();

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowSlots()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowSlots();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus("Dragon Slots ready. Rest your fingers on SPIN for a second to spin; every control is a long press. Laptop clicks also work.");
    }

    private void ShowSettings()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowSettings();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus(_scene.HasBoardMediaClip
            ? "Settings ready. Open Hand-Tracking or Licenses; license documents open on the laptop. Back returns to the menu."
            : "Settings selected. Licenses and notices are available here on the laptop; start board setup to project Settings.");
    }

    // A compact view for control clients; the full grid is in capture_slots_preview.
    private object SlotsStatus()
    {
        var game = _scene.SlotsState;
        return new
        {
            phase = game.Phase.ToString(), game.Balance, game.Bet, game.RoundWin, game.Status, game.Banner,
            game.InRespins, game.RespinsLeft, game.RespinTotal, game.InFreeSpins, game.FreeSpinsRemaining,
            game.ElixirLevel, keys = game.Keys, game.BuyCost, game.AvailableActions, game.SpinNumber,
            lineWins = game.LineWins.Select(win => new { win.Line, symbol = win.Symbol.ToString(), win.Count, win.Amount }),
            grid = Enumerable.Range(0, ProjectTabletop.Interaction.SlotGame.Rows).Where(game.IsActiveRow)
                .Select(row => string.Join(" ", Enumerable.Range(0, ProjectTabletop.Interaction.SlotGame.Reels)
                    .Select(reel => game.Cell(reel, row) is var cell && cell.IsBonus
                        ? $"{cell.Symbol}:{ProjectTabletop.Interaction.SlotGame.Format(cell.Value)}" : cell.Symbol.ToString())))
        };
    }

    private async Task<string> SaveSlotsPreviewAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "SlotsSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"slots-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        const float width = 1920, height = 1200;
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using (var drawing = target.CreateDrawingSession()) _scene.DrawSlotsPreview(drawing, width, height);
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
