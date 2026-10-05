using Microsoft.Graphics.Canvas;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async Task WarmMenuPreviewResourcesAsync()
    {
        try { await _scene.EnsureMenuPreviewResourcesAsync(CanvasDevice.GetSharedDevice()); }
        catch (Exception error)
        {
            if (!_closing) AppLog.Write("Load menu thumbnails", error);
        }
    }
}
