using Microsoft.Graphics.Canvas;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private BitmapAssetSet? _rouletteImages;
    private bool _rouletteArtworkPublished;

    private BitmapAssetSet GetRouletteImages(CanvasDevice device)
    {
        if (_rouletteImages is null || _rouletteImages.Device != device)
        {
            DisposeRouletteArtwork();
            _rouletteImages = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "Roulette"),
                ["vice-royale-casino.png", "roulette-burl-wood.png"]);
        }
        return _rouletteImages;
    }

    internal async Task EnsureRouletteResourcesAsync(CanvasDevice device)
    {
        BitmapAssetSet images;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); images = GetRouletteImages(device); }
        await images.EnsureLoadedAsync().ConfigureAwait(false);
        lock (_gate)
            if (!_disposed && ReferenceEquals(images, _rouletteImages)) PrepareRouletteResources(device);
    }

    private bool PrepareRouletteResources(CanvasDevice device)
    {
        var images = GetRouletteImages(device);
        if (!images.IsLoaded) return false;
        if (_rouletteArtworkPublished) return true;
        _rouletteBackdrop = images.Image("vice-royale-casino.png");
        _rouletteBurlBitmap = images.Image("roulette-burl-wood.png");
        foreach (var (file, error) in images.Errors)
            AppLog.Write("Roulette artwork: " + file, new InvalidDataException(error));
        // The optional texture's fallback is tiny and created once. PNG
        // decoding and file access never occur in a drawing callback.
        _rouletteFallbackBurl = _rouletteBurlBitmap is null
            ? CanvasBitmap.CreateFromColors(device, [ThemeColor(123, 69, 39)], 1, 1, 96) : null;
        _rouletteBurlBitmap ??= _rouletteFallbackBurl;
        _rouletteMaterialDevice = device;
        _rouletteArtworkPublished = true;
        InvalidateBoardArtworkSurface();
        return true;
    }

    private CanvasBitmap? _rouletteFallbackBurl;

    private void DisposeRouletteArtwork()
    {
        DisposeRouletteMaterials();
        _rouletteImages?.Dispose(); _rouletteImages = null;
        _rouletteFallbackBurl?.Dispose(); _rouletteFallbackBurl = null;
        _rouletteBackdrop = null;
        _rouletteBurlBitmap = null;
        _rouletteArtworkPublished = false;
    }
}
