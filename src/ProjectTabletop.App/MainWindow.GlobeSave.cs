using ProjectTabletop.App.Projection;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private Task? _globeSaveTask;
    private string? _lastSavedGlobePath;

    private void QueueGlobeSave()
    {
        if (_closing || _globeSaveTask is { IsCompleted: false }) return;
        try
        {
            if (_scene.TryTakeGlobeSaveRequest(out var image))
                _globeSaveTask = SaveStoredGlobeAsync(image);
        }
        catch (Exception error) { AppLog.Write("Globe artwork snapshot", error); }
    }

    private async Task SaveStoredGlobeAsync(SceneCompositor.GlobeMemoryImage image)
    {
        try { _lastSavedGlobePath = await SaveGlobeMemoryImageAsync(_scene, image); }
        catch (Exception error) { AppLog.Write("Globe image save", error); }
        finally { if (!_closing) UpdateBoardAppStatus(); }
    }

    private static async Task<string> SaveGlobeMemoryImageAsync(SceneCompositor scene,
        SceneCompositor.GlobeMemoryImage image, string? directory = null)
    {
        try
        {
            string path = await PhotoCopyImageStore.SaveBgraAsync(image.Width, image.Height,
                image.Width * 4, image.BgraPixels, directory ?? BoardImageDirectories.Globe, filenamePrefix: "globe");
            scene.CompleteGlobeSave(image, success: true);
            return path;
        }
        catch
        {
            scene.CompleteGlobeSave(image, success: false);
            throw;
        }
    }
}
