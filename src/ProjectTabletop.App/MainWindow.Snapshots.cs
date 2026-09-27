using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Graphics.DirectX;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private async void SaveRawSnapshot_Click(object sender, RoutedEventArgs e)
    {
        try { SetStatus("Saved full-resolution raw camera PNG to " + await SaveRawSnapshotAsync()); }
        catch (Exception ex) { SetStatus("Raw camera snapshot could not be saved: " + ex.Message); }
    }

    internal async Task<string> SaveRawSnapshotAsync()
    {
        var frame = _camera.CaptureSnapshot();
        if (frame is null)
            throw new InvalidOperationException("Start the webcam and wait for a frame before saving a raw snapshot.");
        var directory = Path.Combine(_appDataDirectory, "RawSnapshots");
        Directory.CreateDirectory(directory);
        var name = $"camera-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{frame.Width}x{frame.Height}-{Guid.NewGuid():N}.png";
        var path = Path.Combine(directory, name);
        using var bitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), frame.Bgra,
            frame.Width, frame.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
            CanvasAlphaMode.Ignore);
        await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return path;
    }
}
