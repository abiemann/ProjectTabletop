using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using ProjectTabletop.App.Projection.WaterGarden;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private BitmapAssetSet? _waterImages;
    private bool _waterArtworkPublished;
    private byte[]? _waterRockPixels;
    private int _waterRockWidth, _waterRockHeight;
    private ShadowEffect? _waterRockShadow;

    private BitmapAssetSet GetWaterGardenImages(CanvasDevice device)
    {
        if (_waterImages is null || _waterImages.Device != device)
        {
            DisposeWaterGardenArtwork();
            _waterImages = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden"),
                ["moss-rocks.png", "warm-limestone.png", "wet-slate.png", "sand-ground.png"]);
        }
        return _waterImages;
    }

    internal async Task EnsureWaterGardenResourcesAsync(CanvasDevice device)
    {
        BitmapAssetSet images;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); images = GetWaterGardenImages(device); }
        await images.EnsureLoadedAsync().ConfigureAwait(false);
        lock (_gate)
            if (!_disposed && ReferenceEquals(images, _waterImages)) PrepareWaterGardenResources(device);
    }

    private bool PrepareWaterGardenResources(CanvasDevice device)
    {
        var images = GetWaterGardenImages(device);
        if (!images.IsLoaded) return false;
        if (_waterArtworkPublished) return images.Image("warm-limestone.png") is not null &&
            images.Image("wet-slate.png") is not null && images.Image("sand-ground.png") is not null;
        if (images.Image("moss-rocks.png") is { } rocks)
        {
            _waterRockWidth = (int)rocks.SizeInPixels.Width;
            _waterRockHeight = (int)rocks.SizeInPixels.Height;
            // Read the static artwork once for precise silhouette hit testing.
            // The live water field remains entirely on the GPU.
            _waterRockPixels = rocks.GetPixelBytes();
            _waterRockShadow = new ShadowEffect
            {
                Source = rocks, BlurAmount = 13,
                ShadowColor = Color.FromArgb(110, 16, 28, 23)
            };
        }
        foreach (var (file, error) in images.Errors)
            AppLog.Write("Water Garden artwork: " + file, new InvalidDataException(error));
        _waterArtworkPublished = true;
        _waterRenderedFrame = null;
        InvalidateBoardArtworkSurface();
        return images.Image("warm-limestone.png") is not null && images.Image("wet-slate.png") is not null &&
            images.Image("sand-ground.png") is not null;
    }

    private (Rect First, Rect Second) WaterGardenRockPlacements() =>
        WaterGardenRockLayout.GetPlacements(PaintBoardAspect(),
            _waterRockWidth / (double)Math.Max(1, _waterRockHeight));

    private void DrawWaterGardenRocks(CanvasDrawingSession ds)
    {
        if (_waterImages?.Image("moss-rocks.png") is not { } rocks) return;
        var (first, second) = WaterGardenRockPlacements();
        Draw(first); Draw(second);
        void Draw(Rect bounds)
        {
            var destination = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
                bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
            var source = new Rect(0, 0, rocks.Size.Width, rocks.Size.Height);
            if (_waterRockShadow is not null)
                ds.DrawImage(_waterRockShadow, new Rect(destination.X + 3 / PaintBoardAspect(), destination.Y + 5,
                    destination.Width, destination.Height), source);
            ds.DrawImage(rocks, destination, source);
        }
    }

    private bool IsWaterGardenRock(double u, double v)
    {
        if (_waterRockPixels is null) return false;
        var (first, second) = WaterGardenRockPlacements();
        return Contains(first) || Contains(second);
        bool Contains(Rect bounds)
        {
            double x = (u - bounds.X) / bounds.Width, y = (v - bounds.Y) / bounds.Height;
            if (x < 0 || y < 0 || x >= 1 || y >= 1) return false;
            int px = Math.Clamp((int)(x * _waterRockWidth), 0, _waterRockWidth - 1);
            int py = Math.Clamp((int)(y * _waterRockHeight), 0, _waterRockHeight - 1);
            return _waterRockPixels[(py * _waterRockWidth + px) * 4 + 3] > 32;
        }
    }

    private void DisposeWaterGardenArtwork()
    {
        // The field borrows the stone texture from this set. Drop its effect
        // references before replacing or disposing the device-owned artwork.
        if (_waterImages is not null) DisposeWaterGardenResources();
        _waterRockShadow?.Dispose(); _waterRockShadow = null;
        _waterImages?.Dispose(); _waterImages = null;
        _waterRockPixels = null;
        _waterRockWidth = _waterRockHeight = 0;
        _waterArtworkPublished = false;
    }
}
