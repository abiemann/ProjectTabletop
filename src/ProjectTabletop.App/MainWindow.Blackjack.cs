using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private void Blackjack_Click(object sender, RoutedEventArgs e) => ShowBlackjack();

    private void ShowBlackjack()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowBlackjack();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus("Blackjack ready. Aim with the middle fingertip and four fingers together; move your index finger sideways to select. Bring it back before selecting again. Pinch or laptop clicks also work.");
    }

    private void BlackjackPreview_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.WaterGarden)
            _scene.DrawWaterGardenPreview(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height);
        else if (_scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.Roulette)
            _scene.DrawRoulettePreview(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height);
        else if (_scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.Slots)
            _scene.DrawSlotsPreview(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height);
        else if (_scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.Globe)
            _scene.DrawGlobePreview(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height);
        else if (_scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.CrownDeed)
            _scene.DrawCrownDeedPreview(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height);
        else _scene.DrawBlackjackPreview(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height);
    }

    private void BlackjackPreview_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(BlackjackPreview);
        if (!point.Properties.IsLeftButtonPressed) return;
        double width = BlackjackPreview.ActualWidth, height = BlackjackPreview.ActualHeight;
        bool crownDeed = _scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.CrownDeed;
        bool globe = _scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.Globe;
        bool slots = _scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.Slots;
        bool roulette = _scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.Roulette;
        bool water = _scene.CurrentBoardScreen == ProjectTabletop.Interaction.BoardScreen.WaterGarden;
        double aspect = water ? _scene.WaterGardenPreviewAspect : roulette ? _scene.RoulettePreviewAspect : slots ? _scene.SlotsPreviewAspect : globe ? _scene.GlobePreviewAspect :
            crownDeed ? _scene.CrownDeedPreviewAspect : 1;
        double drawWidth = Math.Min(width, height * aspect), drawHeight = drawWidth / aspect;
        if (drawWidth <= 0 || drawHeight <= 0) return;
        double u = (point.Position.X - (width - drawWidth) / 2) / drawWidth;
        double v = (point.Position.Y - (height - drawHeight) / 2) / drawHeight;
        if (water ? _scene.ActivateWaterGardenAt(u, v) : roulette ? _scene.ActivateRouletteAt(u, v) : slots ? _scene.ActivateSlotsAt(u, v) : globe ? _scene.ActivateGlobeAt(u, v) :
            crownDeed ? _scene.ActivateCrownDeedAt(u, v) : _scene.ActivateBlackjackAt(u, v))
        {
            if (crownDeed) QueueCrownDeedSave();
            UpdateBoardAppStatus();
            e.Handled = true;
        }
    }

    private async Task<string> SaveBlackjackPreviewAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "BlackjackSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"blackjack-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1200, 1200, 96);
        using (var drawing = target.CreateDrawingSession()) _scene.DrawBlackjackPreview(drawing, 1200, 1200);
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
