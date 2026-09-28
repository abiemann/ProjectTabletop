using ProjectTabletop.App.Projection;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private Task? _paintSaveTask;
    private string? _lastSavedPaintPath;
    private static string PaintSaveDirectory => Path.Combine(PhotoCopyImageStore.DefaultDirectory, "Paint");

    // Called immediately after this frame's board selection. Rendering freezes
    // its artwork synchronously; PNG encoding and publication run asynchronously.
    private void QueuePaintSave(DateTimeOffset frameTime)
    {
        try
        {
            if (!_scene.TryTakePaintSaveRequest(frameTime, out var image)) return;
            var previous = _paintSaveTask;
            _paintSaveTask = SaveStoredPaintAsync(image, previous);
        }
        catch (Exception error)
        {
            AppLog.Write("Paint artwork snapshot", error);
            if (!_closing) UpdateBoardAppStatus();
        }
    }

    private async Task SaveStoredPaintAsync(SceneCompositor.PaintMemoryImage image, Task? previous)
    {
        try
        {
            // A new Paint session may begin while its predecessor is finishing
            // a disk write. Keep encoding bounded to one image at a time.
            if (previous is not null) await previous;
            var path = await SavePaintMemoryImageAsync(_scene, image);
            if (path is not null) _lastSavedPaintPath = path;
        }
        catch (Exception error) { AppLog.Write("Paint image save", error); }
        finally { if (!_closing) UpdateBoardAppStatus(); }
    }

    private static async Task<string?> SavePaintMemoryImageAsync(SceneCompositor scene,
        SceneCompositor.PaintMemoryImage image, string? directory = null)
    {
        if (!scene.IsPaintMemoryImageCurrent(image)) return null;
        try
        {
            var path = await PhotoCopyImageStore.SaveBgraAsync(image.Width, image.Height,
                image.Width * 4, image.BgraPixels, directory ?? PaintSaveDirectory, filenamePrefix: "paint");
            scene.CompletePaintSave(image);
            return path;
        }
        catch
        {
            scene.FailPaintSave(image);
            throw;
        }
    }
}
