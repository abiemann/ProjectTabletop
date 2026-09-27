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

    private void BlackjackPreview_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args) =>
        _scene.DrawBlackjackPreview(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height);

    private void BlackjackPreview_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(BlackjackPreview);
        if (!point.Properties.IsLeftButtonPressed) return;
        double width = BlackjackPreview.ActualWidth, height = BlackjackPreview.ActualHeight;
        double size = Math.Min(width, height);
        if (size <= 0) return;
        double u = (point.Position.X - (width - size) / 2) / size;
        double v = (point.Position.Y - (height - size) / 2) / size;
        if (_scene.ActivateBlackjackAt(u, v))
        {
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
