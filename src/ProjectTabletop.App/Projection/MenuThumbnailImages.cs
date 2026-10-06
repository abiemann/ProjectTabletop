using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

/// <summary>
/// Pre-rendered menu artwork, decoded away from the rendering thread. The set
/// is published together; a missing or damaged file only leaves its tile bare.
/// </summary>
internal sealed class MenuThumbnailImages : IDisposable
{
    private static readonly (BoardScreen Screen, string File)[] Assets =
    [
        (BoardScreen.HandTracking, "hand-tracking.png"),
        (BoardScreen.PhotoCopy, "photo-copy.png"),
        (BoardScreen.Blackjack, "blackjack.png"),
        (BoardScreen.Paint, "paint.png"),
        (BoardScreen.CrownDeed, "crown-deed.png"),
        (BoardScreen.Globe, "globe.png"),
        (BoardScreen.Slots, "dragon-slots.png"),
        (BoardScreen.Roulette, "roulette.png")
    ];

    private readonly BitmapAssetSet _images;

    public MenuThumbnailImages(CanvasDevice device) =>
        _images = new(device, Path.Combine(AppContext.BaseDirectory, "Assets", "MenuPreviews"),
            Assets.Select(asset => asset.File).ToArray());

    public CanvasDevice Device => _images.Device;
    public bool IsReady => _images.IsLoaded;
    public int LoadedCount => Assets.Count(asset => _images.Image(asset.File) is not null);
    public string? Error => _images.Error ?? (_images.Errors.Count == 0 ? null
        : string.Join("; ", _images.Errors.Select(error => error.Key + ": " + error.Value)));
    public Task EnsureReadyAsync() => _images.EnsureLoadedAsync();

    public CanvasBitmap? Image(BoardScreen screen)
    {
        foreach (var (assetScreen, file) in Assets)
            if (assetScreen == screen) return _images.Image(file);
        return null;
    }

    public void Dispose() => _images.Dispose();
}
