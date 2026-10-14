using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using ProjectTabletop.App.Projection.WaterGarden;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // The simulation cannot render without these; the rock cutouts are optional.
    private static readonly string[] WaterGardenBasinFiles = ["wet-slate.png", "sand-ground.png"];
    private BitmapAssetSet? _waterImages;
    private bool _waterArtworkPublished;

    private bool WaterGardenBasinLoaded => _waterImages is { } images &&
        WaterGardenBasinFiles.All(file => images.Image(file) is not null);

    private bool WaterGardenArtworkReady => _waterArtworkPublished && WaterGardenBasinLoaded;

    private string? WaterGardenArtworkError => _waterImages is not { } images ? null :
        images.Error ?? (!images.IsLoaded ? null : WaterGardenBasinFiles
            .Where(file => images.Image(file) is null)
            .Select(file => file + ": " + images.Errors.GetValueOrDefault(file, "not loaded"))
            .FirstOrDefault());
    private byte[]? _waterRockPixels;
    private int _waterRockWidth, _waterRockHeight;
    private double _waterRockOpaqueFoot;
    private CanvasBitmap? _waterRockFootMap;
    private Transform2DEffect? _waterRockFootSource;
    private PixelShaderEffect<RockImmersionShader>? _waterRockImmersion;

    private BitmapAssetSet GetWaterGardenImages(CanvasDevice device)
    {
        if (_waterImages is null || _waterImages.Device != device)
        {
            DisposeWaterGardenArtwork();
            _waterImages = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden"),
                ["moss-rocks.png", "wet-slate.png", "sand-ground.png"]);
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
        if (_waterArtworkPublished) return WaterGardenBasinLoaded;
        if (images.Image("moss-rocks.png") is { } rocks)
        {
            _waterRockWidth = (int)rocks.SizeInPixels.Width;
            _waterRockHeight = (int)rocks.SizeInPixels.Height;
            // Read the static artwork once for precise silhouette hit testing.
            // The live water field remains entirely on the GPU.
            _waterRockPixels = rocks.GetPixelBytes();
            // One static column footprint lets water wrap each irregular rock
            // foot without tinting a rectangular band or copying live frames.
            var feet = new byte[_waterRockWidth * 4];
            int lowest = 0;
            for (int x = 0; x < _waterRockWidth; x++)
            {
                int bottom = 0;
                for (int y = _waterRockHeight - 1; y >= 0; y--)
                    if (_waterRockPixels[(y * _waterRockWidth + x) * 4 + 3] > 32)
                    { bottom = y + 1; break; }
                lowest = Math.Max(lowest, bottom);
                feet[x * 4 + 1] = (byte)(bottom & 255);
                feet[x * 4 + 2] = (byte)(bottom >> 8);
                feet[x * 4 + 3] = 255;
            }
            _waterRockOpaqueFoot = lowest / (double)_waterRockHeight;
            _waterRockFootMap = CanvasBitmap.CreateFromBytes(device, feet, _waterRockWidth, 1,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _waterRockFootSource = new Transform2DEffect
            { Source = _waterRockFootMap, TransformMatrix = Matrix3x2.CreateScale(1, _waterRockHeight) };
            _waterRockImmersion = new PixelShaderEffect<RockImmersionShader>();
            _waterRockImmersion.Sources[0] = rocks;
            _waterRockImmersion.Sources[1] = _waterRockFootSource;
        }
        foreach (var (file, error) in images.Errors)
            AppLog.Write("Water Garden artwork: " + file, new InvalidDataException(error));
        _waterArtworkPublished = true;
        _waterRenderedFrame = null;
        InvalidateBoardArtworkSurface();
        return WaterGardenBasinLoaded;
    }

    private (WaterGardenRockPlacement First, WaterGardenRockPlacement Second)
        WaterGardenRockPlacements(float entranceProgress = 1) =>
        WaterGardenRockLayout.GetPlacements(PaintBoardAspect(),
            _waterRockWidth / (double)Math.Max(1, _waterRockHeight), _waterRockOpaqueFoot, entranceProgress);

    private void DrawWaterGardenRocks(CanvasDrawingSession ds)
    {
        if (_waterImages?.Image("moss-rocks.png") is not { } rocks || _waterRockImmersion is null) return;
        var (first, second) = WaterGardenRockPlacements(_waterEntranceProgress);
        Draw(first); Draw(second);
        void Draw(WaterGardenRockPlacement placement)
        {
            var bounds = placement.Bounds;
            var destination = new Rect(bounds.X * BoardSurfaceSize, bounds.Y * BoardSurfaceSize,
                bounds.Width * BoardSurfaceSize, bounds.Height * BoardSurfaceSize);
            var source = new Rect(0, 0, rocks.Size.Width, rocks.Size.Height);
            _waterRockImmersion.ConstantBuffer = new RockImmersionShader(
                new Float2(_waterRockWidth, _waterRockHeight),
                (float)(placement.SubmergedHeight / bounds.Height * _waterRockHeight));
            ds.DrawImage(_waterRockImmersion, destination, source);
        }
    }

    private bool IsWaterGardenRock(double u, double v)
    {
        if (_waterRockPixels is null) return false;
        var (first, second) = WaterGardenRockPlacements();
        return Contains(first.Bounds) || Contains(second.Bounds);
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
        _waterRockImmersion?.Dispose(); _waterRockImmersion = null;
        _waterRockFootSource?.Dispose(); _waterRockFootSource = null;
        _waterRockFootMap?.Dispose(); _waterRockFootMap = null;
        _waterImages?.Dispose(); _waterImages = null;
        _waterRockPixels = null;
        _waterRockWidth = _waterRockHeight = 0;
        _waterRockOpaqueFoot = 0;
        _waterArtworkPublished = false;
    }
}
