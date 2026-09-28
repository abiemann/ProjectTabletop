using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task WarmGlobeResourcesAsync()
    {
        try { await _scene.EnsureGlobeResourcesAsync(CanvasDevice.GetSharedDevice()); }
        catch (Exception error)
        {
            if (!_closing) AppLog.Write("Load Globe imagery", error);
        }
    }

    private void Globe_Click(object sender, RoutedEventArgs e) => ShowGlobe();

    private void ShowGlobe()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowGlobe();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus("Globe ready. Zoom into Earth or rotate left and right. Aim with four fingers together, then move your index sideways to select. Laptop clicks also work.");
    }

    private async Task<string> SaveGlobePreviewAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "GlobeSnapshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"globe-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        var device = CanvasDevice.GetSharedDevice();
        await _scene.EnsureGlobeResourcesAsync(device);
        const float width = 3840, height = 2160;
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (var drawing = target.CreateDrawingSession()) _scene.DrawGlobePreview(drawing, width, height);
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
